using System.Text.Json;
using System.Text.Json.Nodes;

namespace GpuOcChecker.Core.Session;

public static class SessionStore
{
    public const string CrashedFile = "session.crashed";

    public static SessionData Load(string directory)
    {
        var sessionPath = Path.Combine(directory, SessionRecorder.SessionFile);
        var csvPath = Path.Combine(directory, SessionRecorder.TelemetryFile);
        SessionData s;
        if (File.Exists(sessionPath))
        {
            s = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(sessionPath), JsonOpts.Default)
                ?? throw new InvalidDataException("Empty session.json");
        }
        else
        {
            s = Recover(directory);
        }
        if (File.Exists(csvPath)) s.Samples = TelemetryCsv.Read(csvPath);
        if (!s.CompletedCleanly && s.Samples.Count > 0)
        {
            s.End = s.Samples[^1].Timestamp;
            foreach (var p in s.Phases.Where(p => p.End == default)) p.End = s.End;
        }
        return s;
    }

    /// <summary>Rebuilds a session that never finished (app or Windows crashed) from lock + journal.</summary>
    private static SessionData Recover(string directory)
    {
        var lockPath = Path.Combine(directory, SessionRecorder.LockFile);
        if (!File.Exists(lockPath)) lockPath = Path.Combine(directory, CrashedFile);
        var s = File.Exists(lockPath)
            ? JsonSerializer.Deserialize<SessionData>(File.ReadAllText(lockPath), JsonOpts.Default) ?? new SessionData()
            : new SessionData();
        s.CompletedCleanly = false;
        s.Phases = new List<PhaseResult>();

        var journal = Path.Combine(directory, SessionRecorder.JournalFile);
        if (!File.Exists(journal)) return s;

        var open = new Dictionary<string, PhaseResult>();
        foreach (var line in ReadLinesShared(journal))
        {
            JsonNode? n;
            try
            {
                n = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue; // torn write at the moment of the crash
            }
            if (n == null) continue;
            switch ((string?)n["t"])
            {
                case "phase_start":
                {
                    var p = new PhaseResult
                    {
                        Name = (string?)n["name"] ?? "",
                        Kind = Enum.TryParse<PhaseKind>((string?)n["kind"], out var k) ? k : PhaseKind.Monitor,
                        Start = DateTimeOffset.Parse((string)n["time"]!),
                    };
                    open[p.Name] = p;
                    s.Phases.Add(p);
                    break;
                }
                case "phase_end":
                {
                    var p = n["phase"].Deserialize<PhaseResult>(JsonOpts.Default);
                    if (p == null) break;
                    int i = s.Phases.FindIndex(x => x.Name == p.Name && x.End == default);
                    if (i >= 0) s.Phases[i] = p; else s.Phases.Add(p);
                    open.Remove(p.Name);
                    break;
                }
                case "error":
                {
                    var e = n["error"].Deserialize<ErrorEvent>(JsonOpts.Default);
                    var name = (string?)n["phase"] ?? "";
                    if (e != null && open.TryGetValue(name, out var p))
                    {
                        p.Errors.Add(e);
                        p.ErrorEvents++;
                        p.ErrorCount += e.Count;
                    }
                    break;
                }
                case "event":
                {
                    var e = n["event"].Deserialize<SystemEvent>(JsonOpts.Default);
                    if (e != null) s.Events.Add(e);
                    break;
                }
                case "note":
                    if ((string?)n["text"] is { } text) s.Notes.Add(text);
                    break;
            }
        }
        return s;
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? line;
        while ((line = r.ReadLine()) != null)
            if (line.Length > 0) yield return line;
    }

    /// <summary>Sessions that were still running when the app/PC died.</summary>
    public static List<string> FindUnfinished() =>
        Directory.EnumerateDirectories(AppPaths.Sessions)
            .Where(d => File.Exists(Path.Combine(d, SessionRecorder.LockFile)))
            .OrderBy(d => d)
            .ToList();

    public static void MarkReviewed(string directory)
    {
        var l = Path.Combine(directory, SessionRecorder.LockFile);
        var target = Path.Combine(directory, CrashedFile);
        if (File.Exists(l)) File.Move(l, target, overwrite: true);
    }

    public static List<string> ListSessions() =>
        Directory.EnumerateDirectories(AppPaths.Sessions).OrderByDescending(d => d).ToList();
}
