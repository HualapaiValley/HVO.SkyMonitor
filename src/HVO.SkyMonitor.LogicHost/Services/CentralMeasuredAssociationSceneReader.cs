using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>The bound scene auxiliary, or the named reason the frozen scene failed verification.</summary>
internal sealed record CentralMeasuredAssociationScene(ProcessingAuxiliaryInput? Input, string? FailureReasonCode);

/// <summary>
/// Binds the frozen projected scene to a measured-association lease as the recipe's <c>scene</c> canonical-JSON
/// auxiliary, exactly as the edge step binds its scene product. The expansion froze the scene's content identity into
/// the node's expected identity and its artifact reference into the lease; here the payload is read under the lease and
/// verified twice, independently: its SHA-256 against the frozen artifact checksum, and its canonical identity,
/// recomputed with the identity field blanked (<see cref="ProjectedSceneJson.ComputeIdentity"/>), against the frozen
/// content identity. Both values are carried on the auxiliary (identity and checksum), and either mismatch fails the
/// node closed with its own reason code.
/// </summary>
internal sealed class CentralMeasuredAssociationSceneReader(
    ApplicationDbContext dbContext,
    ICentralArtifactObjectReader objectReader)
{
    internal const string ChecksumMismatchReasonCode = "measured-associations.scene-checksum-mismatch";
    internal const string IdentityMismatchReasonCode = "measured-associations.scene-identity-mismatch";

    /// <summary>Whether a lease executes the measured-association recipe against a frozen scene.</summary>
    internal static bool BindsScene(CentralDerivativeJobLease lease)
        => string.Equals(lease.RecipeName, BuiltInProcessingRecipes.MeasuredStellarAssociations, StringComparison.Ordinal)
            && lease.ProjectedScene is not null;

    /// <summary>The identity-bearing form of the auxiliary; the payload never enters the recipe identity.</summary>
    internal static ProcessingAuxiliaryInput CreateIdentityInput(CentralProjectedSceneReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new(
            BuiltInProcessingRecipes.MeasuredStellarAssociationsSceneInputName,
            ProcessingAuxiliaryInputKind.CanonicalJson,
            // Only v1 could bind a measured-association lease before schema was recorded. This fallback preserves
            // its identity; verification must never use it to relabel a legacy v2 annotation reference as v1.
            SchemaVersion: reference.SchemaVersion ?? ProjectedSceneV1.CurrentSchemaVersion,
            IdentitySha256: reference.ContentIdentitySha256);
    }

    public async Task<CentralMeasuredAssociationScene> ReadAsync(
        CentralDerivativeJobLease lease,
        ICentralDerivativeJobService jobService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(jobService);
        var reference = lease.ProjectedScene
            ?? throw new CentralDerivativeJobStateException("The projected-scene reference is missing.");
        var input = lease.Inputs?.SingleOrDefault(item => item.BindingName == CentralProjectedSceneResolver.BindingName);
        if (reference.CaptureId != lease.FrameId || input is null ||
            input.CentralArtifactId != reference.CentralArtifactId || input.ArtifactId != reference.ArtifactId ||
            !string.Equals(input.ChecksumSha256, reference.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDerivativeJobStateException("The projected-scene reference does not belong to the frozen lease.");
        }
        var artifact = await dbContext.CentralArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == reference.CentralArtifactId, cancellationToken)
            .ConfigureAwait(false);
        byte[] payload;
        try
        {
            if (artifact is null || artifact.ArtifactId != reference.ArtifactId ||
                artifact.ObjectState != CentralArtifactObjectState.Available ||
                artifact.ReconstructionState != CentralReconstructionState.Complete ||
                !string.Equals(artifact.ChecksumSha256, reference.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CentralArtifactIntegrityException("projected-scene.unavailable");
            }
            if (artifact.ByteLength is < 1 or > StructuredProcessingProductDescriptorV1.MaximumPayloadBytes)
            {
                throw new CentralArtifactIntegrityException("projected-scene.length-invalid");
            }
            var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            await using var destination = new MemoryStream(checked((int)artifact.ByteLength));
            await objectReader.CopyToAsync(snapshot, destination, null, cancellationToken).ConfigureAwait(false);
            if (destination.Length != artifact.ByteLength)
            {
                throw new CentralArtifactIntegrityException("object.length-mismatch", snapshot.StorageETag);
            }
            payload = destination.ToArray();
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactIntegrityException)
        {
            if (artifact is not null)
            {
                await jobService.MarkInputUnavailableAsync(lease.JobId, lease.LeaseToken, artifact.Id, artifact.RowVersion,
                    exception is CentralArtifactIntegrityException integrity ? integrity.ReasonCode : "object.missing",
                    quarantine: exception is CentralArtifactIntegrityException, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        return Verify(reference, payload);
    }

    /// <summary>The two independent checks over a scene payload read for a frozen reference.</summary>
    internal static CentralMeasuredAssociationScene Verify(CentralProjectedSceneReference reference, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(payload);
        var checksum = ProcessingIdentity.ComputePayloadSha256(payload);
        if (!string.Equals(checksum, reference.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, ChecksumMismatchReasonCode);
        }
        var scene = ProjectedSceneJson.Parse(payload).Scene;
        var identity = scene is null ? null : ProjectedSceneJson.ComputeIdentity(scene);
        if (scene is null || identity is null ||
            !ProjectedSceneV1.IsSupportedSchemaVersion(scene.SchemaVersion) ||
            !string.Equals(scene.SchemaVersion, reference.SchemaVersion ?? ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(scene.SceneIdentitySha256, identity, StringComparison.Ordinal) ||
            !string.Equals(identity, reference.ContentIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
            scene.Source.CaptureId != reference.CaptureId ||
            scene.Source != reference.Source)
        {
            return new(null, IdentityMismatchReasonCode);
        }
        return new(CreateIdentityInput(reference) with { Payload = payload, ChecksumSha256 = checksum }, null);
    }
}
