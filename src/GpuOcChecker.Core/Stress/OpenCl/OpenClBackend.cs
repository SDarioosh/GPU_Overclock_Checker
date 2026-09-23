using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GpuOcChecker.Core.Telemetry;
using static GpuOcChecker.Core.Stress.OpenCl.ClNative;

namespace GpuOcChecker.Core.Stress.OpenCl;

public sealed record ClDeviceInfo(int Index, string Name, string BoardName, string Vendor, string Platform, bool IsGpu,
    long GlobalMem, long MaxAlloc, int ComputeUnits, string Driver)
{
    public string DisplayName => string.IsNullOrWhiteSpace(BoardName) ? Name : $"{BoardName} [{Name}]";
}

public sealed class OpenClBackend : IStressBackend
{
    private const uint Seed = 0xC0FFEE11;
    private const int FlopsPerItemIter = 8 * 16 * 3;

    private readonly ClDeviceInfo _info;
    private readonly IntPtr _device;
    private IntPtr _context, _queue, _program;
    private IntPtr _kCompute, _kFill, _kInvert, _kCheck, _kCopy;
    private IntPtr _outBuf, _errBuf;
    private readonly List<(IntPtr Buf, long Bytes, long BaseIndex)> _chunks = new();
    private uint[] _reference = Array.Empty<uint>();
    private uint[] _result = Array.Empty<uint>();
    private int _heavyItems, _lightItems;
    private uint _iters = 64;
    private Action<string> _log = _ => { };

    private OpenClBackend(ClDeviceInfo info, IntPtr device)
    {
        _info = info;
        _device = device;
    }

    public string DeviceName => _info.DisplayName;
    public string Description => $"{_info.Platform} / driver {_info.Driver} / {_info.ComputeUnits} CUs / {_info.GlobalMem / (1L << 20)} MiB";
    public long VramBytesUnderTest => _chunks.Sum(c => c.Bytes);

    // ---------------------------------------------------------------- device discovery

    public static List<ClDeviceInfo> ListDevices() => Enumerate().Select(d => d.Info).ToList();

    private static List<(ClDeviceInfo Info, IntPtr Device)> Enumerate()
    {
        NativeLibraries.EnsureRegistered();
        var list = new List<(ClDeviceInfo, IntPtr)>();
        if (clGetPlatformIDs(0, null, out uint np) != CL_SUCCESS || np == 0) return list;
        var platforms = new IntPtr[np];
        clGetPlatformIDs(np, platforms, out _);
        int index = 0;
        foreach (var p in platforms)
        {
            string pname = PlatformString(p, CL_PLATFORM_NAME);
            if (clGetDeviceIDs(p, CL_DEVICE_TYPE_ALL, 0, null, out uint nd) != CL_SUCCESS || nd == 0) continue;
            var devs = new IntPtr[nd];
            clGetDeviceIDs(p, CL_DEVICE_TYPE_ALL, nd, devs, out _);
            foreach (var d in devs)
            {
                ulong type = DeviceULong(d, CL_DEVICE_TYPE);
                list.Add((new ClDeviceInfo(index++,
                    DeviceString(d, CL_DEVICE_NAME).Trim(),
                    DeviceString(d, CL_DEVICE_BOARD_NAME_AMD).Trim(),
                    DeviceString(d, CL_DEVICE_VENDOR).Trim(),
                    pname.Trim(),
                    (type & CL_DEVICE_TYPE_GPU) != 0,
                    (long)DeviceULong(d, CL_DEVICE_GLOBAL_MEM_SIZE),
                    (long)DeviceULong(d, CL_DEVICE_MAX_MEM_ALLOC_SIZE),
                    (int)DeviceUInt(d, CL_DEVICE_MAX_COMPUTE_UNITS),
                    DeviceString(d, CL_DRIVER_VERSION).Trim()), d));
            }
        }
        return list;
    }

    /// <summary>Picks the device matching the monitored GPU, otherwise the GPU with the most memory.</summary>
    public static OpenClBackend Create(StressSetup setup)
    {
        var devices = Enumerate();
        if (devices.Count == 0)
            throw new InvalidOperationException("No OpenCL devices found. Install/repair the GPU driver (AMD Software includes OpenCL).");

        if (setup.DeviceIndex is int idx)
        {
            var d = devices.FirstOrDefault(x => x.Info.Index == idx);
            if (d.Device == IntPtr.Zero) throw new ArgumentException($"OpenCL device {idx} does not exist.");
            return new OpenClBackend(d.Info, d.Device);
        }

        var pick = devices
            .OrderByDescending(x => NameMatchScore(x.Info, setup.PreferName))
            .ThenByDescending(x => x.Info.IsGpu)
            .ThenByDescending(x => x.Info.GlobalMem)
            .First();
        return new OpenClBackend(pick.Info, pick.Device);
    }

