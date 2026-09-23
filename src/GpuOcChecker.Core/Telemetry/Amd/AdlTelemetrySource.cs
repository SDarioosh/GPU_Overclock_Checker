using System.Runtime.InteropServices;
using System.Text;
using static GpuOcChecker.Core.Telemetry.Amd.AdlNative;

namespace GpuOcChecker.Core.Telemetry.Amd;

/// <summary>Radeon telemetry through ADL PMLog (RDNA1/2/3/4 incl. RX 7900 XTX).</summary>
public sealed class AdlTelemetrySource : ITelemetrySource
{
    private readonly AdlContext _ctx;
    private readonly int _adapterIndex;
    private readonly IntPtr _buffer;
    private bool _disposed;

    private AdlTelemetrySource(AdlContext ctx, int adapterIndex, GpuInfo gpu)
    {
        _ctx = ctx.AddRef();
        _adapterIndex = adapterIndex;
        Gpu = gpu;
        _buffer = Marshal.AllocHGlobal(PmLogOutputSize);
    }

    public GpuInfo Gpu { get; }

    public static List<ITelemetrySource> Discover(Action<string>? log = null)
    {
        var result = new List<ITelemetrySource>();
        if (!OperatingSystem.IsWindows()) return result;

        NativeLibraries.EnsureRegistered();
        AdlContext ctx;
        try
        {
            ctx = AdlContext.Create();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            log?.Invoke($"ADL not available: {e.Message}");
            return result;
        }

        var adapters = ctx.EnumerateAdapters().ToList();
        if (adapters.Count == 0) log?.Invoke("ADL: driver reports no adapters.");
        var seenBus = new HashSet<string>();
        var failures = new List<string>();
        // Present adapters first; ADL lists one entry per display output of the same GPU.
        foreach (var a in adapters.OrderByDescending(x => x.Present))
        {
            if (!IsAmd(a.VendorId, a.Name))
            {
                failures.Add($"#{a.Index} '{a.Name}' skipped (vendor id {a.VendorId})");
                continue;
            }
            var busKey = $"{a.Bus}|{a.Pnp}";
            if (seenBus.Contains(busKey)) continue;

            var src = new AdlTelemetrySource(ctx, a.Index,
                new GpuInfo(GpuVendor.Amd, a.Name.Trim(), $"amd-bus{a.Bus}-{Sanitize(a.Name)}", "AMD ADL", a.Bus));
            try
            {
                // Only some of the per-output entries answer PMLog queries.
                int rc = src.Query();
                if (rc != ADL_OK)
                {
                    failures.Add($"#{a.Index} '{a.Name}' bus {a.Bus}: PMLog query returned {rc}");
                    src.Dispose();
                    continue;
                }
            }
            catch (Exception e)
            {
                failures.Add($"#{a.Index} '{a.Name}': PMLog query threw {e.GetType().Name}: {e.Message}");
                src.Dispose();
                continue;
            }
            seenBus.Add(busKey);
            result.Add(src);
        }
        if (result.Count == 0)
            foreach (var f in failures.Distinct().Take(12)) log?.Invoke("ADL: " + f);
        ctx.Release(); // discovery's own reference; each source holds one
        return result;
    }

