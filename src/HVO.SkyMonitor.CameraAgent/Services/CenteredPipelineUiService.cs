using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

public sealed record CenteredPipelineRunningSnapshot(
    CameraKey? Camera = null,
    string? UnavailableReason = null,
    bool Paused = false,
    CenteredExposureRuntimeSnapshot? Acquisition = null,
    ExposureInputV1? Head = null,
    Guid? HeadExecutionId = null,
    CenteredViewCurrent? Display = null,
    CenteredWindowSnapshot? Window = null,
    IReadOnlySet<CenteredViewLayerKey>? PendingLayers = null);

internal sealed partial class CenteredPipelineUiService(
    AuthenticationStateProvider authentication,
    IAuthorizationService authorization,
    CaptureScheduleRuntimeCoordinator schedule,
    CaptureAdmissionCoordinator admission,
    ICenteredExposureRuntimeReader runtime,
    IExposureProductReader exposures,
    ICenteredViewReadIndex publications,
    ICenteredViewLookup lookup,
    ICenteredWindowReader windows,
    ICenteredViewLayerProgressReader layerProgress,
    IProcessingGraphOperations executions,
    ILogger<CenteredPipelineUiService> logger)
{
    public async ValueTask<OperatorUiResult<CenteredPipelineRunningSnapshot>> ReadRunningAsync(CancellationToken cancellationToken)
    {
        var state = await authentication.GetAuthenticationStateAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!(await authorization.AuthorizeAsync(state.User, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1)
                .ConfigureAwait(false)).Succeeded)
            return OperatorUiResult<CenteredPipelineRunningSnapshot>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

        if (schedule.Snapshot is null)
            return Unavailable("The capture schedule is still initializing.");
        var context = schedule.CaptureContext;
        var configuration = context.Snapshot.Configuration;
        if (configuration.CenteredExposure is not { } profile)
            return Unavailable("The selected profile uses legacy presentation. Capture details remain available under Processing executions.");
        if (!profile.IsValid() || string.IsNullOrWhiteSpace(configuration.AgentId))
            return Unavailable("The selected centered camera is unavailable.");

        var camera = new CameraKey(configuration.AgentId, profile.CameraId);
        var observed = await ReadEvidenceAsync(() => runtime.ReadCurrentAsync(camera, cancellationToken), "acquisition", cancellationToken)
            .ConfigureAwait(false);
        if (!MatchesCamera(observed, camera))
            observed = null;

        var current = await ReadEvidenceAsync(
            () => CenteredPipelinePublications.ReadLastLiveAsync(publications, camera, cancellationToken), "display", cancellationToken)
            .ConfigureAwait(false);
        var definition = current?.View.Base.Definition;
        var head = observed?.AcquisitionHead is { } admitted
            ? await ReadHeadAsync(admitted.Exposure, definition, cancellationToken).ConfigureAwait(false)
            : null;
        var headExecution = head is null || observed?.GraphRevisionId is not { } generation
            ? null : await ReadHeadExecutionAsync(head, generation, cancellationToken).ConfigureAwait(false);
        var activeStream = observed is { RunId: { } runId, GraphRevisionId: { } graphRevision }
            ? new CenteredViewStream(camera, runId, graphRevision) : null;
        var window = await ReadWindowAsync(definition, activeStream, observed?.AcquisitionHead?.Exposure.CaptureId, cancellationToken)
            .ConfigureAwait(false);
        var paused = context.Snapshot.CurrentDecision is { Admitted: false } ||
            admission.Snapshot.State is CaptureAdmissionState.PauseRequested or CaptureAdmissionState.Paused;
        var pending = current is not null && definition!.Stream == activeStream &&
            observed?.Basis == CenteredRuntimeEvidenceBasis.ActiveAcquisition
            ? await ReadEvidenceAsync(() => layerProgress.ReadPendingAsync(current.View, cancellationToken),
                "active layer producers", cancellationToken).ConfigureAwait(false)
            : null;

        // All facts above belong to the camera selected by this schedule revision.
        cancellationToken.ThrowIfCancellationRequested();
        if (context.RevisionChanged.IsCancellationRequested)
            return Unavailable("The selected capture profile changed while evidence was being read.");
        return OperatorUiResult<CenteredPipelineRunningSnapshot>.Success(new(camera, null, paused, observed, head,
            headExecution?.ExecutionId, current, window, pending));
    }

    private static OperatorUiResult<CenteredPipelineRunningSnapshot> Unavailable(string reason)
        => OperatorUiResult<CenteredPipelineRunningSnapshot>.Success(new(UnavailableReason: reason));

    internal static bool MatchesCamera(CenteredExposureRuntimeSnapshot? observation, CameraKey camera) =>
        observation is not null && observation.Camera == camera &&
        (observation.Basis == CenteredRuntimeEvidenceBasis.Unavailable || observation.RunId is not null) &&
        (observation.AcquisitionHead is not { } head || head.Exposure.Camera == camera && head.Exposure.RunId == observation.RunId);

    private async ValueTask<ExposureInputV1?> ReadHeadAsync(ExposureKey key, CenteredViewBaseDefinition? display, CancellationToken token)
    {
        var retained = display?.Contributors.FirstOrDefault(contributor => contributor.Exposure.Input.Key == key)?.Exposure.Input;
        var input = retained ?? await ReadEvidenceAsync(() => exposures.ReadExposureAsync(key, token), "admitted input", token)
            .ConfigureAwait(false);
        return input?.Key == key && input.Raw.Capture.CaptureId == key.CaptureId ? input : null;
    }

    private ValueTask<ProcessingGraphExecutionState?> ReadHeadExecutionAsync(ExposureInputV1 head, string generation, CancellationToken token)
        => ReadEvidenceAsync(async () =>
        {
            var id = await executions.ReadLiveExecutionIdAsync(head.Key.CaptureId, token).ConfigureAwait(false);
            if (id is null)
                return null;
            var execution = await executions.ReadExecutionAsync(id.Value, token).ConfigureAwait(false);
            return execution is { ExecutionClass: ProcessingGraphExecutionClass.Live } && execution.ExecutionId == id &&
                execution.CaptureId == head.Key.CaptureId && execution.GraphRevisionId == generation &&
                execution.PrimaryArtifactId == head.Raw.Artifact.ArtifactId ? execution : null;
        }, "acquisition execution", token);

    private async ValueTask<CenteredWindowSnapshot?> ReadWindowAsync(CenteredViewBaseDefinition? display,
        CenteredViewStream? active, Guid? acquisitionCaptureId, CancellationToken token)
    {
        // A stack's slot membership comes from its published base. An older stack without those slots stays unavailable.
        if (display?.Kind == CenteredViewKind.Stack)
            return null;
        var stream = display?.Stream ?? active;
        var capture = display?.Reference.Capture.CaptureId ?? acquisitionCaptureId;
        if (stream is null || capture is null)
            return null;
        var window = await ReadEvidenceAsync(() => windows.ReadAsync(capture.Value, token), "centered window", token)
            .ConfigureAwait(false);
        return window?.ReferenceCaptureId == capture && window.Camera == stream.Camera && window.RunId == stream.RunId &&
            (window.GenerationId is null || window.GenerationId == stream.GenerationId || window.GenerationId == stream.RunId.ToString("N"))
            ? window : null;
    }

    // Called after the execution-detail service has authorized its read.
    internal async ValueTask<CenteredPipelineRunPublications?> ReadRunAsync(ProcessingGraphExecutionState execution, CancellationToken token)
    {
        if (execution.ExecutionClass != ProcessingGraphExecutionClass.Live)
            return null;
        var views = await ReadEvidenceAsync(() => CenteredPipelinePublications.ReadRunAsync(publications, lookup, execution, logger, token),
            "retained publications", token).ConfigureAwait(false);
        if (views is not { Count: > 0 })
            return null;
        var scope = views[0].Base.Definition.Stream;
        var recorded = await ReadEvidenceAsync(() => runtime.ReadRunAsync(scope.Camera, scope.RunId, token), "retained acquisition run", token)
            .ConfigureAwait(false);
        var version = MatchesCamera(recorded, scope.Camera) && recorded!.RunId == scope.RunId &&
            recorded.GraphRevisionId == scope.GenerationId ? recorded.ExecutionVersion : null;
        return new(views, version);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An unavailable read must not hide independently recorded operator evidence.")]
    private async ValueTask<T?> ReadEvidenceAsync<T>(Func<ValueTask<T>> read, string source, CancellationToken token) where T : class?
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            ReadFailed(logger, source, exception);
            return null;
        }
    }

    [LoggerMessage(2534, LogLevel.Warning, "Operations could not read centered pipeline {Source} evidence.")]
    private static partial void ReadFailed(ILogger logger, string source, Exception exception);
}