    internal static int NameMatchScore(ClDeviceInfo d, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return 0;
        string Norm(string s) => new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var p = Norm(preferred.Replace("(simulated)", ""));
        foreach (var candidate in new[] { d.BoardName, d.Name })
        {
            var c = Norm(candidate);
            if (c.Length == 0) continue;
            if (c == p) return 3;
            if (p.Contains(c) || c.Contains(p)) return 2;
        }
        return 0;
    }

    // ---------------------------------------------------------------- setup

    public void Initialize(StressSetup setup, Action<string> log)
    {
        _log = log;
        var devs = new[] { _device };
        _context = clCreateContext(IntPtr.Zero, 1, devs, IntPtr.Zero, IntPtr.Zero, out int err);
        Check(err, "clCreateContext");
        _queue = clCreateCommandQueue(_context, _device, 0, out err);
        Check(err, "clCreateCommandQueue");
        _program = clCreateProgramWithSource(_context, 1, new[] { ClKernels.Source }, IntPtr.Zero, out err);
        Check(err, "clCreateProgramWithSource");
        err = clBuildProgram(_program, 1, devs, "-cl-std=CL1.2", IntPtr.Zero, IntPtr.Zero);
        if (err != CL_SUCCESS)
            throw new InvalidOperationException($"OpenCL kernel build failed ({ErrorName(err)}):\n{BuildLog()}");

        _kCompute = Kernel("compute_check");
        _kFill = Kernel("vram_fill");
        _kInvert = Kernel("vram_invert");
        _kCheck = Kernel("vram_check");
        _kCopy = Kernel("bw_copy");

        _errBuf = Buffer(4 * 64);

        if (setup.NeedCompute) SetupCompute(setup);
        if (setup.NeedVram) SetupVram(setup);
    }

    private void SetupCompute(StressSetup setup)
    {
        int cus = Math.Max(1, _info.ComputeUnits);
        _heavyItems = _info.IsGpu ? cus * 2048 : cus * 256;
        _lightItems = _info.IsGpu ? Math.Max(64, cus / 4 * 64) : Math.Max(16, cus * 4);
        _outBuf = Buffer(_heavyItems * 4L);
        _reference = new uint[_heavyItems];
        _result = new uint[_heavyItems];

        // Calibrate the iteration count so one heavy dispatch takes ~TargetDispatchMs.
        _iters = 16;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            double secs = Dispatch(_heavyItems, _result);
            double scale = setup.TargetDispatchMs / 1000.0 / Math.Max(secs, 1e-5);
            if (scale is > 0.7 and < 1.4) break;
            _iters = (uint)Math.Clamp(_iters * Math.Min(scale, 16), 4, 1 << 20);
        }

