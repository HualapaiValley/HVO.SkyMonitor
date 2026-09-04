using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Distinguishes a newly accepted replay request from a durable request that an identical
/// idempotency key returned unchanged. Neither outcome promotes or publishes anything.
/// </summary>
public enum ReplaySubmissionOutcome
{
    Accepted,
    ExistingRequestReturned
}

/// <summary>
/// Client-side summary of the inputs the delivered replay store freezes at submission.
/// Nothing here is durable; every value is resolved again inside <c>SubmitReplayAsync</c>.
/// </summary>
public sealed record ReplayCandidateView(
    Guid CaptureId,
    string AgentId,
    string? RigId,
    long CaptureSequence,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset DurableIngressUtc,
    string RawState,
    string EvidenceAvailability,
    bool? RawRetentionHold,
    ReplayCandidateArtifactView? PrimaryArtifact,
    string RegistryMode,
    string ActiveRevisionId,
    long RegistryStateVersion,
    IReadOnlyList<ReplayCandidateRevisionView> EligibleRevisions,
    ReplayCapacityFactsView Capacity);

public sealed record ReplayCandidateArtifactView(
    Guid ArtifactId,
    string Role,
    string? Variant,
    string ChecksumSha256,
    long? ByteLength,
    string Availability,
    string RetentionState,
    string DeliveryAvailability);

public sealed record ReplayCandidateRevisionView(
    string RevisionId,
    string Name,
    string Revision,
    string Lifecycle,
    string DefinitionIdentitySha256,
    bool IsActive);

/// <summary>Configured replay execution facts read from options; never a runtime probe.</summary>
public sealed record ReplayCapacityFactsView(
    string ReplayProfile,
    int MaximumConcurrency,
    int MaximumPendingCount,
    int DeadlineSeconds,
    int MaximumQueueAgeSeconds);

public sealed record ReplaySubmissionView(
    ReplaySubmissionOutcome Outcome,
    ReplayExecutionView Execution);

public sealed record ReplayExecutionView(
    Guid ExecutionId,
    string ExecutionClass,
    string Status,
    bool IsTerminal,
    Guid CaptureId,
    Guid PrimaryArtifactId,
    string GraphRevisionId,
    string GraphDefinitionIdentitySha256,
    string SharedPlanIdentitySha256,
    string LocalPlanIdentitySha256,
    string TriggerKind,
    string? TriggerReference,
    int Priority,
    int AttemptCount,
    bool CancellationRequested,
    DateTimeOffset AcceptedUtc,
    DateTimeOffset AvailableUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset MaximumAgeUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureReason,
    IReadOnlyList<ReplayExecutionNodeView> Nodes);

public sealed record ReplayExecutionNodeView(
    string NodeId,
    bool Required,
    string Status,
    string? Reason,
    int AttemptCount,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<ReplayExecutionAttemptView> Attempts,
    IReadOnlyList<ReplayExecutionInputView> Inputs,
    IReadOnlyList<ReplayExecutionOutputView> Outputs);

public sealed record ReplayExecutionAttemptView(
    int AttemptNumber,
    string Status,
    string? Outcome,
    string? Reason,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    TimeSpan? Duration);

public sealed record ReplayExecutionInputView(
    int Ordinal,
    int WindowPosition,
    string Kind,
    Guid CaptureId,
    Guid ArtifactId,
    string? DescriptorSha256,
    string? PayloadSha256);

public sealed record ReplayExecutionOutputView(
    int Ordinal,
    Guid ArtifactId,
    string Role,
    string Variant,
    string OutputIdentitySha256,
    string AvailabilityState,
    string? AvailabilityReason);

