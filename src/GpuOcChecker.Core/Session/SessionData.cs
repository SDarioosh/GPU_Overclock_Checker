using System.Text.Json.Serialization;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Session;

public enum PhaseKind
{
    Idle,
    Bandwidth,
    VramPattern,
    LightLoad,
    Transient,
    HeavyLoad,
    Monitor,
}

public static class PhaseKinds
{
    public static string Describe(PhaseKind k) => k switch
    {
        PhaseKind.Idle => "Idle / desktop",
        PhaseKind.Bandwidth => "VRAM bandwidth",
        PhaseKind.VramPattern => "VRAM pattern test",
        PhaseKind.LightLoad => "Light load (max boost clock)",
        PhaseKind.Transient => "Transient load steps",
        PhaseKind.HeavyLoad => "Heavy sustained load",
        PhaseKind.Monitor => "Real-world monitoring",
        _ => k.ToString(),
    };

    public static bool IsMemory(PhaseKind k) => k is PhaseKind.Bandwidth or PhaseKind.VramPattern;
}

public sealed class ErrorEvent
{
    public DateTimeOffset Time { get; set; }

    /// <summary>"compute", "vram", "device-lost", "calibration".</summary>
    public string Kind { get; set; } = "";

    public long Count { get; set; }
    public string? Detail { get; set; }
}

public sealed class PhaseResult
{
    public string Name { get; set; } = "";
    public PhaseKind Kind { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool Completed { get; set; }
    public long Iterations { get; set; }
    public long ErrorEvents { get; set; }
    public long ErrorCount { get; set; }
    public List<ErrorEvent> Errors { get; set; } = new();

    /// <summary>Per-dispatch throughput (GFLOP/s for compute, GB/s for bandwidth).</summary>
    public List<double> Throughput { get; set; } = new();

    public string? ThroughputUnit { get; set; }
    public bool DeviceLost { get; set; }
    public string? Failure { get; set; }

    [JsonIgnore]
    public double Duration => (End - Start).TotalSeconds;
}

public enum EventCategory
{
    DriverTimeout,
    DriverError,
    Whea,
    UnexpectedShutdown,
    Other,
}

public sealed class SystemEvent
{
    public DateTimeOffset Time { get; set; }
    public string Log { get; set; } = "";
    public string Source { get; set; } = "";
    public int EventId { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public EventCategory Category { get; set; }

    public string Short => $"{Source} {EventId}";
}

public sealed class SessionData
{
    public string Mode { get; set; } = "stress";
    public string? Profile { get; set; }
    public GpuInfo? Gpu { get; set; }
    public string? StressDevice { get; set; }
    public string? ToolVersion { get; set; }

    /// <summary>What the user told us about their Adrenalin/Afterburner settings (optional).</summary>
    public Analysis.TuningSettings? Tuning { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }

    /// <summary>False when the session was recovered from its journal after a crash.</summary>
    public bool CompletedCleanly { get; set; }

    public List<PhaseResult> Phases { get; set; } = new();
    public List<SystemEvent> Events { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    [JsonIgnore]
    public List<TelemetrySample> Samples { get; set; } = new();
}
