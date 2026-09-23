using GpuOcChecker.Cli;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Stress;
using GpuOcChecker.Core.Stress.OpenCl;
using GpuOcChecker.Core.Telemetry;
using Spectre.Console;

namespace GpuOcChecker.Commands;

/// <summary>
/// Interactive memory sweep: the user changes the VRAM clock in AMD Software / Afterburner between
/// measurements; each step measures real bandwidth plus a short pattern test.
/// </summary>
public static class MemTuneCommand
{
    private const double BandwidthSeconds = 12;
    private const int PatternPasses = 6;

    public static int Run(Options o)
    {
        using var hw = Hardware.Open(o);
        string key = hw.Telemetry?.Gpu.Key ?? "unknown-gpu";
        var entries = MemTune.Load(key);

        AnsiConsole.MarkupLine("[bold]Memory tuning assistant[/]");
        AnsiConsole.MarkupLine("1. Leave core settings where you want them (or at stock).  2. Set a memory clock in AMD Software (Performance → Tuning → VRAM) or Afterburner and apply.");
        AnsiConsole.MarkupLine("3. Press Enter here to measure. Repeat with +25 MHz steps. The best setting is the one with the [bold]highest bandwidth[/], not the highest clock.");
        if (entries.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]{entries.Count} earlier measurements loaded for this GPU.[/]");
            if (AnsiConsole.Confirm("Clear them and start a new sweep?", false)) entries.Clear();
        }

        IStressBackend? backend = null;
        try
        {
            while (true)
            {
                PrintTable(entries);
                var label = AnsiConsole.Prompt(new TextPrompt<string>("Label for this step (e.g. [grey]2700 FT[/]), Enter = measure, [grey]q[/] = quit:").AllowEmpty());
                if (label.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) break;
                if (hw.Simulation != null)
                    hw.Simulation.MemClock = AnsiConsole.Prompt(new TextPrompt<double>("[grey](simulation)[/] memory clock to simulate:").DefaultValue(hw.Simulation.MemClock + 25));

                try
                {
                    backend ??= CreateBackend(hw, o);
                    var entry = Measure(backend, hw.Telemetry);
                    entry.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
                    entries.Add(entry);
                    MemTune.Save(key, entries);
                    AnsiConsole.MarkupLine(Markup.Escape(MemTune.Assess(entries)));
                }
                catch (ClException e) when (e.DeviceLost)
                {
                    AnsiConsole.MarkupLine($"[red]The driver reset during the measurement ({Markup.Escape(e.Message)}).[/] This memory setting is unstable. " +
                                           "AMD Software has probably reverted to default settings; re-apply a lower memory clock.");
                    entries.Add(new MemTuneEntry { Time = DateTimeOffset.Now, MemClock = hw.Telemetry?.Read()[Metric.MemClock] ?? 0, Label = "driver reset", VramErrors = 1 });
                    MemTune.Save(key, entries);
                    backend?.Dispose();
                    backend = null;
                }
            }
        }
        finally
        {
            backend?.Dispose();
        }

        var best = MemTune.Best(entries);
        if (best != null)
            AnsiConsole.MarkupLine($"[green]Best measured setting:[/] [bold]{best.MemClock:0} MHz[/]{(best.Label != null ? $" ({Markup.Escape(best.Label)})" : "")} at {best.BandwidthGBps:0} GB/s. " +
                                   "Confirm it with a full 'stress' run and some gaming with 'monitor'.");
        return 0;
    }

    private static IStressBackend CreateBackend(Hardware hw, Options o)
    {
        var setup = new StressSetup { NeedCompute = false, NeedVram = true, VramFraction = Math.Min(o.VramPercent, 40) / 100.0 };
        var b = hw.CreateBackend(o, setup);
        AnsiConsole.Status().Start("Allocating VRAM…", _ => b.Initialize(setup, _ => { }));
        return b;
    }

    private static MemTuneEntry Measure(IStressBackend backend, ITelemetrySource? telemetry)
    {
        var bw = new List<double>();
        var clocks = new List<double>();
        double? memTemp = null;
        long errors = 0;

        AnsiConsole.Progress().AutoClear(true).Start(ctx =>
        {
            var tb = ctx.AddTask("Bandwidth", maxValue: BandwidthSeconds);
            var tp = ctx.AddTask("Pattern test", maxValue: PatternPasses);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var nextSample = TimeSpan.Zero;
            while (sw.Elapsed.TotalSeconds < BandwidthSeconds)
            {
                bw.Add(backend.RunBandwidth().GBps);
                if (telemetry != null && sw.Elapsed >= nextSample)
                {
                    nextSample = sw.Elapsed + TimeSpan.FromMilliseconds(250);
                    try
                    {
                        var s = telemetry.Read();
                        if (s[Metric.MemClock] is double mc) clocks.Add(mc);
                        if (s[Metric.MemTemp] is double mt) memTemp = Math.Max(memTemp ?? 0, mt);
                    }
                    catch (Exception)
                    {
                        // keep measuring
                    }
                }
                tb.Value = sw.Elapsed.TotalSeconds;
            }
            tb.Value = BandwidthSeconds;
            for (uint p = 0; p < PatternPasses; p++)
            {
                errors += backend.RunVramPass(p).Errors;
                tp.Increment(1);
            }
        });

        // Skip the warm-up quarter, where clocks are still ramping.
        var steady = bw.Skip(bw.Count / 4).ToList();
        var st = Stats.Of(steady) ?? Stats.Of(bw)!;
        return new MemTuneEntry
        {
            Time = DateTimeOffset.Now,
            MemClock = clocks.Count > 0 ? clocks.OrderBy(x => x).ElementAt(clocks.Count / 2) : 0,
            BandwidthGBps = st.Mean,
            BandwidthCov = st.Cov,
            VramErrors = errors,
            MemTemp = memTemp,
        };
    }

    private static void PrintTable(List<MemTuneEntry> entries)
    {
        if (entries.Count == 0) return;
        var best = MemTune.Best(entries);
        var t = new Table().Border(TableBorder.Rounded).Title("Memory sweep");
        t.AddColumns("#", "Label", "Mem clock", "Bandwidth", "Variation", "VRAM errors", "Mem temp");
        int i = 1;
        foreach (var e in entries)
        {
            bool isBest = e == best;
            string bwText = $"{e.BandwidthGBps:0} GB/s" + (isBest ? " [green]◀ best[/]" : "");
            t.AddRow(i++.ToString(), Markup.Escape(e.Label ?? ""), $"{e.MemClock:0} MHz", bwText, $"±{e.BandwidthCov * 100:0.0}%",
                e.VramErrors > 0 ? $"[red]{e.VramErrors}[/]" : "[green]0[/]", e.MemTemp is double mt ? $"{mt:0} °C" : "–");
        }
        AnsiConsole.Write(t);
    }
}
