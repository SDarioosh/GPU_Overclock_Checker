using System.Collections.Concurrent;
using GpuOcChecker.Core.Events;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Cli;

/// <summary>Telemetry sampling + event-log watching + crash-safe recording for stress and monitor.</summary>
public sealed class LiveSession : IDisposable
{
    private readonly ITelemetrySource? _source;
    private readonly GpuEventLog _events = new();
    private readonly ConcurrentQueue<string> _log = new();
    private int _consecutiveReadFailures;

    public LiveSession(ITelemetrySource? source, SessionData data)
    {
        _source = source;
        Data = data;
        Recorder = new SessionRecorder(data);
        _events.Watch(OnEvent);
    }

    public SessionData Data { get; }
    public SessionRecorder Recorder { get; }
    public TelemetrySample? Last { get; private set; }
    public string? Phase { get; set; }
    public MinMax Tracker { get; } = new();
    public int EventCount => Data.Events.Count;
    public Action<SystemEvent>? EventReceived { get; set; }

    public IReadOnlyCollection<string> RecentLog => _log;

    public void Log(string line)
    {
        _log.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Count > 7) _log.TryDequeue(out _);
    }

    private void OnEvent(SystemEvent e)
    {
        lock (Data.Events)
        {
            if (Data.Events.Any(x => x.Time == e.Time && x.Source == e.Source && x.EventId == e.EventId)) return;
            Data.Events.Add(e);
        }
        Recorder.Event(e);
        EventReceived?.Invoke(e);
    }

    public void Sample()
    {
        if (_source == null) return;
        try
        {
            var s = _source.Read();
            s.Elapsed = (s.Timestamp - Data.Start).TotalSeconds;
            s.Phase = Phase;
            Last = s;
            Tracker.Add(s);
            Data.Samples.Add(s);
            Recorder.AddSample(s);
            if (_consecutiveReadFailures > 3) Log("[green]Telemetry recovered.[/]");
            _consecutiveReadFailures = 0;
        }
        catch (Exception e)
        {
            if (++_consecutiveReadFailures == 3)
            {
                Log($"[red]Telemetry not responding ({Spectre.Console.Markup.Escape(e.Message)}) — driver may be resetting.[/]");
                Recorder.Note($"Telemetry read failures: {e.Message}");
            }
        }
    }

    /// <summary>Authoritative pass over the event log for the whole session (catches what the watcher missed).</summary>
    public void CollectEvents()
    {
        var found = GpuEventLog.Query(Data.Start.AddSeconds(-5), DateTimeOffset.Now);
        lock (Data.Events)
        {
            foreach (var e in found)
                if (!Data.Events.Any(x => x.Time == e.Time && x.Source == e.Source && x.EventId == e.EventId))
                    Data.Events.Add(e);
            Data.Events.Sort((a, b) => a.Time.CompareTo(b.Time));
        }
    }

    public void Dispose()
    {
        _events.Dispose();
        Recorder.Dispose();
    }
}

public sealed class MinMax
{
    private readonly double?[] _min = new double?[Metrics.Count];
    private readonly double?[] _max = new double?[Metrics.Count];

    public void Add(TelemetrySample s)
    {
        foreach (var m in Metrics.All)
        {
            if (s[m.Metric] is not double v) continue;
            int i = (int)m.Metric;
            _min[i] = _min[i] is double a ? Math.Min(a, v) : v;
            _max[i] = _max[i] is double b ? Math.Max(b, v) : v;
        }
    }

    public double? Min(Metric m) => _min[(int)m];
    public double? Max(Metric m) => _max[(int)m];
}
