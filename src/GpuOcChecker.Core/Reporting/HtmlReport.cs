using System.Globalization;
using System.Net;
using System.Text;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Reporting;

/// <summary>Self-contained HTML report (inline SVG charts, no external resources).</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Series(string Label, Metric Metric, string Color, double Scale = 1);

    public static string Write(SessionData s, AnalysisReport r, string path)
    {
        File.WriteAllText(path, Render(s, r), new UTF8Encoding(false));
        return path;
    }

    public static string Render(SessionData s, AnalysisReport r)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>GPU OC Report</title><style>{Css}</style></head><body><main>");

        string verdictClass = r.Verdict switch
        {
            Verdict.Stable => "pass",
            Verdict.StableWithWarnings => "warn",
            Verdict.Unstable => "crit",
            _ => "info",
        };
        sb.Append($"<header><div class=\"sub\">{E(r.GpuName)} · {E(s.Mode)}{(s.Profile != null ? " · " + E(s.Profile) : "")} · {s.Start:yyyy-MM-dd HH:mm} · reference: {E(r.ProfileName)}</div>");
        sb.Append($"<h1 class=\"verdict {verdictClass}\">{E(r.Headline)}</h1>");
        if (s.Tuning is { IsEmpty: false } t) sb.Append($"<div class=\"sub\">Tuning under test: {E(t.ToString())}</div>");
        if (!s.CompletedCleanly) sb.Append("<div class=\"sub crit-text\">This session did not finish normally — it was recovered from the crash journal.</div>");
        sb.Append("</header>");

        // key stats
        sb.Append("<section class=\"tiles\">");
        foreach (var (k, v) in r.KeyStats)
            sb.Append($"<div class=\"tile\"><div class=\"k\">{E(k)}</div><div class=\"v\">{v.ToString(k.Contains("(V)") ? "0.000" : "0.#", Inv)}</div></div>");
        sb.Append("</section>");

        // findings
        sb.Append("<section><h2>Findings</h2>");
        foreach (var f in r.Findings)
        {
            string cls = f.Severity switch { Severity.Critical => "crit", Severity.Warning => "warn", Severity.Pass => "pass", _ => "info" };
            sb.Append($"<article class=\"finding {cls}\"><div class=\"fh\"><span class=\"badge {cls}\">{f.Severity}</span>");
            sb.Append($"<span class=\"area\">{AreaLabel(f.Area)}</span>");
            if (f.Severity >= Severity.Warning) sb.Append($"<span class=\"conf\">confidence: {f.Confidence.ToString().ToLowerInvariant()}</span>");
            sb.Append($"</div><h3>{E(f.Title)}</h3>");
            if (f.Evidence.Count > 0)
            {
                sb.Append("<ul class=\"ev\">");
                foreach (var e in f.Evidence) sb.Append($"<li>{E(e.Trim())}</li>");
                sb.Append("</ul>");
            }
            if (!string.IsNullOrWhiteSpace(f.Recommendation)) sb.Append($"<p class=\"rec\"><b>What to do:</b> {E(f.Recommendation)}</p>");
            sb.Append("</article>");
        }
        sb.Append("</section>");

        // charts
        if (s.Samples.Count > 1)
        {
            sb.Append("<section><h2>Telemetry</h2><p class=\"sub\">Shaded bands are test phases; red lines mark failures.</p>");
            var marks = r.FailureMarks;
            sb.Append(Chart("Core & memory clock (MHz)", s, r, marks, new Series("Core", Metric.CoreClock, "var(--c1)"), new Series("Memory", Metric.MemClock, "var(--c2)")));
            sb.Append(Chart("Core voltage (V)", s, r, marks, new Series("Core V", Metric.CoreVoltage, "var(--c3)")));
            sb.Append(Chart("Temperatures (°C)", s, r, marks, new Series("Edge", Metric.EdgeTemp, "var(--c1)"), new Series("Hotspot", Metric.HotspotTemp, "var(--c4)"), new Series("Memory", Metric.MemTemp, "var(--c2)")));
            sb.Append(Chart("Board power (W) & load (%)", s, r, marks, new Series("Power", Metric.Power, "var(--c4)"), new Series("Load", Metric.CoreLoad, "var(--c5)")));
            if (r.VfCurve.Count > 1) sb.Append(VfChart(r.VfCurve));
            sb.Append("</section>");
        }

        // phases
        if (r.Phases.Count > 0)
        {
            sb.Append("<section><h2>Phases</h2><div class=\"scroll\"><table><thead><tr><th>Phase</th><th>Duration</th><th>Result</th><th>Throughput</th><th>Core clock avg / max</th><th>Voltage min / max</th><th>Hotspot max</th><th>Power avg</th></tr></thead><tbody>");
            foreach (var p in r.Phases)
            {
                string res = p.DeviceLost ? "<span class=\"crit-text\">device lost</span>"
                    : p.ErrorCount > 0 ? $"<span class=\"crit-text\">{p.ErrorEvents} error events ({p.ErrorCount})</span>"
                    : p.Completed ? "<span class=\"pass-text\">pass</span>" : "incomplete";
                string tp = p.Throughput != null ? $"{p.Throughput.Mean:0} {p.ThroughputUnit} ±{p.Throughput.Cov * 100:0.0}%" : "";
                sb.Append($"<tr><td>{E(p.Name)}</td><td>{p.Seconds:0} s</td><td>{res}</td><td>{tp}</td>");
                sb.Append($"<td>{Fmt(p.Mean(Metric.CoreClock), "0")} / {Fmt(p.Max(Metric.CoreClock), "0")}</td>");
                sb.Append($"<td>{Fmt(p.Min(Metric.CoreVoltage), "0.000")} / {Fmt(p.Max(Metric.CoreVoltage), "0.000")}</td>");
                sb.Append($"<td>{Fmt(p.Max(Metric.HotspotTemp) ?? p.Max(Metric.EdgeTemp), "0")}</td><td>{Fmt(p.Mean(Metric.Power), "0")}</td></tr>");
            }
            sb.Append("</tbody></table></div></section>");
        }

        // events
        if (s.Events.Count > 0)
        {
            sb.Append("<section><h2>Windows events</h2><div class=\"scroll\"><table><thead><tr><th>Time</th><th>Source</th><th>Category</th><th>Message</th></tr></thead><tbody>");
            foreach (var e in s.Events)
                sb.Append($"<tr><td>{e.Time:HH:mm:ss}</td><td>{E(e.Short)}</td><td>{e.Category}</td><td>{E(e.Message.Length > 240 ? e.Message[..240] + "…" : e.Message)}</td></tr>");
            sb.Append("</tbody></table></div></section>");
        }

        if (s.Notes.Count > 0)
        {
            sb.Append("<section><h2>Notes</h2><ul>");
            foreach (var n in s.Notes) sb.Append($"<li>{E(n)}</li>");
            sb.Append("</ul></section>");
        }

        sb.Append($"<footer>GPU OC Checker {E(s.ToolVersion ?? "")} · telemetry: {E(s.Gpu?.Backend ?? "?")}{(s.StressDevice != null ? " · stress device: " + E(s.StressDevice) : "")}</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string AreaLabel(Area a) => a switch
    {
        Area.CoreUndervolt => "Core voltage / undervolt",
        Area.CoreFrequency => "Core frequency",
        Area.Core => "Core",
        Area.Memory => "Memory (VRAM)",
        Area.Thermal => "Thermals",
        Area.Power => "Power",
        Area.Driver => "Driver",
        Area.System => "System",
        _ => "Telemetry",
    };

    private static string Fmt(double? v, string f) => v is double d ? d.ToString(f, Inv) : "–";
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string N(double v) => v.ToString("0.##", Inv);

    private const double W = 960, H = 220, L = 56, Rt = 12, T = 14, B = 26;

    private static string Chart(string title, SessionData s, AnalysisReport r, List<(DateTimeOffset Time, string Label)> marks, params Series[] series)
    {
        var present = series.Where(x => s.Samples.Any(p => p.Has(x.Metric))).ToArray();
        if (present.Length == 0) return "";

        var t0 = s.Samples[0].Timestamp;
        double tMax = Math.Max(1, (s.Samples[^1].Timestamp - t0).TotalSeconds);
        var vals = present.SelectMany(x => s.Samples.Where(p => p.Has(x.Metric)).Select(p => p[x.Metric]!.Value)).ToList();
        double lo = vals.Min(), hi = vals.Max();
        if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
        double pad = (hi - lo) * 0.08;
        lo = Math.Max(lo - pad, vals.Min() >= 0 ? 0 : double.MinValue);
        hi += pad;

        double X(double sec) => L + sec / tMax * (W - L - Rt);
        double Y(double v) => T + (1 - (v - lo) / (hi - lo)) * (H - T - B);

        var sb = new StringBuilder();
        sb.Append($"<figure><figcaption>{E(title)}<span class=\"legend\">");
        foreach (var x in present) sb.Append($"<span><i style=\"background:{x.Color}\"></i>{E(x.Label)}</span>");
        sb.Append($"</span></figcaption><svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"{E(title)}\">");

        // phase bands
        int band = 0;
        foreach (var p in r.Phases)
        {
            double a = X(Math.Max(0, (p.Start - t0).TotalSeconds)), b = X(Math.Min(tMax, (p.End - t0).TotalSeconds));
            if (b - a < 1) continue;
            if (band++ % 2 == 0) sb.Append($"<rect x=\"{N(a)}\" y=\"{T}\" width=\"{N(b - a)}\" height=\"{H - T - B}\" class=\"band\"/>");
            if (b - a > 60) sb.Append($"<text x=\"{N(a + 4)}\" y=\"{T + 11}\" class=\"plabel\">{E(Short(p.Name))}</text>");
        }

        // grid + axis labels
        for (int i = 0; i <= 4; i++)
        {
            double v = lo + (hi - lo) * i / 4;
            double y = Y(v);
            sb.Append($"<line x1=\"{L}\" x2=\"{W - Rt}\" y1=\"{N(y)}\" y2=\"{N(y)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{L - 6}\" y=\"{N(y + 4)}\" class=\"axis\" text-anchor=\"end\">{v.ToString(hi - lo < 5 ? "0.00" : "0", Inv)}</text>");
        }
        for (int i = 0; i <= 5; i++)
        {
            double sec = tMax * i / 5;
            sb.Append($"<text x=\"{N(X(sec))}\" y=\"{H - 8}\" class=\"axis\" text-anchor=\"middle\">{TimeLabel(sec)}</text>");
        }

        // series (downsampled)
        int stride = Math.Max(1, s.Samples.Count / 1200);
        foreach (var x in present)
        {
            var pts = new StringBuilder();
            for (int i = 0; i < s.Samples.Count; i += stride)
            {
                var p = s.Samples[i];
                if (p[x.Metric] is not double v) continue;
                pts.Append(N(X((p.Timestamp - t0).TotalSeconds))).Append(',').Append(N(Y(v))).Append(' ');
            }
            sb.Append($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{x.Color}\" stroke-width=\"1.5\" stroke-linejoin=\"round\"/>");
        }

        foreach (var (time, label) in marks)
        {
            double mx = X(Math.Clamp((time - t0).TotalSeconds, 0, tMax));
            sb.Append($"<line x1=\"{N(mx)}\" x2=\"{N(mx)}\" y1=\"{T}\" y2=\"{H - B}\" class=\"fail\"><title>{E(time.ToString("HH:mm:ss") + " " + label)}</title></line>");
        }
        sb.Append("</svg></figure>");
        return sb.ToString();
    }

    private static string VfChart(List<VfPoint> vf)
    {
        double cLo = vf.Min(p => p.ClockMHz) - 50, cHi = vf.Max(p => p.ClockMHz) + 50;
        double vLo = vf.Min(p => p.MinVoltage) - 0.02, vHi = vf.Max(p => p.AvgVoltage) + 0.02;
        double X(double c) => L + (c - cLo) / (cHi - cLo) * (W - L - Rt);
        double Y(double v) => T + (1 - (v - vLo) / (vHi - vLo)) * (H - T - B);
        var sb = new StringBuilder();
        sb.Append("<figure><figcaption>Observed V/F curve (voltage per 50 MHz clock bin)<span class=\"legend\"><span><i style=\"background:var(--c3)\"></i>average</span><span><i style=\"background:var(--c4)\"></i>minimum</span></span></figcaption>");
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"V/F curve\">");
        for (int i = 0; i <= 4; i++)
        {
            double v = vLo + (vHi - vLo) * i / 4;
            sb.Append($"<line x1=\"{L}\" x2=\"{W - Rt}\" y1=\"{N(Y(v))}\" y2=\"{N(Y(v))}\" class=\"grid\"/><text x=\"{L - 6}\" y=\"{N(Y(v) + 4)}\" class=\"axis\" text-anchor=\"end\">{v:0.000}</text>");
            double c = cLo + (cHi - cLo) * i / 4;
            sb.Append($"<text x=\"{N(X(c))}\" y=\"{H - 8}\" class=\"axis\" text-anchor=\"middle\">{c:0} MHz</text>");
        }
        sb.Append($"<polyline points=\"{string.Join(' ', vf.Select(p => $"{N(X(p.ClockMHz))},{N(Y(p.AvgVoltage))}"))}\" fill=\"none\" stroke=\"var(--c3)\" stroke-width=\"2\"/>");
        foreach (var p in vf)
        {
            sb.Append($"<circle cx=\"{N(X(p.ClockMHz))}\" cy=\"{N(Y(p.AvgVoltage))}\" r=\"3\" fill=\"var(--c3)\"><title>{p.ClockMHz:0} MHz: avg {p.AvgVoltage:0.000} V ({p.Samples} samples)</title></circle>");
            sb.Append($"<circle cx=\"{N(X(p.ClockMHz))}\" cy=\"{N(Y(p.MinVoltage))}\" r=\"2.5\" fill=\"var(--c4)\"><title>{p.ClockMHz:0} MHz: min {p.MinVoltage:0.000} V</title></circle>");
        }
        sb.Append("</svg></figure>");
        return sb.ToString();
    }

    private static string Short(string name) => name.Length > 22 ? name[..21] + "…" : name;

    private static string TimeLabel(double sec) =>
        sec >= 3600 ? $"{(int)(sec / 3600)}h{(int)(sec % 3600 / 60):00}" : $"{(int)(sec / 60)}:{(int)(sec % 60):00}";

    private const string Css = """
:root{--bg:#f7f7f5;--card:#fff;--fg:#1d1d1f;--muted:#6b6b70;--line:#e3e3e0;--band:rgba(0,0,0,.035);
--crit:#c62828;--warn:#b26a00;--pass:#2e7d32;--info:#3a5a8c;
--c1:#2f6fdb;--c2:#15a38a;--c3:#8a4fd8;--c4:#e0512b;--c5:#9aa0a6}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#141416;--card:#1d1d20;--fg:#ececef;--muted:#9a9aa2;--line:#303036;--band:rgba(255,255,255,.04);
--crit:#ff6b6b;--warn:#f0a53a;--pass:#5cc26b;--info:#7fa6e6;--c1:#6c9cff;--c2:#3cc9ad;--c3:#b48cff;--c4:#ff7a52;--c5:#8d939b}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:1040px;margin:0 auto;padding:24px 16px 48px}
h1{font-size:1.45rem;margin:.3em 0}h2{font-size:1.1rem;margin:1.8em 0 .6em}h3{font-size:1rem;margin:.3em 0}
.sub{color:var(--muted);font-size:.88rem}.verdict.crit{color:var(--crit)}.verdict.warn{color:var(--warn)}.verdict.pass{color:var(--pass)}.verdict.info{color:var(--info)}
.crit-text{color:var(--crit)}.pass-text{color:var(--pass)}
.tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:8px;margin-top:16px}
.tile{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 12px}.tile .k{color:var(--muted);font-size:.78rem}.tile .v{font-size:1.2rem;font-variant-numeric:tabular-nums}
.finding{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--info);border-radius:10px;padding:12px 16px;margin:10px 0}
.finding.crit{border-left-color:var(--crit)}.finding.warn{border-left-color:var(--warn)}.finding.pass{border-left-color:var(--pass)}
.fh{display:flex;gap:10px;align-items:center;flex-wrap:wrap;font-size:.8rem;color:var(--muted)}
.badge{font-weight:600;text-transform:uppercase;letter-spacing:.04em}.badge.crit{color:var(--crit)}.badge.warn{color:var(--warn)}.badge.pass{color:var(--pass)}.badge.info{color:var(--info)}
.ev{margin:.4em 0;padding-left:1.2em;color:var(--muted);font-size:.9rem;white-space:pre-wrap}.rec{margin:.5em 0 0}
figure{background:var(--card);border:1px solid var(--line);border-radius:10px;margin:10px 0;padding:10px 12px}
figcaption{font-size:.9rem;font-weight:600;display:flex;justify-content:space-between;flex-wrap:wrap;gap:8px}
.legend{font-weight:400;color:var(--muted);display:flex;gap:12px}.legend i{display:inline-block;width:10px;height:3px;margin-right:5px;vertical-align:middle}
svg{width:100%;height:auto;display:block}.grid{stroke:var(--line);stroke-width:1}.axis{fill:var(--muted);font-size:11px}
.band{fill:var(--band)}.plabel{fill:var(--muted);font-size:10px}.fail{stroke:var(--crit);stroke-width:1.5;stroke-dasharray:4 3}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;background:var(--card);font-size:.85rem}
th,td{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}th{color:var(--muted);font-weight:600}
td{font-variant-numeric:tabular-nums}footer{margin-top:32px;color:var(--muted);font-size:.8rem}
""";
}
