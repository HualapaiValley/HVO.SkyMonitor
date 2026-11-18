namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class LocalReplayRunnerPathTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OrdinaryBuildIsSelectedEvenWithStaleRidPublish(bool stalePublish)
    {
        var root = Directory.CreateTempSubdirectory("hvo-replay-path-");
        try
        {
            var bin = Path.Combine(root.FullName, "src", "HVO.SkyMonitor.CameraAgent.ReplayRunner", "bin", "Release", "net10.0");
            Directory.CreateDirectory(bin);
            var expected = Path.Combine(bin, "HVO.SkyMonitor.CameraAgent.ReplayRunner");
            File.WriteAllText(expected, "fresh ordinary build");
            if (stalePublish)
            {
                Directory.CreateDirectory(Path.Combine(bin, "linux-x64"));
                File.WriteAllText(Path.Combine(bin, "linux-x64", "HVO.SkyMonitor.CameraAgent.ReplayRunner"), "stale publish");
            }
            Assert.AreEqual(expected, LocalReplayRunnerPerformanceTests.ResolveRunnerPath(root.FullName, null));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ExplicitExecutableIsPreserved()
    {
        var root = Directory.CreateTempSubdirectory("hvo-replay-path-");
        try
        {
            var configured = Path.Combine(root.FullName, "published-runner");
            File.WriteAllText(configured, "explicit publish");
            Assert.AreEqual(configured, LocalReplayRunnerPerformanceTests.ResolveRunnerPath(root.FullName, configured));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void MissingExecutableExplainsBuildAndOverrideWithoutPrivatePath()
    {
        var exception = Assert.Throws<AssertInconclusiveException>(() =>
            LocalReplayRunnerPerformanceTests.ResolveRunnerPath("synthetic-private-root", null));
        StringAssert.Contains(exception.Message, "dotnet build", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "HVO_REPLAY_RUNNER_EXECUTABLE", StringComparison.Ordinal);
        Assert.IsFalse(exception.Message.Contains("synthetic-private-root", StringComparison.Ordinal));
    }
}
