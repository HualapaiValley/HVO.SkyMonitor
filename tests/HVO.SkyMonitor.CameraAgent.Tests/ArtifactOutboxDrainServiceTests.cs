using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ArtifactOutboxDrainServiceTests
{
    [TestMethod]
    [DataRow(1, 10)]
    [DataRow(2, 20)]
    [DataRow(3, 40)]
    [DataRow(4, 60)]
    [DataRow(30, 60)]
    public void CalculateRetryDelay_UsesBoundedExponentialBackoff(int attempt, int expectedSeconds)
    {
        var delay = ArtifactOutboxDrainService.CalculateRetryDelay(
            attempt,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60));

        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), delay);
    }
}
