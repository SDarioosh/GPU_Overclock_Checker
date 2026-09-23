using GpuOcChecker.Core.Stress;
using GpuOcChecker.Core.Stress.OpenCl;
using GpuOcChecker.Core.Telemetry;
using GpuOcChecker.Core.Telemetry.Simulation;
using Spectre.Console;

namespace GpuOcChecker.Cli;

/// <summary>Resolves the telemetry source and stress backend for a command (real hardware or simulator).</summary>
public sealed class Hardware : IDisposable
{
    private readonly List<ITelemetrySource> _all = new();

    private Hardware()
    {
    }

    public ITelemetrySource? Telemetry { get; private set; }
    public SimulationState? Simulation { get; private set; }

    public static Hardware Open(Options o, bool quiet = false)
    {
        var h = new Hardware();
        if (o.Simulate != null)
        {
            if (!Enum.TryParse<SimScenario>(o.Simulate, true, out var scenario))
                throw new ArgumentException($"Unknown simulation scenario '{o.Simulate}'. Use: {string.Join(", ", Enum.GetNames<SimScenario>()).ToLowerInvariant()}");
            h.Simulation = new SimulationState(scenario);
            h.Telemetry = new SimulatedTelemetrySource(h.Simulation);
            return h;
        }

        var notes = h.Notes;
        h._all.AddRange(TelemetryDiscovery.Discover(notes.Add));
        if (h._all.Count == 0)
        {
            if (!quiet)
            {
                AnsiConsole.MarkupLine("[yellow]No GPU telemetry found (AMD ADL / NVIDIA NVML).[/] Tests still detect errors, but causes can't be attributed.");
                foreach (var n in notes) AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(n)}[/]");
                AnsiConsole.MarkupLine("[grey]  If this is an AMD/NVIDIA card with a current driver, run 'GpuOcChecker list' and report its output.[/]");
            }
            return h;
        }
        int idx = o.Gpu ?? 0;
        if (idx < 0 || idx >= h._all.Count) throw new ArgumentException($"--gpu {idx} out of range (0..{h._all.Count - 1}); see 'list'.");
        h.Telemetry = h._all[idx];
        if (!quiet)
            AnsiConsole.MarkupLine($"Monitoring [bold]{Markup.Escape(h.Telemetry.Gpu.Name)}[/] [grey]via {Markup.Escape(h.Telemetry.Gpu.Backend)}[/]");
        return h;
    }

    public IReadOnlyList<ITelemetrySource> All => _all;

    /// <summary>Discovery diagnostics (why a backend or adapter was not used).</summary>
    public List<string> Notes { get; } = new();

    public IStressBackend CreateBackend(Options o, StressSetup setup)
    {
        if (Simulation != null) return new SimulatedBackend(Simulation);
        setup.DeviceIndex = o.ClDevice;
        setup.PreferName = Telemetry?.Gpu.Name;
        return OpenClBackend.Create(setup);
    }

    public void Dispose()
    {
        foreach (var s in _all) s.Dispose();
        if (!_all.Contains(Telemetry!)) Telemetry?.Dispose();
    }
}
