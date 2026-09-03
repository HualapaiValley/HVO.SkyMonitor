using HVO.SkyMonitor.LogicHost.TestInfrastructure;

namespace HVO.SkyMonitor.IntegrationTests;

internal static class AssemblyHooks
{
    private static readonly string[] RecurringWorkerSuppressionEvidenceVariables =
    [
        "HVO_ISSUE_246_RETENTION_EVIDENCE",
        "HVO_ISSUE_248_BASELINE_EVIDENCE",
        "HVO_ISSUE_248_SMOKE",
        "HVO_ISSUE_248_CENSORED_SMOKE",
        "HVO_ISSUE_248_AGGREGATE_ONLY",
        "HVO_ISSUE_250_EVIDENCE"
    ];
    private static readonly Lazy<IntegrationTestFixture> FixtureFactory = new(CreateFixture);

    internal static IntegrationTestFixture Fixture => FixtureFactory.Value;

    internal static void Dispose()
    {
        if (FixtureFactory.IsValueCreated)
        {
            FixtureFactory.Value.Dispose();
        }
    }

    private static IntegrationTestFixture CreateFixture()
    {
        var suppressRecurringWorkers = RecurringWorkerSuppressionEvidenceVariables
            .Any(name => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal));
        var fixture = new IntegrationTestFixture(suppressRecurringWorkers: suppressRecurringWorkers);
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
