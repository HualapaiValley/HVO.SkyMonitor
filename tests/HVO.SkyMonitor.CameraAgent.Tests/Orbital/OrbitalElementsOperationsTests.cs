using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsOperationsTests : OrbitalStoreTestBase
{
    private static readonly TimeSpan Floor = OrbitalElementsStoreTestData.Interval;
    private static readonly int[] QueuedOnly = [7755];

    [TestMethod]
    public async Task StatusBeforeAnyDefinitionNamesTheMissingScheduleAndValidates()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalRefreshStatus.CurrentVersion, status.SchemaVersion);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), status.ObservedUtc);
        Assert.IsTrue(status.Enabled);
        Assert.AreEqual(OrbitalElementsOperations.DefinitionMissingReasonCode, status.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.Configure, status.RecoveryAction);
        Assert.IsNull(status.Automation);
        Assert.IsNull(status.NextDueUtc);
        Assert.IsNull(status.LastAttemptUtc);
        Assert.IsNull(status.NextPermittedAttemptUtc);
        Assert.IsNull(status.CurrentManifestSha256);
    }

    [TestMethod]
    public async Task AScheduledDefinitionWithoutASnapshotReportsItsRevisionAndNextDueTime()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var saved = (await fixture.SaveDefinitionAsync().ConfigureAwait(false)).State.Definitions.Single();

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsOperations.NoActiveSnapshotReasonCode, status.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.None, status.RecoveryAction);
        Assert.AreEqual(new OrbitalRefreshAutomationReference(OrbitalElementsAutomationAdapter.DefinitionId,
            saved.RevisionSha256, null), status.Automation);
        Assert.AreEqual(saved.NextRunUtc, status.NextDueUtc);
        Assert.IsTrue(status.NextDueUtc > status.ObservedUtc);
        Assert.IsTrue(status.NextDueUtc <= status.ObservedUtc + Floor + TimeSpan.FromMinutes(30),
            "The first due time is one interval after the installation's phase origin.");
    }

    [TestMethod]
    public async Task ARefreshIsQueuedOnceAsARunNowOccurrenceAndNeverFetchesDirectly()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);

        var queued = await fixture.Operations.RefreshAsync(Refresh("refresh-1"), CancellationToken.None).ConfigureAwait(false);
        var replay = await fixture.Operations.RefreshAsync(Refresh("refresh-1"), CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Operations.RefreshAsync(Refresh("refresh-2"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsCommandStatus.Accepted, queued.Status);
        Assert.AreEqual(OrbitalElementsOperations.RefreshQueuedReasonCode, queued.ReasonCode);
        Assert.IsNull(queued.NextPermittedAttemptUtc);
        Assert.IsNotNull(queued.Refresh);
        Assert.AreEqual(OrbitalElementsCommandStatus.Replayed, replay.Status);
        Assert.AreEqual(OrbitalElementsOperations.RefreshReplayedReasonCode, replay.ReasonCode);
        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, second.Status);
        Assert.AreEqual(LocalAutomationContract.RunNowPendingReasonCode, second.ReasonCode);
        var automation = await fixture.AutomationStateAsync().ConfigureAwait(false);
        Assert.AreEqual(1, automation.QueuedRunCount);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(automation.Runs.Single().RunKey));
        Assert.AreEqual(0, fixture.Orbital.Client.Calls, "Operations only queue; the runner is the execution authority.");
        Assert.AreEqual(0L, fixture.Orbital.AttemptCount());
        CollectionAssert.AreEqual(QueuedOnly, fixture.Logger.EventIds.Where(static id => id == 7755).ToArray());
        Assert.IsFalse(fixture.Logger.Rendered().Contains("owner", StringComparison.Ordinal), "Logs never carry the actor.");
    }

    [TestMethod]
    public async Task TheQueuedRefreshRunsThroughTheRunnerAsAManualAttemptAndStatusNamesItsRun()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Accepted,
            (await fixture.Operations.RefreshAsync(Refresh("refresh-1"), CancellationToken.None).ConfigureAwait(false)).Status);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        using var runner = fixture.CreateRunner();

        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);

        var run = (await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome);
        Assert.AreEqual("Activated|orbital.activated|Manual", fixture.Orbital.LastAttempt());
        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.ReadyReasonCode, status.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.None, status.RecoveryAction,
            "A success inside the floor needs no operator action.");
        Assert.AreEqual(run.RunKey, status.Automation?.RunKey);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), status.LastActivationUtc);
        Assert.AreEqual(fixture.Clock.GetUtcNow() + Floor, status.NextPermittedAttemptUtc);
        Assert.IsNotNull(status.CurrentManifestSha256);
        Assert.IsNotNull(status.CurrentPayloadSha256);
    }

    [TestMethod]
    public async Task ARefreshInsideTheFloorIsRefusedWithTheNextPermittedAttemptAndQueuesNothing()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var next = fixture.Clock.GetUtcNow() + Floor;
        fixture.Clock.Advance(Floor - TimeSpan.FromMilliseconds(1));

        var refused = await fixture.Operations.RefreshAsync(Refresh("refresh-early"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, refused.Status);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, refused.ReasonCode);
        Assert.AreEqual(next, refused.NextPermittedAttemptUtc);
        Assert.AreEqual(next, refused.Refresh?.NextPermittedAttemptUtc);
        Assert.IsFalse((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs
            .Any(static run => run.Outcome == LocalAutomationRunOutcome.Queued));
        Assert.IsTrue(fixture.Logger.Rendered().Contains(OrbitalElementsOperations.NotYetPermittedReasonCode, StringComparison.Ordinal));

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var accepted = await fixture.Operations.RefreshAsync(Refresh("refresh-on-time"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsCommandStatus.Accepted, accepted.Status);
    }

    [TestMethod]
    public async Task AHaltBlocksRefreshUntilResumedAndResumeNeverMovesTheFloor()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Halt(429));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var haltedAt = fixture.Clock.GetUtcNow();

        var halted = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var blocked = await fixture.Operations.RefreshAsync(Refresh("refresh-halted"), CancellationToken.None).ConfigureAwait(false);
        var mismatch = await fixture.Operations.ResumeAsync(Resume(haltedAt.AddSeconds(-1), "resume-wrong"),
            CancellationToken.None).ConfigureAwait(false);
        var resumed = await fixture.Operations.ResumeAsync(Resume(haltedAt, "resume-1"), CancellationToken.None).ConfigureAwait(false);
        var replayed = await fixture.Operations.ResumeAsync(Resume(haltedAt, "resume-1"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, halted.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.ResumeProvider, halted.RecoveryAction);
        Assert.AreEqual(429, halted.ProviderHalt?.HttpStatusCode);
        Assert.AreEqual(haltedAt, halted.ProviderHalt?.SinceUtc);
        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, blocked.Status);
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, blocked.ReasonCode,
            "The halt is named before the floor, because only a resume can clear it.");
        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, mismatch.Status);
        Assert.AreEqual("orbital.halt-mismatch", mismatch.ReasonCode);
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status);
        Assert.AreEqual("orbital.resumed", resumed.ReasonCode);
        Assert.IsNull(resumed.Refresh?.ProviderHalt);
        Assert.AreEqual(halted.NextPermittedAttemptUtc, resumed.Refresh?.NextPermittedAttemptUtc);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, resumed.Refresh?.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.WaitForPermittedAttempt, resumed.Refresh?.RecoveryAction);
        Assert.AreEqual(OrbitalElementsCommandStatus.Replayed, replayed.Status);
        Assert.AreEqual("orbital.resume-replayed", replayed.ReasonCode);
        Assert.IsTrue(fixture.Logger.EventIds.Contains(7757));

        var early = await fixture.Operations.RefreshAsync(Refresh("refresh-after-resume"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, early.ReasonCode);
        Assert.AreEqual(haltedAt + Floor, early.NextPermittedAttemptUtc);
        fixture.Clock.Advance(Floor);
        Assert.AreEqual(OrbitalElementsCommandStatus.Accepted,
            (await fixture.Operations.RefreshAsync(Refresh("refresh-after-floor"), CancellationToken.None).ConfigureAwait(false)).Status);
        Assert.AreEqual(1, fixture.Orbital.Client.Calls);
    }

    [TestMethod]
    public async Task ResumeIsPermittedWhileRefreshIsDisabledAndDisabledIsNamedFirst()
    {
        var root = CreateRoot();
        DateTimeOffset haltedAt;
        using (var enabled = await OrbitalAutomationFixture.CreateAsync(root).ConfigureAwait(false))
        {
            await enabled.SaveDefinitionAsync().ConfigureAwait(false);
            enabled.Orbital.Client.Enqueue(enabled.Orbital.Halt(503));
            await enabled.Orbital.RunAsync().ConfigureAwait(false);
            haltedAt = enabled.Clock.GetUtcNow();
        }
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root, enabled: false).ConfigureAwait(false);
        fixture.Clock.Advance(haltedAt - fixture.Clock.GetUtcNow());

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var refresh = await fixture.Operations.RefreshAsync(Refresh("refresh-disabled"), CancellationToken.None).ConfigureAwait(false);
        var resumed = await fixture.Operations.ResumeAsync(Resume(haltedAt, "resume-disabled"), CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(status.Enabled);
        Assert.AreEqual(OrbitalElementsOperations.DisabledReasonCode, status.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.Configure, status.RecoveryAction);
        Assert.AreEqual(503, status.ProviderHalt?.HttpStatusCode, "The halt stays visible while disabled.");
        Assert.IsNull(status.NextDueUtc, "No attempt is due while the feature is disabled, whatever the definition says.");
        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, refresh.Status);
        Assert.AreEqual(OrbitalElementsOperations.DisabledReasonCode, refresh.ReasonCode);
        Assert.IsNull(refresh.Refresh, "A disabled refresh is refused before the store is read.");
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status);
        Assert.IsNull(resumed.Refresh?.ProviderHalt);
    }

    [TestMethod]
    [DataRow("missing-definition", OrbitalElementsOperations.DefinitionMissingReasonCode)]
    [DataRow("disabled-definition", OrbitalElementsOperations.DefinitionDisabledReasonCode)]
    public async Task AMissingOrDisabledDefinitionIsAConfigurationConflict(string scenario, string reasonCode)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        if (scenario == "disabled-definition")
        {
            await fixture.SaveDefinitionAsync(enabled: false).ConfigureAwait(false);
        }

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var refresh = await fixture.Operations.RefreshAsync(Refresh("refresh-unconfigured"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(reasonCode, status.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.Configure, status.RecoveryAction);
        Assert.IsNull(status.NextDueUtc);
        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, refresh.Status);
        Assert.AreEqual(reasonCode, refresh.ReasonCode);
        Assert.IsEmpty((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    [DataRow("blank-key", OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow("long-key", OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow("untrimmed-key", OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow("blank-actor", OrbitalElementsOperations.InvalidActorReasonCode)]
    [DataRow("long-actor", OrbitalElementsOperations.InvalidActorReasonCode)]
    [DataRow("blank-reason", OrbitalElementsOperations.InvalidReasonReasonCode)]
    [DataRow("long-reason", OrbitalElementsOperations.InvalidReasonReasonCode)]
    [DataRow("control-reason", OrbitalElementsOperations.InvalidReasonReasonCode)]
    public async Task AnInvalidRefreshRequestIsRejectedAndQueuesNothing(string scenario, string reasonCode)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var request = scenario switch
        {
            "blank-key" => Refresh(" "),
            "long-key" => Refresh(new string('k', LocalAutomationContract.MaximumIdempotencyKeyLength + 1)),
            "untrimmed-key" => Refresh(" refresh"),
            "blank-actor" => Refresh() with { Actor = "" },
            "long-actor" => Refresh() with { Actor = new string('a', LocalAutomationContract.MaximumActorLength + 1) },
            "blank-reason" => Refresh() with { Reason = "  " },
            "long-reason" => Refresh() with { Reason = new string('r', LocalAutomationContract.MaximumReasonLength + 1) },
            _ => Refresh() with { Reason = "line\nbreak" }
        };

        var result = await fixture.Operations.RefreshAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsCommandStatus.Invalid, result.Status);
        Assert.AreEqual(reasonCode, result.ReasonCode);
        Assert.IsNull(result.Refresh);
        Assert.IsEmpty((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    public async Task AnUnavailableStoreIsReportedAsRepairStorageAndRefusesCommandsUntilItRecovers()
    {
        var root = CreateRoot();
        var owner = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root, initialize: false).ConfigureAwait(false);

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var refresh = await fixture.Operations.RefreshAsync(Refresh(), CancellationToken.None).ConfigureAwait(false);
        var resume = await fixture.Operations.ResumeAsync(Resume(OrbitalElementsStoreTestData.Start, "resume-locked"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0L, status.Revision);
        Assert.IsTrue(status.Enabled);
        Assert.AreEqual("orbital.storage-locked", status.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.RepairStorage, status.RecoveryAction);
        Assert.IsNull(status.Automation);
        Assert.AreEqual(OrbitalElementsCommandStatus.Unavailable, refresh.Status);
        Assert.AreEqual("orbital.storage-locked", refresh.ReasonCode);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.RepairStorage, refresh.Refresh?.RecoveryAction);
        Assert.AreEqual(OrbitalElementsCommandStatus.Unavailable, resume.Status);
        Assert.AreEqual("orbital.storage-locked", resume.ReasonCode);
        Assert.IsTrue(fixture.Logger.EventIds.Contains(7756));
        Assert.IsTrue(fixture.Logger.EventIds.Contains(7757));

        owner.Dispose();
        var recovered = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsOperations.DefinitionMissingReasonCode, recovered.Reason);
        Assert.IsTrue(fixture.Availability.IsAvailable);
    }

    [TestMethod]
    public async Task AQuarantinedCandidateIsReportedForInspectionWithItsReason()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var invalid = Encoding.UTF8.GetBytes("{\"not\":\"an array\"}");
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(invalid));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var state = await fixture.Orbital.StateAsync().ConfigureAwait(false);

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalRefreshRecoveryAction.InspectQuarantine, status.RecoveryAction);
        Assert.AreEqual(state.LastAttemptReasonCode, status.Reason);
        StringAssert.StartsWith(status.Reason, "orbital.", StringComparison.Ordinal);
        Assert.AreEqual(CelesTrakGpTestData.Sha256(invalid), status.CandidateQuarantine?.RawSha256);
        Assert.IsNull(status.CurrentManifestSha256);
    }

    [TestMethod]
    public async Task AStorageFailureIsReportedAsRepairStorage()
    {
        var faults = new OrbitalElementsStoreTestData.Faults
        {
            ArmedPoint = OrbitalStorageFaultPoint.AfterPayload,
            Throw = static () => new IOException("synthetic disk fault")
        };
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot(), faults: faults).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalRefreshRecoveryAction.RepairStorage, status.RecoveryAction);
        StringAssert.StartsWith(status.Reason, "orbital.storage-", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AnAttemptLeftInFlightByFailedClosesIsUnsettledButARunningAttemptIsNot()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        fixture.Orbital.Client.EnqueueHang();
        var running = fixture.Orbital.RunAsync(cancellationToken: cancellation.Token).AsTask();
        await fixture.Orbital.Client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        var during = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("InFlight||Scheduled", fixture.Orbital.LastAttempt());
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, during.Reason,
            "An attempt this process is running is not an unsettled one.");
        Assert.AreEqual(OrbitalRefreshRecoveryAction.WaitForPermittedAttempt, during.RecoveryAction);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => running).ConfigureAwait(false);
        Assert.AreEqual(0L, fixture.Orbital.Job.ActivityGeneration % 2);
        // The cancelled request may have reached the provider, so it is a fail-safe halt an operator resumes.
        var halted = await fixture.Orbital.StateAsync().ConfigureAwait(false);
        Assert.AreEqual("Interrupted|orbital.provider-outcome-unknown|Scheduled", fixture.Orbital.LastAttempt());
        Assert.IsNull(halted.Halt?.HttpStatusCode);
        var resumed = await fixture.Operations.ResumeAsync(Resume(halted.Halt!.SinceUtc, "resume-cancelled"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status);

        fixture.Clock.Advance(Floor);
        fixture.Orbital.Intercept.TransportFault = static _ => new IOException("synthetic");
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Result(OrbitalFetchDisposition.TransportFailure, null,
            "orbital.transport-connection", "synthetic"));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);

        var unsettled = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("InFlight||Scheduled", fixture.Orbital.LastAttempt());
        Assert.AreEqual(OrbitalElementsOperations.AttemptUnsettledReasonCode, unsettled.Reason);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.RepairStorage, unsettled.RecoveryAction);
    }

    [TestMethod]
    [DataRow("disabled-before-halt", OrbitalElementsOperations.DisabledReasonCode, OrbitalRefreshRecoveryAction.Configure)]
    [DataRow("halt-before-quarantine", OrbitalElementsOperations.ProviderHaltedReasonCode, OrbitalRefreshRecoveryAction.ResumeProvider)]
    [DataRow("quarantine-before-unsettled", "orbital.schema", OrbitalRefreshRecoveryAction.InspectQuarantine)]
    [DataRow("quarantine-without-reason", OrbitalElementsOperations.CandidateQuarantinedReasonCode, OrbitalRefreshRecoveryAction.InspectQuarantine)]
    [DataRow("storage-without-reason", OrbitalElementsOperations.StorageFailedReasonCode, OrbitalRefreshRecoveryAction.RepairStorage)]
    [DataRow("transport-inside-floor", OrbitalElementsOperations.NotYetPermittedReasonCode, OrbitalRefreshRecoveryAction.WaitForPermittedAttempt)]
    [DataRow("unchanged-inside-floor", OrbitalElementsOperations.ReadyReasonCode, OrbitalRefreshRecoveryAction.None)]
    [DataRow("transport-after-floor", OrbitalElementsOperations.NoActiveSnapshotReasonCode, OrbitalRefreshRecoveryAction.None)]
    public async Task ReasonAndRecoveryActionFollowAFixedPrecedence(string scenario, string reasonCode,
        OrbitalRefreshRecoveryAction action)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var definition = (await fixture.SaveDefinitionAsync().ConfigureAwait(false)).State.Definitions.Single();
        var now = OrbitalElementsStoreTestData.Start;
        var halt = new OrbitalProviderHalt(now, 429, "synthetic");
        var (enabled, state, unsettled) = scenario switch
        {
            "disabled-before-halt" => (false, State(OrbitalAttemptOutcome.ProviderHalted, now + Floor, halt: halt), false),
            "halt-before-quarantine" => (true, State(OrbitalAttemptOutcome.Quarantined, now + Floor, halt: halt), false),
            "quarantine-before-unsettled" => (true, State(OrbitalAttemptOutcome.Quarantined, now + Floor, "orbital.schema"), true),
            "quarantine-without-reason" => (true, State(OrbitalAttemptOutcome.Quarantined, now + Floor), false),
            "storage-without-reason" => (true, State(OrbitalAttemptOutcome.StorageFailed, now + Floor), false),
            "transport-inside-floor" => (true, State(OrbitalAttemptOutcome.TransportFailed, now + Floor), false),
            "unchanged-inside-floor" => (true, State(OrbitalAttemptOutcome.UnchangedPayload, now + Floor, manifest: true), false),
            _ => (true, State(OrbitalAttemptOutcome.TransportFailed, now), false)
        };

        var classified = OrbitalElementsOperations.Classify(now, enabled, state, definition, unsettled);

        Assert.AreEqual((reasonCode, action), classified);
    }

    [TestMethod]
    [DataRow(0L, 0L, true)]
    [DataRow(2L, 2L, true)]
    [DataRow(1L, 1L, false)]
    [DataRow(0L, 2L, false)]
    [DataRow(1L, 2L, false)]
    public void AnInFlightAttemptIsUnsettledOnlyWhenNoOccurrenceRanAcrossTheRead(long before, long after, bool idle)
        => Assert.AreEqual(idle, OrbitalElementsRefreshJob.IsIdleAcross(before, after));

    private static OrbitalElementsRefreshRequest Refresh(string key = "refresh-1") => new(key, "owner", "operator refresh");

    private static OrbitalResumeRequest Resume(DateTimeOffset since, string key)
        => new(since, key, "owner", "provider confirmed recovery");

    private static OrbitalProviderState State(OrbitalAttemptOutcome outcome, DateTimeOffset next, string? reasonCode = null,
        OrbitalProviderHalt? halt = null, bool manifest = false)
    {
        var sha = manifest ? new string('A', 64) : null;
        return new OrbitalProviderState(3, sha, sha, manifest ? 2 : null, manifest ? 2 : null, null, null,
            OrbitalElementsStoreTestData.Start, outcome, "run", reasonCode, null, null, next, halt, null);
    }
}
