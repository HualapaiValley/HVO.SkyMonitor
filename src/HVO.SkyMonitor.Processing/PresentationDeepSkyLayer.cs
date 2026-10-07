using System.Collections.ObjectModel;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Bounded style of the opt-in deep-sky layer. Featured objects, those with a Messier or Caldwell number or a common
/// name, are labelled by default; other NGC and IC designations only when <paramref name="LabelCatalogObjects"/> is
/// set. Label scale is a pixel multiplier from 1 through 8 and the minimum glyph radius is in output pixels.
/// </summary>
public sealed record PresentationDeepSkyStyleV1(
    int MaximumLabels = 24,
    int MaximumLabelCharacters = 24,
    int LabelScale = 1,
    bool LabelCatalogObjects = false,
    int MinimumGlyphRadius = 7,
    PresentationColor? Color = null);

/// <summary>What the deep-sky layer drew for one object.</summary>
public enum PresentationDeepSkyDrawing
{
    /// <summary>The object's sourced catalog outline.</summary>
    Outline,

    /// <summary>The object's catalog ellipse footprint.</summary>
    Footprint,

    /// <summary>A class symbol at the object's pixel.</summary>
    Glyph,

    /// <summary>Nothing: the layer budget was spent on objects of higher priority.</summary>
    Omitted
}

/// <summary>The drawing decision for one deep-sky object, in the order the layer spent its budget.</summary>
public sealed record PresentationDeepSkyObjectDrawingV1(
    string Id,
    DeepSkyRepresentation Representation,
    PresentationDeepSkyDrawing Drawing,
    string? Label);

/// <summary>A deep-sky layer payload and the per-object decisions that produced it.</summary>
public sealed record PresentationDeepSkyLayerV1(
    PresentationLayerPayloadV1 Payload,
    IReadOnlyList<PresentationDeepSkyObjectDrawingV1> Objects);

/// <summary>
/// Draws a projected scene's deep-sky collection as expected catalog geometry. Every primitive is dashed and drawn in
/// one colour that no measured layer uses, because catalog presence is never a detection claim. The layer reads only
/// canonical scene facts, never base pixels, so every host that composes it from the same scene draws the same bytes.
/// </summary>
public static class PresentationDeepSkyLayerProducer
{
    public const string ProducerVersion = "deep-sky-presentation-layer-v1";

    /// <summary>The geometry basis an overlay manifest records for the layer.</summary>
    public const string Basis = "catalog-expected-geometry";

    /// <summary>The legend an overlay manifest records for the layer.</summary>
    public const string Legend = "catalog position — not a detection";

    /// <summary>The largest number of primitives one layer may carry, the compositor's per-layer budget.</summary>
    public const int MaximumPrimitives = PresentationLayerCompositor.MaximumLayerPrimitives;

    /// <summary>
    /// The compositor work one layer may spend, an eighth of the composition budget, so enabling the layer cannot
    /// exhaust the work the existing layers need.
    /// </summary>
    public const long MaximumWork = PresentationLayerCompositor.MaximumGeometryWork / 8;

    /// <summary>The largest glyph radius in output pixels. A wider object is drawn as its outline or footprint.</summary>
    public const int MaximumGlyphRadius = 512;

    private const int Thickness = 2;
    private static readonly PresentationColor DefaultColor = new(255, 196, 120);
    private static readonly PresentationStrokeV2 GeometryStroke = new(6, 4, 900_000);
    private static readonly PresentationStrokeV2 UnknownExtentStroke = new(2, 3, 900_000);

    /// <summary>
    /// Creates the layer. Objects are taken in the scene's priority order. Each draws its outline or footprint, falls
    /// back to its class glyph when that geometry would exceed the layer budget, and is omitted only when neither fits;
    /// labels then follow in the same order within their own reserved budget.
    /// </summary>
    public static PresentationDeepSkyLayerV1 Create(ProjectedSceneV1 scene, PresentationDeepSkyStyleV1? style = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ProjectedSceneJson.Validate(scene);
        style ??= new PresentationDeepSkyStyleV1();
        if (style.MaximumLabels is < 0 or > PresentationLayerPayloadV1.MaximumTextBlocks ||
            style.MaximumLabelCharacters is < 0 or > PresentationLayerPayloadV1.MaximumLineCharacters ||
            style.LabelScale is < 1 or > 8 || style.MinimumGlyphRadius is < 2 or > 32)
            throw new ArgumentOutOfRangeException(nameof(style));
        var width = scene.ImageTransform.OutputWidthPixels;
        var height = scene.ImageTransform.OutputHeightPixels;
        if (scene.DeepSky is not { } deepSky)
            return new(PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, width, height),
                ReadOnlyCollection<PresentationDeepSkyObjectDrawingV1>.Empty);

