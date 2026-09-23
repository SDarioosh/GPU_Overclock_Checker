using System.Diagnostics;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Telemetry;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GpuOcChecker.Cli;

public static class Ui
{
    private static readonly Metric[] DashboardMetrics =
    {
        Metric.CoreClock, Metric.MemClock, Metric.CoreVoltage, Metric.CoreLoad, Metric.Power,
        Metric.EdgeTemp, Metric.HotspotTemp, Metric.MemTemp, Metric.VrTemp, Metric.FanRpm, Metric.FanPercent,
    };

    public static string Fmt(double? v, Metric m) => v is double d ? d.ToString(Metrics.Info(m).Format) : "–";

    public static IRenderable SensorTable(LiveSession live)
    {
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn("Sensor");
        t.AddColumn(new TableColumn("Now").RightAligned());
        t.AddColumn(new TableColumn("Min").RightAligned());
        t.AddColumn(new TableColumn("Max").RightAligned());
        var s = live.Last;
        foreach (var m in DashboardMetrics)
        {
            if (s == null || !s.Has(m)) continue;
            var info = Metrics.Info(m);
            string now = Fmt(s[m], m);
            if (m == Metric.HotspotTemp && s[Metric.EdgeTemp] is double edge && s[m] is double hot && hot - edge >= 25)
                now = $"[yellow]{now}[/] [grey](Δ{hot - edge:0})[/]";
            t.AddRow($"{info.Label} [grey]{info.Unit}[/]", now, Fmt(live.Tracker.Min(m), m), Fmt(live.Tracker.Max(m), m));
        }
        if (s == null) t.AddRow("[grey]no telemetry[/]", "", "", "");
        else if (s.Throttle != ThrottleFlags.None) t.AddRow("Limiters", s.Throttle.ToString(), "", "");
        return t;
    }

    public static IRenderable LogPanel(LiveSession live)
    {
        var lines = live.RecentLog.ToList();
        var text = lines.Count == 0 ? "[grey]—[/]" : string.Join("\n", lines);
        return new Panel(new Markup(text)).Header("Log").Border(BoxBorder.Rounded).Expand();
    }

    public static string Bar(double fraction, int width = 30)
    {
        int filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return "[green]" + new string('█', filled) + "[/][grey]" + new string('░', width - filled) + "[/]";
    }

    public static void PrintReport(AnalysisReport r, string? htmlPath)
    {
        var color = r.Verdict switch
        {
            Verdict.Stable => "green",
            Verdict.StableWithWarnings => "yellow",
            Verdict.Unstable => "red",
            _ => "blue",
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold {color}]{Markup.Escape(r.Headline)}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(r.GpuName)} · reference: {Markup.Escape(r.ProfileName)}[/]");
        AnsiConsole.WriteLine();

        foreach (var f in r.Findings)
        {
            var (c, label) = f.Severity switch
            {
                Severity.Critical => ("red", "CRITICAL"),
                Severity.Warning => ("yellow", "WARNING"),
                Severity.Pass => ("green", "OK"),
                _ => ("blue", "INFO"),
            };
            string conf = f.Severity >= Severity.Warning ? $" [grey](confidence: {f.Confidence.ToString().ToLowerInvariant()})[/]" : "";
            AnsiConsole.MarkupLine($"[bold {c}]{label}[/] [bold]{Markup.Escape(f.Title)}[/]{conf}");
            foreach (var e in f.Evidence) AnsiConsole.MarkupLine($"   [grey]{Markup.Escape(e)}[/]");
            if (!string.IsNullOrWhiteSpace(f.Recommendation))
                AnsiConsole.MarkupLine($"   [{c}]→[/] {Markup.Escape(f.Recommendation)}");
            AnsiConsole.WriteLine();
        }

        if (r.Phases.Count > 0)
        {
            var t = new Table().Border(TableBorder.Simple).Title("Phases");
            t.AddColumns("Phase", "Time", "Result", "Throughput", "Clock avg/max", "Volt min/max", "Hotspot max", "Power avg");
            foreach (var p in r.Phases)
            {
                string res = p.DeviceLost ? "[red]device lost[/]" : p.ErrorCount > 0 ? $"[red]{p.ErrorEvents} error events[/]" : p.Completed ? "[green]pass[/]" : "[grey]incomplete[/]";
                t.AddRow(Markup.Escape(p.Name), $"{p.Seconds:0}s", res,
                    p.Throughput != null ? $"{p.Throughput.Mean:0} {p.ThroughputUnit}" : "",
                    $"{Fmt(p.Mean(Metric.CoreClock), Metric.CoreClock)}/{Fmt(p.Max(Metric.CoreClock), Metric.CoreClock)}",
                    $"{Fmt(p.Min(Metric.CoreVoltage), Metric.CoreVoltage)}/{Fmt(p.Max(Metric.CoreVoltage), Metric.CoreVoltage)}",
                    Fmt(p.Max(Metric.HotspotTemp) ?? p.Max(Metric.EdgeTemp), Metric.HotspotTemp),
                    Fmt(p.Mean(Metric.Power), Metric.Power));
            }
            AnsiConsole.Write(t);
        }
        if (htmlPath != null)
            AnsiConsole.MarkupLine($"Full report with charts: [link]{Markup.Escape(htmlPath)}[/]");
    }

    public static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AnsiConsole.MarkupLine($"[grey]Could not open {Markup.Escape(path)}: {Markup.Escape(e.Message)}[/]");
        }
    }

    /// <summary>Non-blocking check for Q / Esc.</summary>
    public static bool QuitPressed()
    {
        try
        {
            if (Console.IsInputRedirected || !Console.KeyAvailable) return false;
            var k = Console.ReadKey(intercept: true).Key;
            return k is ConsoleKey.Q or ConsoleKey.Escape;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
