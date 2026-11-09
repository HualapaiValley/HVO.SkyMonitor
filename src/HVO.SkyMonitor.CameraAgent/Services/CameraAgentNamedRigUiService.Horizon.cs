using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record HorizonObstructionPreview(string ActiveRevisionId, long SelectionVersion,
    HorizonObstructionProfile Profile, IReadOnlyList<HorizonObstructionInterval> Intervals);

internal sealed partial class CameraAgentNamedRigUiService
{
    private const string HorizonRevalidationRequired =
        "Horizon revalidation required. Detection is quarantined and sky overlays are withheld after activation until a revalidated horizon is explicitly activated.";

    private string HorizonCompatibility(CameraRigConfig rig)
    {
        if (rig.HorizonObstruction is not { } profile) return "This revision has no obstruction profile.";
        try
        {
            // Draft equipment can be retained before the ordinary rig preview rejects its geometry.
            // A changed binding already requires revalidation; do not construct an invalid projector
            // just to rediscover that fact, or let one draft hide the other catalog revisions.
            if (profile.GeometryIdentitySha256 != HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig))
                return HorizonRevalidationRequired;
            var snapshot = runtime.Snapshot;
            if (snapshot is null) return "Horizon compatibility unavailable; revalidate before use.";
            var site = snapshot.Configuration.ResolveObservatory(timeProvider.GetUtcNow());
            var evaluator = new HorizonObstructionEvaluator(profile, rig,
                new(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters), snapshot.Location);
            return evaluator.IsCompatible ? "Horizon compatible with this rig and the current site." : HorizonRevalidationRequired;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return "This revision has invalid horizon geometry; revalidation is required before activation.";
        }
    }

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
            if (HorizonObstructionProfile.ComputeGeometryIdentitySha256(configuration.Rig) !=
                    HorizonObstructionProfile.ComputeGeometryIdentitySha256(active.Rig) ||
                configuration.Rig.HorizonObstruction?.ComputeIdentitySha256() != active.Rig.HorizonObstruction?.ComputeIdentitySha256() ||
                CaptureContractJson.ComputeCanonicalJsonSha256(configuration.Module) != CaptureContractJson.ComputeCanonicalJsonSha256(active.Module))
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
            return new HorizonObstructionPreview(activeRevisionId, version, profile, evaluator.KnownIntervals());
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
