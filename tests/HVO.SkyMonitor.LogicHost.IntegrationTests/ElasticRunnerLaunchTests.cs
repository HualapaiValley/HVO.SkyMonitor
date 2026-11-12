using FluentAssertions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class ElasticRunnerLaunchTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FreshBuildAppHostWinsOverPublishedLeftovers(bool stalePublish)
    {
        var root = Directory.CreateTempSubdirectory("hvo-elastic-path-");
        try
        {
            var expected = Path.Combine(root.FullName, "HVO.SkyMonitor.ProcessingRunner");
            File.WriteAllText(expected, "fresh build");
            if (stalePublish)
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, "linux-x64"));
                File.WriteAllText(Path.Combine(root.FullName, "linux-x64", "HVO.SkyMonitor.ProcessingRunner"), "stale publish");
            }
            var launch = ElasticProviderIntegrationTests.ElasticHost.ResolveRunnerLaunch(root.FullName);
            launch.Executable.Should().Be(expected);
            launch.Arguments.Should().BeEmpty();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void FreshDllWinsWhenOnlyStalePublishedAppHostExists()
    {
        var root = Directory.CreateTempSubdirectory("hvo-elastic-path-");
        try
        {
            var dll = Path.Combine(root.FullName, "HVO.SkyMonitor.ProcessingRunner.dll");
            File.WriteAllText(dll, "fresh DLL");
            Directory.CreateDirectory(Path.Combine(root.FullName, "linux-x64"));
            File.WriteAllText(Path.Combine(root.FullName, "linux-x64", "HVO.SkyMonitor.ProcessingRunner"), "stale publish");
            var launch = ElasticProviderIntegrationTests.ElasticHost.ResolveRunnerLaunch(root.FullName);
            launch.Arguments.Should().Equal(dll);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
