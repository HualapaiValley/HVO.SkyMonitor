using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public async Task RunAsync_DoesNotExposeDistributionTokenToChildProcess()
    {
        var previous = Environment.GetEnvironmentVariable("HVO_GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("HVO_GITHUB_TOKEN", "must-not-reach-child");

            var result = await new ProcessRunner().RunAsync("env", [], CancellationToken.None);

            Assert.AreEqual(0, result.ExitCode);
            Assert.IsFalse(result.StandardOutput.Contains("must-not-reach-child", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_GITHUB_TOKEN", previous);
        }
    }
}
