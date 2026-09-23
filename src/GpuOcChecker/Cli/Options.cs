using System.Globalization;

namespace GpuOcChecker.Cli;

public sealed class Options
{
    public string Command { get; set; } = "";
    public List<string> Positional { get; } = new();
    public int? Gpu { get; set; }
    public string Profile { get; set; } = "standard";
    public double? Minutes { get; set; }
    public double Scale { get; set; } = 1;
    public string? Phases { get; set; }
    public string? Settings { get; set; }
    public bool SaveBaseline { get; set; }
    public double VramPercent { get; set; } = 60;
    public int? ClDevice { get; set; }
    public string? Simulate { get; set; }
    public int IntervalMs { get; set; }
    public double? DurationMinutes { get; set; }
    public int Days { get; set; } = 14;
    public bool Yes { get; set; }
    public bool Open { get; set; }
    public bool Raw { get; set; }
    public bool NoCrashCheck { get; set; }
    public bool Interactive { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            double Num() => double.Parse(Next(), CultureInfo.InvariantCulture);

            switch (a.ToLowerInvariant())
            {
                case "--gpu": o.Gpu = (int)Num(); break;
                case "--profile" or "-p": o.Profile = Next().ToLowerInvariant(); break;
                case "--minutes" or "-m": o.Minutes = Num(); break;
                case "--scale": o.Scale = Num(); break;
                case "--phases": o.Phases = Next(); break;
                case "--settings" or "-s": o.Settings = Next(); break;
                case "--save-baseline": o.SaveBaseline = true; break;
                case "--vram-percent": o.VramPercent = Num(); break;
                case "--cl-device": o.ClDevice = (int)Num(); break;
                case "--simulate":
                    o.Simulate = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "undervolt";
                    break;
                case "--interval": o.IntervalMs = (int)Num(); break;
                case "--duration": o.DurationMinutes = Num(); break;
                case "--days": o.Days = (int)Num(); break;
                case "--yes" or "-y": o.Yes = true; break;
                case "--open": o.Open = true; break;
                case "--raw": o.Raw = true; break;
                case "--no-crash-check": o.NoCrashCheck = true; break;
                case "--help" or "-h" or "/?": o.Command = "help"; break;
                default:
                    if (a.StartsWith("--")) throw new ArgumentException($"Unknown option {a}");
                    if (o.Command.Length == 0) o.Command = a.ToLowerInvariant();
                    else o.Positional.Add(a);
                    break;
            }
        }
        return o;
    }

    public const string Help = """
GPU Overclock Checker — finds where a GPU overclock / undervolt is failing and why.

USAGE
  GpuOcChecker                       interactive menu
  GpuOcChecker <command> [options]

COMMANDS
  stress      Run the stability test suite (VRAM, bandwidth, light/transient/heavy core load)
              while recording telemetry, then diagnose failures.
  monitor     Record telemetry + watch for driver crashes while you game. Stop with Q / Ctrl+C.
  memtune     Assisted memory overclock sweep: measures real bandwidth at each memory clock
              you set, and finds the point where error-correction retries start.
  analyze     Analyze a previous session folder, a GpuOcChecker CSV or an HWiNFO CSV log.
  events      Show GPU driver crashes (TDR), WHEA errors and unexpected reboots from the event log.
  list        List detected GPUs (telemetry) and OpenCL devices (stress).
  sensors     Print current sensor readings (--raw for every raw sensor).
  help        This text.

OPTIONS
  --profile quick|standard|thorough   stress length (~4 / ~11 / ~57 min)       [standard]
  --minutes N           scale the stress profile to about N minutes
  --phases a,b,..       only run these phases: idle,bandwidth,vram,light,transient,heavy
  --settings "..."      your current tuning, e.g. "uv=-80,max=2900,min=500,mem=2700,ft=1,pl=15"
                        (uv=voltage offset mV, max/min=frequency MHz, mem=VRAM MHz, ft=fast timing,
                         pl=power limit %, maxv=max voltage mV). Makes recommendations concrete.
  --save-baseline       store this run as the stock reference (run it at STOCK settings)
  --gpu N               telemetry GPU index from 'list'
  --cl-device N         OpenCL device index from 'list' (default: matches the telemetry GPU)
  --vram-percent P      share of VRAM for the pattern test                       [60]
  --duration MIN        stop 'monitor' after MIN minutes
  --interval MS         sampling interval (stress 250, monitor 500)
  --days N              history window for 'events'                              [14]
  --open                open the HTML report when done
  --yes, -y             skip the confirmation prompt
  --simulate [scenario] no hardware needed: stable|undervolt|memory|thermal|crash (demo/testing)

Reports and logs are written to Documents\GpuOcChecker\sessions.
""";
}
