using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// One association between a capture of the camera and a central transient event: the capture supplies an
/// observation source, a background reference, or both. An event spanning several captures appears once per capture.
/// </summary>
internal sealed record CameraTransientEventAssociation(
    Guid CaptureId,
    DateTimeOffset CapturedAtUtc,
    Guid LogicalCameraInstallationId,
    Guid CentralTransientEventId,
    Guid EventId,
    DateTimeOffset EventCreatedUtc,
    int Version,
    TransientEventState EventState,
    CentralTransientReviewState ReviewState,
    TransientClassification EffectiveClassification,
    int EffectiveConfidenceMillionths,
    DateTimeOffset UpdatedUtc,
    bool CaptureIsObservationSource,
    bool CaptureIsBackground);

internal sealed partial class CameraOperationsReadService
{
    /// <summary>
    /// Event associations of the camera's captures, newest capture first. Camera membership opens the camera; each
    /// event still requires the existing all-contributor rule, so an event with a contributor outside the caller's
    /// memberships or credential scope is omitted rather than partially shown.
    /// </summary>
    public Task<CameraCaptureAnchoredPage<CameraTransientEventAssociation>> ListEventAssociationsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default)
    {
        var credentialScope = CentralArtifactCredentialAccess.GetObservatoryScope(principal);
        return CaptureAnchoredAsync(
            principal, observatoryId, cameraId, "events", take, cursor,
            (owner, position, limit) => EventAssociations(owner, credentialScope, observatoryId, cameraId,
                    [position.Id], position.ChildId)
                .Take(limit).ToListAsync(cancellationToken),
            (owner, captureIds, limit) => EventAssociations(owner, credentialScope, observatoryId, cameraId,
                    captureIds, null)
                .Take(limit).ToListAsync(cancellationToken),
            item => (item.CaptureId, item.CapturedAtUtc, null, item.CentralTransientEventId),
            cancellationToken);
    }

    /// <summary>
    /// Drives from the window's artifacts through the (CentralArtifactId) indexes on observation sources and
    /// backgrounds, so only events that reference these captures are read.
    /// </summary>
    internal IQueryable<CameraTransientEventAssociation> EventAssociations(
        string owner, Guid? credentialScope, Guid observatoryId, Guid cameraId, List<Guid> captureIds,
        Guid? beforeEventId)
    {
        var sources = db.CentralTransientObservationSources.AsNoTracking();
        var backgrounds = db.CentralTransientObservationBackgrounds.AsNoTracking();
        var frames = db.CentralFrames.AsNoTracking().Where(frame => captureIds.Contains(frame.Id) &&
            frame.ObservatoryId == observatoryId &&
            frame.LogicalCameraInstallation!.LogicalCameraId == cameraId &&
            frame.LogicalCameraInstallation.LogicalCamera!.ObservatoryId == observatoryId);
        var events = CentralTransientEventReadService.ApplyAccess(
            db, db.CentralTransientEventCurrent.AsNoTracking(), isAdmin: false, owner, credentialScope);
        var pairs = sources.Where(source => captureIds.Contains(source.Artifact!.CentralFrameId))
            .Select(source => new { CaptureId = source.Artifact!.CentralFrameId, source.Observation!.CentralTransientEventId })
            .Union(backgrounds.Where(background => captureIds.Contains(background.Artifact!.CentralFrameId))
                .Select(background => new
                {
                    CaptureId = background.Artifact!.CentralFrameId,
                    background.Observation!.CentralTransientEventId
                }));
        if (beforeEventId is { } before)
            pairs = pairs.Where(pair => pair.CentralTransientEventId.CompareTo(before) < 0);
        return pairs
            .Join(frames, pair => pair.CaptureId, frame => frame.Id, (pair, frame) => new { pair, frame })
            .Join(events, row => row.pair.CentralTransientEventId, item => item.CentralTransientEventId,
                (row, item) => new { row.frame, item })
            .OrderByDescending(row => row.frame.CapturedAtUtc)
            .ThenByDescending(row => row.frame.Id)
            .ThenByDescending(row => row.item.CentralTransientEventId)
            .Select(row => new CameraTransientEventAssociation(
                row.frame.Id,
                row.frame.CapturedAtUtc,
                row.frame.LogicalCameraInstallationId!.Value,
                row.item.CentralTransientEventId,
                row.item.Event!.EventId,
                row.item.Event.EventCreatedUtc,
                row.item.LatestVersion,
                row.item.LatestEventVersion!.State,
                row.item.ReviewState,
                row.item.EffectiveClassification,
                row.item.EffectiveConfidenceMillionths,
                row.item.UpdatedUtc,
                sources.Any(source => source.Artifact!.CentralFrameId == row.frame.Id &&
                    source.Observation!.CentralTransientEventId == row.item.CentralTransientEventId),
                backgrounds.Any(background => background.Artifact!.CentralFrameId == row.frame.Id &&
                    background.Observation!.CentralTransientEventId == row.item.CentralTransientEventId)));
    }
}
