using GpuOcChecker.Core.Session;

namespace GpuOcChecker.Core.Stress;

public sealed record PhasePlan(string Name, PhaseKind Kind, TimeSpan Duration);

public static class StressProfiles
{
    public static readonly string[] Names = { "quick", "standard", "thorough" };

    /// <summary>
    /// Phase order matters: memory is tested cold first, then light/transient loads (where undervolts
    /// fail), then a long heavy phase that heat-soaks the card (where thermals and vdroop show up).
    /// </summary>
    public static List<PhasePlan> Get(string name, double scale = 1.0, ISet<PhaseKind>? only = null)
    {
        (double idle, double bw, double vram, double light, double trans, double heavy) = name.ToLowerInvariant() switch
        {
            "quick" => (8, 20, 45, 45, 60, 75),
            "thorough" => (20, 60, 600, 600, 900, 1200),
            _ => (15, 40, 120, 120, 150, 180),
        };
        var plan = new List<PhasePlan>
        {
            new("Idle baseline", PhaseKind.Idle, S(idle)),
            new("VRAM bandwidth", PhaseKind.Bandwidth, S(bw)),
            new("VRAM pattern test", PhaseKind.VramPattern, S(vram)),
            new("Light load / max boost", PhaseKind.LightLoad, S(light)),
            new("Transient load steps", PhaseKind.Transient, S(trans)),
            new("Heavy sustained load", PhaseKind.HeavyLoad, S(heavy)),
            new("Cool-down", PhaseKind.Idle, S(idle)),
        };
        if (only is { Count: > 0 })
            plan = plan.Where(p => only.Contains(p.Kind)).ToList();
        return plan;

        TimeSpan S(double seconds) => TimeSpan.FromSeconds(Math.Max(3, seconds * scale));
    }

    public static PhaseKind? ParseKind(string s) => s.Trim().ToLowerInvariant() switch
    {
        "idle" => PhaseKind.Idle,
        "bandwidth" or "bw" => PhaseKind.Bandwidth,
        "vram" or "memory" or "mem" => PhaseKind.VramPattern,
        "light" => PhaseKind.LightLoad,
        "transient" => PhaseKind.Transient,
        "heavy" => PhaseKind.HeavyLoad,
        _ => null,
    };
}
