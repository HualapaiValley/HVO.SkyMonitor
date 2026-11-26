using System.Collections.ObjectModel;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Typed, independently selectable presentation layers derived solely from compact analytical association evidence
/// (and optional bound projected-scene names). Never reads base pixels and never mixes expected positions with
/// authoritative measured detections.
/// </summary>
public static class MeasuredAssociationPresentation
{
    public const string MeasuredLayerKind = "measured-stellar-associations";
    public const string ExpectedNotDetectedLayerKind = "expected-not-detected";
    public const string Version = "measured-stellar-associations-presentation-v1";
    public const string ExpectedNotDetectedVersion = "expected-not-detected-presentation-v1";
    public const double DefaultMaximumLabelMagnitude = 6.5;

    private static readonly PresentationColor EligibleStarColor = new(120, 219, 255);
    private static readonly PresentationColor EligiblePlanetColor = new(102, 232, 212);
    private static readonly PresentationColor IneligibleAssociationColor = new(160, 190, 200);
    private static readonly PresentationColor UnclaimedCentroidColor = new(180, 224, 240);
    private static readonly PresentationColor MeasuredLabelColor = new(225, 248, 255);
    private static readonly PresentationColor ExpectedDiagnosticColor = new(210, 153, 34);

    /// <summary>
    /// Creates the pre-solve measured association overlay: policy-eligible measured star and point-source planet
    /// detections (with authoritative labels), associated-but-ineligible detections (marked without labels), and
    /// unclaimed centroids (crosshairs without catalog identities).
    /// </summary>
    public static PresentationLayerPayloadV1 CreateMeasuredPayload(
        MeasuredStellarAssociationsV1 associations,
        ProjectedSceneV1? scene = null,
        PresentationAnnotationStyleV1? style = null)
    {
        ArgumentNullException.ThrowIfNull(associations);
        if (!MeasuredStellarAssociationJson.Validate(associations).IsValid)
            throw new ArgumentException("Measured associations are invalid.", nameof(associations));
        ValidateOptionalScene(associations, scene);
        var width = associations.Source.WidthPixels;
        var height = associations.Source.HeightPixels;
        var maxLabelMag = style?.MaximumLabelMagnitude ?? DefaultMaximumLabelMagnitude;
        var maxLabelChars = style?.MaximumLabelCharacters ?? 24;
        var labelScale = style?.LabelScale ?? 1;
        var labelColor = style?.LabelColor ?? MeasuredLabelColor;
        var sceneNames = BuildSceneDisplayNames(scene);

        var markers = new List<PresentationMarkerV1>();
        var labelCandidates = new List<(string Id, string Label, PixelPoint Pixel, double Magnitude, int Clearance)>();

        if (associations.PointSourcePlanets is { } planets)
        {
            foreach (var planet in planets.Associations
                .OrderBy(static item => item.Magnitude)
                .ThenBy(static item => item.ObjectId, StringComparer.Ordinal))
            {
                var pixel = new PixelPoint(planet.MeasuredX, planet.MeasuredY);
                if (!Inside(pixel, width, height)) continue;
                if (markers.Count < PresentationLayerPayloadV1.MaximumMarkers)
                {
                    markers.Add(new(pixel, planet.LabelEligible ? 5 : 4,
                        planet.LabelEligible ? EligiblePlanetColor : IneligibleAssociationColor, Crosshair: true));
                }
                if (planet.LabelEligible && maxLabelChars > 0)
                {
                    var name = ResolvePlanetDisplayName(planet.ObjectId, planet.Body, sceneNames);
                    var formatted = AnnotationLabelFormatter.Format(
                        new ProjectedAnnotationObject(planet.ObjectId, name, pixel), maxLabelChars);
                    if (formatted is not null)
                        labelCandidates.Add((planet.ObjectId, formatted, pixel, planet.Magnitude, 5));
                }
            }
        }

        foreach (var star in associations.Associations
            .OrderByDescending(static item => item.LabelEligible)
            .ThenBy(static item => item.Magnitude)
            .ThenBy(static item => item.CatalogId, StringComparer.Ordinal))
        {
            var pixel = new PixelPoint(star.MeasuredX, star.MeasuredY);
            if (!Inside(pixel, width, height)) continue;
            if (markers.Count < PresentationLayerPayloadV1.MaximumMarkers)
            {
                markers.Add(new(pixel, star.LabelEligible ? 4 : 3,
                    star.LabelEligible ? EligibleStarColor : IneligibleAssociationColor));
            }
            if (star.LabelEligible && maxLabelChars > 0 && star.Magnitude <= maxLabelMag)
            {
                var name = ResolveStarDisplayName(star.CatalogId, sceneNames);
                if (name is null) continue;
                var formatted = AnnotationLabelFormatter.Format(
                    new ProjectedAnnotationObject(star.CatalogId, name, pixel), maxLabelChars);
                if (formatted is not null)
                    labelCandidates.Add((star.CatalogId, formatted, pixel, star.Magnitude, 4));
            }
        }

        // Preserve unclaimed measured centroids deterministically without ever assigning a catalog identity.
        foreach (var detection in associations.UnassociatedDetections
            .OrderByDescending(static item => item.Flux)
            .ThenBy(static item => item.DetectionIndex))
        {
            if (markers.Count >= PresentationLayerPayloadV1.MaximumMarkers) break;
            var pixel = new PixelPoint(detection.X, detection.Y);
            if (!Inside(pixel, width, height)) continue;
            markers.Add(new(pixel, 2, UnclaimedCentroidColor, Crosshair: true));
        }

        var remainingPrimitives = Math.Max(0, PresentationLayerCompositor.MaximumLayerPrimitives - markers.Count);
        var textBlocks = PlaceLabels(labelCandidates, width, height, labelScale, labelColor, remainingPrimitives);
        return PresentationLayerPayloadJson.Create(
            associations.AssociationIdentitySha256, width, height, markers: markers, textBlocks: textBlocks);
    }

