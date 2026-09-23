using System.Reflection;
using System.Runtime.InteropServices;

namespace GpuOcChecker.Core.Telemetry;

/// <summary>
/// Maps the logical library names used in [DllImport] to the per-OS file names, so the same
/// P/Invoke declarations work on Windows (the real target) and on Linux (used for development/CI).
/// </summary>
internal static class NativeLibraries
{
    public const string OpenCl = "gpuoc_opencl";
    public const string Nvml = "gpuoc_nvml";
    public const string Adl = "gpuoc_adl";

    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        foreach (var candidate in Candidates(name))
        {
            if (NativeLibrary.TryLoad(candidate, asm, path, out var h)) return h;
            if (Path.IsPathRooted(candidate) && NativeLibrary.TryLoad(candidate, out h)) return h;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        bool win = OperatingSystem.IsWindows();
        switch (name)
        {
            case OpenCl:
                if (win) { yield return "OpenCL.dll"; }
                else { yield return "libOpenCL.so.1"; yield return "libOpenCL.so"; }
                break;
            case Nvml:
                if (win)
                {
                    yield return "nvml.dll";
                    yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");
                    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    yield return Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll");
                }
                else { yield return "libnvidia-ml.so.1"; yield return "libnvidia-ml.so"; }
                break;
            case Adl:
                if (win) { yield return "atiadlxx.dll"; yield return "atiadlxy.dll"; }
                break;
            default:
                yield return name;
                break;
        }
    }
}
