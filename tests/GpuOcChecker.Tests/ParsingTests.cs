using GpuOcChecker.Core;
using GpuOcChecker.Core.Analysis;
using GpuOcChecker.Core.Session;
using GpuOcChecker.Core.Stress;
using GpuOcChecker.Core.Stress.OpenCl;
using GpuOcChecker.Core.Telemetry;
using Xunit;

namespace GpuOcChecker.Tests;

public class ParsingTests
{
    [Fact]
    public void Tuning_settings_parse()
    {
        var t = TuningSettings.Parse("uv=80, max=2900mhz, min=500, mem=2700, ft=1, pl=+15%");
        Assert.Equal(-80, t.VoltageOffsetMv); // positive undervolt is normalized to a negative offset
        Assert.Equal(2900, t.MaxFreqMHz);
        Assert.Equal(500, t.MinFreqMHz);
        Assert.Equal(2700, t.MemClockMHz);
        Assert.True(t.FastTiming);
        Assert.Equal(15, t.PowerLimitPct);
        Assert.True(TuningSettings.Parse("").IsEmpty);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", "Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon RX 7900 XT", "Radeon RX 7900 XT")]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", "GeForce RTX 4080 SUPER")]
    [InlineData("NVIDIA GeForce RTX 4080", "GeForce RTX 4080")]
    [InlineData("AMD Radeon(TM) Graphics", "Generic GPU")]
    public void Card_profiles_prefer_longest_match(string name, string expected) =>
        Assert.Equal(expected, CardProfiles.Find(name).Name);

    [Fact]
    public void Discrete_gpu_ranks_above_igpu()
    {
        Assert.True(TelemetryDiscovery.DiscreteScore("AMD Radeon RX 7900 XTX") > TelemetryDiscovery.DiscreteScore("AMD Radeon(TM) Graphics"));
    }

    [Fact]
    public void OpenCl_device_matches_telemetry_name_by_board_name()
    {
        var xtx = new ClDeviceInfo(1, "gfx1100", "AMD Radeon RX 7900 XTX", "AMD", "AMD APP", true, 24L << 30, 4L << 30, 48, "x");
        var igpu = new ClDeviceInfo(0, "gfx1036", "AMD Radeon(TM) Graphics", "AMD", "AMD APP", true, 2L << 30, 1L << 30, 2, "x");
        Assert.True(OpenClBackend.NameMatchScore(xtx, "AMD Radeon RX 7900 XTX") > OpenClBackend.NameMatchScore(igpu, "AMD Radeon RX 7900 XTX"));
    }

    [Fact]
    public void Hwinfo_columns_are_mapped()
    {
        var header = new[]
        {
            "Date", "Time", "CPU Package [°C]", "GPU Temperature [°C]", "GPU Memory Junction Temperature [°C]",
            "GPU Hot Spot Temperature [°C]", "GPU Core Voltage (VDDCR_GFX) [mV]", "GPU Clock [MHz]", "GPU Effective Clock [MHz]",
            "GPU Memory Clock [MHz]", "GPU ASIC Power [W]", "GPU Utilization [%]", "GPU Fan [RPM]", "GPU Fan [%]",
        };
        var map = HwInfoImporter.MapColumns(header);
        Assert.Equal(3, map[Metric.EdgeTemp].Column);
        Assert.Equal(4, map[Metric.MemTemp].Column);
        Assert.Equal(5, map[Metric.HotspotTemp].Column);
        Assert.Equal(6, map[Metric.CoreVoltage].Column);
        Assert.Equal(0.001, map[Metric.CoreVoltage].Scale);
        Assert.Equal(7, map[Metric.CoreClock].Column); // "GPU Clock" beats "GPU Effective Clock"
        Assert.Equal(9, map[Metric.MemClock].Column);
        Assert.Equal(10, map[Metric.Power].Column);
        Assert.Equal(11, map[Metric.CoreLoad].Column);
        Assert.Equal(12, map[Metric.FanRpm].Column);
    }

    [Fact]
    public void Hwinfo_file_imports()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hwinfo-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, new[]
        {
            "Date,Time,GPU Temperature [°C],GPU Hot Spot Temperature [°C],GPU Clock [MHz],GPU Core Voltage [V],GPU Utilization [%],",
            "23.9.2026,20:01:02.123,55.0,70.0,2950.0,1.050,45.0,",
            "23.9.2026,20:01:04.125,56.0,71.0,2940.0,1.045,50.0,",
            "Date,Time,GPU Temperature [°C],GPU Hot Spot Temperature [°C],GPU Clock [MHz],GPU Core Voltage [V],GPU Utilization [%],",
            ",,\"GPU [#0]: AMD Radeon RX 7900 XTX: \",,,,,",
        });
        try
        {
            var s = HwInfoImporter.Import(path);
            Assert.Equal(2, s.Samples.Count);
            Assert.Equal(2950, s.Samples[0][Metric.CoreClock]);
            Assert.Equal(1.045, s.Samples[1][Metric.CoreVoltage]);
            Assert.Equal("AMD Radeon RX 7900 XTX", s.Gpu!.Name);
            Assert.InRange(s.Samples[1].Elapsed, 1.9, 2.1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Telemetry_csv_round_trips()
    {
        var s = new TelemetrySample { Timestamp = DateTimeOffset.Now, Elapsed = 1.5, Phase = "Heavy, sustained", Throttle = ThrottleFlags.Power };
        s[Metric.CoreClock] = 2650;
        s[Metric.CoreVoltage] = 0.987;
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, new[] { TelemetryCsv.Header, TelemetryCsv.Format(s), "2026-01-01T00:00" /* torn line */ });
        var back = TelemetryCsv.Read(path).Single();
        File.Delete(path);
        Assert.Equal("Heavy, sustained", back.Phase);
        Assert.Equal(2650, back[Metric.CoreClock]);
        Assert.Equal(0.987, back[Metric.CoreVoltage]);
        Assert.Null(back[Metric.MemTemp]);
        Assert.Equal(ThrottleFlags.Power, back.Throttle);
    }

    [Fact]
    public void Crashed_session_is_recovered_from_journal()
    {
        AppPaths.Root = Path.Combine(Path.GetTempPath(), $"gpuoc-{Guid.NewGuid():N}");
        var data = new SessionData { Mode = "stress", Start = DateTimeOffset.Now, Gpu = new GpuInfo(GpuVendor.Amd, "RX 7900 XTX", "k", "t") };
        var rec = new SessionRecorder(data);
        var p = new PhaseResult { Name = "Light load / max boost", Kind = PhaseKind.LightLoad, Start = DateTimeOffset.Now };
        rec.PhaseStarted(p);
        rec.Error(p, new ErrorEvent { Time = DateTimeOffset.Now, Kind = "compute", Count = 3 });
        var s = new TelemetrySample { Timestamp = DateTimeOffset.Now, Phase = p.Name };
        s[Metric.CoreClock] = 3000;
        rec.AddSample(s);
        rec.Dispose(); // no Complete(): simulates the PC dying

        Assert.Single(SessionStore.FindUnfinished());
        var loaded = SessionStore.Load(rec.Directory);
        Assert.False(loaded.CompletedCleanly);
        Assert.Equal("RX 7900 XTX", loaded.Gpu!.Name);
        Assert.Equal(3, loaded.Phases.Single().ErrorCount);
        Assert.Single(loaded.Samples);
        Directory.Delete(AppPaths.Root, true);
    }

    [Fact]
    public void Memtune_detects_bandwidth_regression()
    {
        var e = new List<MemTuneEntry>
        {
            new() { MemClock = 2500, BandwidthGBps = 800 },
            new() { MemClock = 2600, BandwidthGBps = 830 },
            new() { MemClock = 2700, BandwidthGBps = 815 },
        };
        Assert.Contains("SLOWER", MemTune.Assess(e));
        Assert.Equal(2600, MemTune.Best(e)!.MemClock);
    }

    [Fact]
    public void Profiles_scale_and_filter()
    {
        var all = StressProfiles.Get("standard");
        Assert.Equal(7, all.Count);
        var only = StressProfiles.Get("quick", 2, new HashSet<PhaseKind> { PhaseKind.HeavyLoad });
        Assert.Equal(TimeSpan.FromSeconds(150), only.Single().Duration);
    }
}
