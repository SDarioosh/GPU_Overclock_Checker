using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuOcChecker.Core;

public static class AppPaths
{
    private static string? _root;

    /// <summary>Documents\GpuOcChecker (override with GPUOC_HOME).</summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            var env = Environment.GetEnvironmentVariable("GPUOC_HOME");
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _root = !string.IsNullOrEmpty(env) ? env : Path.Combine(docs, "GpuOcChecker");
            Directory.CreateDirectory(_root);
            return _root;
        }
        set => _root = value;
    }

    public static string Sessions => Ensure(Path.Combine(Root, "sessions"));
    public static string Baselines => Ensure(Path.Combine(Root, "baselines"));
    public static string MemTune => Ensure(Path.Combine(Root, "memtune"));
    public static string ThresholdsFile => Path.Combine(Root, "thresholds.json");

    private static string Ensure(string p)
    {
        Directory.CreateDirectory(p);
        return p;
    }
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static readonly JsonSerializerOptions Line = new(Default) { WriteIndented = false };
}
