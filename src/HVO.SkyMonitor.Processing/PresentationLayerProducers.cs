using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Bounded legacy-compatible scene selection and raster style facts. Label and cardinal scales are independent pixel
/// multipliers from 1 through 8; default circle and cardinal colors reproduce W6 Mono/RGB-equivalent values 96 and 255.
/// </summary>
public sealed record PresentationAnnotationStyleV1(
    int MarkerRadius = 6,
    int LabelScale = 1,
    int MaximumLabelCharacters = 24,
    double MaximumLabelMagnitude = 2.5,
    int SegmentThickness = 1,
    IReadOnlyList<string>? ConstellationIds = null,
    PresentationColor? MarkerColor = null,
    PresentationColor? LabelColor = null,
    PresentationColor? SegmentColor = null,
    PresentationColor? ImageCircleColor = null,
    PresentationColor? CardinalColor = null,
    int CardinalScale = 2);

/// <summary>Scene primitive groups that may be assigned independent layer opacity and order.</summary>
public sealed record PresentationSceneLayerPayloadsV1(
    PresentationLayerPayloadV1 AnnotationAndGeometry,
    PresentationLayerPayloadV1 Constellations);

/// <summary>Scene primitive groups split for independent presentation controls.</summary>
public sealed record PresentationSceneLayerPayloadsV2(
    PresentationLayerPayloadV1 StarAnnotations,
    PresentationLayerPayloadV1 CardinalDirections,
    PresentationLayerPayloadV1 ImageCircle,
    PresentationLayerPayloadV1 Constellations);

/// <summary>Cloud mask and label groups whose legacy raster operations use different blend semantics.</summary>
public sealed record PresentationCloudLayerPayloadsV1(
    PresentationLayerPayloadV1 Mask,
    PresentationLayerPayloadV1 Labels);

/// <summary>Exact source-identified text facts for the four image corners.</summary>
public sealed record PresentationMetadataFactsV1(
    string SourceIdentitySha256,
    IReadOnlyList<string> TopLeft,
    IReadOnlyList<string> TopRight,
    IReadOnlyList<string> BottomLeft,
    IReadOnlyList<string> BottomRight);

public sealed record PresentationEnvironmentalFactV1(
    string Kind,
    string Status,
    string PolicyIdentitySha256,
    string AssociationIdentitySha256,
    string? TargetRigId,
    DateTimeOffset ExposureFromUtc,
    DateTimeOffset ExposureThroughUtc,
    DateTimeOffset EvaluatedUtc,
    Guid? ObservationId,
    string? ObservationSourceIdentitySha256,
    string? ObservationSourceContentSha256,
    string? ObservationContentSha256,
    DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? StaleAfterUtc,
    string? Quality,
    IReadOnlyList<PresentationEnvironmentalConflictV1> ConflictingObservations,
    string DisplayLine);

public sealed record PresentationEnvironmentalConflictV1(
    Guid ObservationId,
    string SourceIdentitySha256,
    string ContentSha256);

public sealed record PresentationMetadataFactsProductV1(
    string SchemaVersion,
    string FactsIdentitySha256,
    Guid CaptureId,
    long CaptureSequence,
    JsonElement Capture,
    IReadOnlyList<PresentationEnvironmentalFactV1> Environment,
    JsonElement Catalog,
    JsonElement Calibration,
    JsonElement Stack,
    JsonElement ProcessingProfile,
    PresentationMetadataFactsV1 Corners,
    IReadOnlyList<Guid>? SourceArtifactIds = null)
{
    public const string CurrentSchemaVersion = "presentation-metadata-facts-v2";
    public const string MediaType = "application/vnd.hvo.presentation-metadata-facts+json";
}

/// <summary>Host-neutral producers that consume canonical facts, never base image pixels.</summary>
public static class PresentationLayerProducers
{
    public const string SceneProducerVersion = "projected-scene-presentation-v3";
    public const string MetadataProducerVersion = "metadata-corner-presentation-v1";
    public const string CloudProducerVersion = "cloud-presentation-v1";

