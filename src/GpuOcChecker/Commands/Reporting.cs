using System.Text.Json;
using GpuOcChecker.Cli;
using GpuOcChecker.Core;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Reporting;
using GpuOcChecker.Core.Session;
using Spectre.Console;

namespace GpuOcChecker.Commands;

public static class Reporting
{
    /// <summary>Analyzes a session, writes report.html + analysis.json into <paramref name="dir"/> and prints the summary.</summary>
    public static AnalysisReport AnalyzeAndReport(SessionData s, string dir, Options o, bool print = true)
    {
        var baseline = s.Gpu != null ? Baseline.Load(s.Gpu.Key) : null;
        var report = new Analyzer(Thresholds.Load()).Analyze(s, baseline);
        Directory.CreateDirectory(dir);
        var html = HtmlReport.Write(s, report, Path.Combine(dir, "report.html"));
        File.WriteAllText(Path.Combine(dir, "analysis.json"), JsonSerializer.Serialize(new
        {
            report.Verdict,
            report.Headline,
            report.GpuName,
            report.ProfileName,
            report.KeyStats,
            report.Findings,
            report.Phases,
        }, JsonOpts.Default));
        if (print) Ui.PrintReport(report, html);
        if (o.Open) Ui.OpenFile(html);
        return report;
    }

    /// <summary>Diagnoses sessions that were still running when the app or PC died.</summary>
    public static void CheckPreviousCrashes(Options o)
    {
        List<string> unfinished;
        try
        {
            unfinished = SessionStore.FindUnfinished();
        }
        catch (Exception)
        {
            return;
        }
        foreach (var dir in unfinished)
        {
            try
            {
                var s = SessionStore.Load(dir);
                if (s.Samples.Count == 0)
                {
                    SessionStore.MarkReviewed(dir);
                    continue;
                }
                foreach (var e in Core.Events.GpuEventLog.Query(s.Start.AddSeconds(-5), s.End.AddHours(1)))
                    if (!s.Events.Any(x => x.Time == e.Time && x.Source == e.Source && x.EventId == e.EventId))
                        s.Events.Add(e);

                AnsiConsole.Write(new Rule("[bold red]The previous session did not finish[/]").LeftJustified());
                AnsiConsole.MarkupLine($"Session [bold]{Markup.Escape(Path.GetFileName(dir))}[/] stopped at {s.End:yyyy-MM-dd HH:mm:ss} " +
                                       $"(last phase: [bold]{Markup.Escape(s.Samples[^1].Phase ?? "?")}[/]). Diagnosing from the crash journal…");
                AnalyzeAndReport(s, dir, new Options());
                SessionStore.MarkReviewed(dir);
                AnsiConsole.WriteLine();
            }
            catch (Exception e)
            {
                AnsiConsole.MarkupLine($"[grey]Could not analyze unfinished session {Markup.Escape(dir)}: {Markup.Escape(e.Message)}[/]");
                SessionStore.MarkReviewed(dir);
            }
        }
    }
}
