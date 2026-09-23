using System.Text.Json;

namespace GpuOcChecker.Core.Analysis;

public sealed class MemTuneEntry
{
    public DateTimeOffset Time { get; set; }
    public double MemClock { get; set; }
    public string? Label { get; set; }
    public double BandwidthGBps { get; set; }
    public double BandwidthCov { get; set; }
    public long VramErrors { get; set; }
    public double? MemTemp { get; set; }
}

/// <summary>
/// Assisted memory sweep. GDDR6/6X hides transmission errors by retrying, so past a certain clock
/// bandwidth stops rising (or falls) long before anything crashes. The best setting is the one with
/// the highest measured bandwidth and zero pattern errors, not the highest clock that "doesn't crash".
/// </summary>
public static class MemTune
{
    public static string PathFor(string gpuKey) => Path.Combine(AppPaths.MemTune, gpuKey + ".json");

    public static List<MemTuneEntry> Load(string gpuKey)
    {
        var p = PathFor(gpuKey);
        return File.Exists(p) ? JsonSerializer.Deserialize<List<MemTuneEntry>>(File.ReadAllText(p), JsonOpts.Default) ?? new() : new();
    }

    public static void Save(string gpuKey, List<MemTuneEntry> entries) =>
        File.WriteAllText(PathFor(gpuKey), JsonSerializer.Serialize(entries, JsonOpts.Default));

    public static MemTuneEntry? Best(IEnumerable<MemTuneEntry> entries) =>
        entries.Where(e => e.VramErrors == 0).MaxBy(e => e.BandwidthGBps);

    /// <summary>Human-readable verdict for the latest measurement relative to the others.</summary>
    public static string Assess(List<MemTuneEntry> entries)
    {
        if (entries.Count == 0) return "No measurements yet.";
        var last = entries[^1];
        if (last.VramErrors > 0)
            return $"{last.MemClock:0} MHz: {last.VramErrors} VRAM errors — unstable, go lower.";
        var best = Best(entries);
        if (entries.Count == 1 || best == null)
            return $"{last.MemClock:0} MHz: {last.BandwidthGBps:0} GB/s. Raise the memory clock 25–50 MHz in your tuning tool and measure again.";

        var lowerClocks = entries.Where(e => e != last && e.MemClock < last.MemClock - 1 && e.VramErrors == 0).ToList();
        var bestBelow = lowerClocks.MaxBy(e => e.BandwidthGBps);
        if (bestBelow != null && last.BandwidthGBps < bestBelow.BandwidthGBps * 0.995)
            return $"{last.MemClock:0} MHz is SLOWER than {bestBelow.MemClock:0} MHz ({last.BandwidthGBps:0} vs {bestBelow.BandwidthGBps:0} GB/s): error-correction retries. " +
                   $"You are past the limit — use {best.MemClock:0} MHz (best: {best.BandwidthGBps:0} GB/s).";
        if (bestBelow != null)
        {
            double clockGain = last.MemClock / bestBelow.MemClock - 1;
            double bwGain = last.BandwidthGBps / bestBelow.BandwidthGBps - 1;
            if (clockGain > 0 && bwGain < clockGain * 0.5)
                return $"{last.MemClock:0} MHz: +{clockGain * 100:0.0}% clock but only +{bwGain * 100:0.0}% bandwidth vs {bestBelow.MemClock:0} MHz — scaling is flattening; you are near the limit.";
        }
        if (last.BandwidthCov > 0.03)
            return $"{last.MemClock:0} MHz: bandwidth is erratic (±{last.BandwidthCov * 100:0.0}%) — typical of retries. Treat as near the limit.";
        return $"{last.MemClock:0} MHz: {last.BandwidthGBps:0} GB/s and still scaling. Best so far: {best.MemClock:0} MHz. Try +25 MHz.";
    }
}
