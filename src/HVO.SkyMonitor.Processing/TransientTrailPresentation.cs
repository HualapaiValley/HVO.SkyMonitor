using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>A measured polyline and the authenticated descriptor for its source-to-detector mapping.</summary>
public sealed record TransientMeasuredTrail(
    TransientGeometryV1 Geometry,
    TransientDetectorInputDescriptorV1 DetectorInput,
    bool ConfirmedMeteor);

/// <summary>Creates the same bounded measured-trail primitives for grouped SVG and saved raster images.</summary>
public static class TransientTrailPresentation
{
    public const string LayerKind = "MeasuredTransientTrails";
    public const string ProducerVersion = "measured-transient-trails-v1";
    public const string CompositionSchemaVersion = "capture-trail-composition-v1";
    public const int MaximumTrails = 32;
    public const int MaximumPointsPerTrail = 256;
    public const string PositionalUncertainty = "POSITIONAL UNCERTAINTY UNAVAILABLE";

    /// <summary>Recognizes only retained producer versions whose algorithms preserve source pixel coordinates.</summary>
    public static bool PreservesSourcePixels(FrameArtifactRole role, RecipeIdentityDescriptor recipe,
        IReadOnlyList<ProcessingAlgorithmIdentity> algorithms)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(algorithms);
        return recipe.Name switch
        {
            BuiltInProcessingRecipes.FixedPreview => role == FrameArtifactRole.Preview &&
                recipe.ImplementationVersion == "fixed-preview-v1" &&
                algorithms.Any(value => value.Name == "fixed-display-transfer" && value.Version == FixedDisplayTransfer.AlgorithmVersion) &&
                algorithms.All(value => value.Name switch
                {
                    "fixed-display-transfer" => value.Version == FixedDisplayTransfer.AlgorithmVersion,
                    "linear-rggb-reconstruction" => value.Version == LinearBayerReconstruction.AlgorithmVersion,
                    _ => false
                }),
            BuiltInProcessingRecipes.EncodedPreview => role == FrameArtifactRole.Preview &&
                recipe.ImplementationVersion == "encoded-preview-v1" &&
                algorithms.Count > 0 && algorithms.All(value => value.Name switch
                {
                    "display-stretch" => value.Version == Mono16DisplayStretch.AlgorithmVersion,
                    "rggb-demosaic" => value.Version == BayerRggb16Demosaicer.AlgorithmVersion,
                    "row-packing" => value.Version == "packed-copy-v1",
                    _ => false
                }),
            BuiltInProcessingRecipes.RollingMean => role == FrameArtifactRole.Combined &&
                recipe.ImplementationVersion == Linear16ArithmeticMean.AlgorithmVersion &&
                algorithms.Count == 1 && algorithms[0] == new ProcessingAlgorithmIdentity("linear-mean", Linear16ArithmeticMean.AlgorithmVersion),
            BuiltInProcessingRecipes.ReferenceCalibration => role == FrameArtifactRole.Calibrated &&
                recipe.ImplementationVersion == "reference-calibration-linear16-v2" && algorithms.Count == 2 &&
                algorithms.Any(value => value.Name == "linear16-reference-calibration" && value.Version == Linear16ReferenceCalibration.AlgorithmVersion) &&
                algorithms.Any(value => value.Name == "calibration-normalization" && value.Version == CalibrationMasterBuilder.NormalizationAlgorithmVersion),
            _ => false
        };
    }

    /// <summary>
    /// Maps only authenticated detector coordinates to an exact source raster. The display stroke is a style,
    /// never trail-width evidence or an uncertainty band; V1 records no positional uncertainty.
    /// </summary>
    public static PresentationLayerPayloadV1 CreatePayload(
        string evidenceIdentitySha256, int sourceWidth, int sourceHeight,
        IReadOnlyList<TransientMeasuredTrail> trails)
    {
        ArgumentNullException.ThrowIfNull(trails);
        if (trails.Count is < 1 or > MaximumTrails)
        {
            throw new ArgumentException("Measured trail count is outside its bound.", nameof(trails));
        }
        var segments = new List<PresentationSegmentV1>();
        foreach (var trail in trails)
        {
            ArgumentNullException.ThrowIfNull(trail);
            var geometry = trail.Geometry;
            var descriptor = trail.DetectorInput;
            if (!TransientContractJson.Validate(descriptor).IsValid ||
                geometry.SourceEvidenceId != descriptor.Source.EvidenceId ||
                geometry.CoordinateWidth != descriptor.Layout.Width || geometry.CoordinateHeight != descriptor.Layout.Height ||
                geometry.Polyline.Count is < 2 or > MaximumPointsPerTrail ||
                geometry.Polyline.All(point => point == geometry.Polyline[0]))
            {
                throw new ArgumentException("Measured trail lacks a compatible authenticated detector mapping.", nameof(trails));
            }
            var transform = descriptor.SourceToDetectorTransform;
            if (descriptor.Layout.Width / transform.ScaleX != sourceWidth ||
                descriptor.Layout.Height / transform.ScaleY != sourceHeight)
            {
                throw new ArgumentException("Measured trail does not map to the exact source raster.", nameof(trails));
            }
            PixelPoint? previous = null;
            var style = trail.ConfirmedMeteor ? null : new PresentationStrokeV2(6, 4, 1_000_000);
            var color = trail.ConfirmedMeteor ? new PresentationColor(115, 232, 157) : new PresentationColor(255, 201, 92);
            foreach (var point in geometry.Polyline)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                    point.X < 0 || point.X > geometry.CoordinateWidth || point.Y < 0 || point.Y > geometry.CoordinateHeight)
                {
                    throw new ArgumentException("Measured trail contains an invalid detector point.", nameof(trails));
                }
                var mapped = new PixelPoint((point.X - transform.OffsetX) / transform.ScaleX,
                    (point.Y - transform.OffsetY) / transform.ScaleY);
                if (previous is { } from)
                {
                    segments.Add(new(from, mapped, 2, color, style));
                }
                previous = mapped;
            }
        }
        return PresentationLayerPayloadJson.Create(evidenceIdentitySha256, sourceWidth, sourceHeight,
            segments: segments,
            textBlocks: [new(PresentationTextAnchor.BottomLeft, new(0, 0), [PositionalUncertainty],
                1, 4, 1, new(255, 201, 92))]);
    }
}
