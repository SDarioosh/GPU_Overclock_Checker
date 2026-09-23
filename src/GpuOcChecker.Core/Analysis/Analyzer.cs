using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Analysis;

/// <summary>What the GPU was doing when something failed. This is what separates the causes.</summary>
public enum LoadContext
{
    /// <summary>Desktop / near idle: memory clock switching, low-V/F-point instability.</summary>
    Idle,

    /// <summary>Low occupancy, clocks at their maximum: top of the V/F curve (classic RDNA3 undervolt failure).</summary>
    PeakBoost,

    /// <summary>Load steps: voltage transients / droop while the regulator reacts.</summary>
    Transient,

    /// <summary>Full load: power-limited clocks, highest current, highest temperature.</summary>
    Heavy,

    /// <summary>Medium load that fits none of the above.</summary>
    Moderate,
}

public sealed class Analyzer
{
    private readonly Thresholds _t;

    public Analyzer(Thresholds? thresholds = null) => _t = thresholds ?? new Thresholds();

    /// <summary>One failure signal (several signals close together form an incident).</summary>
    private sealed record Signal(DateTimeOffset Time, string Source, bool MemorySpecific, long Count, string? Detail, bool Fatal);

    private sealed class Incident
    {
        public List<Signal> Signals { get; } = new();
        public DateTimeOffset Time => Signals.Min(s => s.Time);
        public bool Memory => Signals.Any(s => s.MemorySpecific);
        public bool Fatal => Signals.Any(s => s.Fatal);
        public PhaseResult? Phase { get; set; }
        public LoadContext Context { get; set; }
        public TelemetrySample? At { get; set; }
        public TelemetrySample? WindowMin { get; set; }
        public TelemetrySample? WindowMax { get; set; }
    }

    private sealed class Ctx
    {
        public required SessionData S;
        public required AnalysisReport R;
        public required CardProfile Profile;
        public Baseline? Baseline;
        public TuningSettings Tuning = new();
        public List<Incident> Incidents = new();
        public bool? UndervoltDetected;
        public string? UndervoltEvidence;
        public double PeakClock;

        public void Add(Finding f) => R.Findings.Add(f);
        public GpuVendor Vendor => S.Gpu?.Vendor is GpuVendor v and not GpuVendor.Unknown and not GpuVendor.Simulated ? v : Profile.Vendor;
        public bool IsAmd => Vendor == GpuVendor.Amd || S.Gpu?.Vendor == GpuVendor.Simulated;
    }

