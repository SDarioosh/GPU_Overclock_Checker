using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Analysis;

public sealed record MetricStats(double Min, double Max, double Mean, double P5, double P95, double StdDev, int N)
{
    public double Cov => Mean != 0 ? StdDev / Math.Abs(Mean) : 0;
}

public static class Stats
{
    public static MetricStats? Of(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return null;
        double mean = v.Average();
        double sd = v.Length > 1 ? Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1)) : 0;
        return new MetricStats(v[0], v[^1], mean, Percentile(v, 0.05), Percentile(v, 0.95), sd, v.Length);
    }

    public static MetricStats? Of(IEnumerable<TelemetrySample> samples, Metric m) =>
        Of(samples.Where(s => s.Has(m)).Select(s => s[m]!.Value));

    /// <summary>Percentile of an already sorted array (linear interpolation).</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    public static IEnumerable<TelemetrySample> Window(IReadOnlyList<TelemetrySample> samples, DateTimeOffset from, DateTimeOffset to) =>
        samples.Where(s => s.Timestamp >= from && s.Timestamp <= to);
}
