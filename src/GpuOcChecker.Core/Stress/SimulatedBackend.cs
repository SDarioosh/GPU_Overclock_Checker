using GpuOcChecker.Core.Stress.OpenCl;
using GpuOcChecker.Core.Telemetry.Simulation;

namespace GpuOcChecker.Core.Stress;

/// <summary>Fake workloads that inject the failure pattern of a <see cref="SimScenario"/>.</summary>
public sealed class SimulatedBackend : IStressBackend
{
    private readonly SimulationState _state;
    private int _lightRuns;

    public SimulatedBackend(SimulationState state) => _state = state;

    public string DeviceName => "Simulated RX 7900 XTX";
    public string Description => $"simulator, scenario '{_state.Scenario}'";
    public long VramBytesUnderTest => 14L << 30;

    public void Initialize(StressSetup setup, Action<string> log) => log($"Simulation scenario: {_state.Scenario}");

    public ComputeRun RunCompute(bool heavy)
    {
        _state.Load = heavy ? SimLoad.Heavy : SimLoad.Light;
        Thread.Sleep(40);
        var r = _state.Random;
        long mismatches = 0;
        switch (_state.Scenario)
        {
            case SimScenario.Undervolt when !heavy:
                if (r.NextDouble() < 0.02) mismatches = r.Next(1, 40);
                break;
            case SimScenario.Thermal when heavy && _state.Heat > 0.8:
                if (r.NextDouble() < 0.03) mismatches = r.Next(1, 400);
                break;
            case SimScenario.Crash when !heavy && ++_lightRuns > 300:
                throw new ClException("clFinish(compute_check)", -5);
        }
        double throughput = heavy ? 58_000 : 5_200; // GFLOP/s-ish
        return new ComputeRun(mismatches, 0.040, throughput * 0.040);
    }

    public VramRun RunVramPass(uint pass)
    {
        _state.Load = SimLoad.Memory;
        Thread.Sleep(120);
        long errors = _state.Scenario == SimScenario.Memory && _state.Random.NextDouble() < 0.15 ? _state.Random.Next(1, 6) : 0;
        return new VramRun(errors, 0.12, VramBytesUnderTest * 5,
            errors > 0 ? $"@{_state.Random.Next(0, 14000)}MiB bits 0x00000100 (pattern {pass & 3})" : null);
    }

    public BandwidthRun RunBandwidth()
    {
        _state.Load = SimLoad.Memory;
        Thread.Sleep(30);
        // Real 7900 XTX copy bandwidth is roughly 800 GB/s at 2500 MHz; memory scenario simulates EDC retries.
        double gbps = 800 * _state.MemClock / 2500;
        if (_state.Scenario == SimScenario.Memory || _state.MemClock > 2700)
            gbps *= 0.93 + _state.Random.NextDouble() * 0.04;
        else
            gbps *= 0.995 + _state.Random.NextDouble() * 0.01;
        return new BandwidthRun(0.03, (long)(gbps * 1e9 * 0.03));
    }

    public void Idle() => _state.Load = SimLoad.Idle;

    public void Dispose()
    {
        _state.Load = SimLoad.Idle;
    }
}
