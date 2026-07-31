using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Assembly-level hooks to bootstrap the shared integration test fixture.
/// </summary>
[TestClass]
public sealed class AssemblyHooks
{
    internal static IntegrationTestFixture Fixture { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION")))
        {
            Issue170PerformanceEvidence.AcquireExclusiveProcessLock();
        }
        Fixture = new IntegrationTestFixture(
            suppressRecurringWorkers: string.Equals(
                Environment.GetEnvironmentVariable("HVO_ISSUE_246_RETENTION_EVIDENCE"),
                "1",
                StringComparison.Ordinal));
        await Fixture.InitializeAsync().ConfigureAwait(false);
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        Fixture.Dispose();
    }
}
