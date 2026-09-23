using System.Globalization;
using System.Text.Json;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Analysis;

public enum Severity
{
    Pass,
    Info,
    Warning,
    Critical,
}

public enum Area
{
    CoreUndervolt,
    CoreFrequency,
    Core,
    Memory,
    Thermal,
    Power,
    Driver,
    System,
    Telemetry,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

public sealed class Finding
{
    public Severity Severity { get; set; }
    public Area Area { get; set; }
    public string Title { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
    public string Recommendation { get; set; } = "";
    public Confidence Confidence { get; set; } = Confidence.Medium;
    public DateTimeOffset? Time { get; set; }
}

public enum Verdict
{
    Stable,
    StableWithWarnings,
    Unstable,
    Inconclusive,
}

public sealed class PhaseSummary
{
    public string Name { get; set; } = "";
    public PhaseKind Kind { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public double Seconds { get; set; }
    public bool Completed { get; set; }
    public bool DeviceLost { get; set; }
    public long ErrorEvents { get; set; }
    public long ErrorCount { get; set; }
    public MetricStats? Throughput { get; set; }
    public string? ThroughputUnit { get; set; }
    public Dictionary<Metric, MetricStats> Stats { get; set; } = new();

    public double? Mean(Metric m) => Stats.TryGetValue(m, out var s) ? s.Mean : null;
    public double? Max(Metric m) => Stats.TryGetValue(m, out var s) ? s.Max : null;
    public double? Min(Metric m) => Stats.TryGetValue(m, out var s) ? s.Min : null;
}

public sealed record VfPoint(double ClockMHz, double MinVoltage, double AvgVoltage, int Samples);

public sealed class AnalysisReport
{
    public Verdict Verdict { get; set; }
    public string Headline { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public List<Finding> Findings { get; set; } = new();
    public List<PhaseSummary> Phases { get; set; } = new();
    public List<VfPoint> VfCurve { get; set; } = new();

    /// <summary>Instants the analysis considers failures (for chart markers).</summary>
    public List<(DateTimeOffset Time, string Label)> FailureMarks { get; set; } = new();

    public Dictionary<string, double> KeyStats { get; set; } = new();
}

/// <summary>The user's current tuning, optional. Makes recommendations concrete ("-100 mV → -90 mV").</summary>
public sealed class TuningSettings
{
    public int? VoltageOffsetMv { get; set; }
    public int? MaxVoltageMv { get; set; }
    public int? MaxFreqMHz { get; set; }
    public int? MinFreqMHz { get; set; }
    public int? MemClockMHz { get; set; }
    public bool? FastTiming { get; set; }
    public int? PowerLimitPct { get; set; }

    public bool IsEmpty => VoltageOffsetMv == null && MaxVoltageMv == null && MaxFreqMHz == null && MinFreqMHz == null
                           && MemClockMHz == null && FastTiming == null && PowerLimitPct == null;

    /// <summary>Parses "uv=-80,max=3000,min=500,mem=2700,ft=1,pl=15,maxv=1100".</summary>
    public static TuningSettings Parse(string? text)
    {
        var t = new TuningSettings();
        if (string.IsNullOrWhiteSpace(text)) return t;
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            var val = kv[1].Trim().ToLowerInvariant().Replace("mv", "").Replace("mhz", "").Replace("%", "");
            if (key is "ft" or "fast" or "fasttiming")
            {
                t.FastTiming = val is "1" or "true" or "yes" or "on" or "fast";
                continue;
            }
            if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) continue;
            switch (key)
            {
                case "uv" or "offset" or "voltage": t.VoltageOffsetMv = v > 0 ? -v : v; break;
                case "maxv" or "maxvoltage": t.MaxVoltageMv = v; break;
                case "max" or "maxfreq" or "maxclock": t.MaxFreqMHz = v; break;
                case "min" or "minfreq" or "minclock": t.MinFreqMHz = v; break;
                case "mem" or "memclock" or "vram": t.MemClockMHz = v; break;
                case "pl" or "power" or "powerlimit": t.PowerLimitPct = v; break;
            }
        }
        return t;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (VoltageOffsetMv is int uv) parts.Add($"voltage offset {uv} mV");
        if (MaxVoltageMv is int mv) parts.Add($"max voltage {mv} mV");
        if (MaxFreqMHz is int mx) parts.Add($"max freq {mx} MHz");
        if (MinFreqMHz is int mn) parts.Add($"min freq {mn} MHz");
        if (MemClockMHz is int mem) parts.Add($"memory {mem} MHz");
        if (FastTiming is bool ft) parts.Add(ft ? "fast timing ON" : "fast timing off");
        if (PowerLimitPct is int pl) parts.Add($"power limit {pl:+0;-0;0}%");
        return parts.Count == 0 ? "not provided" : string.Join(", ", parts);
    }
}

/// <summary>Reference numbers from a run at stock settings; later runs are compared against it.</summary>
public sealed class Baseline
{
    public string GpuKey { get; set; } = "";
    public string GpuName { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public string? Settings { get; set; }
    public double? HeavyGflops { get; set; }
    public double? HeavyAvgClock { get; set; }
    public double? HeavyGflopsPerMHz { get; set; }
    public double? HeavyAvgPower { get; set; }
    public double? BandwidthGBps { get; set; }
    public double? BandwidthMemClock { get; set; }
    public double? LightPeakClock { get; set; }
    public double? LightMaxVoltage { get; set; }

    public static string PathFor(string gpuKey) => System.IO.Path.Combine(AppPaths.Baselines, gpuKey + ".json");

    public static Baseline? Load(string gpuKey)
    {
        var p = PathFor(gpuKey);
        return File.Exists(p) ? JsonSerializer.Deserialize<Baseline>(File.ReadAllText(p), JsonOpts.Default) : null;
    }

    public void Save() => File.WriteAllText(PathFor(GpuKey), JsonSerializer.Serialize(this, JsonOpts.Default));

    public static Baseline FromReport(SessionData s, AnalysisReport r)
    {
        var heavy = r.Phases.Where(p => p.Kind == PhaseKind.HeavyLoad).MaxBy(p => p.Seconds);
        var bw = r.Phases.Where(p => p.Kind == PhaseKind.Bandwidth).MaxBy(p => p.Seconds);
        var light = r.Phases.Where(p => p.Kind == PhaseKind.LightLoad).MaxBy(p => p.Seconds);
        var b = new Baseline
        {
            GpuKey = s.Gpu?.Key ?? "unknown",
            GpuName = s.Gpu?.Name ?? "unknown",
            Created = DateTimeOffset.Now,
            Settings = s.Tuning is { IsEmpty: false } t ? t.ToString() : null,
            HeavyGflops = heavy?.Throughput?.Mean,
            HeavyAvgClock = heavy?.Mean(Metric.CoreClock),
            HeavyAvgPower = heavy?.Mean(Metric.Power),
            BandwidthGBps = bw?.Throughput?.Mean,
            BandwidthMemClock = bw?.Mean(Metric.MemClock),
            LightPeakClock = light?.Stats.GetValueOrDefault(Metric.CoreClock)?.P95,
            LightMaxVoltage = light?.Max(Metric.CoreVoltage),
        };
        if (b.HeavyGflops is double g && b.HeavyAvgClock is double c && c > 0) b.HeavyGflopsPerMHz = g / c;
        return b;
    }
}
