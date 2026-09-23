using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using GpuOcChecker.Core.Session;

namespace GpuOcChecker.Core.Events;

/// <summary>
/// Reads and watches the Windows event log for the traces an unstable GPU leaves behind:
/// driver timeouts (TDR, "Display" 4101 / LiveKernelEvent 141/117), AMD/NVIDIA driver errors,
/// WHEA hardware errors and unexpected reboots (Kernel-Power 41).
/// </summary>
public sealed class GpuEventLog : IDisposable
{
    private readonly List<IDisposable> _watchers = new();

    public static bool IsSupported => OperatingSystem.IsWindows();

    private const string SystemFilter =
        "(Provider[@Name='Display'] and EventID=4101)" +
        " or (Provider[@Name='amdkmdag'] and (Level=1 or Level=2 or Level=3))" +
        " or (Provider[@Name='amdwddmg'] and (Level=1 or Level=2 or Level=3))" +
        " or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2 or Level=3))" +
        " or (Provider[@Name='igfxn'] and (Level=1 or Level=2))" +
        " or Provider[@Name='Microsoft-Windows-WHEA-Logger']" +
        " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)" +
        " or (Provider[@Name='EventLog'] and EventID=6008)";

    private const string ApplicationFilter = "(Provider[@Name='Windows Error Reporting'] and EventID=1001)";

    public static List<SystemEvent> Query(DateTimeOffset from, DateTimeOffset? to = null)
    {
        var result = new List<SystemEvent>();
        if (!OperatingSystem.IsWindows()) return result;
        result.AddRange(QueryLog("System", SystemFilter, from, to));
        result.AddRange(QueryLog("Application", ApplicationFilter, from, to));
        return result.OrderBy(e => e.Time).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SystemEvent> QueryLog(string log, string filter, DateTimeOffset from, DateTimeOffset? to)
    {
        string time = $"TimeCreated[@SystemTime>='{from.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}'";
        if (to is { } t) time += $" and @SystemTime<='{t.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}'";
        time += "]";
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[({filter}) and {time}]]");
        var list = new List<SystemEvent>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    var e = Convert(rec, log);
                    if (e != null) list.Add(e);
                }
            }
        }
        catch (EventLogException)
        {
            // Log unavailable or query rejected: return what we have.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return list;
    }

    /// <summary>Starts raising <paramref name="onEvent"/> for matching events as they are written.</summary>
    public void Watch(Action<SystemEvent> onEvent)
    {
        if (OperatingSystem.IsWindows()) WatchWindows(onEvent);
    }

    [SupportedOSPlatform("windows")]
    private void WatchWindows(Action<SystemEvent> onEvent)
    {
        foreach (var (log, filter) in new[] { ("System", SystemFilter), ("Application", ApplicationFilter) })
        {
            try
            {
                var w = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, $"*[System[{filter}]]"));
                w.EventRecordWritten += (_, args) =>
                {
                    if (args.EventRecord is not { } rec) return;
                    using (rec)
                    {
                        var e = Convert(rec, log);
                        if (e != null) onEvent(e);
                    }
                };
                w.Enabled = true;
                _watchers.Add(w);
            }
            catch (Exception)
            {
                // Watching is best-effort; the post-run Query() still catches everything.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static SystemEvent? Convert(EventRecord rec, string log)
    {
        string message;
        try
        {
            message = rec.FormatDescription() ?? "";
        }
        catch (Exception)
        {
            message = "";
        }
        if (string.IsNullOrWhiteSpace(message))
            message = string.Join(" | ", rec.Properties.Select(p => p.Value?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));

        var e = new SystemEvent
        {
            Time = rec.TimeCreated is DateTime dt ? new DateTimeOffset(dt) : DateTimeOffset.Now,
            Log = log,
            Source = rec.ProviderName ?? "",
            EventId = rec.Id,
            Level = rec.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" },
            Message = Collapse(message),
        };
        e.Category = Categorize(e);
        return e.Category == EventCategory.Other && e.Source == "Windows Error Reporting" ? null : e;
    }

    private static string Collapse(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 600 ? s[..600] + "…" : s;
    }

    public static EventCategory Categorize(SystemEvent e)
    {
        switch (e.Source)
        {
            case "Display" when e.EventId == 4101:
                return EventCategory.DriverTimeout;
            case "amdkmdag" or "amdwddmg" or "nvlddmkm" or "igfxn":
                return EventCategory.DriverError;
            case "Microsoft-Windows-WHEA-Logger":
                return EventCategory.Whea;
            case "Microsoft-Windows-Kernel-Power" or "EventLog":
                return EventCategory.UnexpectedShutdown;
            case "Windows Error Reporting":
                // LiveKernelEvent 141 = GPU engine hang, 117 = TDR, 1a1/1a8 = display watchdog
                var m = Regex.Match(e.Message, @"LiveKernelEvent.*?\b(141|117|1a1|1a8|193)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return m.Success ? EventCategory.DriverTimeout : EventCategory.Other;
            default:
                return EventCategory.Other;
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
