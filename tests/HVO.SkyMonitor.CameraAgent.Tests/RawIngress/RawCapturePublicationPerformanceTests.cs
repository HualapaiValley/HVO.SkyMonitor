namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class RawCapturePublicationPerformanceTests
{
    [TestMethod]
    public async Task ForcedInitializationSettlesPublicationContentionWithinFiveSeconds() =>
        Assert.IsLessThan(TimeSpan.FromSeconds(5), await RawCaptureIngressTests
            .VerifyInitializationWaitsForPublicationAsync().ConfigureAwait(false));
}
