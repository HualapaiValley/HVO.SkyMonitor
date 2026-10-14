using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentTransientOperationsUiServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task UnauthorizedCallerReadsNothing()
    {
        var principal = Principal();
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Failed());
        var runtime = new Mock<ITransientRuntimeManagement>(MockBehavior.Strict);
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        var processing = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);

        var result = await CreateService(principal, authorization.Object, runtime.Object, projection.Object, processing.Object)
            .GetOverviewAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task OverviewComposesTheLaneAndLinksTheNewestCandidateCaptureToItsRun()
    {
        var principal = Principal();
        var older = Outcome(sequence: 40, candidates: 1);
        var newer = Outcome(sequence: 42, candidates: 2);
        var quiet = Outcome(sequence: 43, candidates: 0);
        var executionId = Guid.NewGuid();
        var runtime = new Mock<ITransientRuntimeManagement>(MockBehavior.Strict);
        runtime.Setup(value => value.ReadRecentCaptureOutcomesAsync(
                CameraAgentTransientOperationsUiService.RecentOutcomeCount, It.IsAny<CancellationToken>()))
            .ReturnsAsync([quiet, newer, older]);
        var candidate = Candidate();
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        projection.Setup(value => value.GetPageAsync(
                It.Is<CameraAgentTransientOperatorQuery>(query =>
                    query.PageSize == CameraAgentTransientOperationsUiService.RecentCandidateCount && query.Cursor == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentTransientOperatorPage([candidate], null));
        var processing = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        processing.Setup(value => value.ReadLiveExecutionIdAsync(newer.CaptureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(executionId);
        var worker = new TransientWorkerState(new FixedTimeProvider(Now));
        worker.Set(TransientWorkerAvailability.Healthy, "ready", 3, 1);

        var result = await CreateService(
                principal, Authorized(principal), runtime.Object, projection.Object, processing.Object,
                HostOptions(TransientOperatingMode.Hybrid, required: true), worker)
            .GetOverviewAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var view = result.Value!;
        Assert.AreEqual(Now, view.EvaluatedUtc);
        Assert.AreEqual(TransientOperatingMode.Hybrid, view.Mode);
        Assert.IsTrue(view.Required);
        Assert.IsTrue(view.CentralIntegrationEnabled);
        Assert.AreEqual(TransientWorkerAvailability.Healthy, view.Worker.Availability);
        Assert.AreEqual(3, view.Worker.PendingFrames);
        Assert.AreEqual("edge-v1", view.Profile.ExtractionProfile);
        Assert.AreEqual(TransientCandidateExtractionProfiles.EdgeV1, view.Profile.Extraction);
        Assert.AreEqual(10, view.Profile.CandidateTimeoutMinutes);
        Assert.AreEqual("adjacent-candidate-association-v1", view.Profile.AssociationAlgorithm);
        CollectionAssert.AreEqual(new[] { quiet, newer, older }, view.RecentOutcomes!.ToArray());
        Assert.IsNull(view.OutcomesUnavailable);
        Assert.AreEqual(candidate, view.RecentCandidates!.Single());
        Assert.IsNull(view.CandidatesUnavailable);
        Assert.AreEqual(new TransientLatestEventRun(newer.CaptureId, 42, executionId), view.LatestEventRun);
    }

    [TestMethod]
    public async Task EachDurableReadFailsOnItsOwnWithASanitizedMessage()
    {
        var principal = Principal();
        var runtime = new Mock<ITransientRuntimeManagement>(MockBehavior.Strict);
        runtime.Setup(value => value.ReadRecentCaptureOutcomesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("/private/journal/raw-ingress.db is locked"));
        var candidate = Candidate();
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        projection.Setup(value => value.GetPageAsync(It.IsAny<CameraAgentTransientOperatorQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentTransientOperatorPage([candidate], null));
        var processing = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);

        var outcomesFailed = (await CreateService(principal, Authorized(principal), runtime.Object, projection.Object, processing.Object)
            .GetOverviewAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsNull(outcomesFailed.RecentOutcomes);
        Assert.AreEqual("Recent detector outcomes are temporarily unavailable.", outcomesFailed.OutcomesUnavailable);
        Assert.AreEqual(candidate, outcomesFailed.RecentCandidates!.Single());
        Assert.IsNull(outcomesFailed.LatestEventRun);

        runtime.Setup(value => value.ReadRecentCaptureOutcomesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Outcome(sequence: 7, candidates: 0)]);
        projection.Setup(value => value.GetPageAsync(It.IsAny<CameraAgentTransientOperatorQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite Error 11: database disk image is malformed"));

        var candidatesFailed = (await CreateService(principal, Authorized(principal), runtime.Object, projection.Object, processing.Object)
            .GetOverviewAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.HasCount(1, candidatesFailed.RecentOutcomes!);
        Assert.IsNull(candidatesFailed.OutcomesUnavailable);
        Assert.IsNull(candidatesFailed.RecentCandidates);
        Assert.AreEqual("Recent candidates are temporarily unavailable.", candidatesFailed.CandidatesUnavailable);
    }

    [TestMethod]
    public async Task AFailedRunLookupKeepsTheCandidateCaptureWithoutARunLink()
    {
        var principal = Principal();
        var outcome = Outcome(sequence: 12, candidates: 1);
        var runtime = new Mock<ITransientRuntimeManagement>(MockBehavior.Strict);
        runtime.Setup(value => value.ReadRecentCaptureOutcomesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([outcome]);
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        projection.Setup(value => value.GetPageAsync(It.IsAny<CameraAgentTransientOperatorQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CameraAgentTransientOperatorPage([], null));
        var processing = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        processing.Setup(value => value.ReadLiveExecutionIdAsync(outcome.CaptureId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("execution store unavailable"));

        var view = (await CreateService(principal, Authorized(principal), runtime.Object, projection.Object, processing.Object)
            .GetOverviewAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(new TransientLatestEventRun(outcome.CaptureId, 12, null), view.LatestEventRun);
    }

    [TestMethod]
    public async Task CancellationIsNotReportedAsAnUnavailableRead()
    {
        var principal = Principal();
        using var cancellation = new CancellationTokenSource();
        var runtime = new Mock<ITransientRuntimeManagement>(MockBehavior.Strict);
        runtime.Setup(value => value.ReadRecentCaptureOutcomesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, CancellationToken token) =>
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return (IReadOnlyList<TransientCaptureOutcome>)[];
            });
        var service = CreateService(
            principal, Authorized(principal), runtime.Object,
            new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict).Object,
            new Mock<IProcessingGraphOperations>(MockBehavior.Strict).Object);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.GetOverviewAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static CameraAgentTransientOperationsUiService CreateService(
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        ITransientRuntimeManagement runtime,
        ICameraAgentTransientOperatorProjection projection,
        IProcessingGraphOperations processing,
        CameraAgentHostOptions? options = null,
        TransientWorkerState? worker = null) => new(
            new FixedAuthenticationStateProvider(principal),
            authorization,
            Options.Create(options ?? HostOptions(TransientOperatingMode.Edge, required: false)),
            worker ?? new TransientWorkerState(new FixedTimeProvider(Now)),
            new TransientCandidateDeliveryState(new FixedTimeProvider(Now)),
            runtime,
            projection,
            processing,
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentTransientOperationsUiService>.Instance);

    private static CameraAgentHostOptions HostOptions(TransientOperatingMode mode, bool required) => new()
    {
        TransientDetection = new TransientDetectionOptions { Mode = mode, Required = required },
        CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled },
    };

    private static TransientCaptureOutcome Outcome(long sequence, int candidates) => new(
        Guid.NewGuid(), sequence, Now.AddMinutes(-sequence), candidates > 0 ? "candidate_persisted" : "completed",
        "completed", true, null, 1, candidates, 0, 0, Now);

    private static CameraAgentTransientOperatorCandidate Candidate() => new(
        Guid.NewGuid(), Guid.NewGuid(), "persisted", "Provisional", "handoff_pending", Now, Now,
        "Available", "Pending", "Pending", "Pending");

    private static IAuthorizationService Authorized(ClaimsPrincipal principal)
    {
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        return authorization.Object;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "operator-id")], "test"));

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
