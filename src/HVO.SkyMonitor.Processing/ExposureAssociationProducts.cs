using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>The centered association producer with explicit lineage to the retained measurement and prediction.</summary>
public static class ExposureAssociationProducts
{
    public const string StepAlias = "ExposureStellarAssociations";
    public const string RecipeName = "associate-exposure-measurement";
    public const string ImplementationVersion = "associate-exposure-measurement-v1";
    public const string Variant = "exposure-stellar-associations-v1";

    public static ProcessingRecipeDefinition Definition { get; } =
        new(RecipeName, "1.0.0", ImplementationVersion, ProcessingOperationKind.Analyzer);

    private static readonly ProcessingAlgorithmIdentity[] Algorithms =
    [
        new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion),
        new("stellar-label-policy", StellarLabelPolicy.Version)
    ];

    public static ProcessingOutcome Associate(
        ProcessingArtifact raw,
        ProcessingArtifact retained,
        ExposureMeasurementInputV1 expected,
        ProcessingArtifact sceneArtifact,
        string variant,
        MeasuredStellarAssociationRecipeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(sceneArtifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        cancellationToken.ThrowIfCancellationRequested();
        var source = expected.Raw;
        if (raw.Role != FrameArtifactRole.Raw || raw.CaptureId != source.CaptureId ||
            raw.ArtifactId != source.ArtifactId || !ExposureMeasurementProducts.Same(raw.DescriptorIdentitySha256, source.DescriptorSha256) ||
            raw.Layout != source.Layout || !ExposureMeasurementProducts.Same(raw.RecipeIdentitySha256, source.RecipeSha256))
            return ProcessingOutcome.TerminalFailure("association.exposure-or-scene-binding-mismatch");

        var scene = ExposureCatalogPredictionProducts.Read(sceneArtifact, raw, expected);
        var measured = ExposureMeasurementProducts.Read(retained, expected).Sources;
        if (measured.Status == ExposureMeasurementStatus.Unavailable ||
            measured.Status == ExposureMeasurementStatus.Unsuitable && measured.Reason != "all-candidates-rejected")
            return ProcessingOutcome.Skipped("association.measurement-unavailable");

        var settings = options?.Association ?? new();
        var policy = options?.LabelPolicy ?? new();
        var association = ExposureMeasurementConsumers.AssociateValidated(retained, expected, measured, scene,
            settings, policy, cancellationToken);
        var method = measured.LocalBayerPattern is { } bayer
            ? $"{LinearBayerReconstruction.AlgorithmVersion}:{bayer.ToString().ToUpperInvariant()}:rec709-luminance"
            : MeasuredStellarAssociationRecipe.MonoReconstruction;
        var evidence = MeasuredStellarAssociationRecipe.CreateEvidence(raw, source.Layout, source.CaptureId, source.DescriptorSha256,
            scene, new(StellarSourceMeasurer.AlgorithmVersion, measured.SettingsSha256!, method, StellarMeasurementStatus.Completed,
                null, null, measured.CandidateCount!.Value, measured.Detections.Count, measured.Exclusions.Count),
            settings, policy, association.Entries);
        var identity = ProcessingIdentity.CreateRecipeIdentity(Definition, ProcessingRecipeSupport.Normalize(
            new AssociationBinding(raw.ArtifactId, retained.ArtifactId, retained.ContentIdentitySha256!,
                sceneArtifact.ArtifactId, association.SceneIdentitySha256, settings, policy)));
        var payload = MeasuredStellarAssociationJson.Serialize(evidence);
        return ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, variant,
            StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, payload, identity, Algorithms,
            [raw, retained, sceneArtifact], raw.Integration, raw.Compatibility, ProcessingProductKind.Metadata,
            MeasuredStellarAssociationsV1.CurrentSchemaVersion, evidence.AssociationIdentitySha256));
    }

    internal static bool Matches(ProcessingArtifact artifact, ProcessingArtifact raw)
    {
        if (artifact.RecipeDescriptor is not { Name: RecipeName, SemanticVersion: "1.0.0", ImplementationVersion: ImplementationVersion } producer ||
            artifact.SourceArtifactIds is not { Count: 3 } lineage ||
            lineage.Distinct().Count() != 3 || lineage[0] != raw.ArtifactId || artifact.CaptureId != raw.CaptureId)
            return false;
        try
        {
            var binding = ProcessingRecipeSupport.ParseOptions<AssociationBinding>(producer.Options);
            return binding.RawArtifactId == raw.ArtifactId &&
                binding.MeasurementArtifactId == lineage[1] && binding.PredictionArtifactId == lineage[2] &&
                binding.MeasurementIdentitySha256 is { Length: 64 } && binding.MeasurementIdentitySha256.All(Uri.IsHexDigit) &&
                binding.SceneIdentitySha256 is { Length: 64 } && binding.SceneIdentitySha256.All(Uri.IsHexDigit) &&
                artifact.ArtifactId == ProcessingIdentity.CreateArtifactId(ProcessingIdentity.CreateOutputIdentity(
                    artifact.Role, artifact.Variant, artifact.RecipeIdentitySha256, lineage));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    internal static bool MatchesScene(ProcessingArtifact artifact, ProjectedSceneV1 scene)
    {
        if (artifact.RecipeDescriptor is not { Name: RecipeName } producer)
            return true;
        var binding = ProcessingRecipeSupport.ParseOptions<AssociationBinding>(producer.Options);
        return ExposureMeasurementProducts.Same(binding.SceneIdentitySha256, scene.SceneIdentitySha256);
    }

    /// <summary>Validates the fitter's association against its committed source measurement and prediction.</summary>
    public static void VerifyMeasurement(ProcessingArtifact association, ProcessingArtifact raw, ProcessingArtifact retained,
        ExposureMeasurementV1 measurement, ProcessingArtifact prediction)
    {
        ArgumentNullException.ThrowIfNull(association);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(prediction);
        if (!Matches(association, raw) || association.SourceArtifactIds![1] != retained.ArtifactId ||
            association.SourceArtifactIds[2] != prediction.ArtifactId ||
            MeasuredStellarAssociationJson.Parse(association.Payload).Associations is not { } evidence ||
            !ExposureMeasurementProducts.Same(evidence.AssociationIdentitySha256, association.ContentIdentitySha256))
            throw new InvalidDataException("The association differs from the retained exposure inputs.");

        _ = ExposureCatalogPredictionProducts.Read(prediction, raw, measurement.Input);
        var binding = ProcessingRecipeSupport.ParseOptions<AssociationBinding>(association.RecipeDescriptor!.Options);
        if (!ExposureMeasurementProducts.Same(binding.MeasurementIdentitySha256, retained.ContentIdentitySha256) ||
            !ExposureMeasurementProducts.Same(binding.SceneIdentitySha256, prediction.ContentIdentitySha256) ||
            !ExposureMeasurementProducts.Same(evidence.Measurement.SettingsIdentitySha256, measurement.Sources.SettingsSha256) ||
            evidence.Method.Settings != binding.Settings || evidence.Method.LabelPolicy != binding.LabelPolicy ||
            evidence.Measurement.DetectionCount != measurement.Sources.Detections.Count ||
            evidence.Associations.Any(member => !MeasuredStellarAssociator.MatchesDetection(member, measurement.Sources.Detections)))
            throw new InvalidDataException("The association uses different measured stellar evidence.");
    }

    private sealed record AssociationBinding(Guid RawArtifactId, Guid MeasurementArtifactId, string MeasurementIdentitySha256,
        Guid PredictionArtifactId, string SceneIdentitySha256, MeasuredStellarAssociationSettingsV1 Settings,
        StellarLabelPolicySettingsV1 LabelPolicy);
}
