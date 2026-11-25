namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class ReleaseManifestPerformanceTests
{
    [TestMethod]
    public Task Parse_TooManyDistinctNotesAreRejectedWithinFiveSeconds() =>
        ReleaseManifestTests.VerifyTooManyNotesAsync(TimeSpan.FromSeconds(5));

    [TestMethod]
    public Task Parse_LargestValidReleaseFinishesWithinFiveSeconds() =>
        ReleaseManifestTests.VerifyLargestValidReleaseAsync(TimeSpan.FromSeconds(5));
}
