using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record HorizonObstructionPreview(string ActiveRevisionId, long SelectionVersion,
    HorizonObstructionProfile Profile, IReadOnlyList<HorizonObstructionSample> Horizon);

internal sealed partial class CameraAgentNamedRigUiService
{
    public ValueTask<OperatorUiResult<HorizonObstructionPreview>> PreviewHorizonAsync(string activeRevisionId, long version,
        string installationId, double maximumGap, IReadOnlyList<HorizonObstructionSample> samples, CancellationToken token)
        => MutateAsync(async _ =>
        {
            var catalog = await store.GetAsync(token).ConfigureAwait(false);
            if (catalog.Selection.ActiveRevisionId != activeRevisionId || catalog.Selection.Version != version ||
                catalog.Selection.PendingRevisionId is not null)
                throw new CaptureScheduleStoreConflictException("Rig selection changed.");
            var active = catalog.Revisions.Single(r => r.RevisionId == activeRevisionId);
            var configuration = runtime.Snapshot?.Configuration ?? throw new InvalidOperationException("Rig runtime is unavailable.");
            if (CameraRigProfileIdentity.ComputeSha256(configuration.Rig) != CameraRigProfileIdentity.ComputeSha256(active.Rig))
                throw new CaptureScheduleStoreConflictException("Active runtime differs from the reviewed rig.");
            var site = configuration.ResolveObservatory(timeProvider.GetUtcNow());
            var history = await store.GetHistoryAsync(active.ProfileId, 1, null, null, token).ConfigureAwait(false);
            var number = checked(history.Revisions.Max(static e => e.RevisionNumber) + 1);
            var profile = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion,
                active.Rig.HorizonObstruction?.ProfileId ?? Guid.NewGuid().ToString("N"), number, installationId,
                HorizonObstructionProfile.ComputeSiteIdentitySha256(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters, runtime.Snapshot!.Location),
                HorizonObstructionProfile.ComputeGeometryIdentitySha256(active.Rig), HorizonObstructionSource.Manual,
                maximumGap, samples);
            var evaluator = new HorizonObstructionEvaluator(profile, active.Rig,
                new(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters), runtime.Snapshot!.Location);
            var horizon = Array.AsReadOnly(Enumerable.Range(0, 361)
                .Select(az => new HorizonObstructionSample(az, evaluator.HorizonElevation(az))).ToArray());
            return new HorizonObstructionPreview(activeRevisionId, version, profile, horizon);
        });

    public ValueTask<OperatorUiResult<NamedRigRevision>> SaveHorizonAsync(HorizonObstructionPreview preview, CancellationToken token)
        => MutateAsync(async _ =>
        {
            ArgumentNullException.ThrowIfNull(preview);
            var configuration = runtime.Snapshot?.Configuration ?? throw new InvalidOperationException("Rig runtime is unavailable.");
            var site = configuration.ResolveObservatory(timeProvider.GetUtcNow());
            var evaluator = new HorizonObstructionEvaluator(preview.Profile, configuration.Rig,
                new(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters), runtime.Snapshot!.Location);
            if (!evaluator.IsCompatible) throw new CaptureScheduleStoreConflictException("Site or rig changed after preview.");
            return await store.RetainHorizonObstructionAsync(preview.ActiveRevisionId, preview.SelectionVersion,
                preview.Profile, token).ConfigureAwait(false);
        });
}
