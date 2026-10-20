using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>
/// The orbital operator service over the delivered composition: the real orbital store, refresh job, automation store,
/// runner, operations and health evaluator. Only the provider client and the clock are scripted.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOrbitalUiServiceTests : OrbitalStoreTestBase
{
    private static readonly TimeSpan Floor = OrbitalElementsStoreTestData.Interval;

    [TestMethod]
    public async Task GetAsync_WhenTheReadPolicyFails_IsUnauthorizedBeforeAnyStoreReadAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        fixture.Orbital.Intercept.ReadFault = new InvalidOperationException("a store read would fail");
        var service = CreateService(fixture, read: false, mutate: false, out var authorization);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind, "The read is refused before the store is read.");
        Assert.IsNull(result.Value);
        authorization.Verify(candidate => candidate.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null,
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Never);
    }

    [TestMethod]
    public async Task Commands_WhenTheMutatePolicyFails_AreUnauthorizedAndChangeNothingAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Halt(403));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var haltedAt = fixture.Clock.GetUtcNow();
        var service = CreateService(fixture, read: true, mutate: false, out _);

        var view = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var refresh = await service.RefreshAsync("refresh-1", "operator refresh", CancellationToken.None).ConfigureAwait(false);
        var resume = await service.ResumeAsync(haltedAt, "resume-1", "checked", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, view.Kind);
        Assert.IsFalse(view.Value!.CanMutate, "A read-only operator sees the state but cannot act on it.");
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, refresh.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, resume.Kind);
        Assert.IsEmpty((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs);
        Assert.IsNotNull((await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).ProviderHalt,
            "An unauthorized resume never reaches the store.");
    }

    [TestMethod]
    public async Task GetAsync_WithoutADefinition_ReportsNoDataAndTheMissingScheduleAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.NoData, view.State);
        Assert.IsFalse(view.HasData);
        Assert.IsNull(view.Manifest);
        Assert.IsNull(view.ManifestUnavailableReason);
        Assert.IsTrue(view.CanMutate);
        Assert.AreEqual(OrbitalScheduleSummary.Missing, view.Schedule);
        Assert.IsFalse(view.CanRefresh);
        Assert.AreEqual(OrbitalElementsOperations.DefinitionMissingReasonCode, view.RefreshBlockedReasonCode);
        Assert.IsFalse(view.CanResume);
    }

    [TestMethod]
    public async Task GetAsync_WithAScheduleAndNoSnapshot_ReportsNoDataAndAllowsRefreshAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync(interval: 21_600).ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.NoData, view.State);
        Assert.AreEqual(OrbitalElementsOperations.NoActiveSnapshotReasonCode, view.Status.Reason);
        Assert.AreEqual(OrbitalOperatorText.NoDataText, OrbitalOperatorText.Describe(view.Status.Reason));
        Assert.IsTrue(view.Schedule is { Exists: true, Enabled: true, IntervalSeconds: 21_600, ActiveRun: false });
        Assert.AreEqual(view.Status.NextDueUtc, view.Schedule!.NextRunUtc);
        Assert.AreEqual(view.Status.Automation?.RevisionSha256, view.Schedule.RevisionSha256);
        Assert.AreEqual(12, view.Schedule.RevisionShort?.Length);
        Assert.IsTrue(view.CanRefresh);
        Assert.IsNull(view.RefreshBlockedReasonCode);
    }

    [TestMethod]
    public async Task GetAsync_AfterAnActivation_ReportsFreshWithTheVerifiedManifestAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.Fresh, view.State);
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, view.HealthState);
        Assert.AreEqual(OrbitalElementsOperations.ReadyReasonCode, view.HealthReason);
        Assert.IsNotNull(view.Manifest);
        Assert.AreEqual(view.Status.CurrentManifestSha256, view.Manifest.ManifestSha256);
        Assert.AreEqual(view.Status.CurrentPayloadSha256, view.Manifest.NormalizedPayloadSha256);
        Assert.AreEqual(2, view.Manifest.RecordCount);
        Assert.AreEqual(CelesTrakGpSource.RequestedPopulation, view.Manifest.Coverage.RequestedPopulation);
        var source = view.Manifest.Sources.Single();
        Assert.AreEqual(CelesTrakGpSource.ProviderKey, source.Provider);
        Assert.AreEqual(CelesTrakGpSource.DatasetKey, source.DatasetKey);
        Assert.AreEqual(TimeSpan.FromHours(6), view.NewestElementAge, "The synthetic epochs are six hours old.");
        Assert.AreEqual(TimeSpan.Zero, view.ActivationAge);
        Assert.AreEqual(TimeSpan.Zero, view.RetrievalAge);
        Assert.IsFalse(view.ElementsStale);
        Assert.IsFalse(view.RefreshOverdue);
        Assert.IsFalse(view.CanRefresh, "A refresh inside the provider floor is not permitted.");
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, view.RefreshBlockedReasonCode);
        Assert.AreEqual(fixture.Clock.GetUtcNow() + Floor, view.Status.NextPermittedAttemptUtc);
    }

    [TestMethod]
    public async Task GetAsync_WhenEveryElementEpochIsOld_ReportsStaleElementsEvenAfterAFreshDownloadAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var oldElements = CelesTrakGpTestData.Payload(
            CelesTrakGpTestData.RecordFor(60001, ("EPOCH", "\"2026-10-01T06:00:00.000000\"")));
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(oldElements));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(fixture.Clock.GetUtcNow(), view.Status.LastActivationUtc, "The download itself is new.");
        Assert.AreEqual(TimeSpan.FromDays(18) + TimeSpan.FromHours(6), view.NewestElementAge);
        Assert.IsTrue(view.ElementsStale, "Element age is judged by the epochs, not by the download time.");
        Assert.IsFalse(view.RefreshOverdue, "Retrieval itself is current.");
        Assert.AreEqual(OrbitalDataState.Stale, view.State);
    }

    [TestMethod]
    public async Task GetAsync_WhenNoRetrievalSucceedsWithinTheStalePeriod_ReportsRefreshOverdueAndAHaltStillWinsAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);
        fixture.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));

        var aged = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Halt(429));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var halted = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsFalse(aged.ElementsStale, "The newest epoch is eighteen hours old, inside the element limit.");
        Assert.IsTrue(aged.RefreshOverdue);
        Assert.AreEqual(OrbitalDataState.RefreshOverdue, aged.State);
        Assert.IsTrue(aged.CanRefresh, "Once the floor has elapsed an overdue snapshot can be refreshed.");
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, halted.HealthReason,
            "Health reports only its first degrading reason.");
        Assert.IsTrue(halted.RefreshOverdue, "The view still knows refresh is overdue behind the halt.");
        Assert.AreEqual(OrbitalDataState.Halted, halted.State);
        Assert.IsTrue(halted.HasData);
    }

    [TestMethod]
    public async Task GetAsync_WhenTheProviderHalts_ReportsHaltedAndAllowsOnlyResumeAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Halt(403));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.Halted, view.State);
        Assert.AreEqual(403, view.Status.ProviderHalt?.HttpStatusCode);
        Assert.IsTrue(view.CanResume);
        Assert.IsFalse(view.CanRefresh);
        Assert.AreEqual(OrbitalElementsOperations.ProviderHaltedReasonCode, view.RefreshBlockedReasonCode);
        Assert.AreEqual(OrbitalRefreshRecoveryAction.ResumeProvider, view.Status.RecoveryAction);
    }

    [TestMethod]
    public async Task GetAsync_WhenACandidateIsQuarantined_ReportsQuarantinedAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(System.Text.Encoding.UTF8.GetBytes("{\"not\":\"an array\"}")));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.Quarantined, view.State);
        Assert.IsNotNull(view.Status.CandidateQuarantine);
        Assert.AreEqual(OrbitalElementsHealthState.Degraded, view.HealthState);
    }

    [TestMethod]
    public async Task GetAsync_WhenRefreshIsDisabled_ReportsDisabledAndHealthyAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot(), enabled: false).ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.Disabled, view.State);
        Assert.AreEqual(OrbitalElementsHealthState.Healthy, view.HealthState);
        Assert.IsFalse(view.StoreUnavailable);
        Assert.AreEqual(OrbitalElementsOperations.DisabledReasonCode, view.RefreshBlockedReasonCode);
    }

    [TestMethod]
    public async Task GetAsync_WhenTheStoreIsHeldByAnotherOwner_ReportsUnavailableAndRefusesCommandsAsync()
    {
        var root = CreateRoot();
        using var owner = OrbitalElementsStoreTestData.Open(root,
            new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root, initialize: false).ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
        var refresh = await service.RefreshAsync("refresh-1", "operator refresh", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalDataState.Unavailable, view.State);
        Assert.AreEqual(OrbitalElementsHealthState.Unhealthy, view.HealthState);
        Assert.AreEqual("orbital.storage-locked", view.RefreshBlockedReasonCode);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, refresh.Kind);
        Assert.AreEqual("orbital.storage-locked", refresh.Value?.ReasonCode);
        Assert.AreEqual(OrbitalOperatorText.Describe("orbital.storage-locked"), refresh.Message);
    }

    [TestMethod]
    public async Task GetAsync_WhenTheManifestFailsVerification_StillShowsTheStatusWithACodeAndNoPathAsync()
    {
        var root = CreateRoot();
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var manifest = (await fixture.Operations.GetStatusAsync(CancellationToken.None).ConfigureAwait(false))
            .CurrentManifestSha256!;
        var file = fixture.Orbital.Store.ManifestPath(manifest);
        var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        await File.WriteAllBytesAsync(file, [.. bytes, (byte)'\n']).ConfigureAwait(false);
        var logger = new OrbitalTestLogger<CameraAgentOrbitalUiService>();
        var service = CreateService(fixture, read: true, mutate: true, out _, logger);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind, "A damaged manifest never hides the status.");
        Assert.IsNull(result.Value!.Manifest);
        Assert.AreEqual(OrbitalElementsAvailability.IntegrityFailureReasonCode, result.Value.ManifestUnavailableReason);
        Assert.IsTrue(result.Value.HasData);
        Assert.IsFalse(logger.Rendered().Contains(root, StringComparison.Ordinal));
        Assert.IsTrue(logger.Entries.All(static entry => entry.Exception is null), "Exceptions can name paths.");
    }

    [TestMethod]
    public async Task GetAsync_WhenTheStatusReadThrows_ReturnsAFixedMessageWithoutTheExceptionTextAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        const string Secret = "/private/orbital/root";
        fixture.Orbital.Intercept.ReadFault = new OrbitalStorageException("orbital.storage-io", "failed at " + Secret, null);
        var logger = new OrbitalTestLogger<CameraAgentOrbitalUiService>();
        var service = CreateService(fixture, read: true, mutate: true, out _, logger);

        var read = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);
        var resume = await service.ResumeAsync(OrbitalElementsStoreTestData.Start, "resume-1", "checked",
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, read.Kind);
        Assert.AreEqual("Orbital data status is unavailable.", read.Message);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, resume.Kind);
        Assert.AreEqual("The orbital command could not be completed.", resume.Message);
        Assert.IsFalse(logger.Rendered().Contains(Secret, StringComparison.Ordinal));
        Assert.IsTrue(logger.Rendered().Contains("orbital.storage-io", StringComparison.Ordinal));
        Assert.IsTrue(logger.Entries.All(static entry => entry.Exception is null));
    }

    [TestMethod]
    public async Task RefreshAsync_QueuesOnceReplaysThenReportsTheRunAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var queued = await service.RefreshAsync("refresh-1", "operator refresh", CancellationToken.None).ConfigureAwait(false);
        var replayed = await service.RefreshAsync("refresh-1", "operator refresh", CancellationToken.None).ConfigureAwait(false);
        var pending = await service.RefreshAsync("refresh-2", "operator refresh", CancellationToken.None).ConfigureAwait(false);
        var whileQueued = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OperatorUiResultKind.Success, queued.Kind);
        Assert.AreEqual(OrbitalElementsCommandStatus.Accepted, queued.Value?.Status);
        Assert.AreEqual(OrbitalElementsOperations.RefreshQueuedReasonCode, queued.Value?.ReasonCode);
        Assert.StartsWith("Refresh queued.", queued.Message!);
        Assert.AreEqual(OperatorUiResultKind.Success, replayed.Kind);
        Assert.AreEqual(OrbitalElementsCommandStatus.Replayed, replayed.Value?.Status);
        Assert.StartsWith("Already queued.", replayed.Message!);
        Assert.AreEqual(OperatorUiResultKind.Conflict, pending.Kind);
        Assert.AreEqual(LocalAutomationContract.RunNowPendingReasonCode, pending.Value?.ReasonCode);
        Assert.IsTrue(whileQueued.Schedule?.ActiveRun);
        Assert.AreEqual(0, fixture.Orbital.Client.Calls, "The service only queues; the runner makes the request.");

        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        using var runner = fixture.CreateRunner();
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        var afterRun = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(OrbitalDataState.Fresh, afterRun.State);
        Assert.IsFalse(afterRun.Schedule!.ActiveRun);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, afterRun.Schedule.LastRunOutcome);
        Assert.AreEqual(afterRun.Status.Automation?.RunKey, afterRun.Schedule.LastRunKey);
        Assert.IsTrue(afterRun.Schedule.LastRunReasonCode is null ||
            OrbitalOperatorText.IsKnown(afterRun.Schedule.LastRunReasonCode), afterRun.Schedule.LastRunReasonCode);
    }

    [TestMethod]
    public async Task RefreshAsync_InsideTheFloor_IsAConflictNamingTheNextPermittedAttemptAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        var refused = await service.RefreshAsync("refresh-early", "operator refresh", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, refused.Kind);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, refused.Value?.ReasonCode);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + Floor, refused.Value?.NextPermittedAttemptUtc);
        Assert.Contains("2026-10-19 14:00:00 UTC", refused.Message!);
        Assert.IsNotNull(refused.Value?.Refresh, "A conflict carries the status the page re-renders.");
    }

    [TestMethod]
    public async Task ResumeAsync_AppliesOnceKeepsTheFloorAndAStaleHaltTimeIsAConflictAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Client.Enqueue(fixture.Orbital.Halt(429));
        await fixture.Orbital.RunAsync().ConfigureAwait(false);
        var haltedAt = fixture.Clock.GetUtcNow();
        var service = CreateService(fixture, read: true, mutate: true, out _);
        var halted = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        var mismatch = await service.ResumeAsync(haltedAt.AddSeconds(-1), "resume-old", "checked", CancellationToken.None)
            .ConfigureAwait(false);
        var resumed = await service.ResumeAsync(haltedAt, "resume-1", "checked", CancellationToken.None).ConfigureAwait(false);
        var replayed = await service.ResumeAsync(haltedAt, "resume-1", "checked", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, mismatch.Kind);
        Assert.AreEqual("orbital.halt-mismatch", mismatch.Value?.ReasonCode);
        Assert.IsNotNull(mismatch.Value?.Refresh?.ProviderHalt, "The page re-renders the current halt.");
        Assert.AreEqual(OperatorUiResultKind.Success, resumed.Kind);
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Value?.Status);
        Assert.IsNull(resumed.Value?.Refresh?.ProviderHalt);
        Assert.AreEqual(halted.Status.NextPermittedAttemptUtc, resumed.Value?.Refresh?.NextPermittedAttemptUtc,
            "Resume never moves the provider floor.");
        Assert.StartsWith("Provider resumed.", resumed.Message!);
        Assert.AreEqual(OperatorUiResultKind.Success, replayed.Kind);
        Assert.AreEqual(OrbitalElementsCommandStatus.Replayed, replayed.Value?.Status);
    }

    [TestMethod]
    public async Task Commands_RejectNullArgumentsAsync()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var service = CreateService(fixture, read: true, mutate: true, out _);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await service.RefreshAsync(null!, "reason", CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await service.ResumeAsync(OrbitalElementsStoreTestData.Start, "key", null!, CancellationToken.None)
                .ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static CameraAgentOrbitalUiService CreateService(
        OrbitalAutomationFixture fixture,
        bool read,
        bool mutate,
        out Mock<IAuthorizationService> authorization,
        ILogger<CameraAgentOrbitalUiService>? logger = null)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(read ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(mutate ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return new CameraAgentOrbitalUiService(
            new StubAuthenticationStateProvider(user),
            authorization.Object,
            fixture.Operations,
            fixture.Orbital.Store,
            new OrbitalElementsHealthEvaluator(fixture.Operations, fixture.AutomationOptions, fixture.Clock),
            fixture.Automation,
            fixture.AutomationOptions,
            logger ?? new OrbitalTestLogger<CameraAgentOrbitalUiService>());
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }
}

/// <summary>Operator text covers every reason code an orbital surface can show, and never echoes free text.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalOperatorTextTests
{
    private static readonly string[] Tones = ["success", "warning", "pending", "neutral", "failure"];

    [TestMethod]
    [DataRow("orbital.activated")]
    [DataRow("orbital.attempt-cancelled")]
    [DataRow("orbital.attempt-in-flight")]
    [DataRow("orbital.attempt-interrupted")]
    [DataRow("orbital.attempt-unsettled")]
    [DataRow("orbital.candidate-quarantined")]
    [DataRow("orbital.candidate-rejected")]
    [DataRow("orbital.client-error")]
    [DataRow("orbital.conflicting-metadata")]
    [DataRow("orbital.content-encoding")]
    [DataRow("orbital.definition-disabled")]
    [DataRow("orbital.definition-missing")]
    [DataRow("orbital.disabled")]
    [DataRow("orbital.duplicate-member")]
    [DataRow("orbital.duplicate-record")]
    [DataRow("orbital.empty-body")]
    [DataRow("orbital.empty-payload")]
    [DataRow("orbital.epoch-range")]
    [DataRow("orbital.first-snapshot-overdue")]
    [DataRow("orbital.floor-not-elapsed")]
    [DataRow("orbital.gzip-invalid")]
    [DataRow("orbital.halt-mismatch")]
    [DataRow("orbital.http-client-timeout")]
    [DataRow("orbital.idempotency-conflict")]
    [DataRow("orbital.internal-error")]
    [DataRow("orbital.interval-below-provider-floor")]
    [DataRow("orbital.interval-out-of-range")]
    [DataRow("orbital.invalid-epoch")]
    [DataRow("orbital.invalid-http-status")]
    [DataRow("orbital.invalid-number")]
    [DataRow("orbital.invalid-object-identity")]
    [DataRow("orbital.invalid-text")]
    [DataRow("orbital.json-root")]
    [DataRow("orbital.json-syntax")]
    [DataRow("orbital.length-mismatch")]
    [DataRow("orbital.manifest-retired")]
    [DataRow("orbital.manual-request-expired")]
    [DataRow("orbital.media-type")]
    [DataRow("orbital.member-type")]
    [DataRow("orbital.missing-member")]
    [DataRow("orbital.no-active-snapshot")]
    [DataRow("orbital.no-halt")]
    [DataRow("orbital.non-200-response")]
    [DataRow("orbital.not-yet-permitted")]
    [DataRow("orbital.provider-halted")]
    [DataRow("orbital.provider-outcome-unknown")]
    [DataRow("orbital.provider-http-403")]
    [DataRow("orbital.raw-bound")]
    [DataRow("orbital.raw-integrity")]
    [DataRow("orbital.ready")]
    [DataRow("orbital.record-bound")]
    [DataRow("orbital.record-contract")]
    [DataRow("orbital.record-shape")]
    [DataRow("orbital.refresh-overdue")]
    [DataRow("orbital.refresh-invalid-actor")]
    [DataRow("orbital.refresh-invalid-idempotency-key")]
    [DataRow("orbital.refresh-invalid-reason")]
    [DataRow("orbital.refresh-queued")]
    [DataRow("orbital.refresh-replayed")]
    [DataRow("orbital.request-timeout")]
    [DataRow("orbital.reserved-definition-id")]
    [DataRow("orbital.resume-invalid-actor")]
    [DataRow("orbital.resume-invalid-halt-time")]
    [DataRow("orbital.resume-invalid-idempotency-key")]
    [DataRow("orbital.resume-invalid-reason")]
    [DataRow("orbital.resume-rejected")]
    [DataRow("orbital.resume-replayed")]
    [DataRow("orbital.resumed")]
    [DataRow("orbital.retrieved")]
    [DataRow("orbital.snapshot-contract")]
    [DataRow("orbital.snapshot-stale")]
    [DataRow("orbital.storage-containment")]
    [DataRow("orbital.storage-database")]
    [DataRow("orbital.storage-failed")]
    [DataRow("orbital.storage-integrity")]
    [DataRow("orbital.storage-io")]
    [DataRow("orbital.storage-locked")]
    [DataRow("orbital.storage-no-space")]
    [DataRow("orbital.storage-permission")]
    [DataRow("orbital.store-unavailable")]
    [DataRow("orbital.transport-authentication")]
    [DataRow("orbital.transport-cancelled")]
    [DataRow("orbital.transport-connection")]
    [DataRow("orbital.transport-failure")]
    [DataRow("orbital.transport-io")]
    [DataRow("orbital.transport-limit")]
    [DataRow("orbital.transport-protocol")]
    [DataRow("orbital.transport-tls")]
    [DataRow("orbital.unchanged-payload")]
    [DataRow("orbital.unknown-member")]
    [DataRow("orbital.unsupported-classification")]
    [DataRow("orbital.unsupported-ephemeris-type")]
    [DataRow("orbital.wire-bound")]
    [DataRow("automation.run-now-pending")]
    [DataRow("automation.run-now-unavailable")]
    [DataRow("automation.invalidCommand")]
    public void Describe_HasFixedOperatorTextForEveryReasonCode(string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(reasonCode);

        var text = OrbitalOperatorText.Describe(reasonCode);

        Assert.IsTrue(OrbitalOperatorText.IsKnown(reasonCode), reasonCode);
        Assert.AreNotEqual(reasonCode, text);
        Assert.DoesNotContain("orbital.", text, "Operator text is plain language, not a code.");
    }

    [TestMethod]
    public void Describe_NamesTheProviderStatusAndTheNextPermittedAttempt()
    {
        Assert.Contains("HTTP 429", OrbitalOperatorText.Describe("orbital.provider-http-429"));
        Assert.Contains("2026-10-19 14:00:00 UTC",
            OrbitalOperatorText.Describe(OrbitalElementsOperations.NotYetPermittedReasonCode, "2026-10-19 14:00:00 UTC"));
        Assert.IsFalse(OrbitalOperatorText.IsKnown("orbital.provider-http-abc"));
        Assert.IsFalse(OrbitalOperatorText.IsKnown("orbital.provider-http-999"));
    }

    [TestMethod]
    public void Describe_ShowsAnUnknownCodeAsItselfAndABlankCodeAsUnrecorded()
    {
        Assert.AreEqual("orbital.some-future-code", OrbitalOperatorText.Describe("orbital.some-future-code"));
        Assert.AreEqual("No reason was recorded.", OrbitalOperatorText.Describe(null));
    }

    [TestMethod]
    [DataRow(403, "HTTP 403")]
    [DataRow(429, "HTTP 429")]
    [DataRow(null, OrbitalOperatorText.NoHttpStatusText)]
    [DataRow(0, OrbitalOperatorText.NoHttpStatusText)]
    [DataRow(600, OrbitalOperatorText.NoHttpStatusText)]
    public void HaltStatus_NeverInventsAnHttpLabel(int? status, string expected)
        => Assert.AreEqual(expected, OrbitalOperatorText.HaltStatus(status));

    [TestMethod]
    public void ReasonText_DescribesCodesAndNeverRendersFreeText()
    {
        Assert.AreEqual(OrbitalOperatorText.Describe(OrbitalOperatorText.ProviderOutcomeUnknownReasonCode),
            OrbitalOperatorText.ReasonText(OrbitalOperatorText.ProviderOutcomeUnknownReasonCode));
        Assert.AreEqual(OrbitalOperatorText.Describe("orbital.json-root"),
            OrbitalOperatorText.ReasonText("orbital.json-root: the root was an object"));
        Assert.AreEqual(OrbitalOperatorText.UnrecordedReasonText,
            OrbitalOperatorText.ReasonText("provider returned HTTP 403; status=403"));
        Assert.AreEqual("No reason was recorded.", OrbitalOperatorText.ReasonText(null));
    }

    [TestMethod]
    public void NoDataText_NeverSaysThatNoSatellitesAreOverhead()
    {
        Assert.Contains("Satellite predictions are unavailable", OrbitalOperatorText.NoDataText);
        Assert.Contains("does not mean no satellites are overhead", OrbitalOperatorText.NoDataText);
    }

    [TestMethod]
    public void Recovery_HasTextForEveryAction()
    {
        foreach (var action in Enum.GetValues<OrbitalRefreshRecoveryAction>())
        {
            Assert.AreNotEqual(action.ToString(), OrbitalOperatorText.Recovery(action), action.ToString());
        }
    }

    [TestMethod]
    public void StateLabelsAndTones_CoverEveryState()
    {
        foreach (var state in Enum.GetValues<OrbitalDataState>())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(OrbitalOperatorText.StateLabel(state)));
            Assert.Contains(OrbitalOperatorText.StateTone(state), Tones);
        }
        Assert.AreEqual("No data", OrbitalOperatorText.StateLabel(OrbitalDataState.NoData));
        Assert.AreEqual("success", OrbitalOperatorText.StateTone(OrbitalDataState.Fresh));
    }

    [TestMethod]
    [DataRow("orbital.floor-not-elapsed: the provider floor has not elapsed", "orbital.floor-not-elapsed")]
    [DataRow("automation.run-now-pending", "automation.run-now-pending")]
    [DataRow("transport failed at /private/root: refused", null)]
    [DataRow("orbital.bad code: text", null)]
    [DataRow("", null)]
    public void LeadingReasonCode_ReturnsOnlyAStableCode(string detail, string? expected)
        => Assert.AreEqual(expected, OrbitalOperatorText.LeadingReasonCode(detail));
}
