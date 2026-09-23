using System.Runtime.InteropServices;

namespace GpuOcChecker.Core.Telemetry.Amd;

/// <summary>
/// Minimal bindings for AMD Display Library (atiadlxx.dll, installed with every Radeon driver).
/// The PMLog interface is what AMD Software (Adrenalin) itself uses for its performance overlay.
/// </summary>
internal static class AdlNative
{
    public const int ADL_OK = 0;
    public const int AmdVendorId = 0x1002;
    public const int MaxPath = 256;
    public const int PmLogMaxSensors = 256;

    // AdapterInfo (Windows layout) is parsed manually from a byte buffer to avoid marshaling surprises.
    public const int AdapterInfoSize = 1572;
    public const int OffAdapterIndex = 4;
    public const int OffBusNumber = 264;
    public const int OffVendorId = 276;
    public const int OffAdapterName = 280;
    public const int OffPresent = 792;
    public const int OffPnpString = 1312;

    // ADLPMLogDataOutput: int size; { int supported; int value; }[256]
    public const int PmLogOutputSize = 4 + PmLogMaxSensors * 8;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr AdlMallocCallback(int size);

    [DllImport(NativeLibraries.Adl, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ADL2_Main_Control_Create(AdlMallocCallback callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport(NativeLibraries.Adl, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport(NativeLibraries.Adl, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);

    [DllImport(NativeLibraries.Adl, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int inputSize);

    [DllImport(NativeLibraries.Adl, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapterIndex, IntPtr output);

    /// <summary>ADL_PMLOG_SENSORS indices (adl_defines.h).</summary>
    public enum PmLog
    {
        ClkGfx = 1,
        ClkMem = 2,
        ClkSoc = 3,
        TempEdge = 8,
        TempMem = 9,
        TempVrVddc = 10,
        TempVrMvdd = 11,
        TempLiquid = 12,
        TempPlx = 13,
        FanRpm = 14,
        FanPercent = 15,
        SocVoltage = 16,
        SocPower = 17,
        SocCurrent = 18,
        ActivityGfx = 19,
        ActivityMem = 20,
        GfxVoltage = 21,
        MemVoltage = 22,
        AsicPower = 23,
        TempVrSoc = 24,
        TempVrMvdd0 = 25,
        TempVrMvdd1 = 26,
        TempHotspot = 27,
        TempGfx = 28,
        TempSoc = 29,
        GfxPower = 30,
        GfxCurrent = 31,
        ThrottlerStatus = 35,
        BusSpeed = 40,
        BusLanes = 41,
        ClkFclk = 44,
        TempHotspotGcd = 50,
        TempHotspotMcd = 51,
        BoardPower = 73,
    }

    public static string SensorName(int index) =>
        Enum.IsDefined(typeof(PmLog), index) ? ((PmLog)index).ToString() : $"PMLog[{index}]";

    public static string SensorUnit(int index)
    {
        if (!Enum.IsDefined(typeof(PmLog), index)) return "";
        var name = ((PmLog)index).ToString();
        if (name.StartsWith("Clk")) return "MHz";
        if (name.StartsWith("Temp")) return "°C";
        if (name.EndsWith("Voltage")) return "mV";
        if (name.EndsWith("Power")) return "W";
        if (name.EndsWith("Current")) return "A";
        if (name.StartsWith("Activity") || name == "FanPercent") return "%";
        if (name == "FanRpm") return "RPM";
        return "";
    }
}
