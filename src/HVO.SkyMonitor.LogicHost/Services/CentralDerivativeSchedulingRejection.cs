using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// Durable per-artifact record of a source whose evidence failed integrity validation while derivatives were being
/// scheduled. The marker keeps background scheduling loops from failing or reselecting the source ahead of good work;
/// the next recovery generation clears it when it verifies the object again and then schedules once more.
/// </summary>
internal static class CentralDerivativeSchedulingRejection
{
    internal const string ReasonCode = "object.derivative-scheduling-rejected";
    internal const string Outcome = "scheduling-rejected";

    internal static Task<int> RecordAsync(ApplicationDbContext db, Guid centralArtifactId, CancellationToken cancellationToken)
        => RecordAsync(db.CentralArtifacts.Where(artifact => artifact.Id == centralArtifactId), cancellationToken);

    internal static Task<int> RecordAsync(
        ApplicationDbContext db,
        Guid devicePublicId,
        Guid artifactId,
        CancellationToken cancellationToken)
        => RecordAsync(
            db.CentralArtifacts.Where(artifact => artifact.DevicePublicId == devicePublicId
                && artifact.ArtifactId == artifactId),
            cancellationToken);

    private static Task<int> RecordAsync(IQueryable<CentralArtifact> target, CancellationToken cancellationToken)
        // Only a usable source without another recorded reason is marked, so a concurrent state change such as a
        // missing object, a quarantine or a lineage finding is never overwritten.
        => target.Where(artifact => artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete
                && artifact.StateReasonCode == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                artifact => artifact.StateReasonCode, ReasonCode), cancellationToken);
}
