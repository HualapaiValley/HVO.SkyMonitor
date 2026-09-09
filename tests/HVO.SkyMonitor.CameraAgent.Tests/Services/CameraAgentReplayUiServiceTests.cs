using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentReplayUiServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaptureId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ExecutionId = Guid.Parse("00000000-0000-0000-0000-0000000000ee");
    private static readonly Guid ArtifactId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly string[] EligibleRevisionIds = ["revision-active", "revision-validated"];

    [TestMethod]
    public async Task GetReplayCandidateAsync_WhenTheReadPolicyFails_DeniesWithoutReadingEvidenceAsync()
    {
        var operations = new StubProcessingGraphOperations();
        var gallery = new StubGallery();
        var service = CreateService(operations, gallery, succeeded: false);

        var result = await service.GetReplayCandidateAsync(CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, gallery.Reads);
        Assert.AreEqual(0, operations.RegistryReads);
    }

    [TestMethod]
    public async Task SubmitReplayAsync_WhenTheMutatePolicyFails_DeniesWithoutSubmittingAsync()
    {
        var operations = new StubProcessingGraphOperations();
        var service = CreateService(operations, new StubGallery(), succeeded: false);

        var result = await service
            .SubmitReplayAsync(CaptureId, "revision-a", "key-1", "why", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, operations.Submissions.Count);
    }

    [TestMethod]
    public async Task GetReplayCandidateAsync_ComposesTheFreezeSummaryFromEligibleRevisionsOnlyAsync()
    {
        var operations = new StubProcessingGraphOperations();
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service.GetReplayCandidateAsync(CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var candidate = result.Value!;
        Assert.AreEqual(CaptureId, candidate.CaptureId);
        Assert.AreEqual(ArtifactId, candidate.PrimaryArtifact!.ArtifactId);
        Assert.AreEqual("Held", candidate.PrimaryArtifact.RetentionState);
        CollectionAssert.AreEqual(
            EligibleRevisionIds,
            candidate.EligibleRevisions.Select(static revision => revision.RevisionId).ToArray());
        Assert.IsTrue(candidate.EligibleRevisions[0].IsActive);
        Assert.AreEqual("InProcess", candidate.Capacity.ReplayProfile);
        Assert.AreEqual(3600, candidate.Capacity.DeadlineSeconds);
    }

    [TestMethod]
    public async Task GetReplayCandidateAsync_ForAnUnknownCapture_ReportsNotFoundAsync()
    {
        var service = CreateService(new StubProcessingGraphOperations(), new StubGallery(hasCapture: false), succeeded: true);

        var result = await service.GetReplayCandidateAsync(CaptureId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.NotFound, result.Kind);
    }

    [TestMethod]
    [DataRow(false, ReplaySubmissionOutcome.Accepted)]
    [DataRow(true, ReplaySubmissionOutcome.ExistingRequestReturned)]
    public async Task SubmitReplayAsync_MapsTheDurableReplayedFlagToADistinctOutcomeAsync(
        bool replayed,
        ReplaySubmissionOutcome expected)
    {
        var operations = new StubProcessingGraphOperations { Replayed = replayed };
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service
            .SubmitReplayAsync(CaptureId, "revision-active", "key-1", "why", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(expected, result.Value!.Outcome);
        Assert.AreEqual(ExecutionId, result.Value.Execution.ExecutionId);
        Assert.AreEqual(("key-1", "owner-id"), operations.Submissions.Single());
    }

    [TestMethod]
    public async Task SubmitReplayAsync_WhenTheQueueIsAtCapacity_ReturnsTheFixedRetryableMessageAsync()
    {
        var operations = new StubProcessingGraphOperations
        {
            SubmitFailure = () => new ProcessingReplayCapacityException("queue path /var/lib/hvo is full")
        };
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service
            .SubmitReplayAsync(CaptureId, "revision-active", "key-1", null, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual(CameraAgentReplayUiService.CapacityMessage, result.Message);
    }

    [TestMethod]
    public async Task SubmitReplayAsync_WhenDurableStateConflicts_ReturnsTheFixedConflictMessageAsync()
    {
        var operations = new StubProcessingGraphOperations
        {
            SubmitFailure = () => new ProcessingGraphStoreConflictException("revision /tmp/x changed")
        };
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service
            .SubmitReplayAsync(CaptureId, "revision-active", "key-1", null, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Conflict, result.Kind);
        Assert.AreEqual(CameraAgentReplayUiService.ConflictMessage, result.Message);
    }

    [TestMethod]
    public async Task SubmitReplayAsync_WhenTheFrozenInputIsMissing_ReportsNotFoundAsync()
    {
        var operations = new StubProcessingGraphOperations
        {
            SubmitFailure = () => new FileNotFoundException("The replay source evidence is no longer retained.")
        };
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service
            .SubmitReplayAsync(CaptureId, "revision-active", "key-1", null, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.NotFound, result.Kind);
    }

    [TestMethod]
    public async Task GetReplayExecutionAsync_RedactsLeaseOwnersAndPathOrTokenLikeReasonsAsync()
    {
        var operations = new StubProcessingGraphOperations();
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service.GetReplayExecutionAsync(ExecutionId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        var execution = result.Value!;
        Assert.AreEqual(CameraAgentReplayUiService.WithheldValue, execution.FailureReason);
        var node = execution.Nodes.Single();
        Assert.AreEqual(CameraAgentReplayUiService.WithheldValue, node.Reason);
        Assert.AreEqual(CameraAgentReplayUiService.WithheldValue, node.Attempts.Single().Reason);
        Assert.IsFalse(
            System.Text.Json.JsonSerializer.Serialize(execution).Contains("lease-owner", StringComparison.OrdinalIgnoreCase),
            "The projection must never carry a durable lease owner.");
        Assert.IsFalse(execution.IsTerminal);
    }

    /// <summary>
    /// The execution route is three-valued and the projection has to carry all three, because the question
    /// issue #799 asks is which nodes dispatched to the local replay runner and which ran in process. A
    /// projection that dropped the route, or that collapsed it to a two-valued dispatched flag, would answer
    /// with the value the reader assumed rather than the one the attempt recorded, so every member is driven
    /// through the projection here and not only the default.
    /// </summary>
    [TestMethod]
    [DataRow(ProcessingNodeExecutionRoute.Unknown, "Unknown")]
    [DataRow(ProcessingNodeExecutionRoute.InProcess, "InProcess")]
    [DataRow(ProcessingNodeExecutionRoute.LocalRunner, "LocalRunner")]
    public async Task GetReplayExecutionAsync_ProjectsEveryRecordedExecutionRouteAsync(
        ProcessingNodeExecutionRoute route,
        string expected)
    {
        var operations = new StubProcessingGraphOperations { AttemptRoute = route };
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service.GetReplayExecutionAsync(ExecutionId, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual(expected, result.Value!.Nodes.Single().Attempts.Single().ExecutionRoute);
    }

    [TestMethod]
    public async Task CancelReplayAsync_ProjectsTheTerminalStateWithoutLeaseEvidenceAsync()
    {
        var operations = new StubProcessingGraphOperations();
        var service = CreateService(operations, new StubGallery(), succeeded: true);

        var result = await service
            .CancelReplayAsync(ExecutionId, "cancel-key", "operator cancellation", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind);
        Assert.AreEqual("Cancelled", result.Value!.Status);
        Assert.IsTrue(result.Value.IsTerminal);
        Assert.AreEqual(("cancel-key", "owner-id"), operations.Cancellations.Single());
    }

    [TestMethod]
    [DataRow("Preview node failed validation.", "Preview node failed validation.")]
    [DataRow("/var/lib/hvo/raw/payload.bin", CameraAgentReplayUiService.WithheldValue)]
    [DataRow("token AAAAAAAAAAAAAAAAAAAAAAAAAAAA", CameraAgentReplayUiService.WithheldValue)]
    public void Sanitize_WithholdsPathLikeAndTokenLikeValues(string value, string expected)
        => Assert.AreEqual(expected, CameraAgentReplayUiService.Sanitize(value));

    private static CameraAgentReplayUiService CreateService(
        IProcessingGraphOperations operations,
        ICameraAgentGallery gallery,
        bool succeeded)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, It.IsAny<string>()))
            .ReturnsAsync(succeeded ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return new CameraAgentReplayUiService(
            new StubAuthenticationStateProvider(user),
            authorization.Object,
            operations,
            gallery,
            Options.Create(new CameraAgentHostOptions()),
            NullLogger<CameraAgentReplayUiService>.Instance);
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class StubGallery(bool hasCapture = true) : ICameraAgentGallery
    {
        private readonly CameraAgentGalleryCapture? _capture = hasCapture ? OperatorUiTestData.Capture() : null;

        internal int Reads { get; private set; }

        public ValueTask<CameraAgentGalleryPage> GetPageAsync(
            CameraAgentGalleryQuery query,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<CameraAgentGalleryCapture?> GetCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(_capture);
        }
    }

    private sealed class StubProcessingGraphOperations : IProcessingGraphOperations
    {
        internal int RegistryReads { get; private set; }

        internal bool Replayed { get; init; }

        internal Func<Exception>? SubmitFailure { get; init; }

        internal List<(string Key, string Actor)> Submissions { get; } = [];

        internal List<(string Key, string Actor)> Cancellations { get; } = [];

        /// <summary>
        /// The route the stubbed attempt recorded. It is settable so a test can drive every member of
        /// <see cref="ProcessingNodeExecutionRoute"/> through the projection rather than only the default.
        /// </summary>
        internal ProcessingNodeExecutionRoute AttemptRoute { get; init; } = ProcessingNodeExecutionRoute.Unknown;

        public ValueTask<ProcessingGraphRegistryState> GetRegistryAsync(CancellationToken cancellationToken)
        {
            RegistryReads++;
            return ValueTask.FromResult(new ProcessingGraphRegistryState(
                ProcessingGraphRegistryMode.Named,
                "revision-active",
                "revision-basic",
                7,
                [
                    Revision("revision-active", ProcessingGraphRevisionLifecycle.Active),
                    Revision("revision-validated", ProcessingGraphRevisionLifecycle.Validated),
                    Revision("revision-draft", ProcessingGraphRevisionLifecycle.Draft),
                    Revision("revision-retired", ProcessingGraphRevisionLifecycle.Retired)
                ]));
        }

        public ValueTask<ProcessingReplaySubmissionResult> SubmitReplayAsync(
            ProcessingReplaySubmission submission,
            string idempotencyKey,
            string actor,
            CancellationToken cancellationToken)
        {
            Submissions.Add((idempotencyKey, actor));
            if (SubmitFailure is not null)
            {
                throw SubmitFailure();
            }
            return ValueTask.FromResult(new ProcessingReplaySubmissionResult(
                Execution(ProcessingGraphExecutionStatus.Pending), Replayed));
        }

        public ValueTask<ProcessingGraphExecutionState?> ReadExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<ProcessingGraphExecutionState?>(
                Execution(ProcessingGraphExecutionStatus.Running));

        public ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
            Guid executionId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<ProcessingGraphExecutionDetail?>(new ProcessingGraphExecutionDetail(
                Execution(ProcessingGraphExecutionStatus.Running),
                [
                    new ProcessingGraphExecutionNodeState(
                        "preview-node",
                        true,
                        new string('A', 64),
                        "Running",
                        "/var/lib/hvo/replay/preview.bin",
                        1,
                        Now,
                        null,
                        [new ProcessingGraphExecutionInputState(
                            0, 0, ProcessingGraphExecutionInputKind.RawCapture, CaptureId, ArtifactId,
                            new string('B', 64), new string('C', 64), null)],
                        [new ProcessingGraphNodeAttemptState(
                            1,
                            "lease-owner-7f3c",
                            Now,
                            null,
                            "Running",
                            ProcessingOutcomeStatus.Produced,
                            @"C:\hvo\lease\token",
                            null,
                            AttemptRoute)],
                        [new ProcessingGraphExecutionOutputState(
                            0, new string('D', 64), ArtifactId, FrameArtifactRole.Preview, "display",
                            "Available", null)])
                ]));


        public ValueTask<CapturePipelineConfig?> ReadRevisionPipelineAsync(string revisionId, CancellationToken cancellationToken)

            => ValueTask.FromResult<CapturePipelineConfig?>(null);

        public ValueTask<ProcessingGraphExecutionState> CancelReplayAsync(
            Guid executionId,
            string idempotencyKey,
            string actor,
            string? reason,
            CancellationToken cancellationToken)
        {
            Cancellations.Add((idempotencyKey, actor));
            return ValueTask.FromResult(Execution(ProcessingGraphExecutionStatus.Cancelled));
        }

        public ValueTask<ProcessingGraphRevisionState> CreateRevisionAsync(
            string name, string revision, CapturePipelineConfig pipeline, string idempotencyKey,
            string actor, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ProcessingGraphRegistryState> ActivateRevisionAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string actor, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ProcessingGraphRegistryState> RollbackRevisionAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string actor, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ProcessingGraphRevisionState> ValidateRevisionAsync(
            string revisionId, string idempotencyKey, string actor, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ProcessingGraphRegistryState> RetireRevisionAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string actor, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ProcessingGraphExecutionState>> ReadExecutionsAsync(
            ProcessingGraphExecutionClass? executionClass, int maximumCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        private static ProcessingGraphRevisionState Revision(
            string revisionId,
            ProcessingGraphRevisionLifecycle lifecycle)
            => new(
                revisionId,
                "nightly",
                "r1",
                lifecycle,
                new string('A', 64),
                new string('B', 64),
                new string('C', 64),
                Now,
                Now,
                lifecycle == ProcessingGraphRevisionLifecycle.Active ? Now : null,
                null);

        private static ProcessingGraphExecutionState Execution(ProcessingGraphExecutionStatus status)
            => new(
                ExecutionId,
                ProcessingGraphExecutionClass.Replay,
                status,
                CaptureId,
                ArtifactId,
                "revision-active",
                new string('A', 64),
                new string('B', 64),
                new string('C', 64),
                "operator",
                null,
                0,
                Now,
                Now,
                Now.AddHours(1),
                Now.AddDays(1),
                Now,
                status == ProcessingGraphExecutionStatus.Running ? null : Now,
                "/var/lib/hvo/replay/failed.log",
                false,
                1);
    }
}