        // Reference = majority of three runs, so a glitch during calibration can't poison it.
        var a = new uint[_heavyItems];
        var b = new uint[_heavyItems];
        var c = new uint[_heavyItems];
        Dispatch(_heavyItems, a);
        Dispatch(_heavyItems, b);
        Dispatch(_heavyItems, c);
        int disagreements = 0;
        for (int i = 0; i < _heavyItems; i++)
        {
            if (a[i] == b[i] || a[i] == c[i]) _reference[i] = a[i];
            else if (b[i] == c[i]) _reference[i] = b[i];
            else _reference[i] = a[i];
            if (a[i] != b[i] || a[i] != c[i]) disagreements++;
        }
        if (disagreements > 0)
            _log($"Warning: {disagreements} result mismatches already during calibration - core is unstable at idle-to-load transition.");
        CalibrationMismatches = disagreements;
        _log($"Compute kernel calibrated: {_heavyItems} work-items x {_iters} iterations per dispatch.");
    }

    public long CalibrationMismatches { get; private set; }

    private void SetupVram(StressSetup setup)
    {
        long target = (long)(_info.GlobalMem * Math.Clamp(setup.VramFraction, 0.05, 0.9));
        // At least 4 buffers so bandwidth has a source and destination; each at most 512 MiB and
        // within the driver's max allocation size.
        long chunk = Math.Min(Math.Min(_info.MaxAlloc > 0 ? _info.MaxAlloc : 256L << 20, 512L << 20), target / 4);
        chunk = Math.Max(chunk - chunk % (1 << 20), 16L << 20);
        long baseIndex = 0;
        while (VramBytesUnderTest + chunk <= target && _chunks.Count < 96)
        {
            var buf = clCreateBuffer(_context, CL_MEM_READ_WRITE, (UIntPtr)chunk, IntPtr.Zero, out int err);
            if (err != CL_SUCCESS) break;
            // Buffers are allocated lazily; touch it now so an over-commit fails here, not mid-test.
            SetArg(_kFill, 0, buf);
            SetArg(_kFill, 1, 0u);
            SetArg(_kFill, 2, (ulong)baseIndex);
            err = Enqueue(_kFill, chunk / 16);
            if (err == CL_SUCCESS) err = clFinish(_queue);
            if (err != CL_SUCCESS)
            {
                clReleaseMemObject(buf);
                break;
            }
            _chunks.Add((buf, chunk, baseIndex));
            baseIndex += chunk / 16;
        }
        if (_chunks.Count < 2) throw new InvalidOperationException("Could not allocate VRAM for the memory tests.");
        _log($"VRAM test region: {VramBytesUnderTest / (1L << 20)} MiB in {_chunks.Count} buffers.");
    }

    // ---------------------------------------------------------------- workloads

    public ComputeRun RunCompute(bool heavy)
    {
        int n = heavy ? _heavyItems : _lightItems;
        double secs = Dispatch(n, _result);
        long mismatches = Compare(n);
        if (mismatches > 0)
        {
            // Tie-break: if an immediate re-run agrees with the bad result, the reference was the bad one.
            var confirm = new uint[n];
            Dispatch(n, confirm);
            bool refWasWrong = true;
            for (int i = 0; i < n && refWasWrong; i++)
                if (confirm[i] != _result[i]) refWasWrong = false;
            if (refWasWrong) Array.Copy(confirm, _reference, n);
        }
        return new ComputeRun(mismatches, secs, (double)n * _iters * FlopsPerItemIter / 1e9);
    }

    private long Compare(int n)
    {
        long m = 0;
        for (int i = 0; i < n; i++)
            if (_result[i] != _reference[i]) m++;
        return m;
    }

    public VramRun RunVramPass(uint pass)
    {
        var sw = Stopwatch.StartNew();
        long errors = 0;
        var detail = new StringBuilder();

        foreach (var c in _chunks)
        {
            SetArg(_kFill, 0, c.Buf);
            SetArg(_kFill, 1, pass);
            SetArg(_kFill, 2, (ulong)c.BaseIndex);
            Check(Enqueue(_kFill, c.Bytes / 16), "vram_fill");
        }
        Check(clFinish(_queue), "clFinish(vram_fill)");

        for (uint inverted = 0; inverted <= 1; inverted++)
        {
            foreach (var c in _chunks)
            {
                if (inverted == 1)
                {
                    SetArg(_kInvert, 0, c.Buf);
                    Check(Enqueue(_kInvert, c.Bytes / 16), "vram_invert");
                }
                errors += CheckChunk(c, pass, inverted, detail);
            }
        }
        long bytes = VramBytesUnderTest * 5; // write + read + read/write + read
        return new VramRun(errors, sw.Elapsed.TotalSeconds, bytes, detail.Length > 0 ? detail.ToString() : null);
    }

    private long CheckChunk((IntPtr Buf, long Bytes, long BaseIndex) c, uint pass, uint inverted, StringBuilder detail)
    {
        var zero = new uint[33];
        Write(_errBuf, zero);
        SetArg(_kCheck, 0, c.Buf);
        SetArg(_kCheck, 1, pass);
        SetArg(_kCheck, 2, (ulong)c.BaseIndex);
        SetArg(_kCheck, 3, inverted);
        SetArg(_kCheck, 4, _errBuf);
        Check(Enqueue(_kCheck, c.Bytes / 16), "vram_check");
        var errs = new uint[33];
        Read(_errBuf, errs);
        if (errs[0] > 0 && detail.Length < 600)
        {
            int shown = (int)Math.Min(errs[0], 4);
            for (int i = 0; i < shown; i++)
            {
                long byteAddr = (long)errs[1 + i * 2] * 16;
                detail.Append($"@{byteAddr / (1 << 20)}MiB bits 0x{errs[2 + i * 2]:X8} (pattern {pass & 3}{(inverted == 1 ? ", inverted" : "")}); ");
            }
        }
        return errs[0];
    }

    public BandwidthRun RunBandwidth()
    {
        var src = _chunks[0];
        var dst = _chunks[1];
        long items = Math.Min(src.Bytes, dst.Bytes) / 16;
        SetArg(_kCopy, 0, src.Buf);
        SetArg(_kCopy, 1, dst.Buf);
        // Warm-up so clocks ramp before timing.
        Check(Enqueue(_kCopy, items), "bw_copy");
        Check(clFinish(_queue), "clFinish(bw_copy)");
        const int reps = 8;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) Check(Enqueue(_kCopy, items), "bw_copy");
        Check(clFinish(_queue), "clFinish(bw_copy)");
        return new BandwidthRun(sw.Elapsed.TotalSeconds, items * 16 * 2 * reps);
    }

    // ---------------------------------------------------------------- helpers

    private double Dispatch(int items, uint[] into)
    {
        SetArg(_kCompute, 0, _outBuf);
        SetArg(_kCompute, 1, _iters);
        SetArg(_kCompute, 2, Seed);
        var sw = Stopwatch.StartNew();
        Check(Enqueue(_kCompute, items), "compute_check");
        Check(clFinish(_queue), "clFinish(compute_check)");
        double secs = sw.Elapsed.TotalSeconds;
        Read(_outBuf, into, items);
        return secs;
    }

    private int Enqueue(IntPtr kernel, long globalItems)
    {
        var global = new[] { (UIntPtr)(ulong)globalItems };
        return clEnqueueNDRangeKernel(_queue, kernel, 1, null, global, null, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private void Read(IntPtr buf, uint[] into, int count = -1)
    {
        if (count < 0) count = into.Length;
        var h = GCHandle.Alloc(into, GCHandleType.Pinned);
        try
        {
            Check(clEnqueueReadBuffer(_queue, buf, 1, UIntPtr.Zero, (UIntPtr)(count * 4L), h.AddrOfPinnedObject(), 0, IntPtr.Zero, IntPtr.Zero), "clEnqueueReadBuffer");
        }
        finally
        {
            h.Free();
        }
    }

    private void Write(IntPtr buf, uint[] data)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            Check(clEnqueueWriteBuffer(_queue, buf, 1, UIntPtr.Zero, (UIntPtr)(data.Length * 4L), h.AddrOfPinnedObject(), 0, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer");
        }
        finally
        {
            h.Free();
        }
    }

    private IntPtr Kernel(string name)
    {
        var k = clCreateKernel(_program, name, out int err);
        Check(err, $"clCreateKernel({name})");
        return k;
    }

    private IntPtr Buffer(long bytes)
    {
        var b = clCreateBuffer(_context, CL_MEM_READ_WRITE, (UIntPtr)bytes, IntPtr.Zero, out int err);
        Check(err, "clCreateBuffer");
        return b;
    }

    private static void SetArg(IntPtr k, uint i, IntPtr mem) => Check(clSetKernelArg(k, i, (UIntPtr)IntPtr.Size, ref mem), "clSetKernelArg");
    private static void SetArg(IntPtr k, uint i, uint v) => Check(clSetKernelArg(k, i, (UIntPtr)4, ref v), "clSetKernelArg");
    private static void SetArg(IntPtr k, uint i, ulong v) => Check(clSetKernelArg(k, i, (UIntPtr)8, ref v), "clSetKernelArg");

    private static void Check(int err, string what)
    {
        if (err != CL_SUCCESS) throw new ClException(what, err);
    }

    private string BuildLog()
    {
        clGetProgramBuildInfo(_program, _device, CL_PROGRAM_BUILD_LOG, UIntPtr.Zero, null, out var size);
        var buf = new byte[(int)size];
        clGetProgramBuildInfo(_program, _device, CL_PROGRAM_BUILD_LOG, size, buf, out _);
        return Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    private static string PlatformString(IntPtr p, uint param)
    {
        if (clGetPlatformInfo(p, param, UIntPtr.Zero, null, out var size) != CL_SUCCESS) return "";
        var buf = new byte[(int)size];
        clGetPlatformInfo(p, param, size, buf, out _);
        return Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    private static string DeviceString(IntPtr d, uint param)
    {
        if (clGetDeviceInfo(d, param, UIntPtr.Zero, null, out var size) != CL_SUCCESS || (ulong)size == 0) return "";
        var buf = new byte[(int)size];
        clGetDeviceInfo(d, param, size, buf, out _);
        return Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    private static ulong DeviceULong(IntPtr d, uint param)
    {
        var buf = new byte[8];
        return clGetDeviceInfo(d, param, (UIntPtr)8, buf, out _) == CL_SUCCESS ? BitConverter.ToUInt64(buf) : 0;
    }

    private static uint DeviceUInt(IntPtr d, uint param)
    {
        var buf = new byte[4];
        return clGetDeviceInfo(d, param, (UIntPtr)4, buf, out _) == CL_SUCCESS ? BitConverter.ToUInt32(buf) : 0;
    }

    public void Dispose()
    {
        // After a device loss these may fail; that's fine, the process is about to report and exit.
        try
        {
            foreach (var c in _chunks) clReleaseMemObject(c.Buf);
            _chunks.Clear();
            foreach (var m in new[] { _outBuf, _errBuf }) if (m != IntPtr.Zero) clReleaseMemObject(m);
            foreach (var k in new[] { _kCompute, _kFill, _kInvert, _kCheck, _kCopy }) if (k != IntPtr.Zero) clReleaseKernel(k);
            if (_program != IntPtr.Zero) clReleaseProgram(_program);
            if (_queue != IntPtr.Zero) clReleaseCommandQueue(_queue);
            if (_context != IntPtr.Zero) clReleaseContext(_context);
        }
        catch
        {
            // ignored
        }
        _program = _queue = _context = IntPtr.Zero;
    }
}
