namespace GpuOcChecker.Core.Telemetry;

/// <summary>Normalized sensor channels. Every vendor backend maps its sensors onto these.</summary>
public enum Metric
{
    CoreClock,
    MemClock,
    SocClock,
    CoreVoltage,
    MemVoltage,
    SocVoltage,
    EdgeTemp,
    HotspotTemp,
    MemTemp,
    VrTemp,
    Power,
    PowerLimit,
    CoreLoad,
    MemLoad,
    FanRpm,
    FanPercent,
    PcieReplays,
}

public sealed record MetricInfo(Metric Metric, string Label, string Unit, string CsvName, string Format);

public static class Metrics
{
    public static readonly int Count = Enum.GetValues<Metric>().Length;

    public static readonly IReadOnlyList<MetricInfo> All = new[]
    {
        new MetricInfo(Metric.CoreClock, "Core clock", "MHz", "core_clock_mhz", "0"),
        new MetricInfo(Metric.MemClock, "Memory clock", "MHz", "mem_clock_mhz", "0"),
        new MetricInfo(Metric.SocClock, "SoC clock", "MHz", "soc_clock_mhz", "0"),
        new MetricInfo(Metric.CoreVoltage, "Core voltage", "V", "core_voltage_v", "0.000"),
        new MetricInfo(Metric.MemVoltage, "Memory voltage", "V", "mem_voltage_v", "0.000"),
        new MetricInfo(Metric.SocVoltage, "SoC voltage", "V", "soc_voltage_v", "0.000"),
        new MetricInfo(Metric.EdgeTemp, "Edge temp", "°C", "edge_temp_c", "0.0"),
        new MetricInfo(Metric.HotspotTemp, "Hotspot temp", "°C", "hotspot_temp_c", "0.0"),
        new MetricInfo(Metric.MemTemp, "Memory temp", "°C", "mem_temp_c", "0.0"),
        new MetricInfo(Metric.VrTemp, "VRM temp", "°C", "vr_temp_c", "0.0"),
        new MetricInfo(Metric.Power, "Board power", "W", "power_w", "0.0"),
        new MetricInfo(Metric.PowerLimit, "Power limit", "W", "power_limit_w", "0.0"),
        new MetricInfo(Metric.CoreLoad, "GPU load", "%", "core_load_pct", "0"),
        new MetricInfo(Metric.MemLoad, "Memory ctrl load", "%", "mem_load_pct", "0"),
        new MetricInfo(Metric.FanRpm, "Fan", "RPM", "fan_rpm", "0"),
        new MetricInfo(Metric.FanPercent, "Fan", "%", "fan_pct", "0"),
        new MetricInfo(Metric.PcieReplays, "PCIe replays", "", "pcie_replays", "0"),
    };

    public static MetricInfo Info(Metric m) => All[(int)m];
}