        var color = style.Color ?? DefaultColor;
        var scale = PresentationFont.StarFrameScale(width, height, style.LabelScale);
        var labels = style.MaximumLabelCharacters == 0 ? 0 : style.MaximumLabels;
        // Labels keep their own share, so a field crowded with geometry still names its featured objects.
        var labelWork = Math.Min(MaximumWork / 2, (long)labels * style.MaximumLabelCharacters * 49 * scale * scale);
        var geometryPrimitives = MaximumPrimitives - labels;
        var geometryWork = MaximumWork - labelWork;

        var outlines = deepSky.Outlines.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var footprints = (scene.ResolvedFootprints ?? [])
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject)
            .ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var ordered = deepSky.Objects.ToList();
        ordered.Sort(ProjectedDeepSkyObject.ComparePriority);

        var segments = new List<PresentationSegmentV1>();
        var work = 0d;
        var drawings = new List<(ProjectedDeepSkyObject Item, PresentationDeepSkyDrawing Drawing, PixelPoint Anchor, double Clearance)>();
        foreach (var item in ordered)
        {
            var drawing = PresentationDeepSkyDrawing.Omitted;
            var anchor = item.Pixel ?? default;
            var clearance = 0d;
            ResolvedFootprintBounds? bounds = null;
            if (item.Representation == DeepSkyRepresentation.Outline && outlines.TryGetValue(item.Id, out var outline) &&
                TryAdd(Polylines(outline.Parts), GeometryStroke))
            {
                drawing = PresentationDeepSkyDrawing.Outline;
                bounds = outline.Bounds;
            }
            else if (item.Representation == DeepSkyRepresentation.Footprint && footprints.TryGetValue(item.Id, out var footprint) &&
                TryAdd(Polylines(footprint.Parts), GeometryStroke))
            {
                drawing = PresentationDeepSkyDrawing.Footprint;
                bounds = footprint.Bounds;
                if (item.Pixel is null) anchor = ProjectedSceneAnnotation.FootprintAnchor(footprint, scene.ImageTransform);
            }
            else if (item.Pixel is { } pixel)
            {
                var radius = GlyphRadius(item, style.MinimumGlyphRadius);
                if (TryAdd(Glyph(item.ObjectType, pixel, radius),
                    item.MajorAxisArcminutes is null && !DeepSkyObjectTypes.IsStellar(item.ObjectType) ? UnknownExtentStroke : GeometryStroke))
                {
                    drawing = PresentationDeepSkyDrawing.Glyph;
                    clearance = radius;
                }
            }
            if (bounds is not null)
            {
                if (item.Pixel is null && drawing == PresentationDeepSkyDrawing.Outline)
                    anchor = new((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2);
                clearance = Math.Max(style.MinimumGlyphRadius, bounds.MaxX - anchor.X);
            }
            drawings.Add((item, drawing, anchor, clearance));
        }

        var texts = new List<PresentationTextBlockV1>();
        var labelled = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels > 0)
        {
            var labelSize = Math.Min(112_000, 9_000 * scale);
            // A regular face in the layer colour keeps catalog names apart from the bold measured-star names.
            var appearance = new PresentationTextAppearanceV3(new(PresentationFontFaceV3.MonoRegular,
                labelSize, labelSize / 25, color, new(3, 8, 14), 950_000, 5_000));
            using var font = PresentationFont.Create(appearance.Body);
            var reserved = ReservedCorners(width, height);
            var occupied = new List<SKRect>();
            var textWork = 0L;
            foreach (var (item, drawing, anchor, clearance) in drawings)
            {
                if (texts.Count == labels) break;
                if (drawing == PresentationDeepSkyDrawing.Omitted || (!item.Featured && !style.LabelCatalogObjects)) continue;
                var name = item.DisplayName[..Math.Min(item.DisplayName.Length, style.MaximumLabelCharacters)];
                var cost = (long)name.Length * 49 * scale * scale;
                if (string.IsNullOrWhiteSpace(name) || textWork + cost > labelWork) continue;
                var y = Math.Round(anchor.Y - 3 * scale, MidpointRounding.AwayFromZero);
                var right = Math.Round(anchor.X + clearance + 2 * scale, MidpointRounding.AwayFromZero);
                var textWidth = PresentationFont.LineBounds(font, name, 0, 0, appearance.Body.LetterSpacingMilliPixels / 1000f).Width;
                var left = Math.Round(anchor.X - clearance - 2 * scale - textWidth, MidpointRounding.AwayFromZero);
                foreach (var x in (ReadOnlySpan<double>)[right, left])
                {
                    var box = PresentationFont.LineBounds(font, name, (float)x, (float)y,
                        appearance.Body.LetterSpacingMilliPixels / 1000f);
                    box.Inflate(appearance.Body.HaloWidthMilliPixels / 2000f + scale,
                        appearance.Body.HaloWidthMilliPixels / 2000f + scale);
                    if (box.Left < 0 || box.Top < 0 || box.Right > width || box.Bottom > height ||
                        occupied.Any(other => Overlaps(other, box)) || reserved.Any(other => Overlaps(other, box))) continue;
                    texts.Add(new(PresentationTextAnchor.Point, new PixelPoint(x, y),
                        new ReadOnlyCollection<string>([name]), scale, 0, 0, color, Appearance: appearance));
                    occupied.Add(box);
                    textWork += cost;
                    labelled.Add(item.Id, name);
                    break;
                }
            }
        }

        var payload = PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, width, height,
            segments: segments, textBlocks: texts);
        return new(payload, drawings.Select(value => new PresentationDeepSkyObjectDrawingV1(value.Item.Id,
            value.Item.Representation, value.Drawing, labelled.GetValueOrDefault(value.Item.Id))).ToArray().AsReadOnly());

        // Adds one object's polylines whole or not at all, carrying the dash phase along each polyline so short
        // tessellated pieces keep their gaps.
        bool TryAdd(IReadOnlyList<(bool Closed, PixelPoint[] Points)> polylines, PresentationStrokeV2 stroke)
        {
            var added = new List<PresentationSegmentV1>();
            var cost = 0d;
            var period = stroke.DashPixels + stroke.GapPixels;
            foreach (var (closed, points) in polylines)
            {
                var offset = 0d;
                for (var index = 1; index <= points.Length; index++)
                {
                    if (index == points.Length && (!closed || points.Length < 3)) break;
                    var from = points[index - 1];
                    var to = points[index % points.Length];
                    var dx = to.X - from.X;
                    var dy = to.Y - from.Y;
                    added.Add(new(from, to, Thickness, color,
                        stroke with { DashOffsetPixels = Math.Round(offset, 3, MidpointRounding.AwayFromZero) }));
                    cost += (Math.Abs(dx) + Math.Abs(dy) + 1) * Thickness;
                    offset = (offset + Math.Sqrt(dx * dx + dy * dy)) % period;
                }
            }
            if (added.Count == 0 || segments.Count + added.Count > geometryPrimitives || work + cost > geometryWork)
                return false;
            segments.AddRange(added);
            work += cost;
            return true;
        }
    }

    // Scene vertices are rounded to a thousandth of a pixel like glyph vertices, and a vertex that rounds onto its
    // predecessor is dropped, so a layer at its primitive cap keeps its grouped-SVG lines short.
    private static List<(bool Closed, PixelPoint[] Points)> Polylines(IReadOnlyList<ResolvedFootprintPart> parts)
    {
        var polylines = new List<(bool Closed, PixelPoint[] Points)>(parts.Count);
        foreach (var part in parts)
        {
            var points = new List<PixelPoint>(part.Points.Count);
            foreach (var point in part.Points)
            {
                var rounded = Offset(point, 0, 0);
                if (points.Count == 0 || points[^1] != rounded) points.Add(rounded);
            }
            if (part.Closed && points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
            if (points.Count >= 2) polylines.Add((part.Closed && points.Count >= 3, [.. points]));
        }
        return polylines;
    }

    // A sized object keeps half its projected major axis; a stellar row, an unknown extent and an object below the
    // resolvable size keep the minimum radius.
    private static double GlyphRadius(ProjectedDeepSkyObject item, int minimum) =>
        item.MajorAxisPixels is { } major && !DeepSkyObjectTypes.IsStellar(item.ObjectType)
            ? Math.Clamp(major / 2, minimum, MaximumGlyphRadius)
            : minimum;

    /// <summary>Returns the class symbol for an OpenNGC type code as closed or open polylines about a centre.</summary>
    private static List<(bool Closed, PixelPoint[] Points)> Glyph(string objectType, PixelPoint center, double radius) =>
        objectType switch
        {
            "G" or "GPair" or "GTrpl" or "GGroup" => [(true, Ellipse(center, radius, radius / 2, 16))],
            "OCl" or "*Ass" => [(true, Ellipse(center, radius, radius, 16))],
            "GCl" =>
            [
                (true, Ellipse(center, radius, radius, 16)),
                (false, [Offset(center, -radius, 0), Offset(center, radius, 0)]),
                (false, [Offset(center, 0, -radius), Offset(center, 0, radius)])
            ],
            "Cl+N" or "DrkN" or "EmN" or "HII" or "Neb" or "RfN" or "SNR" =>
            [
                (true, [Offset(center, -radius, -radius), Offset(center, radius, -radius),
                    Offset(center, radius, radius), Offset(center, -radius, radius)])
            ],
            "PN" =>
            [
                (true, Ellipse(center, radius * 0.6, radius * 0.6, 12)),
                (false, [Offset(center, 0, -radius * 0.6), Offset(center, 0, -radius)]),
                (false, [Offset(center, radius * 0.6, 0), Offset(center, radius, 0)]),
                (false, [Offset(center, 0, radius * 0.6), Offset(center, 0, radius)]),
                (false, [Offset(center, -radius * 0.6, 0), Offset(center, -radius, 0)])
            ],
            DeepSkyObjectTypes.Star => [(true, Diamond(center, radius * 0.7))],
            DeepSkyObjectTypes.DoubleStar =>
            [
                (true, Diamond(Offset(center, -radius * 0.45, 0), radius * 0.45)),
                (true, Diamond(Offset(center, radius * 0.45, 0), radius * 0.45))
            ],
            _ =>
            [
                (false, [Offset(center, -radius * 0.7, -radius * 0.7), Offset(center, radius * 0.7, radius * 0.7)]),
                (false, [Offset(center, -radius * 0.7, radius * 0.7), Offset(center, radius * 0.7, -radius * 0.7)])
            ]
        };

    private static PixelPoint[] Ellipse(PixelPoint center, double radiusX, double radiusY, int vertices) =>
        Enumerable.Range(0, vertices).Select(index =>
        {
            var angle = 2 * Math.PI * index / vertices;
            return Offset(center, radiusX * Math.Cos(angle), radiusY * Math.Sin(angle));
        }).ToArray();

    private static PixelPoint[] Diamond(PixelPoint center, double radius) =>
        [Offset(center, 0, -radius), Offset(center, radius, 0), Offset(center, 0, radius), Offset(center, -radius, 0)];

    // Glyph vertices are rounded to a thousandth of a pixel, so payload bytes do not carry trigonometric noise.
    private static PixelPoint Offset(PixelPoint center, double dx, double dy) => new(
        Math.Round(center.X + dx, 3, MidpointRounding.AwayFromZero),
        Math.Round(center.Y + dy, 3, MidpointRounding.AwayFromZero));

    // The same corner boxes the star layer keeps clear for the separately produced environment layer.
    private static List<SKRect> ReservedCorners(int width, int height)
    {
        if (width < 640 || height < 480) return [];
        using var cornerFont = PresentationFont.Create(PresentationFont.FrameScale(width, height));
        var cornerHeight = Math.Min(height / 2f, 64 +
            (PresentationLayerPayloadV1.MaximumLinesPerBlock - 1) * (cornerFont.Size * 1.2f + 16) +
            cornerFont.Size + PresentationFont.Halo(PresentationFont.FrameScale(width, height)));
        return
        [
            new SKRect(0, 0, width / 3f, cornerHeight),
            new SKRect(width * 2 / 3f, 0, width, cornerHeight),
            new SKRect(0, height - cornerHeight, width / 3f, height),
            new SKRect(width * 2 / 3f, height - cornerHeight, width, height)
        ];
    }

    private static bool Overlaps(SKRect a, SKRect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
