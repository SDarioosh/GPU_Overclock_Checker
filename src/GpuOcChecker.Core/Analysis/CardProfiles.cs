using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Analysis;

/// <summary>Reference specs used to put telemetry in context. Unknown cards fall back to generic limits.</summary>
public sealed record CardProfile(
    string Name,
    string[] Match,
    GpuVendor Vendor,
    double BoostClockMHz,
    double MemGbps,
    int BusWidthBits,
    double TbpW,
    double HotspotLimitC,
    double MemTempLimitC,
    double? StockMaxVoltage = null,
    double? StockMemClockReported = null,
    double MaxPowerLimitPct = 15)
{
    public double TheoreticalBandwidthGBps => MemGbps * BusWidthBits / 8;

    /// <summary>Theoretical bandwidth at a reported memory clock (only when the vendor's clock units are known).</summary>
    public double? TheoreticalBandwidthAt(double reportedMemClock) =>
        StockMemClockReported is double stock && stock > 0 ? TheoreticalBandwidthGBps * reportedMemClock / stock : null;

    public bool IsGeneric => Match.Length == 0;
}

public static class CardProfiles
{
    // AMD: ADL reports the memory clock such that 2500 MHz == 20 Gbps GDDR6 (x8).
    public static readonly IReadOnlyList<CardProfile> All = new[]
    {
        new CardProfile("Radeon RX 7900 XTX", new[] { "7900 XTX" }, GpuVendor.Amd, 2500, 20, 384, 355, 110, 100, 1.15, 2500),
        new CardProfile("Radeon RX 7900 XT", new[] { "7900 XT" }, GpuVendor.Amd, 2400, 20, 320, 315, 110, 100, 1.15, 2500),
        new CardProfile("Radeon RX 7900 GRE", new[] { "7900 GRE" }, GpuVendor.Amd, 2245, 18, 256, 260, 110, 100, 1.15, 2250),
        new CardProfile("Radeon RX 7800 XT", new[] { "7800 XT" }, GpuVendor.Amd, 2430, 19.5, 256, 263, 110, 100, 1.15, 2438),
        new CardProfile("Radeon RX 7700 XT", new[] { "7700 XT" }, GpuVendor.Amd, 2544, 18, 192, 245, 110, 100, 1.15, 2250),
        new CardProfile("Radeon RX 7600 XT", new[] { "7600 XT" }, GpuVendor.Amd, 2755, 18, 128, 190, 110, 100, 1.15, 2250),
        new CardProfile("Radeon RX 7600", new[] { "7600" }, GpuVendor.Amd, 2655, 18, 128, 165, 110, 100, 1.15, 2250),
        new CardProfile("Radeon RX 9070 XT", new[] { "9070 XT" }, GpuVendor.Amd, 2970, 20, 256, 304, 110, 100, null, 2518),
        new CardProfile("Radeon RX 9070", new[] { "9070" }, GpuVendor.Amd, 2520, 20, 256, 220, 110, 100, null, 2518),
        new CardProfile("Radeon RX 6950 XT", new[] { "6950 XT" }, GpuVendor.Amd, 2310, 18, 256, 335, 110, 100, 1.2, 2250),
        new CardProfile("Radeon RX 6900 XT", new[] { "6900 XT" }, GpuVendor.Amd, 2250, 16, 256, 300, 110, 100, 1.175, 2000),
        new CardProfile("Radeon RX 6800 XT", new[] { "6800 XT" }, GpuVendor.Amd, 2250, 16, 256, 300, 110, 100, 1.15, 2000),
        new CardProfile("Radeon RX 6800", new[] { "6800" }, GpuVendor.Amd, 2105, 16, 256, 250, 110, 100, 1.025, 2000),
        new CardProfile("Radeon RX 6700 XT", new[] { "6700 XT" }, GpuVendor.Amd, 2581, 16, 192, 230, 110, 100, 1.2, 2000),
        new CardProfile("GeForce RTX 5090", new[] { "RTX 5090" }, GpuVendor.Nvidia, 2407, 28, 512, 575, 90, 100),
        new CardProfile("GeForce RTX 5080", new[] { "RTX 5080" }, GpuVendor.Nvidia, 2617, 30, 256, 360, 90, 100),
        new CardProfile("GeForce RTX 5070 Ti", new[] { "RTX 5070 Ti" }, GpuVendor.Nvidia, 2452, 28, 256, 300, 90, 100),
        new CardProfile("GeForce RTX 4090", new[] { "RTX 4090" }, GpuVendor.Nvidia, 2520, 21, 384, 450, 90, 100),
        new CardProfile("GeForce RTX 4080 SUPER", new[] { "RTX 4080 SUPER" }, GpuVendor.Nvidia, 2550, 23, 256, 320, 90, 100),
        new CardProfile("GeForce RTX 4080", new[] { "RTX 4080" }, GpuVendor.Nvidia, 2505, 22.4, 256, 320, 90, 100),
        new CardProfile("GeForce RTX 4070 Ti", new[] { "RTX 4070 Ti" }, GpuVendor.Nvidia, 2610, 21, 192, 285, 90, 100),
        new CardProfile("GeForce RTX 4070", new[] { "RTX 4070" }, GpuVendor.Nvidia, 2475, 21, 192, 200, 90, 100),
        new CardProfile("GeForce RTX 3090", new[] { "RTX 3090" }, GpuVendor.Nvidia, 1695, 19.5, 384, 350, 90, 105),
        new CardProfile("GeForce RTX 3080", new[] { "RTX 3080" }, GpuVendor.Nvidia, 1710, 19, 320, 320, 90, 105),
    };

    public static CardProfile Generic(GpuVendor vendor) =>
        new("Generic GPU", Array.Empty<string>(), vendor, 0, 0, 0, 0, vendor == GpuVendor.Nvidia ? 90 : 110, 100);

    /// <summary>Longest match wins, so "7900 XTX" beats "7900 XT".</summary>
    public static CardProfile Find(string? gpuName, GpuVendor vendor = GpuVendor.Unknown)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return Generic(vendor);
        var n = gpuName.ToUpperInvariant();
        var hit = All
            .SelectMany(p => p.Match.Select(m => (p, m)))
            .Where(x => ContainsToken(n, x.m.ToUpperInvariant()))
            .OrderByDescending(x => x.m.Length)
            .Select(x => x.p)
            .FirstOrDefault();
        return hit ?? Generic(vendor == GpuVendor.Unknown && n.Contains("RADEON") ? GpuVendor.Amd : vendor);
    }

    private static bool ContainsToken(string haystack, string needle)
    {
        int i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            int end = i + needle.Length;
            bool rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            bool leftOk = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            if (leftOk && rightOk) return true;
            i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal);
        }
        return false;
    }
}
