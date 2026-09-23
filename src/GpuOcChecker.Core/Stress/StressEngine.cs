using System.Diagnostics;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Stress.OpenCl;

namespace GpuOcChecker.Core.Stress;

/// <summary>Runs the phase plan on a background thread; the caller samples telemetry meanwhile.</summary>
public sealed class StressEngine
{
    private readonly IStressBackend _backend;
    private readonly IReadOnlyList<PhasePlan> _plan;
    private readonly StressSetup _setup;
    private readonly Random _rng = new();
    private Thread? _thread;
    private long _heartbeatTicks = DateTimeOffset.Now.UtcTicks;

    public StressEngine(IStressBackend backend, IReadOnlyList<PhasePlan> plan, StressSetup setup)
    {
        _backend = backend;
        _plan = plan;
        _setup = setup;
    }

    public event Action<PhaseResult>? PhaseStarted;
    public event Action<PhaseResult>? PhaseEnded;
    public event Action<PhaseResult, ErrorEvent>? ErrorDetected;
    public event Action<string>? Log;

    public List<PhaseResult> Results { get; } = new();
    public volatile PhaseResult? Current;
    public volatile bool Initializing = true;
    public volatile bool Finished;
    public Exception? FatalError { get; private set; }

    private long _totalErrors;

    /// <summary>Errors across all phases (thread-safe, for display).</summary>
    public long TotalErrors => Interlocked.Read(ref _totalErrors);

    /// <summary>Most recent throughput sample (GFLOP/s or GB/s) for display.</summary>
    public double LastThroughput { get; private set; }

    /// <summary>0..1 progress of the current phase.</summary>
    public double PhaseProgress { get; private set; }

    public int PhaseIndex { get; private set; }
    public int PhaseCount => _plan.Count;

    /// <summary>Last time the GPU completed work. A stale heartbeat under load means the GPU hung.</summary>
    public DateTimeOffset Heartbeat => new(Interlocked.Read(ref _heartbeatTicks), TimeSpan.Zero);

    public void Start(CancellationToken ct)
    {
        _thread = new Thread(() => Run(ct)) { IsBackground = true, Name = "stress-engine" };
        _thread.Start();
    }

    public bool Join(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    private void Beat() => Interlocked.Exchange(ref _heartbeatTicks, DateTimeOffset.Now.UtcTicks);

    private void Run(CancellationToken ct)
    {
        try
        {
            _backend.Initialize(_setup, m => Log?.Invoke(m));
            Beat();
            if (_backend is OpenClBackend ob && ob.CalibrationMismatches > 0)
            {
                var cal = new PhaseResult
                {
                    Name = "Calibration (idle to load step)", Kind = PhaseKind.Transient,
                    Start = DateTimeOffset.Now, End = DateTimeOffset.Now, Completed = true,
                };
                Results.Add(cal);
                AddError(cal, "compute", ob.CalibrationMismatches, "Result mismatch between the three reference runs");
            }
        }
        catch (Exception e)
        {
            FatalError = e;
            Initializing = false;
            Finished = true;
            return;
        }
        Initializing = false;

        for (int i = 0; i < _plan.Count && !ct.IsCancellationRequested; i++)
        {
            PhaseIndex = i;
            var plan = _plan[i];
            var r = new PhaseResult { Name = plan.Name, Kind = plan.Kind, Start = DateTimeOffset.Now };
            Results.Add(r);
            Current = r;
            PhaseProgress = 0;
            PhaseStarted?.Invoke(r);
            try
            {
                RunPhase(plan, r, ct);
                r.Completed = !ct.IsCancellationRequested;
            }
            catch (Exception e)
            {
                r.Failure = e.Message;
                r.DeviceLost = e is ClException;
                AddError(r, "device-lost", 1, e.Message);
                FatalError = e;
            }
            r.End = DateTimeOffset.Now;
            PhaseEnded?.Invoke(r);
            if (FatalError != null) break; // the OpenCL context is unusable after a device loss
        }
        Current = null;
        Finished = true;
    }

    private void RunPhase(PhasePlan plan, PhaseResult r, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        bool Running()
        {
            PhaseProgress = Math.Min(1, sw.Elapsed / plan.Duration);
            return sw.Elapsed < plan.Duration && !ct.IsCancellationRequested;
        }

        uint pass = 0;
        switch (plan.Kind)
        {
            case PhaseKind.Idle:
                _backend.Idle();
                while (Running()) { Beat(); ct.WaitHandle.WaitOne(100); }
                break;

            case PhaseKind.Bandwidth:
                r.ThroughputUnit = "GB/s";
                while (Running())
                {
                    var b = _backend.RunBandwidth();
                    Beat();
                    r.Iterations++;
                    r.Throughput.Add(b.GBps);
                    LastThroughput = b.GBps;
                }
                break;

            case PhaseKind.VramPattern:
                r.ThroughputUnit = "GB/s";
                while (Running())
                {
                    var v = _backend.RunVramPass(pass++);
                    Beat();
                    r.Iterations++;
                    if (v.Seconds > 0) r.Throughput.Add(LastThroughput = v.Bytes / v.Seconds / 1e9);
                    if (v.Errors > 0) AddError(r, "vram", v.Errors, v.Detail);
                }
                break;

            case PhaseKind.LightLoad:
            case PhaseKind.HeavyLoad:
                r.ThroughputUnit = "GFLOP/s";
                while (Running()) Compute(r, plan.Kind == PhaseKind.HeavyLoad);
                break;

            case PhaseKind.Transient:
                r.ThroughputUnit = "GFLOP/s";
                while (Running())
                {
                    // Bursts of full load separated by idle gaps: the voltage regulator and the
                    // firmware's clock/voltage controller must react to every step.
                    int burst = _rng.Next(1, 5);
                    for (int b = 0; b < burst && Running(); b++) Compute(r, heavy: _rng.NextDouble() < 0.8);
                    int gap = _rng.NextDouble() < 0.2 ? _rng.Next(250, 900) : _rng.Next(5, 120);
                    Beat();
                    _backend.Idle();
                    ct.WaitHandle.WaitOne(gap);
                }
                break;
        }
    }

    private void Compute(PhaseResult r, bool heavy)
    {
        var c = _backend.RunCompute(heavy);
        Beat();
        r.Iterations++;
        if (heavy && c.Seconds > 0) r.Throughput.Add(LastThroughput = c.GigaOps / c.Seconds);
        if (c.Mismatches > 0)
            AddError(r, "compute", c.Mismatches, $"{c.Mismatches} wrong results in one {(heavy ? "full-GPU" : "light")} dispatch");
    }

    private void AddError(PhaseResult r, string kind, long count, string? detail)
    {
        var e = new ErrorEvent { Time = DateTimeOffset.Now, Kind = kind, Count = count, Detail = detail };
        lock (r.Errors) r.Errors.Add(e);
        r.ErrorEvents++;
        r.ErrorCount += count;
        Interlocked.Add(ref _totalErrors, count);
        ErrorDetected?.Invoke(r, e);
    }
}
