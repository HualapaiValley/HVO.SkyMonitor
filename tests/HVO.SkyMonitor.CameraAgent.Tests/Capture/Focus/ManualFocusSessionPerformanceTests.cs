namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Performance")]
[TestProperty("Area", "Capture.Focus")]
[DoNotParallelize]
public sealed class ManualFocusSessionPerformanceTests
{
    [TestMethod]
    public async Task StopInterruptsPacedWaitWithinFiveSeconds() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(5), await ManualFocusSessionCoordinatorTests
            .VerifyStopDuringPacedWaitAsync().ConfigureAwait(false));

    [TestMethod]
    public async Task StopInterruptsExposureWithinFiveSeconds() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(5), await ManualFocusSessionCoordinatorTests
            .VerifyStopDuringExposureAsync().ConfigureAwait(false));
}
