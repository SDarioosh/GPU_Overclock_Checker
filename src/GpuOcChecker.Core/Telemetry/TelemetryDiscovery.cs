using GpuOcChecker.Core.Telemetry.Amd;
using GpuOcChecker.Core.Telemetry.Nvidia;

namespace GpuOcChecker.Core.Telemetry;

public static class TelemetryDiscovery
{
    /// <summary>Finds every GPU that exposes telemetry, most-likely-discrete first.</summary>
    public static List<ITelemetrySource> Discover(Action<string>? log = null)
    {
        var all = new List<ITelemetrySource>();
        all.AddRange(AdlTelemetrySource.Discover(log));
        all.AddRange(NvmlTelemetrySource.Discover(log));
        return all.OrderByDescending(s => DiscreteScore(s.Gpu.Name)).ToList();
    }

    /// <summary>Ranks discrete cards above integrated graphics (e.g. a Ryzen 7000 iGPU next to a 7900 XTX).</summary>
    public static int DiscreteScore(string name)
    {
        var n = name.ToUpperInvariant();
        int score = 0;
        if (n.Contains(" RX ") || n.Contains("RTX") || n.Contains("GTX") || n.Contains("ARC") || n.Contains("RADEON PRO")) score += 10;
        if (n.Contains("XTX")) score += 2;
        if (n.Contains("(TM) GRAPHICS") || n.EndsWith("RADEON GRAPHICS") || n.Contains("VEGA")) score -= 10;
        return score;
    }
}
