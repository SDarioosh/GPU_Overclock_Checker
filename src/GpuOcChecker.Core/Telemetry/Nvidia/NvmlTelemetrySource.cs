using System.Runtime.InteropServices;
using System.Text;

namespace GpuOcChecker.Core.Telemetry.Nvidia;

/// <summary>GeForce/RTX telemetry through NVML (nvml.dll ships with the NVIDIA driver). NVML exposes no core voltage.</summary>
public sealed class NvmlTelemetrySource : ITelemetrySource
{
    private static int _initRefs;
    private static readonly object InitLock = new();
    private readonly IntPtr _device;
    private bool _disposed;

    private NvmlTelemetrySource(IntPtr device, GpuInfo gpu)
    {
        _device = device;
        Gpu = gpu;
    }

    public GpuInfo Gpu { get; }

    public static List<ITelemetrySource> Discover(Action<string>? log = null)
    {
        var result = new List<ITelemetrySource>();
        NativeLibraries.EnsureRegistered();
        try
        {
            if (!AddInitRef()) return result;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            log?.Invoke($"NVML not available: {e.Message}");
            return result;
        }

        try
        {
            if (Native.nvmlDeviceGetCount_v2(out uint count) != 0) return result;
            for (uint i = 0; i < count; i++)
            {
                if (Native.nvmlDeviceGetHandleByIndex_v2(i, out var dev) != 0) continue;
                var name = new byte[96];
                Native.nvmlDeviceGetName(dev, name, (uint)name.Length);
                var n = Encoding.ASCII.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0))).Trim();
                if (n.Length == 0) n = $"NVIDIA GPU {i}";
                AddInitRef();
                result.Add(new NvmlTelemetrySource(dev,
                    new GpuInfo(GpuVendor.Nvidia, n, $"nvidia-{i}-{Sanitize(n)}", "NVIDIA NVML")));
            }
        }
        finally
        {
            ReleaseInitRef();
        }
        return result;
    }

    private static string Sanitize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private static bool AddInitRef()
    {
        lock (InitLock)
        {
            if (_initRefs == 0 && Native.nvmlInit_v2() != 0) return false;
            _initRefs++;
            return true;
        }
    }

    private static void ReleaseInitRef()
    {
        lock (InitLock)
        {
            if (--_initRefs == 0) Native.nvmlShutdown();
        }
    }

    public TelemetrySample Read()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NvmlTelemetrySource));
        var s = new TelemetrySample { Timestamp = DateTimeOffset.Now };

        if (Native.nvmlDeviceGetClockInfo(_device, 0, out uint gfx) == 0) s[Metric.CoreClock] = gfx;
        else throw new InvalidOperationException("nvmlDeviceGetClockInfo failed (GPU lost or driver resetting?)");
        if (Native.nvmlDeviceGetClockInfo(_device, 2, out uint mem) == 0) s[Metric.MemClock] = mem;
        if (Native.nvmlDeviceGetTemperature(_device, 0, out uint t) == 0) s[Metric.EdgeTemp] = t;
        if (Native.nvmlDeviceGetPowerUsage(_device, out uint mw) == 0) s[Metric.Power] = mw / 1000.0;
        if (Native.nvmlDeviceGetEnforcedPowerLimit(_device, out uint lim) == 0) s[Metric.PowerLimit] = lim / 1000.0;
        if (Native.nvmlDeviceGetUtilizationRates(_device, out var u) == 0)
        {
            s[Metric.CoreLoad] = u.Gpu;
            s[Metric.MemLoad] = u.Memory;
        }
        if (Native.nvmlDeviceGetFanSpeed(_device, out uint fan) == 0) s[Metric.FanPercent] = fan;
        try
        {
            if (Native.nvmlDeviceGetPcieReplayCounter(_device, out uint replays) == 0) s[Metric.PcieReplays] = replays;
            if (Native.nvmlDeviceGetCurrentClocksThrottleReasons(_device, out ulong reasons) == 0)
                s.Throttle = MapThrottle(reasons);
        }
        catch (EntryPointNotFoundException)
        {
            // Optional on older/newer NVML builds.
        }
        return s;
    }

    internal static ThrottleFlags MapThrottle(ulong r)
    {
        var f = ThrottleFlags.None;
        if ((r & 0x1) != 0) f |= ThrottleFlags.Idle;
        if ((r & (0x4 | 0x80)) != 0) f |= ThrottleFlags.Power;          // SW power cap, HW power brake
        if ((r & (0x20 | 0x40)) != 0) f |= ThrottleFlags.Thermal;       // SW/HW thermal slowdown
        if ((r & 0x8) != 0) f |= ThrottleFlags.Hardware;                // HW slowdown (PSU/thermal/power brake)
        if ((r & (0x2 | 0x10 | 0x100)) != 0) f |= ThrottleFlags.Other;
        return f;
    }

    public IReadOnlyList<RawSensor> ReadRaw()
    {
        var s = Read();
        var list = Metrics.All.Where(m => s.Has(m.Metric))
            .Select(m => new RawSensor(m.Label, s[m.Metric]!.Value, m.Unit)).ToList();
        if (Native.nvmlDeviceGetCurrentClocksThrottleReasons(_device, out ulong r) == 0)
            list.Add(new RawSensor("Throttle reasons (bitmask)", r, "hex"));
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseInitRef();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private static class Native
    {
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlInit_v2();
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlShutdown();
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetPcieReplayCounter(IntPtr device, out uint count);
        [DllImport(NativeLibraries.Nvml)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
    }
}
