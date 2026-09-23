using System.Globalization;
using System.Text;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Session;

/// <summary>The tool's own telemetry log format (also easy to open in Excel).</summary>
public static class TelemetryCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Header =>
        "timestamp,elapsed_s,phase," + string.Join(",", Metrics.All.Select(m => m.CsvName)) + ",throttle";

    public static string Format(TelemetrySample s)
    {
        var sb = new StringBuilder(256);
        sb.Append(s.Timestamp.ToString("o", Inv)).Append(',');
        sb.Append(s.Elapsed.ToString("0.000", Inv)).Append(',');
        sb.Append(Escape(s.Phase)).Append(',');
        foreach (var m in Metrics.All)
        {
            if (s[m.Metric] is double v) sb.Append(v.ToString("0.####", Inv));
            sb.Append(',');
        }
        sb.Append((int)s.Throttle);
        return sb.ToString();
    }

    private static string Escape(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static bool IsOwnFormat(string headerLine) => headerLine.StartsWith("timestamp,elapsed_s,phase", StringComparison.Ordinal);

    public static List<TelemetrySample> Read(string path)
    {
        var list = new List<TelemetrySample>();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var header = reader.ReadLine();
        if (header == null || !IsOwnFormat(header)) throw new InvalidDataException("Not a GpuOcChecker telemetry file.");
        var cols = SplitCsv(header);
        var index = cols.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            if (f.Count < 3 || !DateTimeOffset.TryParse(f[0], Inv, DateTimeStyles.None, out var ts)) continue; // torn last line after a crash
            var s = new TelemetrySample
            {
                Timestamp = ts,
                Elapsed = double.TryParse(f[1], NumberStyles.Float, Inv, out var el) ? el : 0,
                Phase = f[2].Length > 0 ? f[2] : null,
            };
            foreach (var m in Metrics.All)
            {
                if (index.TryGetValue(m.CsvName, out int i) && i < f.Count &&
                    double.TryParse(f[i], NumberStyles.Float, Inv, out var v))
                    s[m.Metric] = v;
            }
            if (index.TryGetValue("throttle", out int ti) && ti < f.Count && int.TryParse(f[ti], out int th))
                s.Throttle = (ThrottleFlags)th;
            list.Add(s);
        }
        return list;
    }

    public static List<string> SplitCsv(string line, char delimiter = ',')
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
