using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

[assembly: DoNotParallelize]

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
public sealed class AssemblyHooks
{
    private static readonly Lazy<CameraAgentIntegrationFixture> FixtureFactory = new(CreateFixture);

    internal static CameraAgentIntegrationFixture Fixture => FixtureFactory.Value;

    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext context)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION")))
        {
            HVO.SkyMonitor.IntegrationTests.Issue170PerformanceEvidence.AcquireExclusiveProcessLock();
        }
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        if (FixtureFactory.IsValueCreated)
        {
            FixtureFactory.Value.Dispose();
        }
        HVO.SkyMonitor.IntegrationTests.AssemblyHooks.Dispose();
    }

    private static CameraAgentIntegrationFixture CreateFixture()
    {
        var fixture = new CameraAgentIntegrationFixture();
        try
        {
            fixture.InitializeAsync().GetAwaiter().GetResult();
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }
}
