namespace GpuOcChecker.Core.Stress;

public sealed class StressSetup
{
    public bool NeedCompute { get; set; } = true;
    public bool NeedVram { get; set; } = true;

    /// <summary>Fraction of VRAM to pattern-test. Too high and WDDM silently pages to system RAM.</summary>
    public double VramFraction { get; set; } = 0.6;

    /// <summary>Target duration of one compute dispatch; well below the 2 s Windows TDR limit.</summary>
    public double TargetDispatchMs { get; set; } = 40;

    /// <summary>Explicit OpenCL device index (see "list"), or null to auto-select.</summary>
    public int? DeviceIndex { get; set; }

    /// <summary>Telemetry GPU name, used to pick the matching OpenCL device when several exist.</summary>
    public string? PreferName { get; set; }
}

/// <summary>Result of one compute dispatch. <see cref="Mismatches"/> &gt; 0 means the GPU computed a wrong answer.</summary>
public readonly record struct ComputeRun(long Mismatches, double Seconds, double GigaOps);

public readonly record struct VramRun(long Errors, double Seconds, long Bytes, string? Detail);

public readonly record struct BandwidthRun(double Seconds, long Bytes)
{
    public double GBps => Seconds > 0 ? Bytes / Seconds / 1e9 : 0;
}

public interface IStressBackend : IDisposable
{
    string DeviceName { get; }
    string Description { get; }
    long VramBytesUnderTest { get; }

    void Initialize(StressSetup setup, Action<string> log);

    /// <summary>Heavy = whole GPU busy (power-limited). Light = a few CUs busy (clocks boost to max).</summary>
    ComputeRun RunCompute(bool heavy);

    VramRun RunVramPass(uint pass);

    BandwidthRun RunBandwidth();

    /// <summary>Called when the engine deliberately leaves the GPU idle.</summary>
    void Idle()
    {
    }
}
