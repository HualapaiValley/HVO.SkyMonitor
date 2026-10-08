using System.Buffers.Binary;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Declared matcher and label-policy settings; measurement thresholds are fixed per linear input format.</summary>
public sealed record MeasuredStellarAssociationRecipeOptions(
    MeasuredStellarAssociationSettingsV1? Association = null,
    StellarLabelPolicySettingsV1? LabelPolicy = null);

/// <summary>
/// Measures stars on the linear raw frame and associates them with the predictions of the capture's own projected
/// scene. The scene only bounds the search; it is never fitted, and synthetic render truth is never consulted.
/// </summary>
internal sealed class MeasuredStellarAssociationRecipe : IProcessingRecipe
{
    internal const string SceneAuxiliaryInputName = BuiltInProcessingRecipes.MeasuredStellarAssociationsSceneInputName;
    internal const string MonoReconstruction = "mono-linear-v1";

    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.MeasuredStellarAssociations,
        "1.0.0",
        "measured-stellar-associations-v1",
        ProcessingOperationKind.Analyzer);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<MeasuredStellarAssociationRecipeOptions>(options);
        var normalized = new MeasuredStellarAssociationRecipeOptions(
            parsed.Association ?? new MeasuredStellarAssociationSettingsV1(),
            parsed.LabelPolicy ?? new StellarLabelPolicySettingsV1());
        normalized.Association!.Validate();
        normalized.LabelPolicy!.Validate();
        return ProcessingRecipeSupport.Normalize(normalized);
    }

    /// <summary>Format-specific measurement thresholds: CFA luminance spreads one photosite over its 3x3 support.</summary>
    public static StellarMeasurementOptions MeasurementOptions(CameraPixelFormat format) => new(
        MinimumPeakAboveBackground: format == CameraPixelFormat.BayerRggb16 ? 32 : 8,
        SaturationDilationPixels: format == CameraPixelFormat.BayerRggb16 ? 1 : 0,
        MaximumSaturatedSamples: format == CameraPixelFormat.BayerRggb16 ? 36 : 16,
        NoiseEstimator: format == CameraPixelFormat.BayerRggb16
            ? StellarNoiseEstimator.ClippedSpread : StellarNoiseEstimator.AdjacentDifferences);

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ProcessingRecipeSupport.ResolveSingle(request, out var sourceFailure);
        if (source is null)
            return ValueTask.FromResult(sourceFailure!);
        if (source.Role != FrameArtifactRole.Raw)
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidSelector, nameof(request.Input)));
        if (!ProcessingRecipeSupport.TryValidateFrame(source, out var layout, out var layoutFailure))
            return ValueTask.FromResult(layoutFailure!);

        var scenes = (request.AuxiliaryInputs ?? []).Where(static input =>
            string.Equals(input.Name, SceneAuxiliaryInputName, StringComparison.Ordinal)).ToArray();
        if (scenes.Length == 0)
            return ValueTask.FromResult(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene, nameof(request.AuxiliaryInputs)));
        if (scenes.Length != 1 || scenes[0].Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            !ProjectedSceneV1.IsSupportedSchemaVersion(scenes[0].SchemaVersion))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidProjectedScene, nameof(request.AuxiliaryInputs)));
        var auxiliary = scenes[0];
        var parsed = ProjectedSceneJson.Parse(auxiliary.Payload);
        if (!parsed.IsValid || parsed.Scene is not { } scene ||
            !string.Equals(scene.SchemaVersion, auxiliary.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(scene.SceneIdentitySha256, auxiliary.IdentitySha256, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidProjectedScene, parsed.ErrorPath ?? nameof(auxiliary.IdentitySha256)));
        if (scene.Source.ArtifactId != source.ArtifactId || source.CaptureId is not { } captureId || scene.Source.CaptureId != captureId)
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.ProjectedSceneSourceMismatch, nameof(scene.Source)));
        if (source.DescriptorIdentitySha256 is not { } descriptorIdentity ||
            !string.Equals(scene.Source.ArtifactIdentitySha256, descriptorIdentity, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.ProjectedSceneDescriptorMismatch, nameof(scene.Source.ArtifactIdentitySha256)));
        if (scene.ImageTransform.OutputWidthPixels != layout.Width || scene.ImageTransform.OutputHeightPixels != layout.Height)
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.ProjectedSceneDimensionMismatch, nameof(scene.ImageTransform)));
        if (!TryDecodeLinear(source.Payload, layout, out var samples, out var saturated, out var pattern))
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.UnsupportedMeasurementInput, nameof(source.Layout)));

        var options = ProcessingRecipeSupport.ParseOptions<MeasuredStellarAssociationRecipeOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var settings = options.Association ?? new MeasuredStellarAssociationSettingsV1();
        var policy = options.LabelPolicy ?? new StellarLabelPolicySettingsV1();
        var aperture = new ProjectedSceneAperture(scene);
        var valid = aperture.CreateSampleMask(cancellationToken);
        var measurementOptions = MeasurementOptions(layout.PixelFormat);
        StellarMeasurementResult measurement;
        string reconstruction;
        if (pattern is { } bayer)
        {
            var luminance = LinearBayerReconstruction.Reconstruct(samples, valid, layout.Width, layout.Height, bayer, cancellationToken)
                .ToLuminance(cancellationToken);
            measurement = StellarSourceMeasurer.Measure(luminance.Pixels.Span, luminance.ValidMask.Span, saturated,
                layout.Width, layout.Height, measurementOptions, cancellationToken);
            reconstruction = $"{LinearBayerReconstruction.AlgorithmVersion}:{bayer.ToString().ToUpperInvariant()}:rec709-luminance";
        }
        else
        {
            measurement = StellarSourceMeasurer.Measure(samples, valid, saturated, layout.Width, layout.Height,
                measurementOptions, cancellationToken);
            reconstruction = MonoReconstruction;
        }
        var entries = MeasuredStellarAssociator.Associate(scene, measurement, settings, policy, cancellationToken);
        var product = new MeasuredStellarAssociationsV1(
            MeasuredStellarAssociationsV1.CurrentSchemaVersion,
            new MeasuredStellarAssociationSourceV1(captureId, source.ArtifactId, descriptorIdentity.ToUpperInvariant(),
                source.Role, source.Variant, source.RecipeIdentitySha256.ToUpperInvariant(), layout.PixelFormat,
                layout.Width, layout.Height),
            new MeasuredStellarAssociationSceneV1(scene.SceneIdentitySha256.ToUpperInvariant(), scene.Kind,
                scene.Catalog.Name, scene.Catalog.Version, scene.Catalog.ChecksumSha256.ToUpperInvariant(),
                scene.Projection.CalibrationVersion, scene.Projection.AlgorithmVersion, scene.AstronomyAlgorithmVersion),
            // The one place the measurer identity enters Processing. Astronomy/Imaging canonicalize identities in lowercase
            // (AstrometricIdentity.RequireSha256; the #1103 measurer and its qualification report); Processing contracts
            // are uppercase-canonical and uppercase a foreign identity where it enters (TransientTemporalBackgrounds).
            new MeasuredStellarMeasurementV1(measurement.AlgorithmVersion,
                measurement.SettingsIdentitySha256.ToUpperInvariant(), reconstruction, measurement.Status,
                RoundOptional(measurement.MedianBackground), RoundOptional(measurement.MedianNoiseSigma),
                measurement.CandidateCount, measurement.Detections.Count, measurement.Exclusions.Count),
            new MeasuredStellarAssociationMethodV1(MeasuredStellarAssociator.AlgorithmVersion, settings,
                StellarLabelPolicy.Version, policy),
            entries.Associations,
            entries.UnmatchedPredictions,
            entries.UnassociatedDetections);
        var payload = MeasuredStellarAssociationJson.Serialize(product);
        var algorithms = Algorithms(pattern is not null);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Metadata,
            request.OutputVariant,
            StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType,
            null,
            payload,
            identity,
            algorithms,
            [source],
            source.Integration,
            source.Compatibility,
            ProcessingProductKind.Metadata,
            MeasuredStellarAssociationsV1.CurrentSchemaVersion,
            product.AssociationIdentitySha256)));
    }

    internal static List<ProcessingAlgorithmIdentity> Algorithms(bool cfa)
    {
        List<ProcessingAlgorithmIdentity> algorithms =
        [
            new("stellar-source-measurer", StellarSourceMeasurer.AlgorithmVersion),
            new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion),
            new("stellar-label-policy", StellarLabelPolicy.Version)
        ];
        if (cfa)
            algorithms.Insert(0, new("linear-bayer-reconstruction", LinearBayerReconstruction.AlgorithmVersion));
        return algorithms;
    }

    /// <summary>
    /// Decodes declared linear 16-bit samples and the white-level saturation mask without discarding saturated
    /// photosites. Opaque or undeclared levels cannot establish saturation, so they are rejected rather than guessed.
    /// </summary>
    internal static bool TryDecodeLinear(
        ReadOnlyMemory<byte> payload,
        FrameLayoutDescriptor layout,
        out double[] samples,
        out bool[] saturated,
        out BayerPattern? pattern)
    {
        samples = [];
        saturated = [];
        pattern = null;
        if (layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.Packing != FrameSamplePacking.ByteAligned || layout.ContainerDepthBits != 16 ||
            layout.ByteOrder != FrameByteOrder.LittleEndian || layout.WhiteLevel is not { } declaredWhite ||
            !double.IsFinite(declaredWhite) || payload.Length != layout.ByteLength)
            return false;
        var codeSpace = layout.LevelCodeSpace ?? (layout.SampleDepthBits == layout.ContainerDepthBits
            ? FrameLevelCodeSpace.StoredContainer : (FrameLevelCodeSpace?)null);
        double white;
        switch (codeSpace)
        {
            case FrameLevelCodeSpace.StoredContainer:
                white = declaredWhite;
                break;
            case FrameLevelCodeSpace.NativeSample:
                switch (layout.StoredCodeTransform)
                {
                    case FrameStoredCodeTransform.RightAlignedV1 or FrameStoredCodeTransform.IdentityV1:
                        white = declaredWhite;
                        break;
                    case FrameStoredCodeTransform.FullRangeScaledV1:
                        white = declaredWhite * ushort.MaxValue / ((1 << layout.SampleDepthBits) - 1);
                        break;
                    case FrameStoredCodeTransform.LeftShiftedV1:
                        white = declaredWhite * (1 << (16 - layout.SampleDepthBits));
                        break;
                    case null when layout.SampleDepthBits == layout.ContainerDepthBits:
                        white = declaredWhite;
                        break;
                    default:
                        return false;
                }
                break;
            default:
                return false;
        }
        if (layout.PixelFormat == CameraPixelFormat.BayerRggb16)
        {
            pattern = (layout.Readout?.CfaOriginX ?? 0, layout.Readout?.CfaOriginY ?? 0) switch
            {
                (0, 0) => BayerPattern.Rggb,
                (1, 0) => BayerPattern.Grbg,
                (0, 1) => BayerPattern.Gbrg,
                (1, 1) => BayerPattern.Bggr,
                _ => null
            };
            if (pattern is null)
                return false;
        }
        samples = new double[checked(layout.Width * layout.Height)];
        saturated = new bool[samples.Length];
        var span = payload.Span;
        for (var y = 0; y < layout.Height; y++)
        {
            var row = span.Slice(y * layout.StrideBytes, layout.Width * 2);
            for (var x = 0; x < layout.Width; x++)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(x * 2, 2));
                samples[y * layout.Width + x] = value;
                saturated[y * layout.Width + x] = value >= white;
            }
        }
        return true;
    }

    private static double? RoundOptional(double? value) => value is { } present && double.IsFinite(present)
        ? Math.Round(present, 6, MidpointRounding.ToEven) : null;
}
