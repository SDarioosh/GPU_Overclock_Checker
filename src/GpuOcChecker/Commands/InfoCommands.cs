using GpuOcChecker.Cli;
using GpuOcChecker.Core;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Events;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Stress.OpenCl;
using GpuOcChecker.Core.Telemetry;
using Spectre.Console;

namespace GpuOcChecker.Commands;

public static class InfoCommands
{
    public static int List(Options o)
    {
        using var hw = Hardware.Open(o, quiet: true);
        var t = new Table().Border(TableBorder.Rounded).Title("GPUs with telemetry (--gpu)");
        t.AddColumns("#", "Name", "Backend", "Reference profile");
        var sources = hw.Simulation != null ? new[] { hw.Telemetry! } : hw.All.ToArray();
        for (int i = 0; i < sources.Length; i++)
        {
            var p = CardProfiles.Find(sources[i].Gpu.Name, sources[i].Gpu.Vendor);
            t.AddRow(i.ToString(), Markup.Escape(sources[i].Gpu.Name), sources[i].Gpu.Backend, p.IsGeneric ? "[grey]generic[/]" : p.Name);
        }
        if (sources.Length == 0) t.AddRow("-", "[yellow]none found[/]", "", "");
        AnsiConsole.Write(t);
        if (sources.Length == 0)
            foreach (var n in hw.Notes) AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(n)}[/]");

