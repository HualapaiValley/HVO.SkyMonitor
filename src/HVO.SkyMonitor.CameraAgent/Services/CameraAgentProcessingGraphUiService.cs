using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Operator-facing reads and lifecycle mutations over the delivered processing-graph
/// operations. Reads are bounded and sanitized; mutations carry the caller's owner id,
/// an idempotency key, and, where the store requires it, the expected registry version.
/// </summary>
internal interface ICameraAgentProcessingGraphUiService
{
    ValueTask<OperatorUiResult<CameraAgentProcessingExecutionsView>> GetExecutionsAsync(
        int maximumPerClass,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentProcessingExecutionDetailView>> GetExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> GetRegistryAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> ActivateAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RollbackAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RetireAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ProcessingGraphRevisionState>> ValidateAsync(
        string revisionId, string idempotencyKey, string? reason, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ProcessingGraphRevisionState>> CreateRevisionAsync(
        string name, string revision, CapturePipelineConfig pipeline, string idempotencyKey, string? reason, CancellationToken cancellationToken);
}

/// <summary>One execution as the operator sees it; lease owners and runner internals are not carried.</summary>
internal sealed record CameraAgentProcessingExecutionSummary(
    Guid ExecutionId,
    ProcessingGraphExecutionClass ExecutionClass,
    ProcessingGraphExecutionStatus Status,
    Guid CaptureId,
    Guid PrimaryArtifactId,
    string GraphRevisionId,
    string GraphDefinitionIdentitySha256,
    string TriggerKind,
    string? TriggerReference,
    int Priority,
    DateTimeOffset AcceptedUtc,
    DateTimeOffset AvailableUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset MaximumAgeUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureReason,
    bool CancellationRequested,
    int AttemptCount)
{
    public bool IsTerminal => Status is ProcessingGraphExecutionStatus.Completed
        or ProcessingGraphExecutionStatus.Failed
        or ProcessingGraphExecutionStatus.Cancelled
        or ProcessingGraphExecutionStatus.Expired;

    public TimeSpan? Duration => StartedUtc is { } started && CompletedUtc is { } completed ? completed - started : null;

    public TimeSpan Age(DateTimeOffset now) => now - AcceptedUtc;
}

internal sealed record CameraAgentProcessingExecutionsView(
    DateTimeOffset ReadUtc,
    int MaximumPerClass,
    IReadOnlyList<CameraAgentProcessingExecutionSummary> Live,
    IReadOnlyList<CameraAgentProcessingExecutionSummary> Replay)
{
    public int Pending(ProcessingGraphExecutionClass executionClass) => Count(executionClass, ProcessingGraphExecutionStatus.Pending);

    public int Running(ProcessingGraphExecutionClass executionClass) => Count(executionClass, ProcessingGraphExecutionStatus.Running);

    public bool HasActiveWork => Live.Concat(Replay).Any(static execution => !execution.IsTerminal);

    private int Count(ProcessingGraphExecutionClass executionClass, ProcessingGraphExecutionStatus status)
        => (executionClass == ProcessingGraphExecutionClass.Live ? Live : Replay).Count(execution => execution.Status == status);
}

internal sealed record CameraAgentProcessingNodeAttemptView(
    int AttemptNumber,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    string Status,
    string? Outcome,
    string? Reason,
    TimeSpan? Duration);

internal sealed record CameraAgentProcessingNodeView(
    string NodeId,
    bool Required,
    string PlanSha256,
    string Status,
    string? Reason,
    int AttemptCount,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<ProcessingGraphExecutionInputState> Inputs,
    IReadOnlyList<CameraAgentProcessingNodeAttemptView> Attempts,
    IReadOnlyList<ProcessingGraphExecutionOutputState> Outputs);

internal sealed record CameraAgentProcessingExecutionDetailView(
    DateTimeOffset ReadUtc,
    CameraAgentProcessingExecutionSummary Execution,
    string SharedPlanIdentitySha256,
    string LocalPlanIdentitySha256,
    IReadOnlyList<CameraAgentProcessingNodeView> Nodes);

internal static class CameraAgentProcessingExecutionProjection
{
    internal const int MaximumPerClass = 50;

    internal static CameraAgentProcessingExecutionSummary Summarize(ProcessingGraphExecutionState execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new CameraAgentProcessingExecutionSummary(
            execution.ExecutionId,
            execution.ExecutionClass,
            execution.Status,
            execution.CaptureId,
            execution.PrimaryArtifactId,
            execution.GraphRevisionId,
            execution.GraphDefinitionIdentitySha256,
            execution.TriggerKind,
            execution.TriggerReference,
            execution.Priority,
            execution.AcceptedUtc,
            execution.AvailableUtc,
            execution.DeadlineUtc,
            execution.MaximumAgeUtc,
            execution.StartedUtc,
            execution.CompletedUtc,
            execution.FailureReason,
            execution.CancellationRequested,
            execution.AttemptCount);
    }

    internal static CameraAgentProcessingExecutionDetailView Detail(ProcessingGraphExecutionDetail detail, DateTimeOffset readUtc)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new CameraAgentProcessingExecutionDetailView(
            readUtc,
            Summarize(detail.Execution),
            detail.Execution.SharedPlanIdentitySha256,
            detail.Execution.LocalPlanIdentitySha256,
            detail.Nodes.Select(static node => new CameraAgentProcessingNodeView(
                node.NodeId,
                node.Required,
                node.PlanSha256,
                node.Status,
                node.Reason,
                node.AttemptCount,
                node.StartedUtc,
                node.CompletedUtc,
                node.Inputs,
                // Lease owners identify runner processes and stay inside the store.
                node.Attempts.Select(static attempt => new CameraAgentProcessingNodeAttemptView(
                    attempt.AttemptNumber,
                    attempt.StartedUtc,
                    attempt.CompletedUtc,
                    attempt.Status,
                    attempt.Outcome?.ToString(),
                    attempt.Reason,
                    attempt.Duration)).ToArray(),
                node.Outputs)).ToArray());
    }
}

internal sealed class CameraAgentProcessingGraphUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IProcessingGraphOperations operations,
    TimeProvider timeProvider,
    ILogger<CameraAgentProcessingGraphUiService> logger) : ICameraAgentProcessingGraphUiService
{
    public ValueTask<OperatorUiResult<CameraAgentProcessingExecutionsView>> GetExecutionsAsync(
        int maximumPerClass,
        CancellationToken cancellationToken)
        => ReadAsync(async token =>
        {
            var bounded = Math.Clamp(maximumPerClass, 1, CameraAgentProcessingExecutionProjection.MaximumPerClass);
            var live = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, bounded, token).ConfigureAwait(false);
            var replay = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Replay, bounded, token).ConfigureAwait(false);
            return new CameraAgentProcessingExecutionsView(
                timeProvider.GetUtcNow(),
                bounded,
                live.Select(CameraAgentProcessingExecutionProjection.Summarize).ToArray(),
                replay.Select(CameraAgentProcessingExecutionProjection.Summarize).ToArray());
        }, "Current execution data is unavailable.", cancellationToken);

    public ValueTask<OperatorUiResult<CameraAgentProcessingExecutionDetailView>> GetExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken)
        => ReadAsync(async token =>
        {
            var detail = await operations.ReadExecutionDetailAsync(executionId, token).ConfigureAwait(false);
            return detail is null ? null : CameraAgentProcessingExecutionProjection.Detail(detail, timeProvider.GetUtcNow());
        }, "The execution could not be read.", cancellationToken, "The execution was not found.");

    public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> GetRegistryAsync(CancellationToken cancellationToken)
        => ReadAsync(async token => await operations.GetRegistryAsync(token).ConfigureAwait(false), "Current graph registry data is unavailable.", cancellationToken);

    public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> ActivateAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        => MutateAsync((actor, token) => operations.ActivateRevisionAsync(revisionId, expectedVersion, idempotencyKey, actor, reason, token), cancellationToken);

    public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RollbackAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        => MutateAsync((actor, token) => operations.RollbackRevisionAsync(revisionId, expectedVersion, idempotencyKey, actor, reason, token), cancellationToken);

    public ValueTask<OperatorUiResult<ProcessingGraphRegistryState>> RetireAsync(
        string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        => MutateAsync((actor, token) => operations.RetireRevisionAsync(revisionId, expectedVersion, idempotencyKey, actor, reason, token), cancellationToken);

    public ValueTask<OperatorUiResult<ProcessingGraphRevisionState>> ValidateAsync(
        string revisionId, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        => MutateAsync((actor, token) => operations.ValidateRevisionAsync(revisionId, idempotencyKey, actor, reason, token), cancellationToken);

    public ValueTask<OperatorUiResult<ProcessingGraphRevisionState>> CreateRevisionAsync(
        string name, string revision, CapturePipelineConfig pipeline, string idempotencyKey, string? reason, CancellationToken cancellationToken)
        => MutateAsync((actor, token) => operations.CreateRevisionAsync(name, revision, pipeline, idempotencyKey, actor, reason, token), cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<T>> ReadAsync<T>(
        Func<CancellationToken, ValueTask<T?>> read,
        string unavailableMessage,
        CancellationToken cancellationToken,
        string? notFoundMessage = null)
        where T : class
    {
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is null)
        {
            return Denied<T>();
        }
        try
        {
            var value = await read(cancellationToken).ConfigureAwait(false);
            return value is null
                ? OperatorUiResult<T>.Failure(OperatorUiResultKind.NotFound, notFoundMessage ?? "Not found.")
                : OperatorUiResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent processing graph UI read failed.");
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, unavailableMessage);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<T>> MutateAsync<T>(
        Func<string, CancellationToken, ValueTask<T>> mutation,
        CancellationToken cancellationToken)
        where T : class
    {
        var principal = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<T>();
        }
        try
        {
            return OperatorUiResult<T>.Success(await mutation(actor, cancellationToken).ConfigureAwait(false));
        }
        catch (ProcessingGraphStoreConflictException)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Conflict, "Graph registry state changed. Refresh and review the command again.");
        }
        catch (KeyNotFoundException)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.NotFound, "The graph revision was not found.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            logger.LogInformation(exception, "CameraAgent processing graph UI mutation rejected.");
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Invalid, "The graph command is invalid for the current registry state.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent processing graph UI mutation failed.");
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, "The graph command could not be completed.");
        }
    }

    private static OperatorUiResult<T> Denied<T>()
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}
