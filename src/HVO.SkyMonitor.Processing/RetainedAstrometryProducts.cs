using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>An explicitly supplied immutable, complete catalog selection; never resolved from a current pointer.</summary>
public sealed record AstrometryCatalogSnapshotV1(CatalogMetadata Metadata, IReadOnlyList<CelestialCatalogObject> Stars,
    AstrometricCatalogProvenance Package, bool Complete, double CompletenessMagnitudeLimit,
    string CoordinateModel = AstrometricConventions.CoordinateModel);

/// <summary>Shared edge/central reconstruction of the same frozen request. No scheduling or persistence ownership.</summary>
public static class RetainedAstrometryProducts
{
    public const string ContextRecipe = "retained-astrometry-context";
    public const string Version = "retained-astrometry-v1";

    public static ProcessingRecipeIdentity RecipeIdentity(ProcessingExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effective = ProcessingIdentity.BindExecutionInputs(BuiltInProcessingRecipes.NormalizeOptions(request.RecipeName, request.Options),
            request.Input, request.Annotation, request.AuxiliaryInputs);
        return ProcessingIdentity.CreateRecipeIdentity(new(BuiltInProcessingRecipes.MeasuredAstrometry,
            "1.0.0", MeasuredAstrometryRecipe.AlgorithmVersion, ProcessingOperationKind.Analyzer), effective);
    }