    /// <summary>
    /// Creates the separately selectable diagnostic layer for expected catalog stars and point-source planets that were
    /// not confirmed by a policy-eligible measured detection. Every label is explicitly marked as expected.
    /// </summary>
    public static PresentationLayerPayloadV1 CreateExpectedNotDetectedPayload(
        MeasuredStellarAssociationsV1 associations,
        ProjectedSceneV1? scene = null,
        PresentationAnnotationStyleV1? style = null)
    {
        ArgumentNullException.ThrowIfNull(associations);
        if (!MeasuredStellarAssociationJson.Validate(associations).IsValid)
            throw new ArgumentException("Measured associations are invalid.", nameof(associations));
        ValidateOptionalScene(associations, scene);
        var width = associations.Source.WidthPixels;
        var height = associations.Source.HeightPixels;
        var maxLabelMag = style?.MaximumLabelMagnitude ?? DefaultMaximumLabelMagnitude;
        var maxLabelChars = style?.MaximumLabelCharacters ?? 24;
        var labelScale = style?.LabelScale ?? 1;
        var markerRadius = style?.MarkerRadius is > 0 ? Math.Min(style.MarkerRadius, 8) : 5;
        var sceneNames = BuildSceneDisplayNames(scene);

        var items = new List<(string Id, string? Label, PixelPoint Pixel, double Magnitude)>();
        if (associations.PointSourcePlanets is { } planets)
        {
            foreach (var unmatched in planets.UnmatchedPredictions)
            {
                if (unmatched.ReasonCode == MeasuredStellarAssociationReasonCodes.OutsideAperture) continue;
                var pixel = new PixelPoint(unmatched.ExpectedX, unmatched.ExpectedY);
                if (!Inside(pixel, width, height)) continue;
                items.Add((unmatched.ObjectId, ResolvePlanetDisplayName(unmatched.ObjectId, unmatched.Body, sceneNames),
                    pixel, unmatched.Magnitude));
            }
            foreach (var ineligible in planets.Associations.Where(static item => !item.LabelEligible))
            {
                var pixel = new PixelPoint(ineligible.ExpectedX, ineligible.ExpectedY);
                if (!Inside(pixel, width, height)) continue;
                items.Add((ineligible.ObjectId, ResolvePlanetDisplayName(ineligible.ObjectId, ineligible.Body, sceneNames),
                    pixel, ineligible.Magnitude));
            }
        }

        foreach (var unmatched in associations.UnmatchedPredictions)
        {
            if (unmatched.ReasonCode == MeasuredStellarAssociationReasonCodes.OutsideAperture ||
                unmatched.Magnitude > maxLabelMag) continue;
            var pixel = new PixelPoint(unmatched.ExpectedX, unmatched.ExpectedY);
            if (!Inside(pixel, width, height)) continue;
            items.Add((unmatched.CatalogId, ResolveStarDisplayName(unmatched.CatalogId, sceneNames),
                pixel, unmatched.Magnitude));
        }
        foreach (var ineligible in associations.Associations.Where(item => !item.LabelEligible && item.Magnitude <= maxLabelMag))
        {
            var pixel = new PixelPoint(ineligible.ExpectedX, ineligible.ExpectedY);
            if (!Inside(pixel, width, height)) continue;
            items.Add((ineligible.CatalogId, ResolveStarDisplayName(ineligible.CatalogId, sceneNames),
                pixel, ineligible.Magnitude));
        }

        return BuildExpectedDiagnosticPayload(
            associations.AssociationIdentitySha256, width, height, items, markerRadius, labelScale, maxLabelChars);
    }