    private static string Sanitize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>ADL's vendor id is documented as 0x1002 but some drivers report the decimal value 1002.</summary>
    internal static bool IsAmd(int vendorId, string name) =>
        vendorId is AmdVendorId or 1002 ||
        name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase);

    private int Query()
    {
        for (int i = 0; i < PmLogOutputSize; i += 4) Marshal.WriteInt32(_buffer, i, 0);
        return ADL2_New_QueryPMLogData_Get(_ctx.Handle, _adapterIndex, _buffer);
    }

    private bool TryQuery() => Query() == ADL_OK;

    private Dictionary<int, int> QueryAll()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdlTelemetrySource));
        if (!TryQuery()) throw new InvalidOperationException("ADL2_New_QueryPMLogData_Get failed (driver reset in progress?)");
        var d = new Dictionary<int, int>();
        for (int i = 0; i < PmLogMaxSensors; i++)
        {
            int supported = Marshal.ReadInt32(_buffer, 4 + i * 8);
            if (supported == 0) continue;
            d[i] = Marshal.ReadInt32(_buffer, 4 + i * 8 + 4);
        }
        return d;
    }

    public TelemetrySample Read()
    {
        var d = QueryAll();
        var s = new TelemetrySample { Timestamp = DateTimeOffset.Now };

        double? Get(PmLog k) => d.TryGetValue((int)k, out var v) ? v : null;
        double? Positive(PmLog k) => Get(k) is double v && v > 0 ? v : null;
        double? Mv(PmLog k) => Positive(k) / 1000.0;

        s[Metric.CoreClock] = Get(PmLog.ClkGfx);
        s[Metric.MemClock] = Get(PmLog.ClkMem);
        s[Metric.SocClock] = Get(PmLog.ClkSoc);
        s[Metric.CoreVoltage] = Mv(PmLog.GfxVoltage);
        s[Metric.MemVoltage] = Mv(PmLog.MemVoltage);
        s[Metric.SocVoltage] = Mv(PmLog.SocVoltage);
        s[Metric.EdgeTemp] = Positive(PmLog.TempEdge) ?? Positive(PmLog.TempGfx);
        s[Metric.HotspotTemp] = Positive(PmLog.TempHotspot) ?? Positive(PmLog.TempHotspotGcd);
        s[Metric.MemTemp] = Positive(PmLog.TempMem);
        s[Metric.VrTemp] = Positive(PmLog.TempVrVddc) ?? Positive(PmLog.TempVrSoc);
        // BOARD_POWER only exists on newer drivers; ASIC_POWER is Adrenalin's "Total Board Power" on RDNA3.
        s[Metric.Power] = Positive(PmLog.BoardPower) ?? Positive(PmLog.AsicPower);
        s[Metric.CoreLoad] = Get(PmLog.ActivityGfx);
        s[Metric.MemLoad] = Get(PmLog.ActivityMem);
        s[Metric.FanRpm] = Get(PmLog.FanRpm);
        s[Metric.FanPercent] = Get(PmLog.FanPercent);
        return s;
    }

    public IReadOnlyList<RawSensor> ReadRaw() =>
        QueryAll().OrderBy(kv => kv.Key)
            .Select(kv => new RawSensor($"{kv.Key,3} {SensorName(kv.Key)}", kv.Value, SensorUnit(kv.Key)))
            .ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.FreeHGlobal(_buffer);
        _ctx.Release();
    }

    /// <summary>Ref-counted ADL context shared by all adapters.</summary>
    private sealed class AdlContext
    {
        // Must stay reachable for the life of the context; ADL calls back into it.
        private static readonly AdlMallocCallback Malloc = size => Marshal.AllocHGlobal(size);
        private int _refs;

        public IntPtr Handle { get; private set; }

        public static AdlContext Create()
        {
            int rc = ADL2_Main_Control_Create(Malloc, 1, out var h);
            if (rc != ADL_OK) throw new InvalidOperationException($"ADL2_Main_Control_Create returned {rc}");
            return new AdlContext { Handle = h, _refs = 1 };
        }

        public IEnumerable<(int Index, int Bus, int VendorId, bool Present, string Name, string Pnp)> EnumerateAdapters()
        {
            if (ADL2_Adapter_NumberOfAdapters_Get(Handle, out int n) != ADL_OK || n <= 0) yield break;
            int size = n * AdapterInfoSize;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var zero = new byte[size];
                Marshal.Copy(zero, 0, buf, size);
                if (ADL2_Adapter_AdapterInfo_Get(Handle, buf, size) != ADL_OK) yield break;
                var bytes = new byte[size];
                Marshal.Copy(buf, bytes, 0, size);
                for (int i = 0; i < n; i++)
                {
                    int o = i * AdapterInfoSize;
                    yield return (
                        BitConverter.ToInt32(bytes, o + OffAdapterIndex),
                        BitConverter.ToInt32(bytes, o + OffBusNumber),
                        BitConverter.ToInt32(bytes, o + OffVendorId),
                        BitConverter.ToInt32(bytes, o + OffPresent) != 0,
                        CString(bytes, o + OffAdapterName),
                        CString(bytes, o + OffPnpString));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static string CString(byte[] b, int offset)
        {
            int end = Array.IndexOf(b, (byte)0, offset, MaxPath);
            if (end < 0) end = offset + MaxPath;
            return Encoding.ASCII.GetString(b, offset, end - offset);
        }

        public AdlContext AddRef() { Interlocked.Increment(ref _refs); return this; }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) > 0 || Handle == IntPtr.Zero) return;
            ADL2_Main_Control_Destroy(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
