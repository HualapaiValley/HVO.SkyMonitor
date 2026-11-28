using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public enum CenteredViewLiveState { Warming, Building, Ready, Paused, Stale, Gap }

/// <summary>The selected centered stream and its durable admission head, independent of display readiness.</summary>
public sealed record CenteredViewLiveContext(
    CenteredViewStream Stream,
    CameraAgentPresentationCapture? AdmissionHead,
    DateTimeOffset ObservedUtc,
    CenteredViewLiveState State,
    string? Reason);

/// <summary>Resolves scope from the selected camera and from retained capture identity.</summary>
public interface ICenteredViewLookup
{
    /// <summary>Returns null if no centered journal run is visible under the selected profile.</summary>
    ValueTask<CenteredViewLiveContext?> ReadLiveContextAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns all retained bases for this reference, including bases in prior runs and generations.</summary>
    ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadArchiveAsync(
        Guid referenceCaptureId, CancellationToken cancellationToken = default);
}

internal sealed class CenteredViewLookup(
    CaptureScheduleRuntimeCoordinator schedule, ICenteredExposureRuntimeReader runtime,
    IExposureProductReader products, SqliteCenteredViews views) : ICenteredViewLookup
{
    internal static CameraKey? ResolveCamera(CaptureScheduleRuntimeSnapshot snapshot) =>
        snapshot.Configuration is { CenteredExposure: { } profile, AgentId: { Length: > 0 } agent } && profile.IsValid()
            ? new(agent, profile.CameraId) : null;

    internal static CenteredViewStream? ResolveStream(CenteredExposureRuntimeSnapshot snapshot) =>
        snapshot is { RunId: { } runId, GraphRevisionId: { Length: > 0 } generation }
            ? new(snapshot.Camera, runId, generation) : null;

    public async ValueTask<CenteredViewLiveContext?> ReadLiveContextAsync(CancellationToken cancellationToken = default)
    {
        while (schedule.Snapshot is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = schedule.CaptureContext;
            if (ResolveCamera(context.Snapshot) is not { } camera)
                return null;

            var acquired = await runtime.ReadCurrentAsync(camera, cancellationToken).ConfigureAwait(false);
            var result = await ReadContextAsync(context.Snapshot, acquired, cancellationToken).ConfigureAwait(false);
            if (!context.RevisionChanged.IsCancellationRequested)
                return result;
        }
        return null;
    }

    private async ValueTask<CenteredViewLiveContext?> ReadContextAsync(CaptureScheduleRuntimeSnapshot scheduleSnapshot,
        CenteredExposureRuntimeSnapshot acquired, CancellationToken token)
    {
        if (ResolveStream(acquired) is not { } stream)
            return null;

        CameraAgentPresentationCapture? admission = null;
        if (acquired.AcquisitionHead is { } head &&
            await products.ReadExposureAsync(head.Exposure, token).ConfigureAwait(false) is { } exposure)
        {
            admission = new(head.Exposure.CaptureId, exposure.Raw.Capture.CaptureSequence, head.ExposureStartedUtc,
                (long)Math.Max(0, (acquired.ObservedUtc - head.ExposureStartedUtc).TotalSeconds), GalleryEvidenceOrigin.Unknown);
        }

        if (scheduleSnapshot.CurrentDecision is { Admitted: false } closed)
            return new(stream, admission, acquired.ObservedUtc, CenteredViewLiveState.Paused, closed.Reason.ToString());
        if (acquired.Basis != CenteredRuntimeEvidenceBasis.ActiveAcquisition)
            return new(stream, admission, acquired.ObservedUtc, CenteredViewLiveState.Stale, acquired.Reason);
        if (acquired.AcquisitionHead is not { } activeHead)
            return new(stream, null, acquired.ObservedUtc, CenteredViewLiveState.Warming, "centered.awaiting-admission");
        if (admission is null)
            return new(stream, null, acquired.ObservedUtc, CenteredViewLiveState.Gap, "centered.admission-evidence-unavailable");

        var current = await views.ReadCurrentAsync(stream, token).ConfigureAwait(false);
        var state = CenteredViewLiveState.Warming;
        if (activeHead.Ordinal > 3)
        {
            var ready = current is not null &&
                (current.View.Base.Definition.Kind == CenteredViewKind.Stack ||
                    await views.IsPublishedSingleFallbackAsync(current.View.Base, token).ConfigureAwait(false));
            state = ready ? CenteredViewLiveState.Ready : CenteredViewLiveState.Building;
        }
        return new(stream, admission, acquired.ObservedUtc, state, state == CenteredViewLiveState.Building
            ? "centered.awaiting-window-publication" : null);
    }

    public ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadArchiveAsync(
        Guid referenceCaptureId, CancellationToken cancellationToken = default) =>
        views.ReadArchiveAsync(referenceCaptureId, cancellationToken);
}
