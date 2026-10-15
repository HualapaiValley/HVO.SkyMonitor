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
            BuiltInProcessingRecipes.LinearNormalization => role == FrameArtifactRole.Calibrated &&
                recipe.ImplementationVersion == "linear-normalization-none-v1" &&
                algorithms.Count == 1 && algorithms[0] == new ProcessingAlgorithmIdentity("linear-normalization", "none-v1"),
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
            segments: segments, textBlocks: CreateUncertaintyCaption(sourceWidth, sourceHeight));
    }

    private static IReadOnlyList<PresentationTextBlockV1> CreateUncertaintyCaption(int width, int height)
    {
        var inset = Math.Min(4, Math.Min(width, height) / 8);
        var color = new PresentationColor(255, 201, 92);
        var style = new PresentationTextStyleV3(PresentationFontFaceV3.MonoRegular,
            3_500 * PresentationFont.FrameScale(width, height), 0, color, default, 0, 0);
        using var initialFont = PresentationFont.Create(style);
        var initialBounds = PresentationFont.LineBounds(initialFont, PositionalUncertainty, 0, 0);
        var ratio = Math.Min(1, Math.Min((width - 2d * inset) / initialBounds.Width,
            (height - 2d * inset) / initialBounds.Height));
        style = style with { SizeMilliPixels = Math.Max(1_000, (int)Math.Floor(style.SizeMilliPixels * ratio)) };
        // Font hinting rounds glyph advances, so scaling the initial bounds is only an estimate.
        // Re-measure discrete sizes with the same pinned font used by both renderers.
        while (true)
        {
            using var fittedFont = PresentationFont.Create(style);
            var fittedBounds = PresentationFont.LineBounds(fittedFont, PositionalUncertainty, 0, 0);
            if (fittedBounds.Width <= width - 2 * inset && fittedBounds.Height <= height - 2 * inset)
                return [new(PresentationTextAnchor.BottomLeft, default, [PositionalUncertainty],
                    1, inset, 1, color, Appearance: new(style))];
            // Tiny rasters cannot carry a legible caption. The control and immutable evidence still disclose it.
            if (style.SizeMilliPixels == 1_000) return [];
            style = style with { SizeMilliPixels = Math.Max(1_000, style.SizeMilliPixels - 100) };
        }
    }
}
