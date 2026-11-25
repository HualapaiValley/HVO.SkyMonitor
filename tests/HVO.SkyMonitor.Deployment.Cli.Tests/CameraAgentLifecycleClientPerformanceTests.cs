namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class CameraAgentLifecycleClientPerformanceTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task PauseAndDrainDoesNotStackDeadlines()
    {
        // Stacking the deadlines would take about 3.5 seconds.
        var elapsed = await CameraAgentLifecycleClientTests.VerifyPauseAndDrainBudgetAsync();
        Assert.IsLessThan(TimeSpan.FromSeconds(3), elapsed);
    }

    [TestMethod]
    public async Task UnacknowledgedResumeUsesTheDrainDeadline()
    {
        var elapsed = await CameraAgentLifecycleClientTests.VerifyUnacknowledgedResumeAsync();
        Assert.IsLessThan(TimeSpan.FromSeconds(5), elapsed);
    }
}
