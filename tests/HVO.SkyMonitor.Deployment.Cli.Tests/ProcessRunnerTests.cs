using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunAsync_ExplicitOutputLimit_StopsExcessiveOutput(bool standardError)
    {
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => new ProcessRunner(1024).RunAsync(
            "bash", ["-c", standardError ? "head -c 65536 /dev/zero >&2" : "head -c 65536 /dev/zero"], CancellationToken.None));
        StringAssert.Contains(exception.Message, "output exceeded", StringComparison.Ordinal);
    }

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