    public AnalysisReport Analyze(SessionData s, Baseline? baseline = null)
    {
        var profile = CardProfiles.Find(s.Gpu?.Name, s.Gpu?.Vendor ?? GpuVendor.Unknown);
        var r = new AnalysisReport
        {
            GpuName = s.Gpu?.Name ?? "Unknown GPU",
            ProfileName = profile.IsGeneric ? "generic limits" : profile.Name,
        };
        var c = new Ctx { S = s, R = r, Profile = profile, Baseline = baseline, Tuning = s.Tuning ?? new TuningSettings() };

        r.Phases = SummarizePhases(s);
        r.VfCurve = BuildVfCurve(s.Samples);
        var clocks = s.Samples.Where(x => x.Has(Metric.CoreClock)).Select(x => x[Metric.CoreClock]!.Value).OrderBy(x => x).ToArray();
        c.PeakClock = clocks.Length > 0 ? Stats.Percentile(clocks, 0.99) : 0;
        FillKeyStats(c);

        AssessUndervolt(c);
        CollectIncidents(c);
        ReportIncidents(c);
        AnalyzeBandwidth(c);
        AnalyzeThermals(c);
        AnalyzePower(c);
        AnalyzePerfPerClock(c);
        AnalyzeOtherEvents(c);
        AnalyzeSensorCoverage(c);
        DecideVerdict(c);

        r.Findings = r.Findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Area)
            .ToList();
        return r;
    }

    // ------------------------------------------------------------------ summaries

    private static List<PhaseSummary> SummarizePhases(SessionData s)
    {
        var list = new List<PhaseSummary>();
        foreach (var p in s.Phases)
        {
            var end = p.End == default ? s.End : p.End;
            var win = Stats.Window(s.Samples, p.Start, end).ToList();
            var ps = new PhaseSummary
            {
                Name = p.Name, Kind = p.Kind, Start = p.Start, End = end, Seconds = (end - p.Start).TotalSeconds,
                Completed = p.Completed, DeviceLost = p.DeviceLost, ErrorEvents = p.ErrorEvents, ErrorCount = p.ErrorCount,
                Throughput = p.Throughput.Count > 0 ? Stats.Of(TrimWarmup(p.Throughput)) : null,
                ThroughputUnit = p.ThroughputUnit,
            };
            foreach (var m in Metrics.All)
                if (Stats.Of(win, m.Metric) is { } st) ps.Stats[m.Metric] = st;
            list.Add(ps);
        }
        return list;
    }

    /// <summary>Drops the first 10% of throughput samples (clock ramp-up) when there are enough.</summary>
    private static IEnumerable<double> TrimWarmup(List<double> v) => v.Count >= 10 ? v.Skip(v.Count / 10) : v;

    private static List<VfPoint> BuildVfCurve(List<TelemetrySample> samples)
    {
        return samples
            .Where(x => x.Has(Metric.CoreClock) && x.Has(Metric.CoreVoltage) && (x[Metric.CoreLoad] ?? 100) >= 10 && x[Metric.CoreClock] > 300)
            .GroupBy(x => Math.Round(x[Metric.CoreClock]!.Value / 50) * 50)
            .Where(g => g.Count() >= 2)
            .OrderBy(g => g.Key)
            .Select(g => new VfPoint(g.Key, g.Min(x => x[Metric.CoreVoltage]!.Value), g.Average(x => x[Metric.CoreVoltage]!.Value), g.Count()))
            .ToList();
    }

    private static void FillKeyStats(Ctx c)
    {
        var s = c.S.Samples;
        void Put(string k, Metric m, Func<MetricStats, double> f)
        {
            if (Stats.Of(s, m) is { } st) c.R.KeyStats[k] = f(st);
        }
        Put("Peak core clock (MHz)", Metric.CoreClock, st => st.Max);
        Put("Max core voltage (V)", Metric.CoreVoltage, st => st.Max);
        Put("Max edge temp (°C)", Metric.EdgeTemp, st => st.Max);
        Put("Max hotspot temp (°C)", Metric.HotspotTemp, st => st.Max);
        Put("Max memory temp (°C)", Metric.MemTemp, st => st.Max);
        Put("Max board power (W)", Metric.Power, st => st.Max);
        Put("Avg memory clock under load (MHz)", Metric.MemClock, st => st.P95);
        c.R.KeyStats["Samples"] = s.Count;
        if (s.Count > 1) c.R.KeyStats["Duration (min)"] = Math.Round((s[^1].Timestamp - s[0].Timestamp).TotalMinutes, 1);
    }

    // ------------------------------------------------------------------ undervolt detection

    private void AssessUndervolt(Ctx c)
    {
        var t = c.Tuning;
        if (t.VoltageOffsetMv is int off)
        {
            c.UndervoltDetected = off < 0;
            c.UndervoltEvidence = $"voltage offset set to {off} mV";
            return;
        }
        if (t.MaxVoltageMv is int maxMv && c.Profile.StockMaxVoltage is double stockV)
        {
            c.UndervoltDetected = maxMv < stockV * 1000 - 5;
            c.UndervoltEvidence = $"max voltage set to {maxMv} mV (stock ~{stockV * 1000:0} mV)";
            return;
        }
        if (c.Profile.StockMaxVoltage is not double stock) return;

        // Highest voltage the card requested under light/transient load, where it runs its top V/F point.
        var candidates = c.S.Samples.Where(x => x.Has(Metric.CoreVoltage) && (x[Metric.CoreLoad] ?? 0) >= 10 &&
                                                (x[Metric.CoreClock] ?? 0) >= c.PeakClock * 0.95).ToList();
        if (candidates.Count < 5) return;
        var sorted = candidates.Select(x => x[Metric.CoreVoltage]!.Value).OrderBy(v => v).ToArray();
        double top = Stats.Percentile(sorted, 0.98);
        if (top < stock - _t.UndervoltDetectMargin)
        {
            c.UndervoltDetected = true;
            c.UndervoltEvidence = $"core voltage at peak clock tops out at {top:0.000} V vs ~{stock:0.000} V stock maximum";
        }
        else if (top >= stock - 0.02)
        {
            c.UndervoltDetected = false;
            c.UndervoltEvidence = $"core voltage at peak clock reaches {top:0.000} V (≈ stock {stock:0.000} V)";
        }
    }

    // ------------------------------------------------------------------ incidents

    private void CollectIncidents(Ctx c)
    {
        var s = c.S;
        var signals = new List<Signal>();

        foreach (var p in s.Phases)
        {
            foreach (var e in p.Errors)
            {
                bool mem = e.Kind == "vram" || (e.Kind == "device-lost" && PhaseKinds.IsMemory(p.Kind));
                string src = e.Kind switch
                {
                    "compute" => "wrong compute results",
                    "vram" => "VRAM data errors",
                    "device-lost" => "GPU stopped responding (OpenCL device lost)",
                    _ => e.Kind,
                };
                signals.Add(new Signal(e.Time, src, mem, e.Count, e.Detail, e.Kind == "device-lost"));
            }
        }

        var from = s.Start.AddSeconds(-5);
        var to = (s.End == default ? DateTimeOffset.MaxValue : s.End.AddSeconds(90));
        foreach (var e in s.Events.Where(e => e.Time >= from && e.Time <= to))
        {
            if (e.Category is EventCategory.DriverTimeout or EventCategory.DriverError)
            {
                var phase = PhaseAt(s, e.Time);
                signals.Add(new Signal(e.Time, $"driver reset / error ({e.Short})", phase != null && PhaseKinds.IsMemory(phase.Kind), 1, e.Message, true));
            }
        }

        if (s.Mode is "stress" or "monitor" && s.Samples.Count > 2)
        {
            for (int i = 1; i < s.Samples.Count; i++)
            {
                double gap = (s.Samples[i].Timestamp - s.Samples[i - 1].Timestamp).TotalSeconds;
                if (gap >= _t.TelemetryGapSeconds)
                {
                    var tm = s.Samples[i - 1].Timestamp;
                    var phase = PhaseAt(s, tm);
                    signals.Add(new Signal(tm, $"telemetry froze for {gap:0.0} s", phase != null && PhaseKinds.IsMemory(phase.Kind), 1, null, false));
                }
            }
        }

        if (!s.CompletedCleanly && s.Samples.Count > 0)
        {
            var last = s.Samples[^1].Timestamp;
            var phase = PhaseAt(s, last);
            var shutdown = s.Events.FirstOrDefault(e => e.Category == EventCategory.UnexpectedShutdown && e.Time >= last.AddMinutes(-1));
            string detail = shutdown != null
                ? $"Windows logged an unexpected shutdown ({shutdown.Short}) at {shutdown.Time:HH:mm:ss}"
                : "the tool never wrote its end-of-session record (system crash/freeze, black screen reboot or the app was killed)";
            signals.Add(new Signal(last, "session ended abruptly", phase != null && PhaseKinds.IsMemory(phase.Kind), 1, detail, true));
        }

        // Merge signals within 20 s into incidents (a TDR produces a device-lost, a telemetry stall and 2-3 events).
        foreach (var sig in signals.OrderBy(x => x.Time))
        {
            var last = c.Incidents.LastOrDefault();
            if (last != null && (sig.Time - last.Signals[^1].Time).TotalSeconds <= 20 && (sig.Fatal || last.Fatal || sig.MemorySpecific == last.Memory))
                last.Signals.Add(sig);
            else
                c.Incidents.Add(new Incident { Signals = { sig } });
        }

        foreach (var inc in c.Incidents)
        {
            inc.Phase = PhaseAt(s, inc.Time);
            var win = Stats.Window(s.Samples, inc.Time.AddSeconds(-_t.FailureContextSeconds), inc.Time.AddSeconds(0.5)).ToList();
            inc.At = win.LastOrDefault() ?? s.Samples.LastOrDefault(x => x.Timestamp <= inc.Time);
            if (win.Count > 0)
            {
                inc.WindowMin = Aggregate(win, Math.Min);
                inc.WindowMax = Aggregate(win, Math.Max);
            }
            inc.Context = Classify(inc.Phase?.Kind, win, c.PeakClock);
            c.R.FailureMarks.Add((inc.Time, inc.Signals[0].Source));
        }
    }

    private static TelemetrySample Aggregate(List<TelemetrySample> win, Func<double, double, double> f)
    {
        var r = new TelemetrySample { Timestamp = win[^1].Timestamp };
        foreach (var m in Metrics.All)
        {
            var vals = win.Where(x => x.Has(m.Metric)).Select(x => x[m.Metric]!.Value).ToList();
            if (vals.Count > 0) r[m.Metric] = vals.Aggregate(f);
        }
        return r;
    }

    private static PhaseResult? PhaseAt(SessionData s, DateTimeOffset t)
    {
        var inside = s.Phases.LastOrDefault(p => p.Start <= t && (p.End == default || t <= p.End.AddSeconds(1)));
        return inside ?? s.Phases.LastOrDefault(p => p.Start <= t);
    }

    /// <summary>Stress phases define the context directly; for real-world logs it is inferred from telemetry.</summary>
    public static LoadContext Classify(PhaseKind? phase, List<TelemetrySample> window, double peakClock)
    {
        switch (phase)
        {
            case PhaseKind.Idle: return LoadContext.Idle;
            case PhaseKind.LightLoad: return LoadContext.PeakBoost;
            case PhaseKind.Transient: return LoadContext.Transient;
            case PhaseKind.HeavyLoad: return LoadContext.Heavy;
        }
        var loads = window.Where(x => x.Has(Metric.CoreLoad)).Select(x => x[Metric.CoreLoad]!.Value).ToList();
        var clocks = window.Where(x => x.Has(Metric.CoreClock)).Select(x => x[Metric.CoreClock]!.Value).ToList();
        if (loads.Count == 0 && clocks.Count == 0) return LoadContext.Moderate;

        double meanLoad = loads.Count > 0 ? loads.Average() : 50;
        double loadRange = loads.Count > 0 ? loads.Max() - loads.Min() : 0;
        double maxClock = clocks.Count > 0 ? clocks.Max() : 0;
        double clockRange = clocks.Count > 0 ? clocks.Max() - clocks.Min() : 0;

        if (meanLoad < 15 && maxClock < peakClock * 0.6) return LoadContext.Idle;
        if (loadRange >= 50 || (peakClock > 0 && clockRange >= peakClock * 0.4)) return LoadContext.Transient;
        if (meanLoad >= 85) return LoadContext.Heavy;
        if (peakClock > 0 && maxClock >= peakClock * 0.95) return LoadContext.PeakBoost;
        return LoadContext.Moderate;
    }

    private void ReportIncidents(Ctx c)
    {
        var memory = c.Incidents.Where(i => i.Memory).ToList();
        var core = c.Incidents.Where(i => !i.Memory).ToList();

        if (memory.Count > 0) c.Add(MemoryFinding(c, memory));
        foreach (var group in core.GroupBy(i => i.Context).OrderByDescending(g => g.Count()))
            c.Add(CoreFinding(c, group.Key, group.ToList()));
    }

    private static string Describe(TelemetrySample? s)
    {
        if (s == null) return "no telemetry at that moment";
        var parts = new List<string>();
        if (s[Metric.CoreClock] is double clk) parts.Add($"{clk:0} MHz" + (s[Metric.CoreVoltage] is double v ? $" @ {v:0.000} V" : ""));
        if (s[Metric.CoreLoad] is double l) parts.Add($"load {l:0}%");
        if (s[Metric.Power] is double p) parts.Add($"{p:0} W");
        if (s[Metric.HotspotTemp] is double h) parts.Add($"hotspot {h:0} °C");
        else if (s[Metric.EdgeTemp] is double e) parts.Add($"GPU {e:0} °C");
        if (s[Metric.MemClock] is double mc) parts.Add($"mem {mc:0} MHz");
        if (s[Metric.MemTemp] is double mt) parts.Add($"mem {mt:0} °C");
        return string.Join(", ", parts);
    }

    private static List<string> IncidentEvidence(List<Incident> incidents, string? extra = null)
    {
        var ev = new List<string>();
        var sources = incidents.SelectMany(i => i.Signals)
            .GroupBy(s => s.Source)
            .Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key);
        ev.Add($"Failures: {string.Join("; ", sources)}" + (incidents.Count > 1 ? $" ({incidents.Count} separate incidents)" : ""));
        foreach (var inc in incidents.Take(3))
        {
            var where = inc.Phase != null ? $" during '{inc.Phase.Name}'" : "";
            ev.Add($"{inc.Time:HH:mm:ss}{where}: {Describe(inc.At)}");
            if (inc.WindowMin?[Metric.CoreVoltage] is double vmin && inc.WindowMax?[Metric.CoreClock] is double cmax)
                ev.Add($"   preceding seconds: clock up to {cmax:0} MHz, voltage down to {vmin:0.000} V");
            var detail = inc.Signals.Select(x => x.Detail).FirstOrDefault(d => !string.IsNullOrEmpty(d));
            if (detail != null) ev.Add("   " + (detail.Length > 200 ? detail[..200] + "…" : detail));
        }
        if (incidents.Count > 3) ev.Add($"… and {incidents.Count - 3} more");
        if (extra != null) ev.Add(extra);
        return ev;
    }

    private Finding MemoryFinding(Ctx c, List<Incident> incidents)
    {
        var t = c.Tuning;
        bool hardErrors = incidents.Any(i => i.Signals.Any(s => s.Source == "VRAM data errors"));
        long errorCount = incidents.SelectMany(i => i.Signals).Where(s => s.Source == "VRAM data errors").Sum(s => s.Count);
        var memClk = incidents.Select(i => i.At?[Metric.MemClock]).FirstOrDefault(x => x != null);

        var rec = new List<string>();
        if (t.FastTiming == true)
            rec.Add("Disable Fast Timing first — tightened timings usually fail before the clock does.");
        else if (t.FastTiming == null && c.IsAmd)
            rec.Add("If Fast Timing (memory timing control) is enabled, disable it first — it usually fails before the clock does.");
        rec.Add(t.MemClockMHz is int m
            ? $"Lower the memory clock in 25–50 MHz steps (from {m} MHz to {m - 50} MHz) and re-test."
            : $"Lower the memory clock in 25–50 MHz steps{(memClk is double mc ? $" (currently ~{mc:0} MHz)" : "")} and re-test.");
        rec.Add("GDDR6/GDDR6X error correction (EDC retries) hides most errors, so a hard error means the setting is well past the limit. " +
                "Choose the final clock with 'memtune' (highest bandwidth, not highest clock).");

        return new Finding
        {
            Severity = Severity.Critical,
            Area = Area.Memory,
            Title = hardErrors ? $"VRAM errors detected ({errorCount} corrupted words): memory overclock is unstable"
                               : "Driver reset / crash during the memory test: memory overclock is the prime suspect",
            Confidence = hardErrors ? Confidence.High : Confidence.Medium,
            Evidence = IncidentEvidence(incidents),
            Recommendation = string.Join(" ", rec),
            Time = incidents[0].Time,
        };
    }

    private Finding CoreFinding(Ctx c, LoadContext ctx, List<Incident> incidents)
    {
        var t = c.Tuning;
        bool hardEvidence = incidents.Any(i => i.Phase != null && i.Phase.Kind != PhaseKind.Monitor);
        bool fatal = incidents.Any(i => i.Fatal);
        string uvNote = c.UndervoltEvidence != null ? $"Undervolt check: {c.UndervoltEvidence}." : "";
        string raiseV = t.VoltageOffsetMv is int off
            ? $"raise the voltage offset by 10 mV (from {off} mV to {off + 10} mV)"
            : t.MaxVoltageMv is int mv ? $"raise max voltage by 10 mV (from {mv} mV to {mv + 10} mV)"
            : "reduce the undervolt by 10 mV";
        string lowerF = t.MaxFreqMHz is int mx
            ? $"lower Max Frequency by 50 MHz (from {mx} to {mx - 50} MHz)"
            : "lower Max Frequency by 50 MHz";

        var f = new Finding
        {
            Severity = Severity.Critical,
            Time = incidents[0].Time,
            Evidence = IncidentEvidence(incidents, uvNote.Length > 0 ? uvNote : null),
            Confidence = hardEvidence ? Confidence.High : Confidence.Medium,
        };
        string what = fatal ? "crashed" : "produced wrong results";

        switch (ctx)
        {
            case LoadContext.PeakBoost:
                f.Title = $"Core {what} at peak boost clock (light load, top of the V/F curve)";
                if (c.UndervoltDetected == false)
                {
                    f.Area = Area.CoreFrequency;
                    f.Recommendation = $"Voltage is at stock level, so the frequency itself is too high: {lowerF} and re-test.";
                }
                else
                {
                    f.Area = Area.CoreUndervolt;
                    f.Recommendation =
                        "Under light load the card boosts to its highest clocks, which need the most voltage — the undervolt is too deep for the top of the V/F curve. " +
                        $"Either {raiseV}, or keep the undervolt and {lowerF} (in games this typically costs <1% FPS). Re-test after each 10 mV / 50 MHz step.";
                    if (c.UndervoltDetected == null) f.Confidence = Confidence.Medium;
                }
                break;

            case LoadContext.Transient:
                f.Area = Area.CoreUndervolt;
                f.Title = $"Core {what} during load transients (idle ↔ load steps)";
                f.Recommendation =
                    "Sudden load steps briefly pull the voltage below its set point before the regulator catches up; an undervolt removes the margin that normally absorbs this. " +
                    $"{Cap(raiseV)} (or 15 mV). Lowering Max Frequency by 50 MHz also shrinks the steps." +
                    (c.IsAmd ? " On Radeon cards, raising Min Frequency (e.g. to ~500 MHz below max) reduces the size of idle→load swings in bursty games." : "");
                break;

            case LoadContext.Heavy:
                double? hot = incidents.Select(i => i.WindowMax?[Metric.HotspotTemp] ?? i.WindowMax?[Metric.EdgeTemp]).Max();
                double limit = incidents.Any(i => i.WindowMax?[Metric.HotspotTemp] != null) ? c.Profile.HotspotLimitC : c.Profile.HotspotLimitC - 15;
                if (hot is double h && h >= limit - _t.HotErrorContextMargin)
                {
                    f.Area = Area.Thermal;
                    f.Title = $"Core {what} under heavy load at high temperature ({h:0} °C)";
                    f.Recommendation =
                        "Silicon needs more voltage as it gets hotter, so a setting that passes cold can fail heat-soaked. " +
                        $"Improve cooling (more aggressive fan curve, check hotspot delta below), lower the power limit, or {raiseV}.";
                }
                else if (c.UndervoltDetected == false)
                {
                    f.Area = Area.CoreFrequency;
                    f.Title = $"Core {what} under heavy sustained load";
                    f.Recommendation = $"Voltage is at stock level, so the loaded clock is too high for this chip: {lowerF}" +
                                       (t.PowerLimitPct is > 0 ? " or reduce the raised power limit (it lets the card run higher clocks under load)." : ".");
                }
                else
                {
                    f.Area = Area.CoreUndervolt;
                    f.Title = $"Core {what} under heavy sustained load (voltage droop)";
                    f.Recommendation =
                        "At full load the highest current flows and the voltage sags furthest (load-line / vdroop); the undervolt leaves too little margin here. " +
                        $"{Cap(raiseV)}." +
                        (t.PowerLimitPct is > 0 ? $" Your +{t.PowerLimitPct}% power limit increases load current; test the same undervolt at 0% to confirm." : " A raised power limit makes this worse.");
                }
                break;

            case LoadContext.Idle:
                f.Area = Area.Core;
                f.Confidence = Confidence.Low;
                f.Title = $"GPU {what} at idle / very low load";
                f.Recommendation =
                    "Idle and desktop crashes are rarely caused by the max-boost undervolt. Most common: memory overclock or Fast Timing (VRAM clock switching at idle, multi-monitor/high refresh), " +
                    "or a very low Min Frequency combined with an undervolt. Re-test with memory at stock first; then raise Min Frequency.";
                break;

            default:
                f.Area = Area.Core;
                f.Confidence = Confidence.Low;
                f.Title = $"GPU {what} at moderate load";
                f.Recommendation =
                    $"Context does not point at a single cause. Back off one setting at a time: first {raiseV}, then memory -50 MHz, then {lowerF}; re-test after each change.";
                break;
        }
        return f;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ------------------------------------------------------------------ memory bandwidth

    private void AnalyzeBandwidth(Ctx c)
    {
        var bw = c.R.Phases.Where(p => p.Kind == PhaseKind.Bandwidth && p.Throughput != null).MaxBy(p => p.Seconds);
        if (bw?.Throughput == null) return;
        double gbps = bw.Throughput.Mean;
        double? memClk = bw.Mean(Metric.MemClock);
        var ev = new List<string> { $"Copy bandwidth {gbps:0} GB/s (variation ±{bw.Throughput.Cov * 100:0.0}%)" + (memClk is double m ? $" at {m:0} MHz memory clock" : "") };
        if (memClk is double mc && c.Profile.TheoreticalBandwidthAt(mc) is double theo && theo > 0)
            ev.Add($"Theoretical at this clock: {theo:0} GB/s → {gbps / theo * 100:0}% efficiency (copy kernels typically reach 75–90%)");

        if (bw.Throughput.Cov > _t.BandwidthCovWarn && bw.Throughput.N >= 5)
        {
            c.Add(new Finding
            {
                Severity = Severity.Warning, Area = Area.Memory, Confidence = Confidence.Medium,
                Title = "Memory bandwidth is erratic",
                Evidence = ev,
                Recommendation = "Run-to-run bandwidth should be nearly constant. Large swings are typical of error-correction retries (memory clock or Fast Timing too aggressive) — lower memory clock 25–50 MHz and compare with 'memtune'.",
            });
        }

        var b = c.Baseline;
        if (b?.BandwidthGBps is double bbw && b.BandwidthMemClock is double bclk && memClk is double clk && bbw > 0 && bclk > 0)
        {
            double rc = clk / bclk, rb = gbps / bbw;
            ev.Add($"Baseline ({b.Created:yyyy-MM-dd}): {bbw:0} GB/s at {bclk:0} MHz → clock {Pct(rc)}, bandwidth {Pct(rb)}");
            if (rc > 1.005 && rb < 1 - _t.BandwidthDrop)
                c.Add(new Finding
                {
                    Severity = Severity.Critical, Area = Area.Memory, Confidence = Confidence.High,
                    Title = "Memory overclock is SLOWER than stock (error-correction retries)",
                    Evidence = ev,
                    Recommendation = "The memory clock is higher but delivers less bandwidth: GDDR6 is detecting transmission errors and retrying. Lower the memory clock until bandwidth peaks (use 'memtune'), and disable Fast Timing if it is on.",
                });
            else if (rc > 1.005 && rb - 1 < (rc - 1) * _t.BandwidthScalingTolerance)
                c.Add(new Finding
                {
                    Severity = Severity.Warning, Area = Area.Memory, Confidence = Confidence.Medium,
                    Title = "Memory overclock is past the point of useful scaling",
                    Evidence = ev,
                    Recommendation = "Bandwidth grew much less than the clock — errors are starting to be retried. Step memory down 25 MHz at a time with 'memtune' and keep the clock with the highest bandwidth.",
                });
            else if (Math.Abs(rc - 1) <= 0.005 && rb < 1 - _t.BandwidthDrop)
                c.Add(new Finding
                {
                    Severity = Severity.Warning, Area = Area.Memory, Confidence = Confidence.Medium,
                    Title = "Lower bandwidth than baseline at the same memory clock",
                    Evidence = ev,
                    Recommendation = "Same clock, less bandwidth: usually Fast Timing causing retries, or memory running hot. Compare with Fast Timing off.",
                });
            else
                c.Add(new Finding
                {
                    Severity = Severity.Pass, Area = Area.Memory,
                    Title = rc > 1.005 ? "Memory overclock scales with clock" : "Memory bandwidth matches baseline",
                    Evidence = ev,
                });
        }
        else
        {
            c.Add(new Finding
            {
                Severity = Severity.Info, Area = Area.Memory,
                Title = "Memory bandwidth measured (no stock baseline to compare)",
                Evidence = ev,
                Recommendation = "Memory OC failures mostly show up as lost bandwidth, not as crashes. Run 'stress --save-baseline' once at stock memory settings, or use 'memtune' to find the clock with the highest bandwidth.",
            });
        }
    }

    private static string Pct(double ratio) => $"{(ratio - 1) * 100:+0.0;-0.0;0.0}%";

    // ------------------------------------------------------------------ thermals

    private void AnalyzeThermals(Ctx c)
    {
        var s = c.S.Samples;
        var p = c.Profile;
        bool any = false;

        if (Stats.Of(s, Metric.HotspotTemp) is { } hot)
        {
            if (hot.Max >= p.HotspotLimitC - _t.HotspotMarginCritical)
            {
                any = true;
                c.Add(new Finding
                {
                    Severity = Severity.Critical, Area = Area.Thermal, Confidence = Confidence.High,
                    Title = $"Hotspot hit {hot.Max:0} °C — thermal throttling (limit {p.HotspotLimitC:0} °C)",
                    Evidence = { $"Hotspot: avg {hot.Mean:0} °C, 95th percentile {hot.P95:0} °C, max {hot.Max:0} °C" },
                    Recommendation = "The card is at its temperature limit and pulls clocks down; overclock/undervolt results are unreliable at this temperature. Improve airflow / fan curve, check the hotspot delta, lower the power limit.",
                });
            }
            else if (hot.Max >= p.HotspotLimitC - _t.HotspotMarginWarn)
            {
                any = true;
                c.Add(new Finding
                {
                    Severity = Severity.Warning, Area = Area.Thermal,
                    Title = $"Hotspot close to its limit ({hot.Max:0} °C of {p.HotspotLimitC:0} °C)",
                    Evidence = { $"Hotspot: avg {hot.Mean:0} °C, max {hot.Max:0} °C" },
                    Recommendation = "Little thermal headroom left; a warmer room or longer session can push it into throttling. Consider a stronger fan curve.",
                });
            }

            var deltas = s.Where(x => x.Has(Metric.HotspotTemp) && x.Has(Metric.EdgeTemp) && (x[Metric.CoreLoad] ?? 100) >= 80)
                .Select(x => x[Metric.HotspotTemp]!.Value - x[Metric.EdgeTemp]!.Value).OrderBy(x => x).ToArray();
            if (deltas.Length >= 10)
            {
                double d95 = Stats.Percentile(deltas, 0.95);
                if (d95 >= _t.HotspotDeltaWarn)
                {
                    any = true;
                    c.Add(new Finding
                    {
                        Severity = d95 >= _t.HotspotDeltaCritical ? Severity.Critical : Severity.Warning,
                        Area = Area.Thermal, Confidence = Confidence.High,
                        Title = $"Large hotspot-to-edge delta under load ({d95:0} °C)",
                        Evidence = { $"Hotspot minus edge temperature at ≥80% load: median {Stats.Percentile(deltas, 0.5):0} °C, 95th percentile {d95:0} °C (healthy: ~10–20 °C)" },
                        Recommendation = "The die is not transferring heat evenly: poor mounting pressure, thermal paste pump-out, or (reference RX 7900 XTX) the known vapor-chamber defect — it often looks worse with the card mounted horizontally. " +
                                         "This limits clocks and makes the card fail an undervolt it would otherwise pass. Repaste/remount, or RMA if it is a reference card.",
                    });
                }
            }
        }

        if (Stats.Of(s, Metric.MemTemp) is { } mem && mem.Max >= p.MemTempLimitC - _t.MemTempMarginWarn)
        {
            any = true;
            c.Add(new Finding
            {
                Severity = Severity.Warning, Area = Area.Memory,
                Title = $"VRAM is hot ({mem.Max:0} °C)",
                Evidence = { $"Memory junction: avg {mem.Mean:0} °C, max {mem.Max:0} °C (throttles around {p.MemTempLimitC:0}–{p.MemTempLimitC + 5:0} °C)" },
                Recommendation = "GDDR error rates rise with temperature, so a memory OC that passes cold can fail (or silently lose bandwidth) hot. Improve case airflow, raise the fan curve, or back the memory clock off 25–50 MHz.",
            });
        }

        if (Stats.Of(s, Metric.VrTemp) is { } vr && vr.Max >= _t.VrTempWarn)
        {
            any = true;
            c.Add(new Finding
            {
                Severity = Severity.Warning, Area = Area.Power,
                Title = $"Voltage regulator hot ({vr.Max:0} °C)",
                Evidence = { $"VRM: avg {vr.Mean:0} °C, max {vr.Max:0} °C" },
                Recommendation = "A hot VRM regulates worse (more ripple and droop). Improve airflow over the card or reduce the power limit.",
            });
        }

        if (!any && (s.Any(x => x.Has(Metric.HotspotTemp)) || s.Any(x => x.Has(Metric.EdgeTemp))))
        {
            var ev = new List<string>();
            if (Stats.Of(s, Metric.EdgeTemp) is { } e) ev.Add($"Edge max {e.Max:0} °C");
            if (Stats.Of(s, Metric.HotspotTemp) is { } h) ev.Add($"Hotspot max {h.Max:0} °C");
            if (Stats.Of(s, Metric.MemTemp) is { } m) ev.Add($"Memory max {m.Max:0} °C");
            c.Add(new Finding { Severity = Severity.Pass, Area = Area.Thermal, Title = "Temperatures are healthy", Evidence = ev });
        }
    }

    // ------------------------------------------------------------------ power

    private static void AnalyzePower(Ctx c)
    {
        var heavy = c.R.Phases.Where(p => p.Kind == PhaseKind.HeavyLoad).MaxBy(p => p.Seconds);
        var samples = heavy != null
            ? Stats.Window(c.S.Samples, heavy.Start.AddSeconds(Math.Min(10, heavy.Seconds / 4)), heavy.End).ToList()
            : c.S.Samples.Where(x => (x[Metric.CoreLoad] ?? 0) >= 90).ToList();
        if (samples.Count < 10) return;

        int flagged = samples.Count(x => x.Throttle != ThrottleFlags.None);
        if (flagged > 0)
        {
            double Frac(ThrottleFlags f) => samples.Count(x => (x.Throttle & f) != 0) / (double)samples.Count;
            var ev = new List<string>
            {
                $"Under load: power-limited {Frac(ThrottleFlags.Power) * 100:0}% of the time, thermal {Frac(ThrottleFlags.Thermal) * 100:0}%, hardware slowdown {Frac(ThrottleFlags.Hardware) * 100:0}%",
            };
            if (Frac(ThrottleFlags.Hardware) > 0.02)
                c.Add(new Finding
                {
                    Severity = Severity.Warning, Area = Area.Power, Title = "Hardware slowdown events (HW slowdown / power brake)", Evidence = ev,
                    Recommendation = "Hardware slowdown is triggered by the board itself (PSU/connector power brake, over-current or over-temperature). Check PSU cabling (12VHPWR/12V-2x6 fully seated) and temperatures.",
                });
            else if (Frac(ThrottleFlags.Thermal) > 0.05)
                c.Add(new Finding { Severity = Severity.Warning, Area = Area.Thermal, Title = "Thermal throttling reported by the driver", Evidence = ev, Recommendation = "Improve cooling or reduce the power limit." });
            else if (Frac(ThrottleFlags.Power) > 0.5)
                c.Add(new Finding
                {
                    Severity = Severity.Info, Area = Area.Power, Title = "Power-limited under heavy load (normal)", Evidence = ev,
                    Recommendation = "Heavy-load clocks are set by the power limit, not by your frequency offset. An undervolt raises clocks within the same power budget.",
                });
            return;
        }

        var power = Stats.Of(samples, Metric.Power);
        var clock = Stats.Of(samples, Metric.CoreClock);
        if (power == null || clock == null) return;

        var evidence = new List<string> { $"Heavy load: {power.Mean:0} W (±{power.Cov * 100:0.0}%), core clock {clock.Mean:0} MHz (±{clock.Cov * 100:0.0}%)" };
        if (c.Profile.TbpW > 0)
        {
            double ratio = power.Mean / c.Profile.TbpW;
            evidence.Add($"Reference board power for {c.Profile.Name}: {c.Profile.TbpW:0} W → running at {ratio * 100:0}%");
        }
        if (power.Cov < 0.03 && clock.Cov > 0.005)
        {
            c.Add(new Finding
            {
                Severity = Severity.Info, Area = Area.Power,
                Title = "Power-limited under heavy load (normal for RDNA3)",
                Evidence = evidence,
                Recommendation = "Power stays pinned while the clock floats: the firmware is holding the power limit. In heavy games your Max Frequency setting is not what limits clocks — an undervolt (or a higher power limit, at the cost of heat) is what raises them. " +
                                 "Light games will still hit Max Frequency, which is why undervolts often fail there first.",
            });
        }
    }

    // ------------------------------------------------------------------ performance per clock

    private void AnalyzePerfPerClock(Ctx c)
    {
        var heavy = c.R.Phases.Where(p => p.Kind == PhaseKind.HeavyLoad && p.Throughput != null).MaxBy(p => p.Seconds);
        if (heavy?.Throughput == null) return;
        double gf = heavy.Throughput.Mean;
        double? clk = heavy.Mean(Metric.CoreClock);
        var ev = new List<string> { $"Heavy-load compute throughput {gf:0} GFLOP/s" + (clk is double ck ? $" at {ck:0} MHz average ({gf / ck:0.00} GFLOP/s per MHz)" : "") };

        var b = c.Baseline;
        if (b?.HeavyGflops is not double bg || bg <= 0)
        {
            return;
        }
        ev.Add($"Baseline ({b.Created:yyyy-MM-dd}{(b.Settings != null ? ", " + b.Settings : "")}): {bg:0} GFLOP/s" +
               (b.HeavyAvgClock is double bc ? $" at {bc:0} MHz" : ""));
        double perf = gf / bg;

        if (clk is double cur && b.HeavyGflopsPerMHz is double bppm && bppm > 0)
        {
            double ppm = gf / cur / bppm;
            ev.Add($"Work done per reported MHz vs baseline: {Pct(ppm)}");
            if (ppm < 1 - _t.PerfPerClockDrop)
            {
                c.Add(new Finding
                {
                    Severity = Severity.Warning, Area = Area.CoreUndervolt, Confidence = Confidence.Medium,
                    Title = "Clock stretching: less work per MHz than at stock",
                    Evidence = ev,
                    Recommendation = "The card reports a clock it is not effectively running — the voltage is too low for the requested frequency and the chip protects itself by stretching clocks. The undervolt costs performance here: reduce it 10 mV at a time until work per MHz is back to baseline.",
                });
                return;
            }
        }

        c.Add(new Finding
        {
            Severity = perf < 0.99 ? Severity.Warning : perf > 1.01 ? Severity.Pass : Severity.Info,
            Area = Area.Core,
            Title = perf < 0.99 ? $"Tuned settings are {(1 - perf) * 100:0.0}% slower than baseline in heavy compute"
                  : perf > 1.01 ? $"Tuned settings are {(perf - 1) * 100:0.0}% faster than baseline in heavy compute"
                  : "Heavy-load performance equals baseline",
            Evidence = ev,
            Recommendation = perf < 0.99 ? "Check for power/thermal throttling and clock stretching; the current tuning is not paying off." : "",
        });
    }

    // ------------------------------------------------------------------ other events

    private static void AnalyzeOtherEvents(Ctx c)
    {
        var s = c.S;
        var to = s.End == default ? DateTimeOffset.MaxValue : s.End.AddSeconds(90);
        var whea = s.Events.Where(e => e.Category == EventCategory.Whea && e.Time >= s.Start.AddSeconds(-5) && e.Time <= to).ToList();
        if (whea.Count > 0)
        {
            c.Add(new Finding
            {
                Severity = Severity.Warning, Area = Area.System, Confidence = Confidence.Medium,
                Title = $"{whea.Count} hardware error(s) (WHEA) logged during the session",
                Evidence = whea.Take(4).Select(e => $"{e.Time:HH:mm:ss} {e.Short}: {Trim(e.Message, 180)}").ToList(),
                Recommendation = "WHEA errors come from PCIe, CPU or RAM, not directly from GPU clocks. During GPU testing they usually mean PCIe link errors (riser cable, PCIe 4.0 signal integrity) — try forcing PCIe Gen 3 in BIOS if you use a riser.",
            });
        }
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    private static void AnalyzeSensorCoverage(Ctx c)
    {
        var s = c.S.Samples;
        if (s.Count == 0)
        {
            c.Add(new Finding
            {
                Severity = Severity.Warning, Area = Area.Telemetry, Title = "No telemetry was recorded",
                Recommendation = "Without sensor data the failure cause cannot be attributed. Make sure the GPU driver is installed and the right GPU is selected ('list').",
            });
            return;
        }
        var missing = new[] { Metric.CoreClock, Metric.CoreVoltage, Metric.HotspotTemp, Metric.Power, Metric.CoreLoad }
            .Where(m => !s.Any(x => x.Has(m))).Select(m => Metrics.Info(m).Label).ToList();
        if (missing.Count > 0)
            c.Add(new Finding
            {
                Severity = Severity.Info, Area = Area.Telemetry,
                Title = "Some sensors are not exposed by this GPU/driver",
                Evidence = { "Missing: " + string.Join(", ", missing) },
                Recommendation = c.S.Gpu?.Vendor == GpuVendor.Nvidia
                    ? "NVIDIA's NVML does not expose core voltage or hotspot; undervolt-vs-frequency attribution relies on the load context only."
                    : "Attribution relies on the remaining sensors and the load context.",
            });
    }

    // ------------------------------------------------------------------ verdict

    private void DecideVerdict(Ctx c)
    {
        var r = c.R;
        var stabilityAreas = new[] { Area.CoreUndervolt, Area.CoreFrequency, Area.Core, Area.Memory, Area.Driver };
        var critical = r.Findings.Where(f => f.Severity == Severity.Critical && (stabilityAreas.Contains(f.Area) || f.Title.StartsWith("Core"))).ToList();

        double loaded = c.S.Phases.Where(p => p.Kind is PhaseKind.LightLoad or PhaseKind.Transient or PhaseKind.HeavyLoad or PhaseKind.VramPattern or PhaseKind.Monitor)
            .Sum(p => ((p.End == default ? c.S.End : p.End) - p.Start).TotalSeconds);

        if (critical.Count > 0)
        {
            r.Verdict = Verdict.Unstable;
            r.Headline = "UNSTABLE — " + critical[0].Title;
            if (c.IsAmd && c.Incidents.Any(i => i.Fatal))
                r.Findings.Add(new Finding
                {
                    Severity = Severity.Info, Area = Area.Driver,
                    Title = "AMD Software may have reset your tuning",
                    Recommendation = "After a driver timeout, AMD Software (Adrenalin) usually restores default tuning. Re-apply your profile (with the suggested change) before the next test.",
                });
        }
        else if (c.S.Mode == "stress" && loaded < _t.MinLoadedSecondsForVerdict)
        {
            r.Verdict = Verdict.Inconclusive;
            r.Headline = $"INCONCLUSIVE — only {loaded:0} s of load testing; run a longer profile";
        }
        else if (r.Findings.Any(f => f.Severity >= Severity.Warning))
        {
            r.Verdict = Verdict.StableWithWarnings;
            r.Headline = "No errors or crashes, but with warnings — " + r.Findings.Where(f => f.Severity >= Severity.Warning).OrderByDescending(f => f.Severity).First().Title;
        }
        else
        {
            r.Verdict = Verdict.Stable;
            r.Headline = c.S.Mode == "stress"
                ? $"STABLE in all tested scenarios ({loaded / 60:0.0} min of load)"
                : "No crashes or instability found in this log";
        }

        if (r.Verdict is Verdict.Stable or Verdict.StableWithWarnings && c.S.Mode == "stress")
            r.Findings.Add(new Finding
            {
                Severity = Severity.Info, Area = Area.Core,
                Title = "Passing a synthetic test is necessary, not sufficient",
                Recommendation = "Also play your most demanding and your lightest (high-FPS / menu-heavy) games with 'monitor' running — it catches crashes in real workloads and tells you what the card was doing at that moment.",
            });
    }
}
