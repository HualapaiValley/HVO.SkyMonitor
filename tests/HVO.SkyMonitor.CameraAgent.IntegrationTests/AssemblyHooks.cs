using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>
/// Assembly-level bootstrapper for the standalone CameraAgent integration fixture.
/// </summary>
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
public sealed class AssemblyHooks
{
    internal static CameraAgentIntegrationFixture Fixture { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        Fixture = new CameraAgentIntegrationFixture();
        await Fixture.InitializeAsync().ConfigureAwait(false);
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        Fixture.Dispose();
    }
}
