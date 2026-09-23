using System.Text;
using GpuOcChecker.Cli;
using GpuOcChecker.Commands;
using Spectre.Console;

namespace GpuOcChecker;

public static class Program
{
    public const string Version = "1.0.0";

    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // redirected output
        }

        Options o;
        try
        {
            o = Options.Parse(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]\n");
            AnsiConsole.Write(Options.Help);
            return 64;
        }

        if (o.Command == "help")
        {
            AnsiConsole.Write(Options.Help);
            return 0;
        }

        if (!o.NoCrashCheck && o.Simulate == null && o.Command is not ("analyze" or "list" or "events"))
            Reporting.CheckPreviousCrashes(o);

        if (o.Command.Length == 0) return Menu(o);
        return Execute(o);
    }

    private static int Execute(Options o)
    {
        try
        {
            return o.Command switch
            {
                "stress" or "test" => StressCommand.Run(o),
                "monitor" => MonitorCommand.Run(o),
                "memtune" => MemTuneCommand.Run(o),
                "analyze" => InfoCommands.Analyze(o),
                "events" => InfoCommands.Events(o),
                "list" => InfoCommands.List(o),
                "sensors" => InfoCommands.Sensors(o),
                _ => Unknown(o.Command),
            };
        }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or InvalidDataException or InvalidOperationException
                                      or DllNotFoundException or Core.Stress.OpenCl.ClException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(e.Message)}");
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(cmd)}'.[/]\n");
        AnsiConsole.Write(Options.Help);
        return 64;
    }

    /// <summary>Double-click experience: a menu instead of command-line flags.</summary>
    private static int Menu(Options baseOptions)
    {
        AnsiConsole.Write(new FigletText("GPU OC Checker").Color(Color.Red));
        AnsiConsole.MarkupLine($"[grey]v{Version} — finds where an overclock/undervolt fails and why. Run with --help for command-line use.[/]\n");

        string? settings = baseOptions.Settings;
        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("What do you want to do?")
                .PageSize(12)
                .AddChoices(
                    "Quick stability test (~4 min)",
                    "Standard stability test (~11 min)",
                    "Thorough stability test (~57 min)",
                    "Monitor while gaming (catch real-world crashes)",
                    "Memory tuning assistant (find the best VRAM clock)",
                    "Save stock baseline (run at STOCK settings)",
                    "Analyze a previous session or HWiNFO log",
                    "GPU crash history (Windows event log)",
                    "Show sensors / devices",
                    $"Set my current tuning [grey]({Markup.Escape(settings ?? "not set")})[/]",
                    "Exit"));

            var o = Options.Parse(Array.Empty<string>());
            o.Simulate = baseOptions.Simulate;
            o.Gpu = baseOptions.Gpu;
            o.ClDevice = baseOptions.ClDevice;
            o.Settings = settings;
            o.Open = true;
            o.NoCrashCheck = true;

            if (choice.StartsWith("Exit")) return 0;
            if (choice.StartsWith("Set my current tuning"))
            {
                AnsiConsole.MarkupLine("Enter what you set in AMD Software / Afterburner, e.g. [bold]uv=-80, max=2900, min=500, mem=2700, ft=1, pl=15[/]");
                AnsiConsole.MarkupLine("[grey]uv = voltage offset (mV), max/min = frequency (MHz), mem = VRAM clock, ft = fast timing (1/0), pl = power limit (%). Leave empty to clear.[/]");
                settings = AnsiConsole.Prompt(new TextPrompt<string>("Tuning:").AllowEmpty());
                if (string.IsNullOrWhiteSpace(settings)) settings = null;
                continue;
            }

            if (choice.StartsWith("Quick")) { o.Command = "stress"; o.Profile = "quick"; }
            else if (choice.StartsWith("Standard")) { o.Command = "stress"; o.Profile = "standard"; }
            else if (choice.StartsWith("Thorough")) { o.Command = "stress"; o.Profile = "thorough"; }
            else if (choice.StartsWith("Monitor")) o.Command = "monitor";
            else if (choice.StartsWith("Memory")) o.Command = "memtune";
            else if (choice.StartsWith("Save stock")) { o.Command = "stress"; o.Profile = "standard"; o.SaveBaseline = true; }
            else if (choice.StartsWith("Analyze"))
            {
                o.Command = "analyze";
                var p = AnsiConsole.Prompt(new TextPrompt<string>("Path to a session folder or CSV (Enter = pick a recent session):").AllowEmpty());
                if (!string.IsNullOrWhiteSpace(p)) o.Positional.Add(p.Trim().Trim('"'));
            }
            else if (choice.StartsWith("GPU crash")) o.Command = "events";
            else if (choice.StartsWith("Show"))
            {
                Execute(new Options { Command = "list", Simulate = o.Simulate });
                o.Command = "sensors";
            }

            Execute(o);
            AnsiConsole.WriteLine();
        }
    }
}