    /// <summary>
    /// Creates the separately selectable diagnostic layer from a projected scene and optional measured associations.
    /// When no measured associations are bound, every clear catalog star and point-source planet within the magnitude
    /// limit is unconfirmed and appears strictly as an expected-position diagnostic.
    /// </summary>
    public static PresentationLayerPayloadV1 CreateExpectedNotDetectedPayload(
        ProjectedSceneV1 scene,
        MeasuredStellarAssociationsV1? associations = null,
        PresentationAnnotationStyleV1? style = null,
        HorizonObstructionSceneEligibility? horizon = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ProjectedSceneJson.Validate(scene);
        horizon?.ValidateScene(scene);
        if (associations is not null)
        {
            if (!MeasuredStellarAssociationJson.Validate(associations).IsValid ||
                !string.Equals(associations.Scene.SceneIdentitySha256, scene.SceneIdentitySha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The measured associations do not describe this projected scene.", nameof(associations));
            if (horizon is null)
                return CreateExpectedNotDetectedPayload(associations, scene, style);
        }

        var width = scene.ImageTransform.OutputWidthPixels;
        var height = scene.ImageTransform.OutputHeightPixels;
        var maxLabelMag = style?.MaximumLabelMagnitude ?? DefaultMaximumLabelMagnitude;
        var maxLabelChars = style?.MaximumLabelCharacters ?? 24;
        var labelScale = style?.LabelScale ?? 1;
        var markerRadius = style?.MarkerRadius is > 0 ? Math.Min(style.MarkerRadius, 8) : 5;
        var resolvedFootprintIds = ProjectedSceneAnnotation.SolarSystemFootprints(scene)
            .Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        if (associations is not null)
        {
            foreach (var item in associations.Associations)
                if (item.LabelEligible) eligible.Add(item.CatalogId);
            if (associations.PointSourcePlanets is { } planets)
                foreach (var item in planets.Associations)
                    if (item.LabelEligible) eligible.Add(item.ObjectId);
        }

        var items = new List<(string Id, string? Label, PixelPoint Pixel, double Magnitude)>();
        foreach (var obj in scene.Objects)
        {
            if (eligible.Contains(obj.Id) || !Inside(obj.Pixel, width, height) ||
                horizon is not null && !horizon.IsClear(obj.Pixel))
                continue;
            if (MeasuredStellarAssociator.IsPointSourcePlanet(obj, resolvedFootprintIds, out var body))
            {
                items.Add((obj.Id, IsNamed(obj.Id, obj.DisplayName) ? obj.DisplayName : body.ToString(),
                    obj.Pixel, obj.Magnitude));
            }
            else if (obj.Kind == CelestialObjectKind.Star && obj.Magnitude <= maxLabelMag && IsNamed(obj.Id, obj.DisplayName))
            {
                items.Add((obj.Id, obj.DisplayName, obj.Pixel, obj.Magnitude));
            }
        }

        return BuildExpectedDiagnosticPayload(
            associations?.AssociationIdentitySha256 ?? scene.SceneIdentitySha256,
            width, height, items, markerRadius, labelScale, maxLabelChars);
    }

    private static PresentationLayerPayloadV1 BuildExpectedDiagnosticPayload(
        string sourceIdentitySha256,
        int width,
        int height,
        List<(string Id, string? Label, PixelPoint Pixel, double Magnitude)> items,
        int markerRadius,
        int labelScale,
        int maxLabelChars)
    {
        var ordered = items
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ThenBy(static item => item.Pixel.X)
            .ThenBy(static item => item.Pixel.Y)
            .ToArray();

        var markers = new List<PresentationMarkerV1>(Math.Min(ordered.Length, PresentationLayerPayloadV1.MaximumMarkers));
        var labelCandidates = new List<(string Id, string Label, PixelPoint Pixel, double Magnitude, int Clearance)>();
        foreach (var item in ordered)
        {
            if (markers.Count < PresentationLayerPayloadV1.MaximumMarkers)
                markers.Add(new(item.Pixel, markerRadius, ExpectedDiagnosticColor));
            if (maxLabelChars > 0 && !string.IsNullOrWhiteSpace(item.Label))
            {
                var formatted = AnnotationLabelFormatter.Format(
                    new ProjectedAnnotationObject(item.Id, item.Label, item.Pixel, ExpectedPosition: true),
                    maxLabelChars);
                if (formatted is not null)
                    labelCandidates.Add((item.Id, formatted, item.Pixel, item.Magnitude, markerRadius));
            }
        }

        var remainingPrimitives = Math.Max(0, PresentationLayerCompositor.MaximumLayerPrimitives - markers.Count);
        var textBlocks = PlaceLabels(labelCandidates, width, height, labelScale, ExpectedDiagnosticColor, remainingPrimitives);
        return PresentationLayerPayloadJson.Create(
            sourceIdentitySha256, width, height, markers: markers, textBlocks: textBlocks);
    }

    private static List<PresentationTextBlockV1> PlaceLabels(
        List<(string Id, string Label, PixelPoint Pixel, double Magnitude, int Clearance)> candidates,
        int width,
        int height,
        int labelScale,
        PresentationColor labelColor,
        int remainingPrimitives)
    {
        var textBlocks = new List<PresentationTextBlockV1>();
        if (candidates.Count == 0 || remainingPrimitives <= 0)
            return textBlocks;

        var scale = PresentationFont.StarFrameScale(width, height, Math.Clamp(labelScale, 1, 8));
        var labelSize = Math.Min(112_000, 9_000 * scale);
        var appearance = new PresentationTextAppearanceV3(new(PresentationFontFaceV3.MonoBold,
            labelSize, labelSize / 25, labelColor, new(3, 8, 14), 950_000, 5_000));
        using var font = PresentationFont.Create(appearance.Body);
        var occupied = new List<SKRect>();
        var maximumBlocks = Math.Min(PresentationLayerPayloadV1.MaximumTextBlocks, remainingPrimitives);

        var letterSpacing = appearance.Body.LetterSpacingMilliPixels / 1000f;
        var pad = appearance.Body.HaloWidthMilliPixels / 2000f + scale;

        foreach (var candidate in candidates
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ThenBy(static item => item.Pixel.X)
            .ThenBy(static item => item.Pixel.Y))
        {
            if (textBlocks.Count >= maximumBlocks) break;
            var baseBounds = PresentationFont.LineBounds(font, candidate.Label, 0f, 0f, letterSpacing);
            var labelWidth = baseBounds.Width;
            var inlineY = Math.Round(candidate.Pixel.Y - 3 * scale, MidpointRounding.AwayFromZero);
            var rightX = Math.Round(candidate.Pixel.X + candidate.Clearance + 2 * scale, MidpointRounding.AwayFromZero);
            var leftX = Math.Round(candidate.Pixel.X - candidate.Clearance - 2 * scale - labelWidth, MidpointRounding.AwayFromZero);
            var centeredX = Math.Round(
                Math.Clamp(candidate.Pixel.X - labelWidth / 2.0, pad, Math.Max(pad, width - pad - labelWidth)),
                MidpointRounding.AwayFromZero);
            var aboveY = Math.Round(candidate.Pixel.Y - candidate.Clearance - 2 * scale - baseBounds.Bottom, MidpointRounding.AwayFromZero);
            var belowY = Math.Round(candidate.Pixel.Y + candidate.Clearance + 2 * scale - baseBounds.Top, MidpointRounding.AwayFromZero);

            ReadOnlySpan<(double X, double Y)> placements =
            [
                (rightX, inlineY),
                (leftX, inlineY),
                (centeredX, aboveY),
                (centeredX, belowY)
            ];
            foreach (var (x, y) in placements)
            {
                var bounds = PresentationFont.LineBounds(font, candidate.Label, (float)x, (float)y, letterSpacing);
                bounds.Inflate(pad, pad);
                if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > width || bounds.Bottom > height ||
                    occupied.Any(box => Overlaps(box, bounds)))
                    continue;
                textBlocks.Add(new(PresentationTextAnchor.Point, new PixelPoint(x, y),
                    new ReadOnlyCollection<string>([candidate.Label]), scale, 0, 0, labelColor, Appearance: appearance));
                occupied.Add(bounds);
                break;
            }
        }
        return textBlocks;
    }

    private static void ValidateOptionalScene(MeasuredStellarAssociationsV1 associations, ProjectedSceneV1? scene)
    {
        if (scene is null) return;
        ProjectedSceneJson.Validate(scene);
        if (!string.Equals(scene.SceneIdentitySha256, associations.Scene.SceneIdentitySha256, StringComparison.OrdinalIgnoreCase) ||
            scene.ImageTransform.OutputWidthPixels != associations.Source.WidthPixels ||
            scene.ImageTransform.OutputHeightPixels != associations.Source.HeightPixels)
            throw new ArgumentException("The projected scene does not match the measured associations.", nameof(scene));
    }

    private static Dictionary<string, string>? BuildSceneDisplayNames(ProjectedSceneV1? scene)
    {
        if (scene is null) return null;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in scene.Objects)
        {
            if (IsNamed(item.Id, item.DisplayName))
                names[item.Id] = item.DisplayName;
        }
        return names;
    }

    private static string ResolvePlanetDisplayName(string objectId, SolarSystemBody body, Dictionary<string, string>? sceneNames) =>
        sceneNames is not null && sceneNames.TryGetValue(objectId, out var name) ? name : body.ToString();

    private static string? ResolveStarDisplayName(string catalogId, Dictionary<string, string>? sceneNames) =>
        sceneNames is not null
            ? sceneNames.GetValueOrDefault(catalogId)
            : catalogId;

    private static bool IsNamed(string id, string displayName) =>
        !string.IsNullOrWhiteSpace(displayName) && !string.Equals(id, displayName, StringComparison.Ordinal);

    private static bool Inside(PixelPoint pixel, int width, int height) =>
        double.IsFinite(pixel.X) && double.IsFinite(pixel.Y) &&
        pixel.X >= 0 && pixel.Y >= 0 && pixel.X <= width && pixel.Y <= height;

    private static bool Overlaps(SKRect left, SKRect right) =>
        left.Left < right.Right && left.Right > right.Left && left.Top < right.Bottom && left.Bottom > right.Top;
}
