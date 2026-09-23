using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Session;

/// <summary>
/// Imports HWiNFO64 sensor logs (CSV), so a real gaming session logged with HWiNFO can be analyzed
/// together with the Windows event log (e.g. "what was the card doing when the game crashed?").
/// </summary>
public static class HwInfoImporter
{
    private sealed record Rule(Metric Metric, Regex Pattern, int Priority);

    // Lower priority number wins when several columns match the same metric.
    private static readonly Rule[] Rules =
    {
        R(Metric.CoreClock, @"^gpu (core |shader |graphics )?clock\b", 0),
        R(Metric.CoreClock, @"^gpu effective clock", 1),
        R(Metric.CoreClock, @"^gpu front ?end clock", 2),
        R(Metric.MemClock, @"^gpu memory clock", 0),
        R(Metric.SocClock, @"^gpu soc clock", 0),
        R(Metric.CoreVoltage, @"^gpu core voltage", 0),
        R(Metric.CoreVoltage, @"^gpu voltage", 1),
        R(Metric.MemVoltage, @"^gpu memory voltage", 0),
        R(Metric.SocVoltage, @"^gpu soc voltage", 0),
        R(Metric.HotspotTemp, @"^gpu (hot ?spot|junction) temperature", 0),
        R(Metric.MemTemp, @"^gpu memory (junction )?temperature", 0),
        R(Metric.EdgeTemp, @"^gpu temperature", 0),
        R(Metric.VrTemp, @"^gpu vr (vddc|gfx|core)? ?temperature", 0),
        R(Metric.Power, @"total board power|^gpu (board|tbp) power", 0),
        R(Metric.Power, @"^gpu asic power", 1),
        R(Metric.Power, @"^gpu (ppt|power)\b", 2),
        R(Metric.CoreLoad, @"^gpu (core load|utilization|usage)", 0),
        R(Metric.CoreLoad, @"^gpu d3d usage", 1),
        R(Metric.MemLoad, @"^gpu memory controller (load|utilization)", 0),
        R(Metric.FanRpm, @"^gpu fan", 0),
    };

    private static Rule R(Metric m, string p, int prio) => new(m, new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled), prio);

    public static bool LooksLikeHwInfo(string headerLine) =>
        headerLine.StartsWith("Date,Time", StringComparison.OrdinalIgnoreCase) ||
        headerLine.StartsWith("\"Date\",\"Time\"", StringComparison.OrdinalIgnoreCase) ||
        headerLine.StartsWith("Date;Time", StringComparison.OrdinalIgnoreCase);

    public static SessionData Import(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool utf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = (utf8Bom ? Encoding.UTF8 : Encoding.Latin1).GetString(bytes, utf8Bom ? 3 : 0, bytes.Length - (utf8Bom ? 3 : 0));
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) throw new InvalidDataException("File has no data rows.");

        char delim = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var header = TelemetryCsv.SplitCsv(lines[0], delim);
        var map = MapColumns(header);
        if (!map.ContainsKey(Metric.CoreClock) && !map.ContainsKey(Metric.EdgeTemp))
            throw new InvalidDataException("No GPU columns recognised. Log the GPU sensors in HWiNFO (Sensors > Logging start).");

        var session = new SessionData { Mode = "import", Gpu = new GpuInfo(GpuVendor.Unknown, GuessGpuName(lines), "hwinfo-import", "HWiNFO log") };
        DateTimeOffset? first = null;
        foreach (var line in lines.Skip(1))
        {
            var f = TelemetryCsv.SplitCsv(line, delim);
            if (f.Count < header.Count - 2) continue;
            // Rows without a valid timestamp are HWiNFO's repeated header / sensor-group footer.
            if (ParseTime(f[0], f.Count > 1 ? f[1] : "") is not { } ts) continue;
            first ??= ts;
            var s = new TelemetrySample { Timestamp = ts, Elapsed = (ts - first.Value).TotalSeconds, Phase = "import" };
            foreach (var (metric, (col, scale)) in map)
            {
                if (col < f.Count && double.TryParse(f[col].Replace(',', delim == ';' ? '.' : ','), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    s[metric] = v * scale;
            }
            session.Samples.Add(s);
        }
        if (session.Samples.Count == 0) throw new InvalidDataException("No parsable rows (unexpected date/time format?).");
        session.Start = session.Samples[0].Timestamp;
        session.End = session.Samples[^1].Timestamp;
        session.CompletedCleanly = true;
        session.Phases.Add(new PhaseResult
        {
            Name = "HWiNFO log", Kind = PhaseKind.Monitor, Start = session.Start, End = session.End, Completed = true,
        });
        return session;
    }

    internal static Dictionary<Metric, (int Column, double Scale)> MapColumns(IReadOnlyList<string> header)
    {
        var best = new Dictionary<Metric, (int Column, double Scale, int Priority)>();
        for (int i = 0; i < header.Count; i++)
        {
            var (name, unit) = SplitUnit(header[i]);
            foreach (var r in Rules)
            {
                if (!r.Pattern.IsMatch(name)) continue;
                if (r.Metric == Metric.FanRpm && !unit.Equals("RPM", StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Metric == Metric.CoreLoad && unit != "%") continue;
                double scale = unit.Equals("mV", StringComparison.OrdinalIgnoreCase) ? 0.001 : 1;
                // first matching column of the best priority wins (HWiNFO lists the primary GPU first)
                if (!best.TryGetValue(r.Metric, out var cur) || r.Priority < cur.Priority)
                    best[r.Metric] = (i, scale, r.Priority);
                break;
            }
        }
        return best.ToDictionary(kv => kv.Key, kv => (kv.Value.Column, kv.Value.Scale));
    }

    private static (string Name, string Unit) SplitUnit(string h)
    {
        var m = Regex.Match(h.Trim(), @"^(.*?)\s*\[(.*?)\]\s*$");
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : (h.Trim(), "");
    }

    private static readonly string[] DateFormats =
        { "d.M.yyyy", "dd.MM.yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy" };

    private static DateTimeOffset? ParseTime(string date, string time)
    {
        date = date.Trim().Trim('"');
        time = time.Trim().Trim('"');
        if (!DateTime.TryParseExact(date, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        if (!TimeSpan.TryParseExact(time, new[] { @"h\:m\:s\.fff", @"h\:m\:s", @"hh\:mm\:ss\.fff", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var t)) return null;
        return new DateTimeOffset(d.Add(t), TimeZoneInfo.Local.GetUtcOffset(d.Add(t)));
    }

    private static string GuessGpuName(List<string> lines)
    {
        // HWiNFO's last line often lists sensor group names, e.g. "GPU [#0]: AMD Radeon RX 7900 XTX: ..."
        foreach (var l in Enumerable.Reverse(lines).Take(3))
        {
            var m = Regex.Match(l, @"GPU \[#\d+\]: ([^:,""]+)");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return "GPU from HWiNFO log";
    }
}
