using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// The issue #528 acceptance cases, end to end through the host composition: the real automation runner sweeping the
/// real automation store, the reserved definition validated by the real adapter, the refresh job, the real CelesTrak
/// client and normalizer, the durable orbital store and the snapshot reader. Only the clock and the client's primary
/// HTTP handler are substituted; the handler serves the S2 synthetic fixtures and never reaches a network.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsEndToEndTests : OrbitalStoreTestBase
{
    private const int SixHours = 6 * 60 * 60;
    private const string UnknownMemberFixture = "invalid-unknown-member.json";
    private static readonly TimeSpan Floor = SqliteOrbitalElementsStore.MinimumProviderAttemptInterval;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task RefreshIsDisabledByDefaultAndADueRunMakesNoRequest()
    {
        var host = await StartAsync(CreateRoot(), enabled: null).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);

        var definition = await host.SaveDefinitionAsync().ConfigureAwait(false);
        host.AdvanceTo(definition.NextRunUtc!.Value);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, run.Outcome);
        AssertDetail(OrbitalElementsOperations.DisabledReasonCode, run);
        Assert.AreEqual(0, host.Handler.Count);
        Assert.AreEqual(0L, host.Scalar("SELECT COUNT(*) FROM orbital_attempts;"));
        var refresh = await host.RefreshAsync("refresh-disabled").ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, refresh.Status);
        Assert.AreEqual(OrbitalElementsOperations.DisabledReasonCode, refresh.ReasonCode);
        var health = await host.Health.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, health.State);
        Assert.AreEqual("Disabled", health.Data["Availability"]);
        var current = await host.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalSnapshotUnavailableReason.NoActiveSnapshot, current.UnavailableReason);
        Assert.AreEqual(0, host.Handler.Count);
    }

    [TestMethod]
    public async Task TheSavedDefinitionIsPhasedWithinThirtyMinutesOfTheSave()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var now = host.Clock.GetUtcNow();

        var definition = await host.SaveDefinitionAsync().ConfigureAwait(false);

        var epoch = definition.Definition.TriggerEpochUtc;
        Assert.IsGreaterThanOrEqualTo(now, epoch);
        Assert.IsLessThan(now + TimeSpan.FromMinutes(30), epoch);
        Assert.AreEqual(TruncateToMillisecond(now + host.Store.PhaseOffset), epoch);
        Assert.AreEqual(epoch.AddSeconds(OrbitalElementsAutomationAdapter.MinimumIntervalSeconds), definition.NextRunUtc);
    }

    [TestMethod]
    public async Task TheFirstDueRunActivatesAndTheReaderServesItByCurrentAndExactIdentity()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = (await host.SaveDefinitionAsync().ConfigureAwait(false)).NextRunUtc!.Value;

        host.AdvanceTo(due - TimeSpan.FromMilliseconds(1));
        await host.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(0, host.Handler.Count, "Nothing runs before the first boundary.");
        Assert.IsEmpty(await host.RunsAsync().ConfigureAwait(false));

        host.AdvanceTo(due);
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome, run.Detail);
        AssertDetail("orbital.activated", run);
        Assert.EndsWith("|p1", run.RunKey, StringComparison.Ordinal);
        var request = host.Handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(CelesTrakGpSource.RequestUri, request.RequestUri);
        Assert.IsFalse(request.HasContent);
        Assert.AreEqual(OrbitalElementsServiceCollectionExtensions.UserAgent, Header(request, "User-Agent"));
        foreach (var forbidden in new[] { "Cookie", "If-None-Match", "If-Modified-Since", "Authorization" })
        {
            Assert.IsNull(Header(request, forbidden), forbidden);
        }

        var state = await host.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Activated, state.LastAttemptOutcome);
        Assert.AreEqual(8, state.CurrentRecordCount);
        Assert.AreEqual(7, state.CurrentObjectCount);
        Assert.AreEqual(due, state.LastActivationUtc);
        Assert.AreEqual(due + Floor, state.NextPermittedAttemptUtc);
        Assert.AreEqual("Activated|orbital.activated|Scheduled", host.Text(LastAttemptSql));

        var current = await host.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(current.UnavailableReason);
        var snapshot = current.Snapshot!;
        Assert.AreEqual(state.CurrentManifestSha256, snapshot.Manifest.ManifestSha256);
        Assert.AreEqual(state.CurrentPayloadSha256, snapshot.Manifest.NormalizedPayloadSha256);
        Assert.AreEqual(8, snapshot.Manifest.RecordCount);
        Assert.AreEqual(7, snapshot.Manifest.ObjectCount);
        Assert.HasCount(8, snapshot.Records);
        var exact = await host.Reader.ReadExactAsync(state.CurrentManifestSha256!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(state.CurrentManifestSha256, exact.Snapshot?.Manifest.ManifestSha256);
        var unknown = await host.Reader.ReadExactAsync(new string('A', 64), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalSnapshotUnavailableReason.UnknownIdentity, unknown.UnavailableReason);

        var status = await host.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.ReadyReasonCode, status.Reason);
        Assert.AreEqual(run.RunKey, status.Automation?.RunKey);
        var health = await host.Health.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, health.State);
        Assert.AreEqual(8, health.Data["ActiveRecordCount"]);
        Assert.AreEqual(1, host.Handler.Count);
    }

    [TestMethod]
    public async Task AnIdenticalBodyIsAnUnchangedPayloadWithItsOwnManifestAndHistoryRowOverASharedPayload()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = await ActivateFirstAsync(host).ConfigureAwait(false);
        var first = await host.StateAsync().ConfigureAwait(false);

        host.AdvanceTo(due + Floor);
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome, run.Detail);
        AssertDetail("orbital.unchanged-payload", run);
        var second = await host.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.UnchangedPayload, second.LastAttemptOutcome);
        Assert.AreNotEqual(first.CurrentManifestSha256, second.CurrentManifestSha256, "Each retrieval has its own manifest.");
        Assert.AreEqual(first.CurrentPayloadSha256, second.CurrentPayloadSha256);
        Assert.AreEqual("activated,unchanged-payload", host.Text(
            "SELECT group_concat(kind, ',') FROM (SELECT kind FROM orbital_activation_history ORDER BY history_sequence);"));
        Assert.AreEqual(second.CurrentManifestSha256, host.Text(
            "SELECT manifest_sha256 FROM orbital_activation_history ORDER BY history_sequence DESC LIMIT 1;"));
        Assert.AreEqual(2L, host.Scalar("SELECT COUNT(*) FROM orbital_snapshots;"));
        Assert.AreEqual(1L, host.Scalar("SELECT COUNT(DISTINCT payload_sha256) FROM orbital_snapshots;"));
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(host.OrbitalRoot, "payloads")), "The payload is stored once.");
        Assert.HasCount(2, Directory.GetFiles(Path.Combine(host.OrbitalRoot, "manifests")));

        var current = await host.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        var previous = await host.Reader.ReadExactAsync(first.CurrentManifestSha256!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(second.CurrentManifestSha256, current.Snapshot?.Manifest.ManifestSha256);
        Assert.AreEqual(first.CurrentManifestSha256, previous.Snapshot?.Manifest.ManifestSha256);
        Assert.AreEqual(previous.Snapshot?.Manifest.NormalizedPayloadSha256, current.Snapshot?.Manifest.NormalizedPayloadSha256);
        Assert.AreEqual(2, host.Handler.Count);
    }

    [TestMethod]
    [DataRow(100)]
    [DataRow(204)]
    [DataRow(206)]
    [DataRow(301)]
    [DataRow(304)]
    [DataRow(307)]
    [DataRow(400)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(503)]
    [DataRow(599)]
    public async Task EveryNon200StatusHaltsTheProviderWideAndNothingIsRequestedWhileHalted(int status)
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = (await host.SaveDefinitionAsync().ConfigureAwait(false)).NextRunUtc!.Value;

        host.AdvanceTo(due);
        host.Handler.EnqueueStatus(status);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false)).Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, run.Outcome);
        AssertDetail(string.Create(CultureInfo.InvariantCulture, $"orbital.provider-http-{status}"), run);
        var state = await host.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.ProviderHalted, state.LastAttemptOutcome);
        Assert.AreEqual(status, state.Halt?.HttpStatusCode);
        Assert.AreEqual(due, state.Halt?.SinceUtc);
        Assert.AreEqual((long)status, host.Scalar("SELECT http_status FROM orbital_attempts;"));

        var refresh = await host.RefreshAsync("refresh-halted").ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, refresh.ReasonCode);
        foreach (var later in new[] { due + Floor, due + (2 * Floor) })
        {
            host.AdvanceTo(later);
            await host.SweepAsync().ConfigureAwait(false);
            var skipped = (await host.RunsAsync().ConfigureAwait(false))[^1];
            Assert.AreEqual(LocalAutomationRunOutcome.Skipped, skipped.Outcome);
            AssertDetail(OrbitalElementsOperations.ProviderHaltedReasonCode, skipped);
        }
        var direct = await host.Provider.GetRequiredService<OrbitalElementsRefreshJob>().ExecuteAsync("halt-probe",
            OrbitalAttemptTrigger.Manual, host.Clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, direct.Outcome);
        Assert.StartsWith(OrbitalElementsOperations.ProviderHaltedReasonCode + ":", direct.Detail, StringComparison.Ordinal);
        Assert.AreEqual(1, host.Handler.Count, "A halt stops every later request, scheduled or manual.");
        Assert.AreEqual(1L, host.Scalar("SELECT COUNT(*) FROM orbital_attempts;"));
        var health = await host.Health.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        Assert.AreEqual(status, health.Data["HaltHttpStatus"]);
    }

    [TestMethod]
    public async Task ResumeClearsTheHaltButTheProviderFloorStillApplies()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = (await host.SaveDefinitionAsync().ConfigureAwait(false)).NextRunUtc!.Value;
        host.AdvanceTo(due);
        host.Handler.EnqueueStatus(429);
        await host.SweepAsync().ConfigureAwait(false);

        host.AdvanceTo(due + TimeSpan.FromMinutes(10));
        var resumed = await host.Operations.ResumeAsync(new OrbitalResumeRequest(due, "resume-1", "owner", "provider recovered"),
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status, resumed.ReasonCode);
        var state = await host.StateAsync().ConfigureAwait(false);
        Assert.IsNull(state.Halt);
        Assert.AreEqual(due + Floor, state.NextPermittedAttemptUtc, "Resume never moves the floor.");
        var early = await host.RefreshAsync("refresh-after-resume").ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, early.ReasonCode);
        Assert.AreEqual(due + Floor, early.NextPermittedAttemptUtc);
        await host.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(1, host.Handler.Count);

        host.AdvanceTo(due + Floor);
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome, run.Detail);
        AssertDetail("orbital.activated", run);
        Assert.AreEqual(2, host.Handler.Count);
    }

    [TestMethod]
    public async Task AManualRefreshIsRefusedInsideTheFloorAndRunsOnceAsARunNowAfterIt()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = await ActivateFirstAsync(host, SixHours).ConfigureAwait(false);

        host.AdvanceTo(due + TimeSpan.FromHours(1));
        var early = await host.RefreshAsync("refresh-early").ConfigureAwait(false);
        await host.SweepAsync().ConfigureAwait(false);

        Assert.AreEqual(OrbitalElementsCommandStatus.Conflict, early.Status);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, early.ReasonCode);
        Assert.AreEqual(due + Floor, early.NextPermittedAttemptUtc);
        Assert.HasCount(1, await host.RunsAsync().ConfigureAwait(false));
        Assert.AreEqual(1, host.Handler.Count);

        host.AdvanceTo(due + Floor);
        var accepted = await host.RefreshAsync("refresh-on-time").ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Accepted, accepted.Status, accepted.ReasonCode);
        host.Handler.EnqueueBody(OrbitalRefreshTestHarness.ValidPayload(60001, 60002));
        await host.SweepAsync().ConfigureAwait(false);
        await host.SweepAsync().ConfigureAwait(false);

        var runs = await host.RunsAsync().ConfigureAwait(false);
        Assert.HasCount(2, runs);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(runs[^1].RunKey));
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, runs[^1].Outcome, runs[^1].Detail);
        Assert.AreEqual("Activated|orbital.activated|Manual", host.Text(LastAttemptSql));
        Assert.AreEqual(2, host.Handler.Count, "The run-now occurrence ran exactly once.");
        Assert.AreEqual(2, (await host.StateAsync().ConfigureAwait(false)).CurrentRecordCount);
        var again = await host.RefreshAsync("refresh-again").ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, again.ReasonCode);
        Assert.AreEqual(due + (2 * Floor), again.NextPermittedAttemptUtc);
    }

    [TestMethod]
    public async Task ATimeoutAndATransportFailureKeepLastKnownGoodAndAreNotRetriedBeforeTheNextPermittedAttempt()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = await ActivateFirstAsync(host).ConfigureAwait(false);
        var good = (await host.StateAsync().ConfigureAwait(false)).CurrentManifestSha256;

        var second = due + Floor;
        host.AdvanceTo(second);
        host.Handler.EnqueueHang();
        host.Handler.ResetSignal();
        var sweep = host.SweepAsync();
        await host.Handler.RequestStarted.Task.WaitAsync(Deadline).ConfigureAwait(false);
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await sweep.WaitAsync(Deadline).ConfigureAwait(false);

        var timedOut = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, timedOut.Outcome);
        AssertDetail("orbital.request-timeout", timedOut);
        await AssertLastKnownGoodAsync(host, good, OrbitalAttemptOutcome.TransportFailed, second + Floor).ConfigureAwait(false);
        await AssertNoRetryAsync(host, second, requests: 2).ConfigureAwait(false);

        var third = second + Floor;
        host.AdvanceTo(third);
        host.Handler.EnqueueTransportFailure();
        await host.SweepAsync().ConfigureAwait(false);

        var failed = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome);
        AssertDetail("orbital.transport-connection", failed);
        await AssertLastKnownGoodAsync(host, good, OrbitalAttemptOutcome.TransportFailed, third + Floor).ConfigureAwait(false);
        await AssertNoRetryAsync(host, third, requests: 3).ConfigureAwait(false);

        host.AdvanceTo(third + Floor);
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await host.RunsAsync().ConfigureAwait(false))[^1].Outcome);
        Assert.AreEqual(4, host.Handler.Count);
    }

    [TestMethod]
    [DataRow(UnknownMemberFixture, "orbital.unknown-member")]
    [DataRow("invalid-duplicate-record.json", "orbital.duplicate-record")]
    public async Task InvalidContentIsQuarantinedAndLastKnownGoodStaysActive(string fixture, string reasonCode)
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = await ActivateFirstAsync(host).ConfigureAwait(false);
        var good = (await host.StateAsync().ConfigureAwait(false)).CurrentManifestSha256;

        host.AdvanceTo(due + Floor);
        host.Handler.EnqueueFixture(fixture);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, run.Outcome);
        AssertDetail(reasonCode, run);
        var state = await host.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Quarantined, state.LastAttemptOutcome);
        Assert.AreEqual(reasonCode, state.LastAttemptReasonCode);
        Assert.IsNotNull(state.Quarantine);
        Assert.IsNull(state.Halt, "Invalid content in a valid 200 is quarantined, never a provider halt.");
        Assert.AreEqual(due + Floor, state.LastRetrievalSuccessUtc, "The provider still answered with a valid 200.");
        Assert.AreEqual(good, state.CurrentManifestSha256);
        var quarantine = Path.Combine(host.OrbitalRoot, "quarantine");
        Assert.HasCount(1, Directory.Exists(quarantine) ? Directory.GetFiles(quarantine) : []);
        var current = await host.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(good, current.Snapshot?.Manifest.ManifestSha256);
        var health = await host.Health.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        Assert.AreEqual(reasonCode, health.Data["Reason"]);
    }

    [TestMethod]
    public async Task AnInvalidHttpStatusHaltsTheProviderWithNoRecordedStatusAndKeepsLastKnownGood()
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = await ActivateFirstAsync(host).ConfigureAwait(false);
        var good = (await host.StateAsync().ConfigureAwait(false)).CurrentManifestSha256;
        var second = due + Floor;

        host.AdvanceTo(second);
        host.Handler.EnqueueStatus(600);
        await host.SweepAsync().ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, run.Outcome);
        AssertDetail("orbital.invalid-http-status", run);
        var state = await host.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.ProviderHalted, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.invalid-http-status", state.LastAttemptReasonCode);
        Assert.IsNotNull(state.Halt);
        Assert.IsNull(state.Halt.HttpStatusCode, "An out-of-range status is never recorded as an HTTP status.");
        Assert.AreEqual(second, state.Halt.SinceUtc);
        Assert.IsNull(state.Quarantine);
        Assert.AreEqual(due, state.LastRetrievalSuccessUtc, "Only a valid 200 is a successful retrieval.");
        Assert.AreEqual(0L, host.Scalar("SELECT COUNT(*) FROM orbital_attempts WHERE http_status IS NOT NULL;"));
        await AssertLastKnownGoodAsync(host, good, OrbitalAttemptOutcome.ProviderHalted, second + Floor).ConfigureAwait(false);
        var health = await host.Health.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, health.Data["Reason"]);
        Assert.IsTrue((bool)health.Data["Halted"]);
        Assert.IsFalse(health.Data.ContainsKey("HaltHttpStatus"), "No status was recorded, so none is reported.");

        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode,
            (await host.RefreshAsync("refresh-invalid-status").ConfigureAwait(false)).ReasonCode);
        host.AdvanceTo(second + Floor);
        await host.SweepAsync().ConfigureAwait(false);
        AssertDetail(OrbitalElementsOperations.ProviderHaltedReasonCode, (await host.RunsAsync().ConfigureAwait(false))[^1]);
        Assert.AreEqual(2, host.Handler.Count, "Nothing is requested again until an operator resumes.");

        var resumed = await host.Operations.ResumeAsync(new OrbitalResumeRequest(second, "resume-invalid-status", "owner",
            "provider status confirmed"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status, resumed.ReasonCode);
        host.AdvanceTo(second + (2 * Floor));
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);
        var after = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, after.Outcome, after.Detail);
        Assert.AreEqual(3, host.Handler.Count);
    }

    [TestMethod]
    public async Task ACrashBetweenAttemptBeginAndCompletionHaltsOnRestartUntilResumedAndStillCountsTowardTheFloor()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        DateTimeOffset second;
        string? good;
        var before = await StartAsync(root, clock: clock).ConfigureAwait(false);
        await using (before.ConfigureAwait(false))
        {
            var due = await ActivateFirstAsync(before).ConfigureAwait(false);
            good = (await before.StateAsync().ConfigureAwait(false)).CurrentManifestSha256;
            second = due + Floor;
            before.AdvanceTo(second);
            var admission = await before.Store.TryBeginAttemptAsync("crash-probe", OrbitalAttemptTrigger.Scheduled,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admission.Status);
        }

        var after = await StartAsync(root, clock: clock).ConfigureAwait(false);
        await using var lifetime = after.ConfigureAwait(false);

        // The crashed attempt may have reached a provider that refused it, so the restart fails safe and halts.
        var state = await after.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Interrupted, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.provider-outcome-unknown", state.LastAttemptReasonCode);
        Assert.IsNotNull(state.Halt);
        Assert.IsNull(state.Halt.HttpStatusCode, "The outcome is unknown, so no HTTP status is claimed.");
        Assert.StartsWith("orbital.provider-outcome-unknown: ", state.Halt.Reason);
        await AssertLastKnownGoodAsync(after, good, OrbitalAttemptOutcome.Interrupted, second + Floor).ConfigureAwait(false);
        var health = await after.Health.EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, health.State);
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, health.Data["Reason"]);
        Assert.IsFalse(health.Data.ContainsKey("HaltHttpStatus"));
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode,
            (await after.RefreshAsync("refresh-after-crash").ConfigureAwait(false)).ReasonCode,
            "A manual refresh never bypasses the unknown-outcome hold.");

        after.AdvanceTo(second + Floor);
        await after.SweepAsync().ConfigureAwait(false);
        var skipped = (await after.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, skipped.Outcome);
        AssertDetail(OrbitalElementsOperations.ProviderHaltedReasonCode, skipped);
        Assert.AreEqual(0, after.Handler.Count, "Nothing is retried automatically once the floor elapses.");

        var resumed = await after.Operations.ResumeAsync(new OrbitalResumeRequest(state.Halt.SinceUtc, "resume-after-crash",
            "owner", "provider access confirmed"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status, resumed.ReasonCode);
        after.AdvanceTo(second + (2 * Floor));
        after.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await after.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await after.RunsAsync().ConfigureAwait(false))[^1].Outcome);
        Assert.AreEqual(1, after.Handler.Count);
    }

    [TestMethod]
    public async Task ARestartPreservesTheHaltTheFloorTheScheduleAndTheHistory()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        OrbitalProviderState halted;
        LocalAutomationDefinitionState definition;
        DateTimeOffset second;
        var before = await StartAsync(root, clock: clock).ConfigureAwait(false);
        await using (before.ConfigureAwait(false))
        {
            var due = await ActivateFirstAsync(before).ConfigureAwait(false);
            second = due + Floor;
            before.AdvanceTo(second);
            before.Handler.EnqueueStatus(503);
            await before.SweepAsync().ConfigureAwait(false);
            halted = await before.StateAsync().ConfigureAwait(false);
            definition = await before.DefinitionAsync().ConfigureAwait(false);
        }

        var after = await StartAsync(root, clock: clock).ConfigureAwait(false);
        await using var lifetime = after.ConfigureAwait(false);

        var state = await after.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(halted.Halt, state.Halt);
        Assert.AreEqual(503, state.Halt?.HttpStatusCode);
        Assert.AreEqual(halted.NextPermittedAttemptUtc, state.NextPermittedAttemptUtc);
        Assert.AreEqual(halted.CurrentManifestSha256, state.CurrentManifestSha256);
        Assert.AreEqual(2L, after.Scalar("SELECT COUNT(*) FROM orbital_attempts;"));
        Assert.AreEqual("activated", after.Text("SELECT group_concat(kind, ',') FROM orbital_activation_history;"));
        var reloaded = await after.DefinitionAsync().ConfigureAwait(false);
        Assert.AreEqual(definition.Definition.TriggerEpochUtc, reloaded.Definition.TriggerEpochUtc);
        Assert.AreEqual(definition.Version, reloaded.Version);
        var current = await after.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(halted.CurrentManifestSha256, current.Snapshot?.Manifest.ManifestSha256,
            "Last-known-good stays readable while halted.");

        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode,
            (await after.RefreshAsync("refresh-after-restart").ConfigureAwait(false)).ReasonCode);
        after.AdvanceTo(second + Floor);
        await after.SweepAsync().ConfigureAwait(false);
        AssertDetail(OrbitalElementsOperations.ProviderHaltedReasonCode, (await after.RunsAsync().ConfigureAwait(false))[^1]);
        Assert.AreEqual(0, after.Handler.Count);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(90)]
    [DataRow(120)]
    public async Task APollSlipWithinThePrecisionWindowWaitsExactlyThatLongOnTheClock(int slipSeconds)
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var slip = TimeSpan.FromSeconds(slipSeconds);
        var due = (await host.SaveDefinitionAsync().ConfigureAwait(false)).NextRunUtc!.Value;
        host.AdvanceTo(due + slip);
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);

        host.AdvanceTo(due + Floor);
        host.Clock.DrainCreatedTimers();
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        var sweep = host.SweepAsync();
        var wait = await host.Clock.WaitForTimerAsync().ConfigureAwait(false);

        Assert.AreEqual(slip, wait);
        Assert.AreEqual(1, host.Handler.Count, "Nothing is requested before the floor.");
        host.Clock.Advance(wait);
        await sweep.WaitAsync(Deadline).ConfigureAwait(false);

        var run = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome, run.Detail);
        Assert.AreEqual(2, host.Handler.Count);
        Assert.AreEqual((due + Floor + slip).ToUnixTimeMilliseconds(), host.Scalar(
            "SELECT started_unix_ms FROM orbital_attempts ORDER BY attempt_sequence DESC LIMIT 1;"));
    }

    [TestMethod]
    [DataRow(121)]
    [DataRow(200)]
    public async Task APollSlipBeyondThePrecisionWindowSkipsTheOccurrenceWithoutARequest(int slipSeconds)
    {
        var host = await StartAsync(CreateRoot()).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var due = (await host.SaveDefinitionAsync().ConfigureAwait(false)).NextRunUtc!.Value;
        host.AdvanceTo(due + TimeSpan.FromSeconds(slipSeconds));
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);

        host.AdvanceTo(due + Floor);
        await host.SweepAsync().ConfigureAwait(false);

        var skipped = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, skipped.Outcome);
        AssertDetail("orbital.floor-not-elapsed", skipped);
        Assert.Contains((due + Floor + TimeSpan.FromSeconds(slipSeconds)).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            skipped.Detail, StringComparison.Ordinal);
        Assert.AreEqual(1, host.Handler.Count);

        host.AdvanceTo(due + (2 * Floor));
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await host.RunsAsync().ConfigureAwait(false))[^1].Outcome);
        Assert.AreEqual(2, host.Handler.Count);
    }

    [TestMethod]
    public async Task TheReaderServesLastKnownGoodWithoutEverReachingTheProvider()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        string? good;
        var before = await StartAsync(root, clock: clock).ConfigureAwait(false);
        await using (before.ConfigureAwait(false))
        {
            await ActivateFirstAsync(before).ConfigureAwait(false);
            good = (await before.StateAsync().ConfigureAwait(false)).CurrentManifestSha256;
        }

        var offline = await StartAsync(root, enabled: false, clock: clock).ConfigureAwait(false);
        await using var lifetime = offline.ConfigureAwait(false);
        var current = await offline.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        var exact = await offline.Reader.ReadExactAsync(good!, CancellationToken.None).ConfigureAwait(false);
        var again = await offline.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(good, current.Snapshot?.Manifest.ManifestSha256);
        Assert.AreEqual(good, exact.Snapshot?.Manifest.ManifestSha256);
        Assert.AreEqual(good, again.Snapshot?.Manifest.ManifestSha256);
        Assert.AreEqual(0, offline.Handler.Count, "Reading a snapshot never issues a provider request.");

        var forbidden = new[] { typeof(HttpClient), typeof(HttpMessageHandler), typeof(IHttpClientFactory),
            typeof(IOrbitalElementsProviderClient), typeof(OrbitalElementsRefreshJob) };
        var dependencies = typeof(OrbitalSnapshotReader)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(static constructor => constructor.GetParameters().Select(static parameter => parameter.ParameterType))
            .Concat(typeof(OrbitalSnapshotReader)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(static field => field.FieldType))
            .ToArray();
        foreach (var type in dependencies)
        {
            Assert.IsFalse(forbidden.Any(item => item.IsAssignableFrom(type)), type.FullName);
        }
    }

    private const string LastAttemptSql =
        "SELECT outcome || '|' || coalesce(reason_code, '') || '|' || trigger_kind FROM orbital_attempts ORDER BY attempt_sequence DESC LIMIT 1;";

    private static Task<OrbitalCompositionHost> StartAsync(string root, bool? enabled = true, SignalingTimeProvider? clock = null)
        => OrbitalCompositionHost.StartAsync(root, clock ?? new SignalingTimeProvider(OrbitalElementsStoreTestData.Start), enabled);

    /// <summary>Saves the reserved definition and activates the valid fixture at its first boundary, which it returns.</summary>
    private static async Task<DateTimeOffset> ActivateFirstAsync(OrbitalCompositionHost host,
        int interval = OrbitalElementsAutomationAdapter.MinimumIntervalSeconds)
    {
        var due = (await host.SaveDefinitionAsync(interval).ConfigureAwait(false)).NextRunUtc!.Value;
        host.AdvanceTo(due);
        host.Handler.EnqueueFixture(CelesTrakGpTestData.ValidFixture);
        await host.SweepAsync().ConfigureAwait(false);
        var run = (await host.RunsAsync().ConfigureAwait(false))[^1];
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, run.Outcome, run.Detail);
        return due;
    }

    private static async Task AssertLastKnownGoodAsync(OrbitalCompositionHost host, string? good,
        OrbitalAttemptOutcome outcome, DateTimeOffset nextPermitted)
    {
        var state = await host.StateAsync().ConfigureAwait(false);
        Assert.AreEqual(outcome, state.LastAttemptOutcome);
        Assert.AreEqual(good, state.CurrentManifestSha256);
        Assert.AreEqual(nextPermitted, state.NextPermittedAttemptUtc);
        var current = await host.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(good, current.Snapshot?.Manifest.ManifestSha256);
    }

    /// <summary>Neither the runner nor an operator can make another request before the next permitted attempt.</summary>
    private static async Task AssertNoRetryAsync(OrbitalCompositionHost host, DateTimeOffset attemptedAt, int requests)
    {
        host.AdvanceTo(attemptedAt + TimeSpan.FromMinutes(5));
        await host.SweepAsync().ConfigureAwait(false);
        var refresh = await host.RefreshAsync(Guid.NewGuid().ToString("N")).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, refresh.ReasonCode);
        Assert.AreEqual(attemptedAt + Floor, refresh.NextPermittedAttemptUtc);
        Assert.AreEqual(requests, host.Handler.Count, "A failed attempt is not retried.");
    }

    private static void AssertDetail(string reasonCode, LocalAutomationRun run)
        => Assert.StartsWith(reasonCode + ":", run.Detail, StringComparison.Ordinal, run.Detail);

    private static string? Header(RecordedRequest request, string name)
        => request.Headers.Where(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(static header => header.Values)
            .SingleOrDefault();

    private static DateTimeOffset TruncateToMillisecond(DateTimeOffset value)
        => new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
}
