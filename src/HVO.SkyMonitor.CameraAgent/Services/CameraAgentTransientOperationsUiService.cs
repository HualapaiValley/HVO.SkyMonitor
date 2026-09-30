using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentTransientOperationsUiService
{
    ValueTask<OperatorUiResult<TransientOperationsView>> GetOverviewAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The detector lane as the Operations workspace shows it. The worker and delivery snapshots are in-memory and always
/// present; each durable read fails on its own, carrying a message instead of a list, so one unavailable store never
/// hides the rest of the lane.
/// </summary>
internal sealed record TransientOperationsView(
    DateTimeOffset EvaluatedUtc,
    TransientOperatingMode Mode,
    bool Required,
    bool CentralIntegrationEnabled,
    TransientDetectorProfileView Profile,
    TransientWorkerSnapshot Worker,
    TransientCandidateDeliverySnapshot Delivery,
    IReadOnlyList<TransientCaptureOutcome>? RecentOutcomes,
    string? OutcomesUnavailable,
    IReadOnlyList<CameraAgentTransientOperatorCandidate>? RecentCandidates,
    string? CandidatesUnavailable,
    TransientLatestEventRun? LatestEventRun);

/// <summary>The newest capture with a causal candidate, and its live pipeline run when one was recorded.</summary>
internal sealed record TransientLatestEventRun(Guid CaptureId, long CaptureSequence, Guid? ExecutionId);

/// <summary>
/// The detector's effective settings: the versioned extraction profile, which is fixed in code, and the lane's
/// configured runtime bounds. Device identity and storage paths are never part of it.
/// </summary>
internal sealed record TransientDetectorProfileView(
    string ExtractionProfile,
    TransientCandidateExtractionOptionsV1 Extraction,
    int CandidateTimeoutMinutes,
    int MaximumAttempts,
    int RetryInitialDelaySeconds,
    int RetryMaximumDelaySeconds,
    int MaximumAdjacentStartIntervalSeconds,
    double StarMaximumMagnitude,
    int StarMaximumResults,
    string AssociationAlgorithm,
    double AssociationMaximumStartIntervalSeconds,
    double AssociationMaximumEndpointGapPixels,
    double AssociationMinimumAxisAlignment);

internal sealed class CameraAgentTransientOperationsUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IOptions<CameraAgentHostOptions> options,
    TransientWorkerState workerState,
    TransientCandidateDeliveryState deliveryState,
    ITransientRuntimeManagement runtime,
    ICameraAgentTransientOperatorProjection projection,
    IProcessingGraphOperations processing,
    TimeProvider timeProvider,
    ILogger<CameraAgentTransientOperationsUiService> logger) : ICameraAgentTransientOperationsUiService
{
    internal const string ExtractionProfileName = "edge-v1";
    internal const int RecentOutcomeCount = 8;
    internal const int RecentCandidateCount = 6;

    public async ValueTask<OperatorUiResult<TransientOperationsView>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        if (!(await authorizationService.AuthorizeAsync(
                state.User, resource: null, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded)
        {
            return OperatorUiResult<TransientOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        var host = options.Value;
        var detection = host.TransientDetection;
        var outcomes = await ReadAsync(
            token => runtime.ReadRecentCaptureOutcomesAsync(RecentOutcomeCount, token),
            "transient capture outcomes",
            "Recent detector outcomes are temporarily unavailable.",
            cancellationToken).ConfigureAwait(false);
        var candidates = await ReadAsync(
            async token => (await projection.GetPageAsync(
                new CameraAgentTransientOperatorQuery(PageSize: RecentCandidateCount), token).ConfigureAwait(false)).Items,
            "transient candidates",
            "Recent candidates are temporarily unavailable.",
            cancellationToken).ConfigureAwait(false);
        return OperatorUiResult<TransientOperationsView>.Success(new(
            timeProvider.GetUtcNow(),
            detection.Mode,
            detection.Required,
            host.CentralIntegration.Mode == CentralIntegrationMode.Enabled,
            Profile(detection),
            workerState.Snapshot,
            deliveryState.Snapshot,
            outcomes.Value,
            outcomes.Message,
            candidates.Value,
            candidates.Message,
            await ReadLatestEventRunAsync(outcomes.Value, cancellationToken).ConfigureAwait(false)));
    }

    private static TransientDetectorProfileView Profile(TransientDetectionOptions detection)
        => new(
            ExtractionProfileName,
            TransientCandidateExtractionProfiles.EdgeV1,
            detection.CandidateTimeoutMinutes,
            detection.MaximumAttempts,
            detection.RetryInitialDelaySeconds,
            detection.RetryMaximumDelaySeconds,
            detection.MaximumAdjacentStartIntervalSeconds,
            detection.StarMaximumMagnitude,
            detection.StarMaximumResults,
            detection.Association.AlgorithmVersion,
            detection.Association.MaximumStartIntervalSeconds,
            detection.Association.MaximumEndpointGapPixels,
            detection.Association.MinimumAbsolutePrincipalAxisAlignment);

    /// <summary>
    /// The run link is a convenience: a failed lookup leaves the link out rather than failing the page, and a capture
    /// whose run was not recorded still names the capture so the operator can open its evidence.
    /// </summary>
    private async ValueTask<TransientLatestEventRun?> ReadLatestEventRunAsync(
        IReadOnlyList<TransientCaptureOutcome>? outcomes, CancellationToken cancellationToken)
    {
        if (outcomes?.FirstOrDefault(static outcome => outcome.CandidateCount > 0) is not { } latest)
        {
            return null;
        }
        var run = await ReadAsync(
            token => processing.ReadLiveExecutionIdAsync(latest.CaptureId, token),
            "transient event run",
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        return new(latest.CaptureId, latest.CaptureSequence, run.Value);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Each durable read logs its internal failure and returns a fixed sanitized message so the rest of the lane still renders.")]
    private async ValueTask<(T? Value, string? Message)> ReadAsync<T>(
        Func<CancellationToken, ValueTask<T>> read, string readName, string unavailable, CancellationToken cancellationToken)
    {
        try
        {
            return (await read(cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent transient operations read of {ReadName} failed.", readName);
            return (default, unavailable);
        }
    }
}
