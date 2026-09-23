namespace GpuOcChecker.Core.Telemetry;

/// <summary>Vendor-neutral limiter flags (NVIDIA reports these directly; AMD does not via ADL).</summary>
[Flags]
public enum ThrottleFlags
{
    None = 0,
    Idle = 1,
    Power = 2,
    Thermal = 4,
    Hardware = 8,
    Other = 16,
}

/// <summary>One telemetry reading. Missing sensors are null.</summary>
public sealed class TelemetrySample
{
    private readonly double?[] _values = new double?[Metrics.Count];

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Seconds since the session started.</summary>
    public double Elapsed { get; set; }

    /// <summary>Stress phase (or "monitor"/"import") active when the sample was taken.</summary>
    public string? Phase { get; set; }

    public ThrottleFlags Throttle { get; set; }

    public double? this[Metric m]
    {
        get => _values[(int)m];
        set => _values[(int)m] = value is double d && (double.IsNaN(d) || double.IsInfinity(d)) ? null : value;
    }

    public bool Has(Metric m) => _values[(int)m].HasValue;

    public TelemetrySample Clone()
    {
        var c = new TelemetrySample { Timestamp = Timestamp, Elapsed = Elapsed, Phase = Phase, Throttle = Throttle };
        Array.Copy(_values, c._values, _values.Length);
        return c;
    }
}

public enum GpuVendor
{
    Unknown,
    Amd,
    Nvidia,
    Intel,
    Simulated,
}

public sealed record GpuInfo(GpuVendor Vendor, string Name, string Key, string Backend, int? PciBus = null)
{
    public override string ToString() => $"{Name} ({Backend})";
}

public sealed record RawSensor(string Name, double Value, string Unit);

public interface ITelemetrySource : IDisposable
{
    GpuInfo Gpu { get; }

    /// <summary>Reads all normalized metrics. Throws on a hard failure (e.g. driver gone).</summary>
    TelemetrySample Read();

    /// <summary>Every raw sensor the backend exposes, for diagnostics ("sensors --raw").</summary>
    IReadOnlyList<RawSensor> ReadRaw();
}