    /// <summary>Creates one combined typed scene payload without reading or copying base pixels.</summary>
    public static PresentationLayerPayloadV1 FromProjectedScene(
        ProjectedSceneV1 scene,
        PresentationAnnotationStyleV1? style = null,
        bool includeMarkers = true,
        bool includeLabels = true,
        bool includeConstellations = true,
        bool includeProjectionGeometry = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var groups = FromProjectedSceneGroupsV2(scene, style, includeMarkers, includeLabels,
            includeConstellations, includeProjectionGeometry, includeProjectionGeometry);
        return PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256,
            scene.ImageTransform.OutputWidthPixels, scene.ImageTransform.OutputHeightPixels,
            groups.StarAnnotations.Markers,
            groups.Constellations.Segments,
            groups.ImageCircle.Ellipses,
            groups.StarAnnotations.TextBlocks.Concat(groups.CardinalDirections.TextBlocks).ToArray());
    }

    /// <summary>Creates the original combined annotation/geometry and constellation groups.</summary>
    public static PresentationSceneLayerPayloadsV1 FromProjectedSceneGroups(
        ProjectedSceneV1 scene,
        PresentationAnnotationStyleV1? style = null,
        bool includeMarkers = true,
        bool includeLabels = true,
        bool includeConstellations = true,
        bool includeProjectionGeometry = true)
    {
        var groups = FromProjectedSceneGroupsV2(scene, style, includeMarkers, includeLabels,
            includeConstellations, includeProjectionGeometry, includeProjectionGeometry);
        return new(
            PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256,
                scene.ImageTransform.OutputWidthPixels, scene.ImageTransform.OutputHeightPixels,
                groups.StarAnnotations.Markers, ellipses: groups.ImageCircle.Ellipses,
                textBlocks: groups.StarAnnotations.TextBlocks.Concat(groups.CardinalDirections.TextBlocks).ToArray()),
            groups.Constellations);
    }

    /// <summary>Creates independently composable star, cardinal, circle, and constellation groups from one canonical scene.</summary>
    public static PresentationSceneLayerPayloadsV2 FromProjectedSceneGroupsV2(
        ProjectedSceneV1 scene,
        PresentationAnnotationStyleV1? style = null,
        bool includeMarkers = true,
        bool includeLabels = true,
        bool includeConstellations = true,
        bool includeImageCircle = true,
        bool includeCardinalDirections = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ProjectedSceneJson.Validate(scene);
        style = style is null
            ? new PresentationAnnotationStyleV1(ConstellationIds: [])
            : style with { ConstellationIds = style.ConstellationIds ?? [] };
        if (style.MarkerRadius is < 0 or > 32 || style.LabelScale is < 1 or > 8 ||
            style.MaximumLabelCharacters is < 0 or > 64 || !double.IsFinite(style.MaximumLabelMagnitude) ||
            style.SegmentThickness is < 1 or > 8 || style.CardinalScale is < 1 or > 8 || style.ConstellationIds is null ||
            style.ConstellationIds.Count > 256 || style.ConstellationIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentOutOfRangeException(nameof(style));
        var markerColor = style.MarkerColor ?? new(144, 144, 144);
        var labelColor = style.LabelColor ?? new(255, 255, 255);
        var segmentColor = style.SegmentColor ?? new(96, 160, 255);
        var imageCircleColor = style.ImageCircleColor ?? new(96, 96, 96);
        var cardinalColor = style.CardinalColor ?? new(255, 255, 255);
        var annotatedObjects = scene.Objects.Where(item => IsNamed(item.Id, item.DisplayName) &&
            (item.Kind == CelestialObjectKind.SolarSystemBody || item.Magnitude <= style.MaximumLabelMagnitude)).ToArray();
        var markers = includeMarkers ? annotatedObjects.Select(item => new PresentationMarkerV1(item.Pixel, style.MarkerRadius, markerColor)) : [];
        var segments = includeConstellations ? scene.Segments.Where(item => style.ConstellationIds.Count == 0 ||
            style.ConstellationIds.Contains(item.ConstellationId, StringComparer.OrdinalIgnoreCase))
            .Select(item => new PresentationSegmentV1(item.FromPixel, item.ToPixel, style.SegmentThickness, segmentColor)) : [];
        var starTexts = new List<PresentationTextBlockV1>();
        if (includeLabels && style.MaximumLabelCharacters > 0)
        {
            var width = scene.ImageTransform.OutputWidthPixels;
            var height = scene.ImageTransform.OutputHeightPixels;
            var scale = Math.Clamp(Math.Max(style.LabelScale, (int)Math.Round(Math.Min(width, height) * 0.019 / 7,
                MidpointRounding.AwayFromZero)), 1, 16);
            var occupied = new List<(double X, double Y, double Right, double Bottom)>();
            foreach (var item in annotatedObjects.OrderBy(static item => item.Magnitude)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .ThenBy(static item => item.DisplayName, StringComparer.Ordinal)
                .ThenBy(static item => item.Pixel.X).ThenBy(static item => item.Pixel.Y))
            {
                if (starTexts.Count == PresentationLayerPayloadV1.MaximumTextBlocks) break;
                var name = item.DisplayName[..Math.Min(item.DisplayName.Length, style.MaximumLabelCharacters)];
                var x = Math.Round(item.Pixel.X + style.MarkerRadius + 2 * scale, MidpointRounding.AwayFromZero);
                var y = Math.Round(item.Pixel.Y - 3 * scale, MidpointRounding.AwayFromZero);
                var right = x + (name.Length * 6 - 1) * scale;
                var bottom = y + 7 * scale;
                var halo = scale > 2 ? Math.Max(1, scale / 4) : 0;
                var padding = scale;
                var left = x - halo;
                var top = y - halo;
                right += halo;
                bottom += halo;
                if (left < padding || top < padding || right + padding > width || bottom + padding > height ||
                    occupied.Any(box => left < box.Right + padding && right + padding > box.X &&
                        top < box.Bottom + padding && bottom + padding > box.Y)) continue;
                starTexts.Add(new(PresentationTextAnchor.Point, new PixelPoint(x, y),
                    new ReadOnlyCollection<string>([name]), scale, 0, 0, labelColor));
                occupied.Add((left, top, right, bottom));
            }
        }
        var ellipses = new List<PresentationEllipseV1>();
        var cardinalTexts = new List<PresentationTextBlockV1>();
        PixelPoint? cardinalCenter = null;
        if ((includeImageCircle || includeCardinalDirections) && scene.Projection.ImageCircleRadiusPixels is { } radius)
        {
            var sourceCenter = new PixelPoint(scene.Projection.PrincipalPointX, scene.Projection.PrincipalPointY);
            var sourceXBasis = new PixelPoint(scene.Projection.PrincipalPointX + radius, scene.Projection.PrincipalPointY);
            var sourceYBasis = new PixelPoint(scene.Projection.PrincipalPointX, scene.Projection.PrincipalPointY + radius);
            if (TryApply(sourceCenter, out var center))
            {
                cardinalCenter = sourceCenter;
                if (includeImageCircle && TryApply(sourceXBasis, out var xBasis) && TryApply(sourceYBasis, out var yBasis))
                {
                    var xVector = new PixelPoint(xBasis.X - center.X, xBasis.Y - center.Y);
                    var yVector = new PixelPoint(yBasis.X - center.X, yBasis.Y - center.Y);
                    var radiusX = Math.Max(Math.Abs(xVector.X), Math.Abs(yVector.X));
                    var radiusY = Math.Max(Math.Abs(xVector.Y), Math.Abs(yVector.Y));
                    if (radiusX > 0 && radiusY > 0)
                        ellipses.Add(new(center, radiusX, radiusY, imageCircleColor));
                }
            }
            if (includeCardinalDirections)
            {
                var projection = new ProjectionContext(scene.Projection.Model, scene.Projection.PrincipalPointX,
                    scene.Projection.PrincipalPointY, scene.Projection.FocalLengthXPixels, scene.Projection.FocalLengthYPixels,
                    scene.Projection.WidthPixels, scene.Projection.HeightPixels, scene.Projection.Aperture,
                    scene.Projection.ImageCircleRadiusPixels, scene.Projection.BoresightAltitudeDegrees,
                    scene.Projection.BoresightAzimuthDegrees, scene.Projection.RollDegrees,
                    scene.Projection.HorizontalFlip, scene.Projection.EnforceSensorBounds);
                if (RigProjectionContextFactory.CreateAnnotationLandmarks(projection) is { } landmarks)
                {
                    cardinalCenter ??= ContainsCrop(landmarks.Center) ? landmarks.Center : null;
                    if (cardinalCenter is not null)
                    {
                        AddCardinal("N", landmarks.North); AddCardinal("E", landmarks.East);
                        AddCardinal("S", landmarks.South); AddCardinal("W", landmarks.West);
                    }
                }
            }
        }
        return new(
            PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, scene.ImageTransform.OutputWidthPixels,
                scene.ImageTransform.OutputHeightPixels, markers, textBlocks: starTexts),
            PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, scene.ImageTransform.OutputWidthPixels,
                scene.ImageTransform.OutputHeightPixels, textBlocks: cardinalTexts),
            PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, scene.ImageTransform.OutputWidthPixels,
                scene.ImageTransform.OutputHeightPixels, ellipses: ellipses),
            PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, scene.ImageTransform.OutputWidthPixels,
                scene.ImageTransform.OutputHeightPixels, segments: segments));

        void AddCardinal(string label, PixelPoint point)
        {
            if (!TryApply(cardinalCenter!.Value, out var outputCenter) || !TryApply(point, out var target))
                return;
            var scale = style.CardinalScale;
            var deltaX = target.X - outputCenter.X;
            var deltaY = target.Y - outputCenter.Y;
            var factor = 1d;
            if (deltaX < 0) factor = Math.Min(factor, (3 * scale - outputCenter.X) / deltaX);
            else if (deltaX > 0) factor = Math.Min(factor,
                (scene.ImageTransform.OutputWidthPixels - 3 * scale - 1 - outputCenter.X) / deltaX);
            if (deltaY < 0) factor = Math.Min(factor, (4 * scale - outputCenter.Y) / deltaY);
            else if (deltaY > 0) factor = Math.Min(factor,
                (scene.ImageTransform.OutputHeightPixels - 4 * scale - 1 - outputCenter.Y) / deltaY);
            factor = Math.Clamp(factor, 0, 1);
            cardinalTexts.Add(new(PresentationTextAnchor.Point,
                new PixelPoint(Math.Round(outputCenter.X + deltaX * factor, MidpointRounding.AwayFromZero) - 2 * scale,
                    Math.Round(outputCenter.Y + deltaY * factor, MidpointRounding.AwayFromZero) - 3 * scale),
                new ReadOnlyCollection<string>([label]), scale, 0, 0, cardinalColor));
        }

        bool TryApply(PixelPoint point, out PixelPoint output)
        {
            if (!ContainsCrop(point))
            {
                output = default;
                return false;
            }
            output = ProjectedSceneImageTransform.Apply(scene.ImageTransform, point);
            return double.IsFinite(output.X) && double.IsFinite(output.Y);
        }

        bool ContainsCrop(PixelPoint point) =>
            double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= scene.ImageTransform.CropX &&
            point.X <= scene.ImageTransform.CropX + scene.ImageTransform.CropWidth &&
            point.Y >= scene.ImageTransform.CropY &&
            point.Y <= scene.ImageTransform.CropY + scene.ImageTransform.CropHeight;

        static bool IsNamed(string id, string displayName) =>
            !string.IsNullOrWhiteSpace(displayName) && !string.Equals(id, displayName, StringComparison.Ordinal);
    }

    /// <summary>Creates a corner-text payload from supplied facts without reading or copying base pixels.</summary>
    public static PresentationLayerPayloadV1 FromMetadataFacts(
        PresentationMetadataFactsV1 facts, int widthPixels, int heightPixels, PresentationColor? color = null,
        int scale = 1, int inset = 4, int lineSpacing = 2)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var blocks = new[]
        {
            Block(PresentationTextAnchor.TopLeft, facts.TopLeft), Block(PresentationTextAnchor.TopRight, facts.TopRight),
            Block(PresentationTextAnchor.BottomLeft, facts.BottomLeft), Block(PresentationTextAnchor.BottomRight, facts.BottomRight)
        };
        return PresentationLayerPayloadJson.Create(facts.SourceIdentitySha256, widthPixels, heightPixels, textBlocks: blocks);

        PresentationTextBlockV1 Block(PresentationTextAnchor anchor, IReadOnlyList<string> lines) =>
            new(anchor, default, lines, scale, inset, lineSpacing, color ?? new(255, 255, 255));
    }

    /// <summary>Creates one combined cloud-mask and label payload without reading or copying base pixels.</summary>
    public static PresentationLayerPayloadV1 FromCloudAssessment(
        CloudAssessmentV1 assessment, int widthPixels, int heightPixels, bool includeLabels = true,
        int lineThickness = 1, PresentationColor? color = null)
    {
        var groups = FromCloudAssessmentGroups(assessment, widthPixels, heightPixels, includeLabels, lineThickness, color);
        return PresentationLayerPayloadJson.Create(groups.Mask.SourceIdentitySha256, widthPixels, heightPixels,
            textBlocks: groups.Labels.TextBlocks, tileMask: groups.Mask.TileMask);
    }

    /// <summary>Creates separate cloud-border and max-composed label groups matching the legacy renderer cadence.</summary>
    public static PresentationCloudLayerPayloadsV1 FromCloudAssessmentGroups(
        CloudAssessmentV1 assessment, int widthPixels, int heightPixels, bool includeLabels = true,
        int lineThickness = 1, PresentationColor? color = null)
    {
        var validation = CloudAssessmentJson.Validate(assessment);
        if (!validation.IsValid) throw new ArgumentException("Cloud assessment is invalid.", nameof(assessment));
        var count = checked(assessment.Grid.Columns * assessment.Grid.Rows);
        var bits = assessment.Mask?.Bits.ToArray() ?? new byte[(count + 7) / 8];
        var texts = includeLabels ? new[]
        {
            new PresentationTextBlockV1(PresentationTextAnchor.Point, new PixelPoint(3, -2),
                new ReadOnlyCollection<string>(
                [
                    assessment.CoverageMillionths is { } coverage
                        ? string.Create(CultureInfo.InvariantCulture, $"Cloud {coverage / 10_000}.{coverage % 10_000 / 1_000}%")
                        : "Cloud unavailable",
                    $"Quality {assessment.Quality}",
                    $"Precipitation {assessment.Environment.PrecipitationStatus}"
                ]), 1, 0, 2, new PresentationColor(255, 255, 255))
        } : [];
        var sourceIdentity = assessment.AssessmentIdentitySha256;
        return new(
            PresentationLayerPayloadJson.Create(sourceIdentity, widthPixels, heightPixels,
                tileMask: new(assessment.Grid.Columns, assessment.Grid.Rows, PresentationTileMaskV1.RowMajorLsbFirst,
                    bits, lineThickness, color ?? new PresentationColor(255, 64, 32))),
            PresentationLayerPayloadJson.Create(sourceIdentity, widthPixels, heightPixels, textBlocks: texts));
    }
}