    /// <summary>Recovers a prepared immutable payload for this exact request, including a budget outcome, without fitting.</summary>
    public static ProcessingProduct Restore(ProcessingExecutionRequest request, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var solution = MeasuredAstrometryJson.Parse(payload);
        var raw = ProcessingRecipeSupport.ResolveSingle(request, out _) ?? throw new ArgumentException("Missing raw input.");
        var product = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, request.OutputVariant, MeasuredAstrometryJson.MediaType,
            null, payload, RecipeIdentity(request), MeasuredAstrometryRecipe.Algorithms, MeasuredAstrometryRecipe.Sources(request, raw),
            raw.Integration, raw.Compatibility, ProcessingProductKind.Metadata, MeasuredAstrometrySolutionV1.CurrentSchemaVersion,
            solution.SolutionIdentitySha256);
        _ = Verify(request, product, cancellationToken);
        return product;
    }

    public static ProcessingProduct Product(ProcessingArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var identity = ProcessingIdentity.CreateRecipeIdentity(artifact.RecipeDescriptor ?? throw new ArgumentException("Missing producer descriptor."));
        if (!MeasuredAstrometryJson.Same(identity.IdentitySha256, artifact.RecipeIdentitySha256))
            throw new ArgumentException("Invalid retained producer identity.");
        return ProcessingRecipeSupport.CreateProduct(artifact.Role, artifact.Variant, artifact.MediaType, artifact.Layout,
            artifact.Payload, identity,
            artifact.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion ? MeasuredAstrometryRecipe.Algorithms : [new(ContextRecipe, Version)],
            artifact.SourceArtifactIds!.Select(id => artifact with { ArtifactId = id }).ToArray(), artifact.Integration,
            artifact.Compatibility, artifact.ProductKind, artifact.SchemaVersion, artifact.ContentIdentitySha256);
    }

    public static MeasuredAstrometryContextV1 CreateContext(ProcessingArtifact raw, ProcessingArtifact measured,
        ProcessingArtifact sceneArtifact, AstrometryCatalogSnapshotV1 catalog, AstrometricClockFacts clock,
        AstrometricFrameAssessment? prior = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(sceneArtifact);
        var scene = ProjectedSceneJson.Parse(sceneArtifact.Payload).Scene ?? throw new ArgumentException("Invalid retained scene.");
        var associations = MeasuredStellarAssociationJson.Parse(measured.Payload).Associations ?? throw new ArgumentException("Invalid retained measurement.");
        var start = raw.ObservationStartedUtc ?? throw new ArgumentException("Missing exposure start.");
        var end = ProcessingArtifact.ResolveObservationEndedUtc(start,
            raw.ObservationEndedUtc ?? throw new ArgumentException("Missing exposure end."), raw.Integration);
        var context = new MeasuredAstrometryContextV1(MeasuredAstrometryContextV1.CurrentSchemaVersion,
            new(raw.CaptureId ?? throw new ArgumentException("Missing capture identity."), raw.ArtifactId,
                raw.DescriptorIdentitySha256!, ProcessingIdentity.ComputePayloadSha256(raw.Payload),
                raw.Compatibility.LocationIdentitySha256 ?? CaptureContractJson.ComputeCanonicalJsonSha256(scene.Observer),
                scene.Observer, start, end, associations.Measurement.AlgorithmVersion, associations.Measurement.SettingsIdentitySha256),
            MeasuredAstrometryRecipe.SceneProjection(scene.Projection), scene.Projection.CalibrationVersion,
            MeasuredAstrometryJson.ComputeReadoutIdentity(raw.Layout!), catalog.Metadata, catalog.Stars, catalog.Complete,
            catalog.CompletenessMagnitudeLimit, catalog.CoordinateModel, catalog.Package, measured.ArtifactId,
            associations.AssociationIdentitySha256, measured.RecipeIdentitySha256, clock, prior);
        _ = MeasuredAstrometryJson.ContextInput(context);
        return context;
    }

    public static ProcessingProduct CreateContextProduct(MeasuredAstrometryContextV1 context, ProcessingArtifact raw,
        ProcessingArtifact measured, ProcessingArtifact scene, string variant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(raw);
        var input = MeasuredAstrometryJson.ContextInput(context);
        var identity = ProcessingIdentity.CreateRecipeIdentity(new(ContextRecipe, "1.0.0", Version, ProcessingOperationKind.Analyzer),
            JsonSerializer.SerializeToElement(new { contextIdentitySha256 = input.IdentitySha256 }));
        return ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, variant, MeasuredAstrometryJson.ContextMediaType,
            null, input.Payload, identity, [new(ContextRecipe, Version)],
            context.PreviousAccepted is { } prior && prior.Frame.SourceArtifactId != raw.ArtifactId
                ? [raw, measured, scene, raw with { ArtifactId = prior.Frame.SourceArtifactId }] : [raw, measured, scene], raw.Integration,
            raw.Compatibility, ProcessingProductKind.Metadata, MeasuredAstrometryContextV1.CurrentSchemaVersion, input.IdentitySha256);
    }

    public static ProcessingExecutionRequest Request(ProcessingArtifact raw, ProcessingArtifact measured,
        ProcessingArtifact scene, ProcessingArtifact contextArtifact, MeasuredAstrometryOptions options, string variant)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(contextArtifact);
        var context = MeasuredAstrometryJson.ParseContext(contextArtifact.Payload);
        return new(BuiltInProcessingRecipes.MeasuredAstrometry, JsonSerializer.SerializeToElement(options),
            ProcessingInputSelector.Raw(raw.Variant), [raw, measured, scene, contextArtifact], variant,
            AuxiliaryInputs: [MeasuredAstrometryJson.ContextInput(context),
                new("scene", ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: scene.SchemaVersion,
                    IdentitySha256: scene.ContentIdentitySha256, Payload: scene.Payload)
                    { ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(scene.Payload) },
                new(BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName, ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.RecipeResult(FrameArtifactRole.Metadata, measured.Variant, measured.RecipeIdentitySha256),
                    ArtifactId: measured.ArtifactId)], InputArtifactId: raw.ArtifactId);
    }

    /// <summary>Verifies retained numerical evidence against the frozen request without performing a fit.</summary>
    public static MeasuredAstrometrySolutionV1 Verify(ProcessingExecutionRequest request, ProcessingProduct solution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(solution);
        var expected = RecipeIdentity(request);
        var contract = BuiltInProcessingRecipes.CreateProductContract(request, solution.Recipe);
        if (solution.Recipe.IdentitySha256 != expected.IdentitySha256 || solution.Role != contract.Role || solution.Kind != ProcessingProductKind.Metadata ||
            solution.SchemaVersion != MeasuredAstrometrySolutionV1.CurrentSchemaVersion ||
            solution.MediaType != MeasuredAstrometryJson.MediaType ||
            !solution.SourceArtifactIds.SequenceEqual(contract.SourceArtifactIds) ||
            solution.ChecksumSha256 != ProcessingIdentity.ComputePayloadSha256(solution.Payload) ||
            solution.OutputIdentitySha256 != ProcessingIdentity.CreateOutputIdentity(solution.Role, solution.Variant,
                solution.Recipe.IdentitySha256, solution.SourceArtifactIds) ||
            !BuiltInProcessingRecipes.ProductPayloadMatchesContract(request, contract, solution.Payload,
                solution.ContentIdentitySha256, solution.Recipe.IdentitySha256, solution.Algorithms, cancellationToken))
            throw new ArgumentException("Retained astrometry differs from its frozen evidence.");
        return MeasuredAstrometryJson.Parse(solution.Payload);
    }
}
