namespace GpuOcChecker.Core.Telemetry.Simulation;

/// <summary>Failure modes the simulator can reproduce (for demos and end-to-end tests without a GPU).</summary>
public enum SimScenario
{
    Stable,
    Undervolt,
    Memory,
    Thermal,
    Crash,
}

public enum SimLoad
{
    Idle,
    Light,
    Heavy,
    Memory,
}

/// <summary>Shared between the simulated stress backend (sets the load) and the simulated sensors (react to it).</summary>
public sealed class SimulationState
{
    public SimulationState(SimScenario scenario, int seed = 1234)
    {
        Scenario = scenario;
        Random = new Random(seed);
    }

    public SimScenario Scenario { get; }
    public Random Random { get; }
    public volatile SimLoad Load;

    /// <summary>0..1 heat soak, rises under heavy load.</summary>
    public double Heat;

    /// <summary>Memory clock the simulated user has "set" (memtune demo).</summary>
    public double MemClock = 2614;
}

/// <summary>Behaves like an RX 7900 XTX with a -80 mV undervolt, +15% power and 2614 MHz memory.</summary>
public sealed class SimulatedTelemetrySource : ITelemetrySource
{
    private readonly SimulationState _state;
    private DateTimeOffset _last = DateTimeOffset.Now;

    public SimulatedTelemetrySource(SimulationState state)
    {
        _state = state;
        Gpu = new GpuInfo(GpuVendor.Simulated, "AMD Radeon RX 7900 XTX (simulated)", "sim-7900xtx", "Simulator");
    }

    public GpuInfo Gpu { get; }

    public TelemetrySample Read()
    {
        var now = DateTimeOffset.Now;
        double dt = Math.Clamp((now - _last).TotalSeconds, 0, 2);
        _last = now;
        var r = _state.Random;
        double N(double sd) => (r.NextDouble() - 0.5) * 2 * sd;

        var load = _state.Load;
        double target = load switch { SimLoad.Heavy => 1.0, SimLoad.Light => 0.45, SimLoad.Memory => 0.6, _ => 0.05 };
        _state.Heat += (target - _state.Heat) * Math.Min(1, dt / 25.0);
        double heat = _state.Heat;

        bool thermal = _state.Scenario == SimScenario.Thermal;
        double edge = 34 + heat * 36;
        double hotspotDelta = thermal ? 8 + heat * 40 : 6 + heat * 16;

        var s = new TelemetrySample { Timestamp = now };
        (double clock, double volt, double power, double util) = load switch
        {
            SimLoad.Heavy => (2640 + N(40) - (thermal ? heat * 180 : 0), 0.985 + N(0.012), 400 + N(3), 99 + N(1)),
            SimLoad.Light => (2985 + N(12), _state.Scenario == SimScenario.Undervolt ? 1.012 + N(0.006) : 1.085 + N(0.006), 190 + N(20), 55 + N(10)),
            SimLoad.Memory => (2300 + N(80), 0.9 + N(0.02), 260 + N(10), 80 + N(8)),
            _ => (40 + N(20), 0.02 + Math.Abs(N(0.01)), 22 + N(3), 1 + Math.Abs(N(1))),
        };
        s[Metric.CoreClock] = clock;
        s[Metric.MemClock] = load == SimLoad.Idle ? 96 : _state.MemClock;
        s[Metric.SocClock] = load == SimLoad.Idle ? 400 : 1200;
        s[Metric.CoreVoltage] = volt;
        s[Metric.MemVoltage] = 1.25;
        s[Metric.SocVoltage] = 0.75;
        s[Metric.EdgeTemp] = edge + N(0.5);
        s[Metric.HotspotTemp] = Math.Min(110, edge + hotspotDelta + N(1));
        s[Metric.MemTemp] = 40 + heat * (_state.Scenario == SimScenario.Memory ? 58 : 38) + N(0.5);
        s[Metric.VrTemp] = 38 + heat * 40;
        s[Metric.Power] = power;
        s[Metric.CoreLoad] = Math.Clamp(util, 0, 100);
        s[Metric.MemLoad] = load == SimLoad.Memory ? 95 : util * 0.4;
        s[Metric.FanRpm] = 800 + heat * 1400;
        s[Metric.FanPercent] = 25 + heat * 45;
        return s;
    }

    public IReadOnlyList<RawSensor> ReadRaw()
    {
        var s = Read();
        return Metrics.All.Where(m => s.Has(m.Metric)).Select(m => new RawSensor(m.Label, s[m.Metric]!.Value, m.Unit)).ToList();
    }

    public void Dispose()
    {
    }
}
