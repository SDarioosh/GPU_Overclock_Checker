using GpuOcChecker.Cli;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Telemetry;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GpuOcChecker.Commands;

/// <summary>Background recording while gaming: when the driver crashes, the report says what the card was doing.</summary>
public static class MonitorCommand
{
    public static int Run(Options o)
    {
        using var hw = Hardware.Open(o);
        if (hw.Telemetry == null)
        {
            AnsiConsole.MarkupLine("[red]Monitoring needs GPU telemetry.[/]");
            return 1;
        }
        var data = new SessionData
        {
            Mode = "monitor",
            Gpu = hw.Telemetry.Gpu,
            ToolVersion = Program.Version,
            Tuning = TuningSettings.Parse(o.Settings),
            Start = DateTimeOffset.Now,
        };
        var phase = new PhaseResult { Name = "Monitoring", Kind = PhaseKind.Monitor, Start = data.Start };
        data.Phases.Add(phase);

        using var live = new LiveSession(hw.Telemetry, data) { Phase = "monitor" };
        live.Recorder.PhaseStarted(phase);
        live.EventReceived = e =>
        {
            if (e.Category is not (EventCategory.DriverTimeout or EventCategory.DriverError)) return;
            var window = data.Samples.Where(s => s.Timestamp >= e.Time.AddSeconds(-8) && s.Timestamp <= e.Time).ToList();
            double peak = data.Samples.Count > 0 ? data.Samples.Max(s => s[Metric.CoreClock] ?? 0) : 0;
            var ctx = Analyzer.Classify(null, window, peak);
            var at = window.LastOrDefault();
            string state = at == null ? "" :
                $" — card was at {Ui.Fmt(at[Metric.CoreClock], Metric.CoreClock)} MHz, {Ui.Fmt(at[Metric.CoreVoltage], Metric.CoreVoltage)} V, load {Ui.Fmt(at[Metric.CoreLoad], Metric.CoreLoad)}%";
            live.Log($"[red bold]DRIVER CRASH[/] {Markup.Escape(e.Short)} at {e.Time:HH:mm:ss}{state} → context: [bold]{ctx}[/]");
        };

        AnsiConsole.MarkupLine("Recording. Play your games now; this window can stay in the background. [grey]Q or Ctrl+C to stop and analyze.[/]");
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;
        int interval = o.IntervalMs > 0 ? o.IntervalMs : 500;
        var until = o.DurationMinutes is double d ? DateTimeOffset.Now.AddMinutes(d) : DateTimeOffset.MaxValue;

        try
        {
            AnsiConsole.Live(new Markup("…")).AutoClear(false).Overflow(VerticalOverflow.Ellipsis).Start(ctx =>
            {
                while (!cts.IsCancellationRequested && DateTimeOffset.Now < until)
                {
                    live.Sample();
                    if (Ui.QuitPressed()) break;
                    ctx.UpdateTarget(Dashboard(live, data));
                    cts.Token.WaitHandle.WaitOne(interval);
                }
            });
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        live.CollectEvents();
        phase.End = data.End = DateTimeOffset.Now;
        phase.Completed = true;
        live.Recorder.PhaseEnded(phase);
        live.Recorder.Complete(data);
        var report = Reporting.AnalyzeAndReport(data, live.Recorder.Directory, o);
        return report.Verdict == Verdict.Unstable ? 2 : 0;
    }

    private static IRenderable Dashboard(LiveSession live, SessionData data)
    {
        var elapsed = DateTimeOffset.Now - data.Start;
        int crashes = data.Events.Count(e => e.Category is EventCategory.DriverTimeout or EventCategory.DriverError);
        var header = new Markup(
            $"[bold]{Markup.Escape(data.Gpu?.Name ?? "GPU")}[/] [grey]· monitor mode · {data.Samples.Count} samples[/]\n" +
            $"Recording {elapsed:hh\\:mm\\:ss}   Driver crashes: {(crashes > 0 ? $"[red bold]{crashes}[/]" : "[green]0[/]")}   [grey](Q to stop)[/]");
        return new Rows(new Panel(header).Border(BoxBorder.Rounded).Expand(), Ui.SensorTable(live), Ui.LogPanel(live));
    }
}
