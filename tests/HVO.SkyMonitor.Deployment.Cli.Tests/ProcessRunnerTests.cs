using System.Diagnostics;
using System.Globalization;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public async Task RunWithInputAsync_LargeInput_IsDeliveredAndClosedWithoutArgumentExpansion()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new ProcessRunner().RunWithInputAsync("wc", ["-c"], new string('x', 256 * 1024), cancellation.Token);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("262144", result.StandardOutput.Trim());
    }

    [TestMethod]
    public async Task RunWithInputAsync_BrokenPipe_ReapsChildEvenAfterBothOutputPipesClosed()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "hvo-input-child-" + Guid.NewGuid().ToString("N") + ".pid");
        Process? child = null;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsExactlyAsync<IOException>(() => new ProcessRunner().RunWithInputAsync("bash",
                ["-c", "printf '%s' $$ > \"$1\"; exec 1>&- 2>&-; sleep 0.1; exec 0<&-; sleep 30", "bash", pidFile], new string('x', 256 * 1024), cancellation.Token));
            Assert.IsTrue(File.Exists(pidFile));
            var pid = int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture);
            try { child = Process.GetProcessById(pid); }
            catch (ArgumentException) { /* Reaped by the process runner. */ }
            Assert.IsTrue(child is null || child.HasExited, "An input failure left the command alive after its output readers completed.");
        }
        finally
        {
            if (child is not null && !child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
            child?.Dispose(); File.Delete(pidFile);
        }
    }

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
