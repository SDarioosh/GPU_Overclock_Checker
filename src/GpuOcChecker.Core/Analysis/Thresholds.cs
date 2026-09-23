using System.Text.Json;

namespace GpuOcChecker.Core.Analysis;

/// <summary>Tunable analysis limits. Override any of them in Documents\GpuOcChecker\thresholds.json.</summary>
public sealed class Thresholds
{
    /// <summary>Hotspot minus edge temperature (°C). The 7900 XTX reference cooler defect showed 40+.</summary>
    public double HotspotDeltaWarn { get; set; } = 25;
    public double HotspotDeltaCritical { get; set; } = 35;

    /// <summary>How close to the card's hotspot limit counts as "throttling" / "near limit".</summary>
    public double HotspotMarginCritical { get; set; } = 2;
    public double HotspotMarginWarn { get; set; } = 10;
    public double MemTempMarginWarn { get; set; } = 6;
    public double VrTempWarn { get; set; } = 105;

    /// <summary>Hotspot at which an error is attributed (partly) to temperature.</summary>
    public double HotErrorContextMargin { get; set; } = 8;

    /// <summary>A measured undervolt: max light-load voltage this far under the stock maximum (V).</summary>
    public double UndervoltDetectMargin { get; set; } = 0.05;

    /// <summary>Relative drop vs. baseline that counts as a regression.</summary>
    public double PerfPerClockDrop { get; set; } = 0.03;
    public double BandwidthScalingTolerance { get; set; } = 0.5;
    public double BandwidthDrop { get; set; } = 0.02;
    public double BandwidthCovWarn { get; set; } = 0.03;

    /// <summary>Seconds without telemetry (while the tool was sampling) that count as a GPU/driver stall.</summary>
    public double TelemetryGapSeconds { get; set; } = 3;

    /// <summary>Seconds of context looked at before a failure.</summary>
    public double FailureContextSeconds { get; set; } = 8;

    /// <summary>Minimum seconds of loaded testing before a clean result is called "stable".</summary>
    public double MinLoadedSecondsForVerdict { get; set; } = 90;

    public static Thresholds Load()
    {
        try
        {
            if (File.Exists(AppPaths.ThresholdsFile))
                return JsonSerializer.Deserialize<Thresholds>(File.ReadAllText(AppPaths.ThresholdsFile), JsonOpts.Default) ?? new();
        }
        catch (Exception)
        {
            // fall back to defaults on a malformed file
        }
        return new Thresholds();
    }
}
