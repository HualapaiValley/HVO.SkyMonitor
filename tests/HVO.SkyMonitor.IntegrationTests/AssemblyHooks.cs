using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Assembly-level hooks to bootstrap the shared integration test fixture.
/// </summary>
[TestClass]
public sealed class AssemblyHooks
{
    private static readonly string[] RecurringWorkerSuppressionEvidenceVariables =
    [
        "HVO_ISSUE_246_RETENTION_EVIDENCE",
        "HVO_ISSUE_248_BASELINE_EVIDENCE",
        "HVO_ISSUE_248_SMOKE",
        "HVO_ISSUE_248_CENSORED_SMOKE",
        "HVO_ISSUE_248_AGGREGATE_ONLY"
    ];
    internal static IntegrationTestFixture Fixture { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION")))
        {
            Issue170PerformanceEvidence.AcquireExclusiveProcessLock();
        }
        var suppressRecurringWorkers = RecurringWorkerSuppressionEvidenceVariables
            .Any(name => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal));
        Fixture = new IntegrationTestFixture(suppressRecurringWorkers: suppressRecurringWorkers);
        await Fixture.InitializeAsync().ConfigureAwait(false);
    }

    [AssemblyCleanup]
    public static void AssemblyCleanup()
    {
        Fixture.Dispose();
    }
}