internal interface ICameraAgentReplayUiService
{
    ValueTask<OperatorUiResult<ReplayCandidateView>> GetReplayCandidateAsync(
        Guid captureId,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ReplaySubmissionView>> SubmitReplayAsync(
        Guid captureId,
        string revisionId,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ReplayExecutionView>> GetReplayExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ReplayExecutionView>> CancelReplayAsync(
        Guid executionId,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read/mutate operator boundary for the archive-to-replay flow. It consumes the delivered
/// <see cref="IProcessingGraphOperations"/> contract without redefining persistence or execution
/// semantics, and never exposes lease owners, retained payload paths, or bearer-like values.
/// </summary>
internal sealed class CameraAgentReplayUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IProcessingGraphOperations operations,
    ICameraAgentGallery gallery,
    IOptions<CameraAgentHostOptions> hostOptions,
    ILogger<CameraAgentReplayUiService> logger) : ICameraAgentReplayUiService
{
    internal const string CapacityMessage =
        "The local replay queue is at capacity. Wait for queued replays to drain and retry this request.";

    internal const string ConflictMessage =
        "Replay state changed before this request was accepted. Refresh the freeze summary before retrying.";

    internal const string WithheldValue = "Withheld";

    private static readonly char[] PathLikeCharacters = ['/', '\\', '~'];

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service returns fixed sanitized failures.")]
    public async ValueTask<OperatorUiResult<ReplayCandidateView>> GetReplayCandidateAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<ReplayCandidateView>();
        }
        if (captureId == Guid.Empty)
        {
            return Invalid<ReplayCandidateView>("A capture identity is required.");
        }
        try
        {
            var capture = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
            if (capture is null)
            {
                return NotFound<ReplayCandidateView>("The requested capture was not found.");
            }
            var registry = await operations.GetRegistryAsync(cancellationToken).ConfigureAwait(false);
            var graphs = hostOptions.Value.ProcessingGraphs;
            return OperatorUiResult<ReplayCandidateView>.Success(new ReplayCandidateView(
                capture.CaptureId,
                capture.AgentId,
                capture.RigId,
                capture.CaptureSequence,
                capture.ExposureStartedUtc,
                capture.DurableIngressUtc,
                capture.RawState,
                capture.Detail?.EvidenceAvailability ?? "Unavailable",
                capture.Detail?.RawRetentionHold,
                ProjectPrimaryArtifact(capture),
                registry.Mode.ToString(),
                registry.ActiveRevisionId,
                registry.StateVersion,
                [.. registry.Revisions
                    .Where(static revision => revision.Lifecycle is ProcessingGraphRevisionLifecycle.Validated
                        or ProcessingGraphRevisionLifecycle.Active)
                    .Select(revision => new ReplayCandidateRevisionView(
                        revision.RevisionId,
                        revision.Name,
                        revision.Revision,
                        revision.Lifecycle.ToString(),
                        revision.DefinitionIdentitySha256,
                        string.Equals(revision.RevisionId, registry.ActiveRevisionId, StringComparison.Ordinal)))],
                new ReplayCapacityFactsView(
                    graphs.ReplayProfile.ToString(),
                    graphs.ReplayMaximumConcurrency,
                    graphs.ReplayMaximumPendingCount,
                    graphs.ReplayDeadlineSeconds,
                    graphs.ReplayMaximumQueueAgeSeconds)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent replay candidate read failed.");
            return Unavailable<ReplayCandidateView>("The replay freeze summary is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<ReplaySubmissionView>> SubmitReplayAsync(
        Guid captureId,
        string revisionId,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            async (actor, token) =>
            {
                var result = await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        captureId,
                        revisionId,
                        PrimaryArtifactId: null,
                        TriggerKind: "operator",
                        TriggerReference: null,
                        Priority: 0,
                        Reason: reason),
                    idempotencyKey,
                    actor,
                    token).ConfigureAwait(false);
                return new ReplaySubmissionView(
                    result.Replayed
                        ? ReplaySubmissionOutcome.ExistingRequestReturned
                        : ReplaySubmissionOutcome.Accepted,
                    Project(result.Execution, nodes: null));
            },
            "The replay request could not be submitted.",
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service returns fixed sanitized failures.")]
    public async ValueTask<OperatorUiResult<ReplayExecutionView>> GetReplayExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false))
        {
            return Denied<ReplayExecutionView>();
        }
        if (executionId == Guid.Empty)
        {
            return Invalid<ReplayExecutionView>("An execution identity is required.");
        }
        try
        {
            var detail = await operations.ReadExecutionDetailAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                var state = await operations.ReadExecutionAsync(executionId, cancellationToken).ConfigureAwait(false);
                return state is null
                    ? NotFound<ReplayExecutionView>("The requested replay execution was not found.")
                    : OperatorUiResult<ReplayExecutionView>.Success(Project(state, nodes: null));
            }
            return OperatorUiResult<ReplayExecutionView>.Success(Project(detail.Execution, detail.Nodes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent replay execution read failed.");
            return Unavailable<ReplayExecutionView>("Replay execution progress is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<ReplayExecutionView>> CancelReplayAsync(
        Guid executionId,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            async (actor, token) => Project(
                await operations.CancelReplayAsync(executionId, idempotencyKey, actor, reason, token)
                    .ConfigureAwait(false),
                nodes: null),
            "The replay cancellation could not be completed.",
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<T>> ExecuteMutationAsync<T>(
        Func<string, CancellationToken, ValueTask<T>> command,
        string unavailableMessage,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        if (principal is null)
        {
            return Denied<T>();
        }
        var actor = CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<T>();
        }
        try
        {
            return OperatorUiResult<T>.Success(await command(actor, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Invalid<T>("The replay command is invalid.");
        }
        catch (ProcessingReplayCapacityException)
        {
            return Unavailable<T>(CapacityMessage);
        }
        catch (ProcessingGraphStoreConflictException)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Conflict, ConflictMessage);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FileNotFoundException)
        {
            return NotFound<T>(
                "The replay source capture, artifact, or graph revision is no longer retained.");
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return Invalid<T>("The replay input or graph revision is not usable for an exact replay.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent replay UI mutation failed.");
            return Unavailable<T>(unavailableMessage);
        }
    }

    private static ReplayCandidateArtifactView? ProjectPrimaryArtifact(CameraAgentGalleryCapture capture)
    {
        var artifact = capture.Artifacts.FirstOrDefault(static candidate => candidate.Role == FrameArtifactRole.Raw)
            ?? (capture.Artifacts.Count > 0 ? capture.Artifacts[0] : null);
        if (artifact is null)
        {
            return null;
        }
        var state = capture.Detail?.ArtifactStates
            .FirstOrDefault(candidate => candidate.ArtifactId == artifact.ArtifactId);
        return new ReplayCandidateArtifactView(
            artifact.ArtifactId,
            artifact.Role.ToString(),
            artifact.Variant,
            artifact.ChecksumSha256,
            artifact.ByteLength,
            artifact.Availability,
            state?.RetentionState ?? "Unavailable",
            state?.DeliveryAvailability ?? "Unavailable");
    }

    /// <summary>Terminal replay states never poll again and never offer cancellation.</summary>
    internal static bool IsTerminalStatus(ProcessingGraphExecutionStatus status)
        => status is ProcessingGraphExecutionStatus.Completed or ProcessingGraphExecutionStatus.Failed
            or ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired;

    internal static ReplayExecutionView Project(
        ProcessingGraphExecutionState execution,
        IReadOnlyList<ProcessingGraphExecutionNodeState>? nodes)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new ReplayExecutionView(
            execution.ExecutionId,
            execution.ExecutionClass.ToString(),
            execution.Status.ToString(),
            IsTerminalStatus(execution.Status),
            execution.CaptureId,
            execution.PrimaryArtifactId,
            execution.GraphRevisionId,
            execution.GraphDefinitionIdentitySha256,
            execution.SharedPlanIdentitySha256,
            execution.LocalPlanIdentitySha256,
            execution.TriggerKind,
            Sanitize(execution.TriggerReference),
            execution.Priority,
            execution.AttemptCount,
            execution.CancellationRequested,
            execution.AcceptedUtc,
            execution.AvailableUtc,
            execution.DeadlineUtc,
            execution.MaximumAgeUtc,
            execution.StartedUtc,
            execution.CompletedUtc,
            Sanitize(execution.FailureReason),
            nodes is null ? [] : [.. nodes.Select(ProjectNode)]);
    }

    private static ReplayExecutionNodeView ProjectNode(ProcessingGraphExecutionNodeState node)
        => new(
            node.NodeId,
            node.Required,
            node.Status,
            Sanitize(node.Reason),
            node.AttemptCount,
            node.StartedUtc,
            node.CompletedUtc,
            // LeaseOwner is deliberately dropped: durable lease identity never reaches the operator UI.
            [.. node.Attempts.Select(static attempt => new ReplayExecutionAttemptView(
                attempt.AttemptNumber,
                attempt.Status,
                attempt.Outcome?.ToString(),
                Sanitize(attempt.Reason),
                attempt.StartedUtc,
                attempt.CompletedUtc,
                attempt.Duration))],
            [.. node.Inputs.Select(static input => new ReplayExecutionInputView(
                input.Ordinal,
                input.WindowPosition,
                input.Kind.ToString(),
                input.CaptureId,
                input.ArtifactId,
                input.DescriptorSha256,
                input.PayloadSha256))],
            [.. node.Outputs.Select(static output => new ReplayExecutionOutputView(
                output.Ordinal,
                output.ArtifactId,
                output.Role.ToString(),
                output.Variant,
                output.OutputIdentitySha256,
                output.AvailabilityState,
                Sanitize(output.AvailabilityReason)))]);

    /// <summary>
    /// Bounds free text and withholds anything path-like or token-like so retained storage
    /// locations and bearer-shaped values cannot leak through a durable reason string.
    /// </summary>
    internal static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        if (trimmed.IndexOfAny(PathLikeCharacters) >= 0)
        {
            return WithheldValue;
        }
        foreach (var word in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 24 && word.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character is '+' or '=' or '_' or '-' or '.'))
            {
                return WithheldValue;
            }
        }
        return trimmed.Length <= 240 ? trimmed : trimmed[..240];
    }

    private async Task<bool> IsAuthorizedAsync(string policy)
        => await GetAuthorizedPrincipalAsync(policy).ConfigureAwait(false) is not null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }

    private static OperatorUiResult<T> Denied<T>()
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

    private static OperatorUiResult<T> Invalid<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, message);

    private static OperatorUiResult<T> NotFound<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.NotFound, message);

    private static OperatorUiResult<T> Unavailable<T>(string message)
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, message);
}
