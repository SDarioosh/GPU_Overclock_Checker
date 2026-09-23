using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpuOcChecker.Core.Telemetry;

namespace GpuOcChecker.Core.Session;

/// <summary>
/// Writes telemetry and a journal to disk continuously (flushed to the physical disk every couple of
/// seconds), so that if the overclock hard-crashes Windows, the next launch can still diagnose it.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    public const string LockFile = "session.lock";
    public const string JournalFile = "journal.jsonl";
    public const string TelemetryFile = "telemetry.csv";
    public const string SessionFile = "session.json";

    private readonly object _gate = new();
    private readonly FileStream _csvStream;
    private readonly StreamWriter _csv;
    private readonly FileStream _journalStream;
    private readonly StreamWriter _journal;
    private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();
    private bool _closed;

    public SessionRecorder(SessionData meta, string? directory = null)
    {
        Directory = directory ?? Path.Combine(AppPaths.Sessions, $"{meta.Start:yyyy-MM-dd_HH-mm-ss}_{meta.Mode}");
        System.IO.Directory.CreateDirectory(Directory);

        File.WriteAllText(Path.Combine(Directory, LockFile), JsonSerializer.Serialize(meta, JsonOpts.Default));

        _csvStream = new FileStream(Path.Combine(Directory, TelemetryFile), FileMode.Create, FileAccess.Write, FileShare.Read);
        _csv = new StreamWriter(_csvStream, new UTF8Encoding(false));
        _csv.WriteLine(TelemetryCsv.Header);

        _journalStream = new FileStream(Path.Combine(Directory, JournalFile), FileMode.Create, FileAccess.Write, FileShare.Read);
        _journal = new StreamWriter(_journalStream, new UTF8Encoding(false));
        Flush(true);
    }

    public string Directory { get; }

    public void AddSample(TelemetrySample s)
    {
        lock (_gate)
        {
            if (_closed) return;
            _csv.WriteLine(TelemetryCsv.Format(s));
            if (_sinceFlush.ElapsedMilliseconds > 2000) Flush(true);
        }
    }

    public void PhaseStarted(PhaseResult p) =>
        Journal(new JsonObject { ["t"] = "phase_start", ["time"] = p.Start.ToString("o"), ["name"] = p.Name, ["kind"] = p.Kind.ToString() });

    public void PhaseEnded(PhaseResult p) =>
        Journal(new JsonObject { ["t"] = "phase_end", ["phase"] = JsonSerializer.SerializeToNode(p, JsonOpts.Line) });

    public void Error(PhaseResult p, ErrorEvent e) =>
        Journal(new JsonObject { ["t"] = "error", ["phase"] = p.Name, ["kind"] = p.Kind.ToString(), ["error"] = JsonSerializer.SerializeToNode(e, JsonOpts.Line) });

    public void Event(SystemEvent e) =>
        Journal(new JsonObject { ["t"] = "event", ["event"] = JsonSerializer.SerializeToNode(e, JsonOpts.Line) });

    public void Note(string text) =>
        Journal(new JsonObject { ["t"] = "note", ["time"] = DateTimeOffset.Now.ToString("o"), ["text"] = text });

    private void Journal(JsonObject o)
    {
        lock (_gate)
        {
            if (_closed) return;
            _journal.WriteLine(o.ToJsonString(JsonOpts.Line));
            Flush(true); // journal entries are rare and precious
        }
    }

    private void Flush(bool toDisk)
    {
        _csv.Flush();
        _journal.Flush();
        if (toDisk)
        {
            _csvStream.Flush(true);
            _journalStream.Flush(true);
        }
        _sinceFlush.Restart();
    }

    /// <summary>Writes the final session.json and removes the crash lock.</summary>
    public void Complete(SessionData session)
    {
        lock (_gate)
        {
            if (_closed) return;
            session.CompletedCleanly = true;
            File.WriteAllText(Path.Combine(Directory, SessionFile), JsonSerializer.Serialize(session, JsonOpts.Default));
            Close();
            File.Delete(Path.Combine(Directory, LockFile));
        }
    }

    private void Close()
    {
        if (_closed) return;
        Flush(true);
        _csv.Dispose();
        _journal.Dispose();
        _closed = true;
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }
}
