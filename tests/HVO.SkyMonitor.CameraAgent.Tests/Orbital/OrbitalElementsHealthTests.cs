using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsHealthTests : OrbitalStoreTestBase
{
    private static readonly TimeSpan Floor = OrbitalElementsStoreTestData.Interval;

    /// <summary>The keys the shared health response allowlists for <c>orbital-elements</c>.</summary>
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "Availability", "Reason", "RecoveryAction", "Halted", "HaltHttpStatus", "ActiveRecordCount", "ActiveObjectCount",
        "NewestElementAgeSeconds", "OldestElementAgeSeconds", "ActivationAgeSeconds", "RetrievalAgeSeconds",
        "LastAttemptOutcome"
    };

    [TestMethod]
    public async Task DisabledRefreshIsHealthyWithoutReadingTheStore()
    {
        var root = CreateRoot();
        using var owner = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root, enabled: false, initialize: false).ConfigureAwait(false);

        var health = await Evaluator(fixture).EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Healthy, health.State);
        Assert.AreEqual("Disabled", health.Data["Availability"]);
        Assert.AreEqual(OrbitalElementsOperations.DisabledReasonCode, health.Data["Reason"]);
        Assert.AreEqual(OrbitalElementsAvailabilityState.NotInitialized, fixture.Availability.State,
            "A locked root would be unhealthy if disabled health had probed it.");
        AssertBounded(health);
    }

    [TestMethod]
    [DataRow("locked", "StorageUnavailable", "orbital.storage-locked")]
    [DataRow("newer-schema", "IntegrityFailure", OrbitalElementsAvailability.IntegrityFailureReasonCode)]
    [DataRow("read-failure", "StorageUnavailable", OrbitalElementsAvailability.StoreUnavailableReasonCode)]
    public async Task AnUnusableStoreIsUnhealthyWithAClassifiedReason(string scenario, string availability, string reasonCode)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        SqliteOrbitalElementsStore? owner = null;
        if (scenario == "locked")
        {
            owner = OrbitalElementsStoreTestData.Open(root, clock);
            await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else if (scenario == "newer-schema")
        {
            using (var original = OrbitalElementsStoreTestData.Open(root, clock))
            {
                await original.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            OrbitalElementsStoreTestData.Execute(root, "PRAGMA user_version = 2;");
        }
        try
        {
            using var fixture = await OrbitalAutomationFixture.CreateAsync(root, initialize: scenario == "read-failure")
                .ConfigureAwait(false);
            if (scenario == "read-failure")
            {
                fixture.Orbital.Store.Dispose();
            }

            var health = await Evaluator(fixture).EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(OrbitalElementsHealthState.Unhealthy, health.State);
            Assert.AreEqual(availability, health.Data["Availability"]);
            Assert.AreEqual(reasonCode, health.Data["Reason"]);
            Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.RepairStorage), health.Data["RecoveryAction"]);
            Assert.IsFalse(health.Description.Contains(root, StringComparison.Ordinal));
            AssertBounded(health);
        }
        finally
        {
            owner?.Dispose();
        }
    }

    [TestMethod]
    public async Task AnActivatedSnapshotIsHealthyUntilNoRetrievalSucceedsWithinTheRefreshBound()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        // Element epochs 6 h and 30 h before the retrieval; freshness follows the newest one.
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(CelesTrakGpTestData.Payload(
            CelesTrakGpTestData.RecordFor(60001), CelesTrakGpTestData.RecordFor(60002, ("EPOCH", "\"2026-10-18T06:00:00.000000\"")))));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var evaluator = Evaluator(fixture);
        var overdueAfter = TimeSpan.FromHours(fixture.Orbital.Options.Value.OrbitalElements.StaleAfterHours);

        var fresh = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(overdueAfter);
        var boundary = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var overdue = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(OrbitalElementsHealthEvaluator.ElementStaleAge);
        var staleAndOverdue = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Healthy, fresh.State);
        Assert.AreEqual("Available", fresh.Data["Availability"]);
        Assert.AreEqual(OrbitalElementsOperations.ReadyReasonCode, fresh.Data["Reason"]);
        Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.None), fresh.Data["RecoveryAction"]);
        Assert.AreEqual(false, fresh.Data["Halted"]);
        Assert.AreEqual(2, fresh.Data["ActiveRecordCount"]);
        Assert.AreEqual(2, fresh.Data["ActiveObjectCount"]);
        Assert.AreEqual((long)TimeSpan.FromHours(6).TotalSeconds, fresh.Data["NewestElementAgeSeconds"]);
        Assert.AreEqual((long)TimeSpan.FromHours(30).TotalSeconds, fresh.Data["OldestElementAgeSeconds"]);
        Assert.AreEqual(0L, fresh.Data["ActivationAgeSeconds"]);
        Assert.AreEqual(0L, fresh.Data["RetrievalAgeSeconds"]);
        Assert.AreEqual(nameof(OrbitalAttemptOutcome.Activated), fresh.Data["LastAttemptOutcome"]);
        Assert.IsFalse(fresh.Data.ContainsKey("HaltHttpStatus"));
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, boundary.State);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, overdue.State);
        Assert.AreEqual(OrbitalElementsHealthEvaluator.RefreshOverdueReasonCode, overdue.Data["Reason"]);
        Assert.AreEqual((long)overdueAfter.TotalSeconds, overdue.Data["RetrievalAgeSeconds"]);
        Assert.AreEqual(OrbitalElementsHealthEvaluator.SnapshotStaleReasonCode, staleAndOverdue.Data["Reason"],
            "Stale elements take precedence over an overdue refresh.");
        AssertBounded(overdue);
        AssertBounded(staleAndOverdue);
    }

    [TestMethod]
    public async Task AnUnchangedOldPayloadNeverLooksFreshBecauseFreshnessFollowsTheNewestElementEpoch()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var activatedAt = fixture.Clock.GetUtcNow();
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var newest = (await fixture.Orbital.StateAsync().ConfigureAwait(false)).CurrentMaximumEpochUtc!.Value;
        var evaluator = Evaluator(fixture);

        // The same bytes are re-downloaded just before, and then after, the newest element turns 48 hours old.
        fixture.Clock.Advance(newest + OrbitalElementsHealthEvaluator.ElementStaleAge - fixture.Clock.GetUtcNow());
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var boundary = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        var atBoundary = await fixture.Orbital.StateAsync().ConfigureAwait(false);
        fixture.Clock.Advance(Floor);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var stale = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        var state = await fixture.Orbital.StateAsync().ConfigureAwait(false);

        Assert.AreEqual(OrbitalAttemptOutcome.UnchangedPayload, atBoundary.LastAttemptOutcome);
        Assert.AreEqual(activatedAt, atBoundary.LastActivationUtc, "An unchanged payload is not a new activation.");
        Assert.AreEqual(fixture.Clock.GetUtcNow() - Floor, atBoundary.LastRetrievalSuccessUtc);
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, boundary.State);
        Assert.AreEqual(OrbitalAttemptOutcome.UnchangedPayload, state.LastAttemptOutcome);
        Assert.AreEqual(activatedAt, state.LastActivationUtc);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), state.LastRetrievalSuccessUtc);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, stale.State, "A fresh download of old elements stays stale.");
        Assert.AreEqual(OrbitalElementsHealthEvaluator.SnapshotStaleReasonCode, stale.Data["Reason"]);
        Assert.AreEqual(0L, stale.Data["RetrievalAgeSeconds"]);
        Assert.AreEqual((long)(fixture.Clock.GetUtcNow() - newest).TotalSeconds, stale.Data["NewestElementAgeSeconds"]);
        Assert.AreEqual((long)(fixture.Clock.GetUtcNow() - activatedAt).TotalSeconds, stale.Data["ActivationAgeSeconds"]);
        AssertBounded(stale);
    }

    [TestMethod]
    public async Task AHaltIsDegradedWithItsStatusAndClearsOnResume()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        fixture.Clock.Advance(Floor);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Halt(403));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var evaluator = Evaluator(fixture);

        var halted = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        await fixture.Operations.ResumeAsync(new OrbitalResumeRequest(fixture.Clock.GetUtcNow(), "resume-1", "owner",
            "provider confirmed recovery"), CancellationToken.None).ConfigureAwait(false);
        var resumed = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Degraded, halted.State);
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, halted.Data["Reason"]);
        Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.ResumeProvider), halted.Data["RecoveryAction"]);
        Assert.AreEqual(true, halted.Data["Halted"]);
        Assert.AreEqual(403, halted.Data["HaltHttpStatus"]);
        Assert.AreEqual(2, halted.Data["ActiveRecordCount"], "The last-known-good snapshot is still reported.");
        Assert.IsFalse(halted.Data.Values.OfType<string>().Any(static value => value.Contains("status=", StringComparison.Ordinal)),
            "The halt's free text never reaches health data.");
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, resumed.State);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, resumed.Data["Reason"]);
        AssertBounded(halted);
    }

    [TestMethod]
    public async Task AQuarantineIsDegradedUntilALaterActivationClearsIt()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(Encoding.UTF8.GetBytes("{\"not\":\"an array\"}")));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var evaluator = Evaluator(fixture);

        var quarantined = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(Floor);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Result(OrbitalFetchDisposition.TransportFailure, null,
            "orbital.transport-connection", "synthetic"));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var stillQuarantined = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(Floor);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var activated = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Degraded, quarantined.State);
        StringAssert.StartsWith((string)quarantined.Data["Reason"], "orbital.", StringComparison.Ordinal);
        Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.InspectQuarantine), quarantined.Data["RecoveryAction"]);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, stillQuarantined.State);
        Assert.AreEqual(OrbitalElementsOperations.CandidateQuarantinedReasonCode, stillQuarantined.Data["Reason"],
            "A quarantine stays visible until a later activation, whatever the latest outcome.");
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, activated.State);
    }

    [TestMethod]
    public async Task NoSnapshotIsHealthyOnlyUntilTheFirstDueTimeHasPassed()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var evaluator = Evaluator(fixture);
        var unscheduled = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        var definition = (await fixture.SaveDefinitionAsync().ConfigureAwait(false)).State.Definitions.Single();
        var due = definition.NextRunUtc!.Value;

        var waiting = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(due + OrbitalElementsHealthEvaluator.DueGrace - fixture.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        var withinGrace = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var overdue = await evaluator.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Degraded, unscheduled.State, "Enabled with nothing scheduled never refreshes.");
        Assert.AreEqual(OrbitalElementsOperations.DefinitionMissingReasonCode, unscheduled.Data["Reason"]);
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, waiting.State);
        Assert.AreEqual(OrbitalElementsOperations.NoActiveSnapshotReasonCode, waiting.Data["Reason"]);
        Assert.IsFalse(waiting.Data.ContainsKey("ActiveRecordCount"));
        Assert.IsFalse(waiting.Data.ContainsKey("ActivationAgeSeconds"));
        Assert.IsFalse(waiting.Data.ContainsKey("NewestElementAgeSeconds"));
        Assert.IsFalse(waiting.Data.ContainsKey("LastAttemptOutcome"));
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, withinGrace.State);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, overdue.State);
        Assert.AreEqual(OrbitalElementsHealthEvaluator.FirstSnapshotOverdueReasonCode, overdue.Data["Reason"]);
    }

    [TestMethod]
    public async Task AFailedFirstAttemptDegradesImmediately()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Result(OrbitalFetchDisposition.TimedOut, null, "orbital.timeout",
            "synthetic"));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);

        var health = await Evaluator(fixture).EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        Assert.AreEqual(OrbitalElementsHealthEvaluator.FirstSnapshotOverdueReasonCode, health.Data["Reason"]);
        Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.WaitForPermittedAttempt), health.Data["RecoveryAction"]);
        Assert.AreEqual(nameof(OrbitalAttemptOutcome.TransportFailed), health.Data["LastAttemptOutcome"]);
    }

    [TestMethod]
    [DataRow("unsettled", OrbitalElementsOperations.AttemptUnsettledReasonCode)]
    [DataRow("storage-failed", "orbital.storage-")]
    public async Task AStorageProblemBehindAnAvailableStoreIsDegradedWithRepairStorage(string scenario, string reasonPrefix)
    {
        var faults = scenario == "storage-failed"
            ? new OrbitalElementsStoreTestData.Faults
            {
                ArmedPoint = OrbitalStorageFaultPoint.AfterPayload,
                Throw = static () => new IOException("synthetic disk fault")
            }
            : null;
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot(), faults: faults).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        if (scenario == "unsettled")
        {
            fixture.Orbital.Intercept.TransportFault = static _ => new IOException("synthetic");
            fixture.Orbital.Client.Enqueue(fixture.Orbital.Result(OrbitalFetchDisposition.TransportFailure, null,
                "orbital.transport-connection", "synthetic"));
        }
        else
        {
            fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        }
        await fixture.Orbital.RunAsync().ConfigureAwait(false);

        var health = await Evaluator(fixture).EvaluateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        StringAssert.StartsWith((string)health.Data["Reason"], reasonPrefix, StringComparison.Ordinal);
        Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.RepairStorage), health.Data["RecoveryAction"]);
        Assert.AreEqual("Available", health.Data["Availability"]);
    }

    [TestMethod]
    public async Task CallerCancellationIsRethrownRatherThanReportedUnhealthy()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Evaluator(fixture).EvaluateAsync(cancellation.Token).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(true, "Healthy")]
    [DataRow(false, "Unhealthy")]
    public async Task TheHostCheckProjectsTheEvaluationWithItsBoundedData(bool available, string status)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var owner = available ? null : OrbitalElementsStoreTestData.Open(root, clock);
        if (owner is not null)
        {
            await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root, initialize: available).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var check = new OrbitalElementsHealthCheck(Evaluator(fixture));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(Enum.Parse<HealthStatus>(status), result.Status);
        Assert.IsNull(result.Exception);
        Assert.IsTrue(result.Data.Keys.All(AllowedKeys.Contains));
        Assert.AreEqual(available ? "Available" : "StorageUnavailable", result.Data["Availability"]);
    }

    [TestMethod]
    public async Task TheHostCheckReportsDegradedAsDegraded()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var check = new OrbitalElementsHealthCheck(Evaluator(fixture));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Degraded, result.Status);
        Assert.AreEqual(OrbitalElementsOperations.DefinitionMissingReasonCode, result.Data["Reason"]);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow(-1L, false)]
    [DataRow(0L, false)]
    [DataRow(1L, true)]
    public void ElementsAreStaleOnlyPastTheElementAgeEnvelopeOrWithoutANewestEpoch(long? ticksPastThreshold,
        bool expected)
    {
        var now = OrbitalElementsStoreTestData.Start;
        DateTimeOffset? newest = ticksPastThreshold is { } ticks
            ? now - OrbitalElementsHealthEvaluator.ElementStaleAge - TimeSpan.FromTicks(ticks)
            : null;

        Assert.AreEqual(TimeSpan.FromHours(48), OrbitalElementsHealthEvaluator.ElementStaleAge);
        Assert.AreEqual(expected, OrbitalElementsHealthEvaluator.IsElementStale(now, newest));
        Assert.IsFalse(OrbitalElementsHealthEvaluator.IsElementStale(now, now.AddHours(1)),
            "An epoch ahead of the clock is not stale.");
    }

    [TestMethod]
    [DataRow(12, null, true)]
    [DataRow(12, -1L, false)]
    [DataRow(12, 0L, false)]
    [DataRow(12, 1L, true)]
    [DataRow(3, 0L, false)]
    [DataRow(3, 1L, true)]
    [DataRow(168, 0L, false)]
    [DataRow(168, 1L, true)]
    public void ARefreshIsOverdueOnlyPastTheRetrievalBoundOrWithoutARetrieval(int staleAfterHours,
        long? ticksPastThreshold, bool expected)
    {
        var now = OrbitalElementsStoreTestData.Start;
        var options = new OrbitalElementsOptions { StaleAfterHours = staleAfterHours };
        DateTimeOffset? retrieved = ticksPastThreshold is { } ticks
            ? now - TimeSpan.FromHours(staleAfterHours) - TimeSpan.FromTicks(ticks)
            : null;

        Assert.AreEqual(expected, OrbitalElementsHealthEvaluator.IsRefreshOverdue(now, retrieved, options));
        Assert.ThrowsExactly<ArgumentNullException>(() => OrbitalElementsHealthEvaluator.IsRefreshOverdue(now, now, null!));
    }

    private static OrbitalElementsHealthEvaluator Evaluator(OrbitalAutomationFixture fixture)
        => new(fixture.Operations, fixture.Orbital.Options, fixture.Clock);

    private static void AssertBounded(OrbitalElementsHealthSnapshot health)
    {
        Assert.IsTrue(health.Data.Keys.All(AllowedKeys.Contains), string.Join(',', health.Data.Keys));
        Assert.IsTrue(health.Data.Values.All(static value => value is string or bool or int or long));
        Assert.IsTrue(health.Data.Values.OfType<string>().All(static value => value.Length <= 128 &&
            !value.Contains('/', StringComparison.Ordinal) && !value.Contains('\\', StringComparison.Ordinal)));
    }
}