        var c = new Table().Border(TableBorder.Rounded).Title("OpenCL devices for the stress tests (--cl-device)");
        c.AddColumns("#", "Device", "Platform", "Type", "Memory", "CUs");
        try
        {
            foreach (var d in OpenClBackend.ListDevices())
                c.AddRow(d.Index.ToString(), Markup.Escape(d.DisplayName), Markup.Escape(d.Platform), d.IsGpu ? "GPU" : "other",
                    $"{d.GlobalMem / (1L << 30):0.0} GiB", d.ComputeUnits.ToString());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            c.AddRow("-", "[yellow]OpenCL runtime not found (reinstall the GPU driver)[/]", "", "", "", "");
        }
        AnsiConsole.Write(c);
        AnsiConsole.MarkupLine($"[grey]Data folder: {Markup.Escape(AppPaths.Root)}[/]");
        return 0;
    }

    public static int Sensors(Options o)
    {
        using var hw = Hardware.Open(o);
        if (hw.Telemetry == null) return 1;
        if (o.Raw)
        {
            var t = new Table().Border(TableBorder.Rounded).Title($"Raw sensors — {Markup.Escape(hw.Telemetry.Gpu.Backend)}");
            t.AddColumns("Sensor", "Value", "Unit");
            foreach (var r in hw.Telemetry.ReadRaw())
                t.AddRow(Markup.Escape(r.Name), r.Value.ToString("0.###"), Markup.Escape(r.Unit));
            AnsiConsole.Write(t);
            return 0;
        }
        var s = hw.Telemetry.Read();
        var n = new Table().Border(TableBorder.Rounded);
        n.AddColumns("Sensor", "Value");
        foreach (var m in Metrics.All.Where(m => s.Has(m.Metric)))
            n.AddRow($"{m.Label} [grey]{m.Unit}[/]", Ui.Fmt(s[m.Metric], m.Metric));
        AnsiConsole.Write(n);
        return 0;
    }

    public static int Events(Options o)
    {
        if (!GpuEventLog.IsSupported)
        {
            AnsiConsole.MarkupLine("[yellow]The Windows event log is only available on Windows.[/]");
            return 1;
        }
        var from = DateTimeOffset.Now.AddDays(-o.Days);
        var events = AnsiConsole.Status().Start("Reading event log…", _ => GpuEventLog.Query(from));
        if (events.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]No GPU driver crashes, WHEA errors or unexpected shutdowns in the last {o.Days} days.[/]");
            return 0;
        }

        var t = new Table().Border(TableBorder.Rounded).Title($"GPU-related events, last {o.Days} days");
        t.AddColumns("Time", "Event", "Category", "Message");
        foreach (var e in events.OrderByDescending(e => e.Time).Take(60))
        {
            string color = e.Category switch
            {
                EventCategory.DriverTimeout or EventCategory.DriverError => "red",
                EventCategory.UnexpectedShutdown => "yellow",
                _ => "grey",
            };
            t.AddRow($"{e.Time:yyyy-MM-dd HH:mm:ss}", Markup.Escape(e.Short), $"[{color}]{e.Category}[/]",
                Markup.Escape(e.Message.Length > 110 ? e.Message[..110] + "…" : e.Message));
        }
        AnsiConsole.Write(t);

        foreach (var g in events.GroupBy(e => e.Category))
            AnsiConsole.MarkupLine($"  {g.Key}: [bold]{g.Count()}[/] (latest {g.Max(e => e.Time):yyyy-MM-dd HH:mm})");
        AnsiConsole.MarkupLine("\n[grey]To see what the card was doing at such a moment, record with 'monitor' (or log with HWiNFO and use 'analyze'). " +
                               "Several TDRs clustered on days you changed settings usually point to that setting.[/]");
        return 0;
    }

    public static int Analyze(Options o)
    {
        string? path = o.Positional.FirstOrDefault();
        if (path == null)
        {
            var sessions = SessionStore.ListSessions().Take(15).ToList();
            if (sessions.Count == 0) throw new ArgumentException("Usage: analyze <session folder | telemetry.csv | HWiNFO.csv>");
            path = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Which session?").AddChoices(sessions).UseConverter(Path.GetFileName));
        }
        path = path.Trim('"');

        SessionData s;
        string outDir;
        if (Directory.Exists(path))
        {
            s = SessionStore.Load(path);
            outDir = path;
        }
        else if (File.Exists(path))
        {
            var header = File.ReadLines(path).FirstOrDefault() ?? "";
            if (TelemetryCsv.IsOwnFormat(header) && File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, SessionRecorder.JournalFile)))
            {
                outDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
                s = SessionStore.Load(outDir);
            }
            else if (TelemetryCsv.IsOwnFormat(header))
            {
                s = new SessionData { Mode = "import", Samples = TelemetryCsv.Read(path), CompletedCleanly = true };
                s.Start = s.Samples.FirstOrDefault()?.Timestamp ?? DateTimeOffset.Now;
                s.End = s.Samples.LastOrDefault()?.Timestamp ?? s.Start;
                s.Phases.Add(new PhaseResult { Name = "Log", Kind = PhaseKind.Monitor, Start = s.Start, End = s.End, Completed = true });
                outDir = Path.Combine(AppPaths.Sessions, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_import");
            }
            else
            {
                s = HwInfoImporter.Import(path);
                outDir = Path.Combine(AppPaths.Sessions, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_import");
                AnsiConsole.MarkupLine($"Imported {s.Samples.Count} HWiNFO samples ({s.Start:yyyy-MM-dd HH:mm} – {s.End:HH:mm}).");
            }
            if (o.Settings != null) s.Tuning = TuningSettings.Parse(o.Settings);
            if (s.Mode == "import" && GpuEventLog.IsSupported)
            {
                // The point of importing a gaming log: line it up with the crashes Windows recorded.
                s.Events.AddRange(GpuEventLog.Query(s.Start.AddMinutes(-1), s.End.AddMinutes(2)));
                AnsiConsole.MarkupLine($"Found {s.Events.Count} GPU-related Windows event(s) in the log's time range.");
            }
        }
        else
        {
            throw new FileNotFoundException($"Not found: {path}");
        }

        if (o.Settings != null) s.Tuning = TuningSettings.Parse(o.Settings);
        Reporting.AnalyzeAndReport(s, outDir, o);
        return 0;
    }
}
