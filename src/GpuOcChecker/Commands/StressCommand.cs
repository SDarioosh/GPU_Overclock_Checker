using GpuOcChecker.Cli;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Stress;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GpuOcChecker.Commands;

public static class StressCommand
{
    public static int Run(Options o)
    {
        if (!StressProfiles.Names.Contains(o.Profile))
            throw new ArgumentException($"Unknown profile '{o.Profile}' (quick, standard, thorough).");

        HashSet<PhaseKind>? only = null;
        if (o.Phases != null)
        {
            only = new HashSet<PhaseKind>();
            foreach (var p in o.Phases.Split(',', StringSplitOptions.RemoveEmptyEntries))
                only.Add(StressProfiles.ParseKind(p) ?? throw new ArgumentException($"Unknown phase '{p}'."));
        }
        double scale = o.Scale;
        if (o.Minutes is double minutes)
        {
            double baseSeconds = StressProfiles.Get(o.Profile, 1, only).Sum(p => p.Duration.TotalSeconds);
            scale = minutes * 60 / baseSeconds;
        }
        var plan = StressProfiles.Get(o.Profile, scale, only);
        if (plan.Count == 0) throw new ArgumentException("No phases selected.");

        using var hw = Hardware.Open(o);
        var tuning = TuningSettings.Parse(o.Settings);

        var total = TimeSpan.FromSeconds(plan.Sum(p => p.Duration.TotalSeconds));
        AnsiConsole.MarkupLine($"Profile [bold]{o.Profile}[/]: {plan.Count} phases, about [bold]{total.TotalMinutes:0.0} min[/].");
        AnsiConsole.MarkupLine($"Tuning under test: [bold]{Markup.Escape(tuning.ToString())}[/]" +
                               (tuning.IsEmpty ? " [grey](pass --settings for concrete recommendations)[/]" : ""));
        if (o.SaveBaseline)
            AnsiConsole.MarkupLine("[yellow]Baseline mode:[/] make sure your GPU is at [bold]stock/default settings[/] for this run.");
        if (!o.Yes && hw.Simulation == null)
        {
            AnsiConsole.MarkupLine("[grey]An unstable overclock can crash the driver or reboot the PC. Save your work first. " +
                                   "If Windows crashes, just start this tool again — it will diagnose the crash from its journal.[/]");
            if (!AnsiConsole.Confirm("Start the test?")) return 1;
        }

        var setup = new StressSetup
        {
            NeedCompute = plan.Any(p => p.Kind is PhaseKind.LightLoad or PhaseKind.Transient or PhaseKind.HeavyLoad),
            NeedVram = plan.Any(p => PhaseKinds.IsMemory(p.Kind)),
            VramFraction = o.VramPercent / 100.0,
        };
        using var backend = hw.CreateBackend(o, setup);

        var data = new SessionData
        {
            Mode = "stress",
            Profile = o.Profile + (o.Phases != null ? $" ({o.Phases})" : ""),
            Gpu = hw.Telemetry?.Gpu,
            StressDevice = backend.DeviceName,
            ToolVersion = Program.Version,
            Tuning = tuning,
            Start = DateTimeOffset.Now,
        };
        using var live = new LiveSession(hw.Telemetry, data);
        live.EventReceived = e => live.Log($"[red]Windows event: {Markup.Escape(e.Short)} ({e.Category})[/]");

        var engine = new StressEngine(backend, plan, setup);
        engine.Log += m => live.Log(Markup.Escape(m));
        engine.PhaseStarted += p =>
        {
            live.Phase = p.Name;
            live.Recorder.PhaseStarted(p);
            live.Log($"Phase: [bold]{Markup.Escape(p.Name)}[/]");
        };
        engine.PhaseEnded += p => live.Recorder.PhaseEnded(p);
        engine.ErrorDetected += (p, e) =>
        {
            live.Recorder.Error(p, e);
            live.Log($"[red]{e.Kind.ToUpperInvariant()} ERROR[/] in {Markup.Escape(p.Name)}: {Markup.Escape(e.Detail ?? e.Count.ToString())}");
        };

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;
        int interval = o.IntervalMs > 0 ? o.IntervalMs : 250;
        bool hung = false;

        try
        {
            live.Phase = "setup";
            engine.Start(cts.Token);
            DateTimeOffset? hangNoted = null;

            AnsiConsole.Live(new Markup("Starting…")).AutoClear(false).Overflow(VerticalOverflow.Ellipsis).Start(ctx =>
            {
                while (!engine.Finished)
                {
                    live.Sample();
                    if (Ui.QuitPressed())
                    {
                        live.Log("[yellow]Stopping (user request)…[/]");
                        cts.Cancel();
                    }

                    // GPU hang watchdog: work stopped completing but no error came back.
                    var cur = engine.Current;
                    var stale = DateTimeOffset.Now - engine.Heartbeat;
                    if (!engine.Initializing && cur != null && cur.Kind != PhaseKind.Idle && stale > TimeSpan.FromSeconds(10))
                    {
                        if (hangNoted == null)
                        {
                            hangNoted = DateTimeOffset.Now;
                            live.Log("[red]GPU has not completed any work for 10 s — hang / driver reset in progress?[/]");
                            live.Recorder.Note($"GPU hang suspected during {cur.Name}");
                        }
                        else if (DateTimeOffset.Now - hangNoted > TimeSpan.FromSeconds(60))
                        {
                            cur.Errors.Add(new ErrorEvent { Time = hangNoted.Value, Kind = "device-lost", Count = 1, Detail = "GPU stopped completing work for over 60 s (hang)" });
                            cur.ErrorEvents++;
                            cur.ErrorCount++;
                            cur.DeviceLost = true;
                            cur.End = DateTimeOffset.Now;
                            hung = true;
                            break;
                        }
                    }
                    else hangNoted = null;

                    ctx.UpdateTarget(Dashboard(live, engine, data));
                    Thread.Sleep(interval);
                }
                ctx.UpdateTarget(Dashboard(live, engine, data));
            });
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        if (!hung) engine.Join(TimeSpan.FromSeconds(10));
        if (engine.FatalError != null && engine.Results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Could not start the GPU workload:[/] {Markup.Escape(engine.FatalError.Message)}");
            live.Recorder.Note("Setup failed: " + engine.FatalError.Message);
        }

        AnsiConsole.MarkupLine("[grey]Collecting Windows event log entries…[/]");
        Thread.Sleep(hw.Simulation != null ? 0 : 3000); // drivers log TDRs a moment after the fact
        live.CollectEvents();
        data.Phases = engine.Results.ToList();
        data.End = DateTimeOffset.Now;
        if (cts.IsCancellationRequested) data.Notes.Add("Stopped early by the user.");
        live.Recorder.Complete(data);

        var report = Reporting.AnalyzeAndReport(data, live.Recorder.Directory, o);

        if (o.SaveBaseline)
        {
            if (report.Verdict == Verdict.Unstable || cts.IsCancellationRequested)
                AnsiConsole.MarkupLine("[red]Baseline NOT saved:[/] the run was unstable or incomplete. A baseline must be a clean run at stock settings.");
            else
            {
                var b = Baseline.FromReport(data, report);
                b.Save();
                AnsiConsole.MarkupLine($"[green]Baseline saved[/] for {Markup.Escape(b.GpuName)}: future runs are compared against it.");
            }
        }

        if (hung)
        {
            // The worker thread is stuck inside the driver; it cannot be joined.
            AnsiConsole.MarkupLine("[red]The GPU is hung. Exiting; a reboot may be required.[/]");
            Environment.Exit(3);
        }
        return report.Verdict == Verdict.Unstable ? 2 : 0;
    }

    private static IRenderable Dashboard(LiveSession live, StressEngine engine, SessionData data)
    {
        var elapsed = DateTimeOffset.Now - data.Start;
        var cur = engine.Current;
        long errors = engine.TotalErrors;
        string phaseLine = engine.Initializing
            ? "[yellow]Compiling kernels, allocating VRAM, calibrating…[/]"
            : cur == null
                ? "[green]Finishing…[/]"
                : $"Phase {engine.PhaseIndex + 1}/{engine.PhaseCount}: [bold]{Markup.Escape(cur.Name)}[/]  {Ui.Bar(engine.PhaseProgress)} {engine.PhaseProgress * 100:0}%";
        string tp = cur?.ThroughputUnit != null && engine.LastThroughput > 0 ? $"   Throughput: {engine.LastThroughput:0} {cur.ThroughputUnit}" : "";
        var header = new Markup(
            $"[bold]{Markup.Escape(data.Gpu?.Name ?? "GPU")}[/] [grey]· stress device: {Markup.Escape(data.StressDevice ?? "?")}[/]\n" +
            $"{phaseLine}\n" +
            $"Elapsed {elapsed:hh\\:mm\\:ss}   Errors: {(errors > 0 ? $"[red bold]{errors}[/]" : "[green]0[/]")}   " +
            $"Driver events: {(live.EventCount > 0 ? $"[red]{live.EventCount}[/]" : "0")}{tp}   [grey](Q to stop)[/]");
        return new Rows(new Panel(header).Border(BoxBorder.Rounded).Expand(), Ui.SensorTable(live), Ui.LogPanel(live));
    }
}
