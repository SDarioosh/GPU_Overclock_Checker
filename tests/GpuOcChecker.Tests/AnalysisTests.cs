using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Telemetry;
using Xunit;

namespace GpuOcChecker.Tests;

public class AnalysisTests
{
    private static readonly GpuInfo Xtx = new(GpuVendor.Amd, "AMD Radeon RX 7900 XTX", "test-xtx", "test");

    private static TelemetrySample Sample(DateTimeOffset t, double clock, double volt, double load, double power = 300,
        double edge = 60, double hot = 80, string? phase = null)
    {
        var s = new TelemetrySample { Timestamp = t, Phase = phase };
        s[Metric.CoreClock] = clock;
        s[Metric.CoreVoltage] = volt;
        s[Metric.CoreLoad] = load;
        s[Metric.Power] = power;
        s[Metric.EdgeTemp] = edge;
        s[Metric.HotspotTemp] = hot;
        s[Metric.MemClock] = 2500;
        return s;
    }

    /// <summary>Monitor session: gaming at moderate load, then a TDR while the card sat at peak boost clock.</summary>
    private static SessionData GamingSessionWithTdr(double loadAtCrash, double clockAtCrash)
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);
        var s = new SessionData { Mode = "monitor", Gpu = Xtx, Start = t0, End = t0.AddMinutes(5), CompletedCleanly = true };
        for (int i = 0; i < 600; i++)
        {
            bool nearCrash = i >= 580;
            s.Samples.Add(Sample(t0.AddSeconds(i * 0.5),
                nearCrash ? clockAtCrash : 2600, nearCrash ? 1.0 : 0.95, nearCrash ? loadAtCrash : 97, hot: 85));
        }
        s.Phases.Add(new PhaseResult { Name = "Monitoring", Kind = PhaseKind.Monitor, Start = t0, End = s.End, Completed = true });
        s.Events.Add(new SystemEvent
        {
            Time = t0.AddSeconds(299), Source = "Display", EventId = 4101, Category = EventCategory.DriverTimeout,
            Message = "Display driver amdkmdag stopped responding and has successfully recovered.",
        });
        return s;
    }

    [Fact]
    public void Tdr_at_light_load_and_peak_clock_is_attributed_to_undervolt()
    {
        var s = GamingSessionWithTdr(loadAtCrash: 45, clockAtCrash: 3000);
        s.Tuning = TuningSettings.Parse("uv=-90,max=3050");
        var r = new Analyzer().Analyze(s);

        Assert.Equal(Verdict.Unstable, r.Verdict);
        var f = r.Findings.First(x => x.Severity == Severity.Critical);
        Assert.Equal(Area.CoreUndervolt, f.Area);
        Assert.Contains("peak boost", f.Title);
        Assert.Contains("-90 mV to -80 mV", f.Recommendation);
        Assert.Contains("3050 to 3000 MHz", f.Recommendation);
    }

    [Fact]
    public void Tdr_under_full_load_is_attributed_to_loaded_voltage()
    {
        var s = GamingSessionWithTdr(loadAtCrash: 99, clockAtCrash: 2600);
        var r = new Analyzer().Analyze(s);
        var f = r.Findings.First(x => x.Severity == Severity.Critical);
        Assert.Contains("heavy", f.Title);
    }

    [Fact]
    public void Clean_log_is_not_unstable()
    {
        var s = GamingSessionWithTdr(45, 3000);
        s.Events.Clear();
        var r = new Analyzer().Analyze(s);
        Assert.NotEqual(Verdict.Unstable, r.Verdict);
    }

    [Fact]
    public void Vram_errors_are_critical_memory_findings_and_mention_fast_timing()
    {
        var t0 = DateTimeOffset.Now;
        var s = new SessionData { Mode = "stress", Gpu = Xtx, Start = t0, End = t0.AddMinutes(3), CompletedCleanly = true, Tuning = TuningSettings.Parse("mem=2700,ft=1") };
        for (int i = 0; i < 360; i++) s.Samples.Add(Sample(t0.AddSeconds(i * 0.5), 2300, 0.9, 80));
        var p = new PhaseResult { Name = "VRAM pattern test", Kind = PhaseKind.VramPattern, Start = t0, End = s.End, Completed = true };
        p.Errors.Add(new ErrorEvent { Time = t0.AddSeconds(60), Kind = "vram", Count = 12 });
        p.ErrorEvents = 1;
        p.ErrorCount = 12;
        s.Phases.Add(p);

        var r = new Analyzer().Analyze(s);
        var f = r.Findings.Single(x => x.Severity == Severity.Critical);
        Assert.Equal(Area.Memory, f.Area);
        Assert.Equal(Confidence.High, f.Confidence);
        Assert.Contains("Fast Timing", f.Recommendation);
        Assert.Contains("2700 MHz to 2650 MHz", f.Recommendation);
    }

    [Fact]
    public void Large_hotspot_delta_is_flagged()
    {
        var t0 = DateTimeOffset.Now;
        var s = new SessionData { Mode = "stress", Gpu = Xtx, Start = t0, End = t0.AddMinutes(3), CompletedCleanly = true };
        for (int i = 0; i < 200; i++) s.Samples.Add(Sample(t0.AddSeconds(i), 2500, 0.95, 99, edge: 65, hot: 108));
        var r = new Analyzer().Analyze(s);
        Assert.Contains(r.Findings, f => f.Area == Area.Thermal && f.Title.Contains("delta") && f.Severity == Severity.Critical);
    }

    [Fact]
    public void Abrupt_session_end_counts_as_crash()
    {
        var t0 = DateTimeOffset.Now;
        var s = new SessionData { Mode = "stress", Gpu = Xtx, Start = t0, CompletedCleanly = false };
        s.Phases.Add(new PhaseResult { Name = "Transient load steps", Kind = PhaseKind.Transient, Start = t0 });
        for (int i = 0; i < 100; i++) s.Samples.Add(Sample(t0.AddSeconds(i), i % 2 == 0 ? 2900 : 500, 0.9, i % 2 == 0 ? 90 : 5));
        s.End = s.Samples[^1].Timestamp;
        var r = new Analyzer().Analyze(s);
        Assert.Equal(Verdict.Unstable, r.Verdict);
        Assert.Contains(r.Findings, f => f.Title.Contains("transients"));
    }

    [Fact]
    public void Classify_infers_context_from_telemetry()
    {
        var t0 = DateTimeOffset.Now;
        var idle = Enumerable.Range(0, 10).Select(i => Sample(t0.AddSeconds(i), 50, 0.7, 2)).ToList();
        var heavy = Enumerable.Range(0, 10).Select(i => Sample(t0.AddSeconds(i), 2600, 0.95, 99)).ToList();
        var peak = Enumerable.Range(0, 10).Select(i => Sample(t0.AddSeconds(i), 3000, 1.05, 50)).ToList();
        var steps = Enumerable.Range(0, 10).Select(i => Sample(t0.AddSeconds(i), i % 2 == 0 ? 3000 : 800, 1, i % 2 == 0 ? 95 : 10)).ToList();
        Assert.Equal(LoadContext.Idle, Analyzer.Classify(null, idle, 3000));
        Assert.Equal(LoadContext.Heavy, Analyzer.Classify(null, heavy, 3000));
        Assert.Equal(LoadContext.PeakBoost, Analyzer.Classify(null, peak, 3000));
        Assert.Equal(LoadContext.Transient, Analyzer.Classify(null, steps, 3000));
        Assert.Equal(LoadContext.Heavy, Analyzer.Classify(PhaseKind.HeavyLoad, peak, 3000));
    }
}
