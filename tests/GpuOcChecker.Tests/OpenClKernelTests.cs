using GpuOcChecker.Core.Stress;
using GpuOcChecker.Core.Stress.OpenCl;
using Xunit;

namespace GpuOcChecker.Tests;

/// <summary>Runs the real kernels when any OpenCL runtime is present (e.g. POCL in CI); otherwise no-ops.</summary>
public class OpenClKernelTests
{
    private static OpenClBackend? TryCreate()
    {
        try
        {
            return OpenClBackend.ListDevices().Count > 0 ? OpenClBackend.Create(new StressSetup()) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [Fact]
    public void Kernels_are_deterministic_and_error_free_on_a_healthy_device()
    {
        using var b = TryCreate();
        if (b == null) return;
        b.Initialize(new StressSetup { VramFraction = 0.05, TargetDispatchMs = 5 }, _ => { });
        Assert.Equal(0, b.CalibrationMismatches);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, b.RunCompute(heavy: true).Mismatches);
            Assert.Equal(0, b.RunCompute(heavy: false).Mismatches); // light results must equal the heavy prefix
        }
        for (uint pass = 0; pass < 4; pass++)
            Assert.Equal(0, b.RunVramPass(pass).Errors);
        Assert.True(b.RunBandwidth().GBps > 0);
    }
}
