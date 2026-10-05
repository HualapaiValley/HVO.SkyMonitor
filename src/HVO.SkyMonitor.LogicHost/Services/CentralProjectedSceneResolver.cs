using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralProjectedSceneReference(
    Guid CentralArtifactId,
    Guid ArtifactId,
    Guid CaptureId,
    string ChecksumSha256,
    string ContentIdentitySha256,
    ProjectedSceneSource Source,
    string SceneId,
    string AnnotationIdentitySha256);

internal sealed record CentralProjectedSceneSelection(
    CentralArtifact Artifact,
    CentralProjectedSceneReference Reference,
    ProcessingAnnotationInput Annotation);

/// <summary>Resolves geometry without copying it into capture or job persistence.</summary>
internal sealed class CentralProjectedSceneResolver(
    ApplicationDbContext dbContext,
    ICentralArtifactObjectReader objectReader)
{
    internal const string BindingName = "capture-projected-scene";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static SceneProvenance? ReadProvenance(string? json)
        => json is null ? null : JsonSerializer.Deserialize<SceneProvenance>(json, JsonOptions);

    public async Task<CentralProjectedSceneSelection?> SelectAsync(
        CentralFrame frame, CancellationToken cancellationToken)
    {
        var provenance = ReadProvenance(frame.SceneProvenanceJson);
        if (provenance?.RequiresProjectedScene != true) return null;
        // Metadata/calibrated arrivals can precede raw. Freeze only after authenticating the raw identity.
        if (!await dbContext.CentralArtifacts.AsNoTracking().AnyAsync(raw =>
                raw.CentralFrameId == frame.Id && raw.Role == FrameArtifactRole.Raw &&
                raw.ObjectState == CentralArtifactObjectState.Available &&
                raw.ReconstructionState == CentralReconstructionState.Complete, cancellationToken).ConfigureAwait(false))
            return null;
        var candidates = await dbContext.CentralArtifacts
            .Include(item => item.StructuredProduct)
            .Where(item => item.CentralFrameId == frame.Id && item.Role == FrameArtifactRole.Metadata &&
                item.MediaType == StructuredProcessingProductContracts.ProjectedSceneMediaType &&
                item.ObjectState == CentralArtifactObjectState.Available &&
                item.ReconstructionState == CentralReconstructionState.Complete)
            .Take(2).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Length == 0) return null;
        if (candidates.Length != 1) throw new CentralArtifactIntegrityException("projected-scene.ambiguous");
        var artifact = candidates[0];
        var scene = await ReadAsync(artifact, cancellationToken).ConfigureAwait(false);
        if (!await dbContext.CentralArtifacts.AsNoTracking().AnyAsync(raw =>
                raw.CentralFrameId == artifact.CentralFrameId && raw.Role == FrameArtifactRole.Raw &&
                raw.ArtifactId == scene.Source.ArtifactId && raw.IdempotencyKey == scene.Source.ArtifactIdentitySha256 &&
                raw.ObjectState == CentralArtifactObjectState.Available &&
                raw.ReconstructionState == CentralReconstructionState.Complete, cancellationToken).ConfigureAwait(false))
            throw new CentralArtifactIntegrityException("projected-scene.raw-source-mismatch");
        ValidateSource(artifact, scene, frame.FrameId, provenance);
        var annotation = CreateAnnotation(scene, provenance.SceneId);
        return new(artifact, new(artifact.Id, artifact.ArtifactId, frame.FrameId, artifact.ChecksumSha256,
            scene.SceneIdentitySha256, scene.Source, provenance.SceneId, annotation.ProvenanceSha256), annotation);
    }

    public async Task<ProcessingAnnotationInput> ResolveAsync(
        CentralProjectedSceneReference reference, CancellationToken cancellationToken)
    {
        var artifact = await dbContext.CentralArtifacts.AsNoTracking()
            .Include(item => item.StructuredProduct)
            .SingleOrDefaultAsync(item => item.Id == reference.CentralArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null || artifact.ArtifactId != reference.ArtifactId ||
            artifact.ObjectState != CentralArtifactObjectState.Available ||
            artifact.ReconstructionState != CentralReconstructionState.Complete ||
            !string.Equals(artifact.ChecksumSha256, reference.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            throw new CentralArtifactIntegrityException("projected-scene.unavailable");
        var scene = await ReadAsync(artifact, cancellationToken).ConfigureAwait(false);
        // The frozen descriptor hash authenticates the source after raw retention. Geometry execution
        // needs the held scene artifact; it does not reread or acquire an undeclared raw pixel input.
        ValidateSource(artifact, scene, reference.CaptureId);
        if (scene.Source != reference.Source || scene.SceneIdentitySha256 != reference.ContentIdentitySha256)
            throw new CentralArtifactIntegrityException("projected-scene.identity-mismatch");
        var annotation = CreateAnnotation(scene, reference.SceneId);
        if (annotation.ProvenanceSha256 != reference.AnnotationIdentitySha256)
            throw new CentralArtifactIntegrityException("projected-scene.annotation-mismatch");
        return annotation;
    }

    public async Task<ProcessingAnnotationInput> ResolveAsync(
        CentralDerivativeJobLease lease, ICentralDerivativeJobService jobService, CancellationToken cancellationToken)
    {
        var reference = lease.ProjectedScene
            ?? throw new CentralDerivativeJobStateException("The projected-scene reference is missing.");
        var input = lease.Inputs?.SingleOrDefault(item => item.BindingName == BindingName);
        if (reference.CaptureId != lease.FrameId || input is null ||
            input.CentralArtifactId != reference.CentralArtifactId || input.ArtifactId != reference.ArtifactId ||
            !string.Equals(input.ChecksumSha256, reference.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            throw new CentralDerivativeJobStateException("The projected-scene reference does not belong to the frozen lease.");
        try
        {
            return await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactIntegrityException)
        {
            var artifact = await dbContext.CentralArtifacts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == reference.CentralArtifactId, CancellationToken.None).ConfigureAwait(false);
            if (artifact is not null)
                await jobService.MarkInputUnavailableAsync(lease.JobId, lease.LeaseToken, artifact.Id, artifact.RowVersion,
                    exception is CentralArtifactIntegrityException integrity ? integrity.ReasonCode : "object.missing",
                    quarantine: exception is CentralArtifactIntegrityException, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ProjectedSceneV1> ReadAsync(CentralArtifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.ByteLength is < 1 or > StructuredProcessingProductDescriptorV1.MaximumPayloadBytes)
            throw new CentralArtifactIntegrityException("projected-scene.length-invalid");
        var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        await using var destination = new MemoryStream(checked((int)artifact.ByteLength));
        await objectReader.CopyToAsync(snapshot, destination, null, cancellationToken).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Parse(destination.ToArray()).Scene
            ?? throw new CentralArtifactIntegrityException("projected-scene.invalid");
        return scene;
    }

    private static void ValidateSource(
        CentralArtifact artifact, ProjectedSceneV1 scene, Guid captureId, SceneProvenance? provenance = null)
    {
        var product = artifact.StructuredProduct
            ?? throw new CentralArtifactIntegrityException("projected-scene.descriptor-missing");
        var descriptor = StructuredProcessingProductManifestJson.ParseDescriptor(product.DescriptorJson);
        var raw = descriptor.SourceCapture;
        if (scene.Source.CaptureId != captureId || raw.Capture.CaptureId != captureId ||
            scene.Source.ArtifactId != raw.Artifact.ArtifactId ||
            scene.Source.ArtifactIdentitySha256 != CaptureContractJson.ComputeDescriptorSha256(raw) ||
            scene.SceneIdentitySha256 != product.ContentIdentitySha256 ||
            scene.ImageTransform.OutputWidthPixels != raw.Layout.Width ||
            scene.ImageTransform.OutputHeightPixels != raw.Layout.Height)
            throw new CentralArtifactIntegrityException("projected-scene.source-mismatch");
        if (provenance is null) return;
        if (scene.EffectiveUtc != provenance.SceneUtc ||
            scene.Catalog.Name != provenance.CatalogName || scene.Catalog.Version != provenance.CatalogVersion ||
            !string.Equals(scene.Catalog.ChecksumSha256, provenance.CatalogChecksumSha256, StringComparison.OrdinalIgnoreCase) ||
            provenance.CatalogSourceUrl is not null && scene.Catalog.SourceUrl != provenance.CatalogSourceUrl ||
            provenance.CatalogLicense is not null && scene.Catalog.License != provenance.CatalogLicense ||
            provenance.CatalogSchemaVersion is not null && scene.Catalog.SchemaVersion != provenance.CatalogSchemaVersion ||
            provenance.CatalogPreprocessingVersion is not null && scene.Catalog.PreprocessingVersion != provenance.CatalogPreprocessingVersion ||
            !string.Equals(scene.Projection.Model.ToString(), provenance.ProjectionModel, StringComparison.OrdinalIgnoreCase) ||
            scene.Projection.AlgorithmVersion != provenance.ProjectionAlgorithmVersion ||
            provenance.ProjectionCalibrationVersion is not null && scene.Projection.CalibrationVersion != provenance.ProjectionCalibrationVersion ||
            scene.AstronomyAlgorithmVersion != provenance.AstronomyAlgorithmVersion ||
            scene.EphemerisModelVersion != provenance.EphemerisModelVersion ||
            scene.ConstellationTopology?.Version != provenance.ConstellationTopologyVersion ||
            scene.ConstellationTopology?.SourceUrl != provenance.ConstellationTopologySourceUrl ||
            !string.Equals(scene.ConstellationTopology?.SourceSha256, provenance.ConstellationTopologySha256, StringComparison.OrdinalIgnoreCase) ||
            scene.ConstellationTopology?.License != provenance.ConstellationTopologyLicense ||
            scene.ConstellationTopology?.PreprocessingVersion != provenance.ConstellationTopologyPreprocessingVersion ||
            !scene.Selection.ConstellationIds.SequenceEqual(provenance.ConstellationIds ?? []) ||
            scene.Selection.IncludeConstellationEndpointStars != provenance.IncludeConstellationEndpointStars)
            throw new CentralArtifactIntegrityException("projected-scene.source-mismatch");
    }

    internal static ProcessingAnnotationInput CreateAnnotation(ProjectedSceneV1 scene, string sceneId)
        => CentralDerivativeJobExecutor.CreateAnnotation(sceneId,
            scene.Objects.Select(static item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            scene.Segments.Select(static item => new ProjectedSegmentProvenance(
                item.ConstellationId, item.FromObjectId, item.ToObjectId, item.FromPixel.X, item.FromPixel.Y,
                item.ToPixel.X, item.ToPixel.Y, item.PartIndex)).ToArray());

    // The existing immutable requirement selector carries the compact reference. The artifact input keeps
    // retention, invalidation and frozen input-set identity authoritative without a second persistence schema.
    internal static void AddRequirement(
        CentralDerivativeJob job, CentralProjectedSceneSelection selected, DateTimeOffset now)
    {
        job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Ordinal = job.InputRequirements.Count,
            BindingName = BindingName,
            SourceKind = CentralDerivativeInputSourceKind.Artifact,
            IsRequired = true,
            SelectorJson = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(selected.Reference)).GetRawText(),
            CompatibilityMode = CentralDerivativeCompatibilityMode.None,
            ExpectedAgentId = job.SourceArtifact!.Frame!.AgentId,
            ExpectedCentralArtifactId = selected.Artifact.Id,
            ExpectedArtifact = selected.Artifact,
            ResolutionState = CentralDerivativeInputResolutionState.Resolved,
            ResolvedAtUtc = now
        });
    }

    internal static CentralProjectedSceneReference? ReadReference(CentralDerivativeJob job)
    {
        var requirement = job.InputRequirements.SingleOrDefault(item => item.BindingName == BindingName);
        if (requirement is null) return null;
        var reference = JsonSerializer.Deserialize<CentralProjectedSceneReference>(requirement.SelectorJson, JsonOptions)
            ?? throw new CentralDerivativeJobStateException("The frozen projected-scene reference is invalid.");
        if (reference.CentralArtifactId != requirement.ExpectedCentralArtifactId)
            throw new CentralDerivativeJobStateException("The frozen projected-scene source is inconsistent.");
        var input = job.Inputs.SingleOrDefault(item => item.CentralDerivativeJobInputRequirementId == requirement.Id);
        if (input is null || input.CentralArtifactId != reference.CentralArtifactId ||
            input.CompatibilityJson != requirement.SelectorJson ||
            input.CompatibilitySha256 != CaptureContractJson.ComputeCanonicalJsonSha256(
                JsonSerializer.Deserialize<JsonElement>(requirement.SelectorJson)))
            throw new CentralDerivativeJobStateException("The frozen projected-scene input identity is inconsistent.");
        return reference;
    }

    internal static IEnumerable<CentralDerivativeJobLeaseInput> RecipeInputs(CentralDerivativeJobLease lease)
        => (lease.Inputs ?? []).Where(input => lease.ProjectedScene is null || input.BindingName != BindingName);

    internal static void MaterializeReference(CentralDerivativeJob job, DateTimeOffset now)
    {
        var requirement = job.InputRequirements.SingleOrDefault(item => item.BindingName == BindingName);
        if (requirement is null || job.Inputs.Any(item => item.CentralDerivativeJobInputRequirementId == requirement.Id)) return;
        var artifact = requirement.ExpectedArtifact
            ?? throw new CentralDerivativeJobStateException("The frozen projected-scene artifact was not loaded.");
        job.Inputs.Add(new CentralDerivativeJobInput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Requirement = requirement,
            CentralDerivativeJobInputRequirementId = requirement.Id,
            Ordinal = requirement.Ordinal,
            CentralArtifactId = artifact.Id,
            Artifact = artifact,
            ByteLength = artifact.ByteLength,
            CompatibilityJson = requirement.SelectorJson,
            CompatibilitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.Deserialize<JsonElement>(requirement.SelectorJson)),
            SelectedAtUtc = now
        });
    }
}
