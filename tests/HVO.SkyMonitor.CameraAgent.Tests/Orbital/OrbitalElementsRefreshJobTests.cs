using System.Globalization;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsRefreshJobTests : OrbitalStoreTestBase
{
    private static readonly TimeSpan Floor = OrbitalElementsStoreTestData.Interval;
    private static readonly int[] FaultedThenClosed = [7745, 7746];

    [TestMethod]
    public async Task DisabledRefreshSkipsWithoutAdmittingOrRequesting()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot(), enabled: false).ConfigureAwait(false);

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Skipped, "orbital.disabled");
        Assert.AreEqual(0, harness.Client.Calls);
        Assert.AreEqual(0L, harness.AttemptCount());
    }

    [TestMethod]
    [DataRow(900_001, true)]
    [DataRow(900_000, false)]
    public async Task AManualRequestOlderThanItsLifetimeIsSkipped(int ageMilliseconds, bool expired)
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        var requested = harness.Clock.GetUtcNow().AddMilliseconds(-ageMilliseconds);

        var execution = await harness.RunAsync(OrbitalAttemptTrigger.Manual, requested).ConfigureAwait(false);

        if (expired)
        {
            AssertExecution(execution, LocalAutomationRunOutcome.Skipped, "orbital.manual-request-expired");
            Assert.AreEqual(0, harness.Client.Calls);
            Assert.AreEqual(0L, harness.AttemptCount());
        }
        else
        {
            AssertExecution(execution, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
            Assert.AreEqual("Activated|orbital.activated|Manual", harness.LastAttempt());
        }
    }

    [TestMethod]
    public async Task ARetrievedSnapshotActivatesAndTheSamePayloadLaterIsUnchanged()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var raw = OrbitalRefreshTestHarness.ValidPayload();
        harness.Client.Enqueue(harness.Retrieved(raw));

        var first = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(first, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
        StringAssert.Contains(first.Detail, "records=2 objects=2", StringComparison.Ordinal);
        var activated = await harness.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(2, activated.CurrentRecordCount);
        Assert.AreEqual("Activated|orbital.activated|Scheduled", harness.LastAttempt());
        StringAssert.Contains(first.Detail, "manifest=" + activated.CurrentManifestSha256, StringComparison.Ordinal);

        harness.Clock.Advance(Floor);
        harness.Client.Enqueue(harness.Retrieved(raw));
        var second = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(second, LocalAutomationRunOutcome.Succeeded, "orbital.unchanged-payload");
        var unchanged = await harness.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(activated.CurrentPayloadSha256, unchanged.CurrentPayloadSha256);
        Assert.AreEqual("UnchangedPayload|orbital.unchanged-payload|Scheduled", harness.LastAttempt());
        Assert.AreEqual(2, harness.Client.Calls);
    }

    [TestMethod]
    [DataRow(304)]
    [DataRow(403)]
    [DataRow(429)]
    [DataRow(503)]
    public async Task AnyNon200ProviderStatusHaltsProviderWideUntilResumed(int status)
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Halt(status));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        var code = string.Create(CultureInfo.InvariantCulture, $"orbital.provider-http-{status}");
        AssertExecution(execution, LocalAutomationRunOutcome.Failed, code);
        Assert.AreEqual($"ProviderHalted|{code}|Scheduled", harness.LastAttempt());
        var state = await harness.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(status, state.Halt?.HttpStatusCode);

        harness.Clock.Advance(Floor * 3);
        var halted = await harness.RunAsync().ConfigureAwait(false);
        var manual = await harness.RunAsync(OrbitalAttemptTrigger.Manual).ConfigureAwait(false);

        AssertExecution(halted, LocalAutomationRunOutcome.Skipped, "orbital.provider-halted");
        AssertExecution(manual, LocalAutomationRunOutcome.Skipped, "orbital.provider-halted");
        StringAssert.Contains(halted.Detail, "an operator resume is required", StringComparison.Ordinal);
        Assert.AreEqual(1, harness.Client.Calls, "A halted provider is never asked again.");
        Assert.AreEqual(1L, harness.AttemptCount());
    }

    [TestMethod]
    [DataRow(200, "status=200")]
    [DataRow(600, "status=600")]
    [DataRow(-1, "status=none")]
    public async Task AHaltDispositionWithoutAValidHaltStatusHaltsWithNoRecordedStatus(int status, string detail)
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Result(OrbitalFetchDisposition.HttpStatusHalt, status < 0 ? null : status,
            "orbital.invalid-http-status", "synthetic"));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.invalid-http-status");
        StringAssert.Contains(execution.Detail, detail, StringComparison.Ordinal);
        StringAssert.Contains(execution.Detail, "halted provider-wide", StringComparison.Ordinal);
        Assert.AreEqual("ProviderHalted|orbital.invalid-http-status|Scheduled", harness.LastAttempt());
        var halt = (await harness.StateAsync().ConfigureAwait(false)).Halt;
        Assert.IsNotNull(halt);
        Assert.IsNull(halt.HttpStatusCode);
        Assert.StartsWith("orbital.invalid-http-status: ", halt.Reason);

        harness.Clock.Advance(Floor * 3);
        var later = await harness.RunAsync().ConfigureAwait(false);
        AssertExecution(later, LocalAutomationRunOutcome.Skipped, "orbital.provider-halted");
        StringAssert.Contains(later.Detail, "no valid HTTP status recorded", StringComparison.Ordinal);
        Assert.AreEqual(1, harness.Client.Calls);
    }

    [TestMethod]
    [DataRow(OrbitalFetchDisposition.TransportFailure, "orbital.transport-connection", "the provider request failed")]
    [DataRow(OrbitalFetchDisposition.TimedOut, "orbital.request-timeout", "the provider request timed out")]
    public async Task TransportFailuresKeepTheLastKnownGoodSnapshotAndNeverRetryBeforeTheFloor(
        OrbitalFetchDisposition disposition, string code, string text)
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await harness.RunAsync().ConfigureAwait(false);
        var good = await harness.StateAsync().ConfigureAwait(false);
        harness.Clock.Advance(Floor);
        harness.Client.Enqueue(harness.Result(disposition, null, code, "synthetic transport detail"));

        var failed = await harness.RunAsync().ConfigureAwait(false);
        var retry = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(failed, LocalAutomationRunOutcome.Failed, code);
        StringAssert.Contains(failed.Detail, text, StringComparison.Ordinal);
        Assert.AreEqual($"TransportFailed|{code}|Scheduled", harness.LastAttempt());
        var after = await harness.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(good.CurrentManifestSha256, after.CurrentManifestSha256);
        Assert.IsNull(after.Halt);
        AssertExecution(retry, LocalAutomationRunOutcome.Skipped, "orbital.floor-not-elapsed");
        Assert.AreEqual(2, harness.Client.Calls);
    }

    [TestMethod]
    public async Task ARejectedResponseIsQuarantinedWithItsClientReason()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Result(OrbitalFetchDisposition.ResponseRejected, 200, "orbital.media-type",
            "media-type=text/html"));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.media-type");
        StringAssert.Contains(execution.Detail, "media-type=text/html", StringComparison.Ordinal);
        Assert.AreEqual("Quarantined|orbital.media-type|Scheduled", harness.LastAttempt());
    }

    [TestMethod]
    public async Task AQuarantinedValidResponseStillAdvancesTheLastSuccessfulRetrieval()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var invalid = Encoding.UTF8.GetBytes("{\"not\":\"an array\"}");
        var fetch = harness.Retrieved(invalid);
        harness.Client.Enqueue(fetch);

        var execution = await harness.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Failed, execution.Outcome);
        StringAssert.StartsWith(harness.LastAttempt(), "Quarantined|orbital.", StringComparison.Ordinal);
        var state = await harness.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(fetch.CompletedUtc, state.LastRetrievalSuccessUtc, "The provider answered with a valid 200.");
        Assert.IsNull(state.LastActivationUtc);
        Assert.AreEqual(1, harness.Intercept.Retrievals);
    }

    [TestMethod]
    public async Task InvalidContentIsQuarantinedAndNeverReplacesTheActiveSnapshot()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await harness.RunAsync().ConfigureAwait(false);
        var good = await harness.StateAsync().ConfigureAwait(false);
        harness.Clock.Advance(Floor);
        var invalid = Encoding.UTF8.GetBytes("{\"not\":\"an array\"}");
        harness.Client.Enqueue(harness.Retrieved(invalid));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Failed, execution.Outcome);
        StringAssert.StartsWith(execution.Detail, "orbital.", StringComparison.Ordinal);
        StringAssert.Contains(execution.Detail, "quarantined and never activated", StringComparison.Ordinal);
        StringAssert.StartsWith(harness.LastAttempt(), "Quarantined|orbital.", StringComparison.Ordinal);
        var after = await harness.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(good.CurrentManifestSha256, after.CurrentManifestSha256);
        Assert.AreEqual(CelesTrakGpTestData.Sha256(invalid), after.Quarantine?.RawSha256);
    }

    [TestMethod]
    public async Task AFloorMoreThanTwoMinutesAwaySkipsWithoutWaitingOrRequesting()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await harness.RunAsync().ConfigureAwait(false);
        var next = OrbitalElementsStoreTestData.Start + Floor;
        harness.Clock.Advance(Floor - OrbitalElementsRefreshJob.MaximumPrecisionWait - TimeSpan.FromMilliseconds(1));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Skipped, "orbital.floor-not-elapsed");
        StringAssert.Contains(execution.Detail, next.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.AreEqual(1, harness.Client.Calls);
        Assert.IsFalse(harness.Logger.EventIds.Contains(7742));
    }

    [TestMethod]
    public async Task AFloorWithinTwoMinutesIsWaitedOutPreciselyAndAdmittedOnce()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await harness.RunAsync().ConfigureAwait(false);
        var shortfall = TimeSpan.FromSeconds(90);
        harness.Clock.Advance(Floor - shortfall);
        harness.Client.Enqueue(_ => ValueTask.FromResult(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload(60003))));

        var run = harness.RunAsync().AsTask();
        var dueTime = await harness.Clock.WaitForTimerAsync().ConfigureAwait(false);

        Assert.AreEqual(shortfall, dueTime);
        harness.Clock.Advance(shortfall - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(run.IsCompleted, "The job must not re-admit before the floor.");
        Assert.AreEqual(1, harness.Client.Calls);

        harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var execution = await run.ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
        Assert.AreEqual(2, harness.Client.Calls);
        Assert.IsTrue(harness.Logger.EventIds.Contains(7742));
        Assert.AreEqual(2L, harness.AttemptCount());
    }

    [TestMethod]
    public async Task ACancelledFetchIsClosedAsAnUnknownOutcomeThatHaltsUntilAnOperatorResumes()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.EnqueueHang();
        using var cancellation = new CancellationTokenSource();

        var run = harness.RunAsync(cancellationToken: cancellation.Token).AsTask();
        await harness.Client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => run).ConfigureAwait(false);
        // The request may have reached the provider and been refused: fail safe, so it halts rather than retrying.
        Assert.AreEqual("Interrupted|orbital.provider-outcome-unknown|Scheduled", harness.LastAttempt());
        Assert.IsFalse(harness.Logger.EventIds.Contains(7748));
        Assert.AreEqual(1, harness.Intercept.Interruptions);
        Assert.AreEqual(0, harness.Intercept.Transports);
        var halt = (await harness.StateAsync().ConfigureAwait(false)).Halt;
        Assert.IsNotNull(halt);
        Assert.IsNull(halt.HttpStatusCode);

        harness.Clock.Advance(Floor);
        var blocked = await harness.RunAsync().ConfigureAwait(false);
        AssertExecution(blocked, LocalAutomationRunOutcome.Skipped, "orbital.provider-halted");
        var resumed = await harness.Store.ResumeProviderAsync(new OrbitalResumeRequest(halt.SinceUtc, "resume-cancelled",
            "operator@example.test", "provider access confirmed"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalCommandStatus.Applied, resumed.Status);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        var later = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(later, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
        Assert.AreEqual(2L, harness.AttemptCount());
    }

    [TestMethod]
    public async Task ACancellationAfterARecordedRetrievalIsAPlainInterruptionWithoutAHalt()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        harness.Intercept.PublishFault = _ =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.RunAsync(cancellationToken: cancellation.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual("Interrupted|orbital.attempt-cancelled|Scheduled", harness.LastAttempt());
        var state = await harness.StateAsync().ConfigureAwait(false);
        Assert.IsNull(state.Halt);
        Assert.IsNotNull(state.LastRetrievalSuccessUtc);
        harness.Intercept.PublishFault = null;
        harness.Clock.Advance(Floor);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        AssertExecution(await harness.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Succeeded, "orbital.activated");
    }

    [TestMethod]
    public async Task ACrashAfterARecordedRetrievalRestartsAsAPlainInterruptionWithoutAHalt()
    {
        var root = CreateRoot();
        using (var harness = await OrbitalRefreshTestHarness.CreateAsync(root).ConfigureAwait(false))
        {
            // Every record after the retrieval fails, so the attempt is left in flight as a crash would leave it.
            harness.Intercept.PublishFault = static _ => new InvalidOperationException("simulated process crash");
            harness.Intercept.TransportFault = static _ => new IOException("synthetic");
            harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
            await harness.RunAsync().ConfigureAwait(false);
            Assert.AreEqual("InFlight||Scheduled", harness.LastAttempt());
            Assert.AreEqual(1, harness.Intercept.Retrievals);
        }

        using var restarted = await OrbitalRefreshTestHarness.CreateAsync(root).ConfigureAwait(false);
        var state = await restarted.StateAsync().ConfigureAwait(false);

        Assert.AreEqual("Interrupted|orbital.attempt-interrupted|Scheduled", restarted.LastAttempt());
        Assert.IsNull(state.Halt);
        AssertExecution(await restarted.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.floor-not-elapsed");
        restarted.Clock.Advance(Floor);
        restarted.Client.Enqueue(restarted.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        AssertExecution(await restarted.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Succeeded, "orbital.activated");
    }

    [TestMethod]
    public async Task ACancelledFetchWhoseInterruptionCannotBeRecordedStaysInFlightAndHaltsAfterARestart()
    {
        var root = CreateRoot();
        using (var harness = await OrbitalRefreshTestHarness.CreateAsync(root).ConfigureAwait(false))
        {
            harness.Intercept.InterruptFault = static _ => new OrbitalStorageException("orbital.storage-io", "synthetic", null);
            harness.Client.EnqueueHang();
            using var cancellation = new CancellationTokenSource();
            var run = harness.RunAsync(cancellationToken: cancellation.Token).AsTask();
            await harness.Client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);

            await Assert.ThrowsAsync<OperationCanceledException>(() => run).ConfigureAwait(false);
            // No provider outcome was seen, so nothing but the fail-safe interruption may close the attempt.
            Assert.AreEqual("InFlight||Scheduled", harness.LastAttempt());
            Assert.AreEqual(1, harness.Intercept.Interruptions);
            Assert.AreEqual(0, harness.Intercept.Transports);
            Assert.AreEqual(0, harness.Intercept.StorageFailures);
            Assert.IsTrue(harness.Logger.EventIds.Contains(7748));
            AssertExecution(await harness.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.floor-not-elapsed");
            harness.Clock.Advance(Floor);
            AssertExecution(await harness.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.attempt-in-flight");
            Assert.AreEqual(1, harness.Client.Calls);
        }

        using var restarted = await OrbitalRefreshTestHarness.CreateAsync(root).ConfigureAwait(false);
        var state = await restarted.StateAsync().ConfigureAwait(false);

        Assert.AreEqual("Interrupted|orbital.provider-outcome-unknown|Scheduled", restarted.LastAttempt());
        Assert.IsNotNull(state.Halt);
        Assert.IsNull(state.Halt.HttpStatusCode);
        Assert.StartsWith("orbital.provider-outcome-unknown: ", state.Halt.Reason);
        restarted.Clock.Advance(Floor);
        AssertExecution(await restarted.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.provider-halted");
        Assert.AreEqual(0, restarted.Client.Calls);
    }

    [TestMethod]
    public async Task AThrowingClientIsClosedAsAClientErrorSoTheNextDueRunIsAdmitted()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Client.Enqueue(static _ => throw new InvalidOperationException("synthetic client defect"));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.client-error");
        StringAssert.Contains(execution.Detail, "exception=InvalidOperationException", StringComparison.Ordinal);
        Assert.IsFalse(execution.Detail.Contains("synthetic client defect", StringComparison.Ordinal));
        Assert.AreEqual("TransportFailed|orbital.client-error|Scheduled", harness.LastAttempt());
        Assert.IsTrue(harness.Logger.EventIds.Contains(7744));

        harness.Clock.Advance(Floor);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        var later = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(later, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
    }

    [TestMethod]
    public async Task AFailedTerminalRecordIsRetriedOnceUnderTheCloseDeadline()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Intercept.TransportFault = static call => call == 1
            ? new OrbitalStorageException("orbital.storage-io", "synthetic", null)
            : null;
        harness.Client.Enqueue(harness.Result(OrbitalFetchDisposition.TransportFailure, null,
            "orbital.transport-connection", "synthetic"));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.storage-io");
        Assert.AreEqual(2, harness.Intercept.Transports);
        Assert.AreEqual("TransportFailed|orbital.transport-connection|Scheduled", harness.LastAttempt());
        CollectionAssert.IsSubsetOf(FaultedThenClosed, harness.Logger.EventIds.ToArray());
    }

    [TestMethod]
    public async Task AQuarantineThatCannotBeRecordedFallsBackToATransportRecord()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Intercept.QuarantineFault = static _ => new IOException("synthetic");
        harness.Client.Enqueue(harness.Result(OrbitalFetchDisposition.ResponseRejected, 200, "orbital.media-type", "synthetic"));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.internal-error");
        Assert.AreEqual(2, harness.Intercept.Quarantines);
        Assert.AreEqual("TransportFailed|orbital.internal-error|Scheduled", harness.LastAttempt());
        Assert.IsTrue(harness.Logger.EventIds.Contains(7747));
    }

    [TestMethod]
    public async Task ARetrievalThatCannotBeRecordedIsClosedAsAStorageFailureThatKeepsTheFloorWithoutAHalt()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var harness = fixture.Orbital;
        harness.Intercept.RetrievalFault = static _ => new OrbitalStorageException("orbital.storage-no-space", "synthetic", null);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        // A local storage fault is not a transport failure and not a provider refusal.
        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.storage-no-space");
        Assert.AreEqual("StorageFailed|orbital.storage-no-space|Scheduled", harness.LastAttempt());
        Assert.AreEqual(1, harness.Intercept.StorageFailures);
        Assert.AreEqual(0, harness.Intercept.Transports);
        Assert.AreEqual(0, harness.Intercept.Publishes);
        CollectionAssert.IsSubsetOf(FaultedThenClosed, harness.Logger.EventIds.ToArray());
        var state = await harness.StateAsync().ConfigureAwait(false);
        Assert.IsNull(state.Halt);
        Assert.IsNull(state.LastRetrievalSuccessUtc, "The retrieval was never recorded, so it never advanced.");
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + Floor, state.NextPermittedAttemptUtc);

        var status = await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.RepairStorage, status.RecoveryAction);
        Assert.AreEqual("orbital.storage-no-space", status.Reason);
        var health = await new OrbitalElementsHealthEvaluator(fixture.Operations, harness.Options, fixture.Clock)
            .EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        Assert.AreEqual("orbital.storage-no-space", (string)health.Data["Reason"]);

        harness.Intercept.RetrievalFault = null;
        harness.Clock.Advance(Floor / 2);
        AssertExecution(await harness.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.floor-not-elapsed");
        Assert.AreEqual(1, harness.Client.Calls);
        harness.Clock.Advance(Floor / 2);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        AssertExecution(await harness.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Succeeded, "orbital.activated");
    }

    [TestMethod]
    public async Task AnAttemptIsLeftInFlightOnlyWhenEveryCloseRecordFails()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Intercept.TransportFault = static _ => new IOException("synthetic");
        harness.Client.Enqueue(harness.Result(OrbitalFetchDisposition.TransportFailure, null,
            "orbital.transport-connection", "synthetic"));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.internal-error");
        Assert.AreEqual(3, harness.Intercept.Transports);
        Assert.AreEqual("InFlight||Scheduled", harness.LastAttempt());
        Assert.IsTrue(harness.Logger.EventIds.Contains(7748));

        harness.Clock.Advance(Floor);
        var blocked = await harness.RunAsync().ConfigureAwait(false);
        AssertExecution(blocked, LocalAutomationRunOutcome.Skipped, "orbital.attempt-in-flight");
        Assert.AreEqual(1, harness.Client.Calls);
    }

    [TestMethod]
    public async Task AHaltThatCannotBeRecordedIsNeverDowngradedToAnotherOutcome()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Intercept.HaltFault = static _ => new OrbitalStorageException("orbital.storage-io", "synthetic", null);
        harness.Client.Enqueue(harness.Halt(429));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.storage-io");
        Assert.AreEqual(2, harness.Intercept.Halts);
        Assert.AreEqual(0, harness.Intercept.Transports);
        Assert.AreEqual(0, harness.Intercept.Quarantines);
        Assert.AreEqual(0, harness.Intercept.StorageFailures);
        Assert.AreEqual("InFlight||Scheduled", harness.LastAttempt());
        Assert.IsTrue(harness.Logger.EventIds.Contains(7748));

        harness.Clock.Advance(Floor);
        var blocked = await harness.RunAsync().ConfigureAwait(false);
        AssertExecution(blocked, LocalAutomationRunOutcome.Skipped, "orbital.attempt-in-flight");
        Assert.AreEqual(1, harness.Client.Calls, "A provider that refused is never asked again while its halt is pending.");
    }

    [TestMethod]
    public async Task AHaltThatCannotBeRecordedBecomesADurableHaltAfterARestartUntilResumed()
    {
        var root = CreateRoot();
        using (var harness = await OrbitalRefreshTestHarness.CreateAsync(root).ConfigureAwait(false))
        {
            harness.Intercept.HaltFault = static _ => new OrbitalStorageException("orbital.storage-io", "synthetic", null);
            harness.Client.Enqueue(harness.Halt(429));
            await harness.RunAsync().ConfigureAwait(false);
            Assert.AreEqual("InFlight||Scheduled", harness.LastAttempt());
        }

        // The process restarts: a new store over the same root reconciles the attempt whose 429 was never recorded.
        using var restarted = await OrbitalRefreshTestHarness.CreateAsync(root).ConfigureAwait(false);
        var state = await restarted.StateAsync().ConfigureAwait(false);
        var admission = await restarted.Store.TryBeginAttemptAsync("after-restart", OrbitalAttemptTrigger.Manual,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("Interrupted|orbital.provider-outcome-unknown|Scheduled", restarted.LastAttempt());
        Assert.AreEqual(OrbitalAdmissionStatus.Halted, admission.Status);
        Assert.IsNotNull(state.Halt);
        Assert.IsNull(state.Halt.HttpStatusCode, "The refused status was never recorded, so none is claimed.");
        Assert.StartsWith("orbital.provider-outcome-unknown: ", state.Halt.Reason);
        restarted.Clock.Advance(Floor * 3);
        AssertExecution(await restarted.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.provider-halted");
        Assert.AreEqual(0, restarted.Client.Calls);

        var resumed = await restarted.Store.ResumeProviderAsync(new OrbitalResumeRequest(state.Halt.SinceUtc, "resume-restart",
            "operator@example.test", "provider access confirmed"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalCommandStatus.Applied, resumed.Status);
        Assert.IsNull(resumed.State.Halt);
        restarted.Client.Enqueue(restarted.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        AssertExecution(await restarted.RunAsync().ConfigureAwait(false), LocalAutomationRunOutcome.Succeeded, "orbital.activated");
    }

    [TestMethod]
    public async Task ACandidateTheStoreRefusesToBindIsQuarantined()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Intercept.PublishFault = static _ => new ArgumentException("synthetic binding", "candidate");
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.candidate-rejected");
        StringAssert.Contains(execution.Detail, "exception=ArgumentException; parameter=candidate", StringComparison.Ordinal);
        Assert.AreEqual("Quarantined|orbital.candidate-rejected|Scheduled", harness.LastAttempt());
        Assert.IsNull((await harness.StateAsync().ConfigureAwait(false)).CurrentManifestSha256);
    }

    [TestMethod]
    public async Task APublicationStorageFaultIsSettledByTheStoreAndALaterRunIsAdmitted()
    {
        var faults = new OrbitalElementsStoreTestData.Faults
        {
            ArmedPoint = OrbitalStorageFaultPoint.AfterPayload,
            Throw = static () => new IOException("synthetic disk fault")
        };
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot(), faults: faults).ConfigureAwait(false);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Failed, execution.Outcome);
        StringAssert.StartsWith(execution.Detail, "orbital.storage-", StringComparison.Ordinal);
        StringAssert.Contains(execution.Detail, "the last-known-good snapshot remains active", StringComparison.Ordinal);
        StringAssert.StartsWith(harness.LastAttempt(), "StorageFailed|orbital.storage-", StringComparison.Ordinal);
        Assert.IsFalse(harness.Logger.EventIds.Contains(7748));

        harness.Clock.Advance(Floor);
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        var later = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(later, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
    }

    [TestMethod]
    public async Task ARetentionFailureNeverFailsAnActivation()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        harness.Intercept.RetentionFault = static _ => new IOException("synthetic");
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Succeeded, "orbital.activated");
        Assert.IsTrue(harness.Logger.EventIds.Contains(7750));
    }

    [TestMethod]
    public async Task AnUnusableStoreFailsAdmissionWithoutARequest()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(root, initialize: false).ConfigureAwait(false);

        var execution = await harness.RunAsync().ConfigureAwait(false);

        AssertExecution(execution, LocalAutomationRunOutcome.Failed, "orbital.storage-locked");
        StringAssert.Contains(execution.Detail, "no request was made", StringComparison.Ordinal);
        Assert.AreEqual(0, harness.Client.Calls);
        Assert.IsTrue(harness.Logger.EventIds.Contains(7743));
    }

    [TestMethod]
    public async Task ConcurrentOccurrencesRunOneAtATime()
    {
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.Enqueue(async _ =>
        {
            await release.Task.ConfigureAwait(false);
            return harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload());
        });

        var first = harness.RunAsync().AsTask();
        await harness.Client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var second = harness.RunAsync(OrbitalAttemptTrigger.Manual).AsTask();
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(second.IsCompleted);
        release.SetResult();

        AssertExecution(await first.ConfigureAwait(false), LocalAutomationRunOutcome.Succeeded, "orbital.activated");
        AssertExecution(await second.ConfigureAwait(false), LocalAutomationRunOutcome.Skipped, "orbital.floor-not-elapsed");
        Assert.AreEqual(1, harness.Client.Calls);
    }

    [TestMethod]
    [DataRow(99, false)]
    [DataRow(100, true)]
    [DataRow(200, false)]
    [DataRow(304, true)]
    [DataRow(599, true)]
    [DataRow(600, false)]
    public void OnlyNon200StatusesInTheHttpRangeHalt(int status, bool halts)
        => Assert.AreEqual(halts, OrbitalElementsRefreshJob.IsHaltStatus(status));

    [TestMethod]
    [DataRow(0L, 0L)]
    [DataRow(1L, 10_000L)]
    [DataRow(10_000L, 10_000L)]
    [DataRow(10_001L, 20_000L)]
    public void PrecisionWaitsRoundUpToWholeMilliseconds(long ticks, long expected)
        => Assert.AreEqual(expected, OrbitalElementsRefreshJob.RoundUpToMillisecond(TimeSpan.FromTicks(ticks)).Ticks);

    [TestMethod]
    public void DetailsAreSanitizedAndBoundedToTheRunDetailLimit()
    {
        var bounded = OrbitalElementsRefreshJob.Describe("orbital.x", new string('a', 600));
        var sanitized = OrbitalElementsRefreshJob.Describe("orbital.x", "line\nbreak\u0007 ");
        var surrogate = OrbitalElementsRefreshJob.Describe("orbital.x", new string('a', 500) + "\U0001F6F0\U0001F6F0\U0001F6F0");

        Assert.AreEqual(LocalAutomationContract.MaximumDetailLength, bounded.Length);
        StringAssert.StartsWith(bounded, "orbital.x: ", StringComparison.Ordinal);
        Assert.AreEqual("orbital.x: line break", sanitized);
        Assert.IsTrue(surrogate.Length <= LocalAutomationContract.MaximumDetailLength);
        Assert.IsFalse(char.IsHighSurrogate(surrogate[^1]));
    }

    private static void AssertExecution(LocalAutomationExecution execution, LocalAutomationRunOutcome outcome, string code)
    {
        Assert.AreEqual(outcome, execution.Outcome, execution.Detail);
        StringAssert.StartsWith(execution.Detail, code + ": ", StringComparison.Ordinal);
        Assert.IsTrue(execution.Detail.Length <= LocalAutomationContract.MaximumDetailLength);
    }
}
