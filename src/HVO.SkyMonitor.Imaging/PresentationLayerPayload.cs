using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using SkiaSharp;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Positions a text block at an explicit point or image corner.</summary>
public enum PresentationTextAnchor { Point, TopLeft, TopRight, BottomLeft, BottomRight }
/// <summary>Selects deterministic channel compositing; <see cref="Lighten"/> is channel-wise maximum.</summary>
public enum PresentationRasterBlendMode { Normal, Multiply, Screen, Lighten }

/// <summary>An 8-bit red, green, and blue presentation color; Mono8 uses <see cref="Red"/>.</summary>
public readonly record struct PresentationColor(byte Red, byte Green, byte Blue);

/// <summary>A dotted-circle marker in continuous top-left image pixels; radius is 0 through 32 pixels.</summary>
public sealed record PresentationMarkerV1(PixelPoint Center, int Radius, PresentationColor Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Crosshair = false);
/// <summary>A clipped line segment in continuous top-left image pixels with thickness 1 through 8.</summary>
public sealed record PresentationSegmentV1(PixelPoint From, PixelPoint To, int Thickness, PresentationColor Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresentationStrokeV2? Stroke = null);
/// <summary>An ellipse in continuous top-left image pixels with positive finite radii.</summary>
public sealed record PresentationEllipseV1(PixelPoint Center, double RadiusX, double RadiusY, PresentationColor Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresentationStrokeV2? Stroke = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ThicknessMilliPixels = null);

/// <summary>Pixel-space dash cadence and stroke coverage shared by SVG and saved raster products.</summary>
public sealed record PresentationStrokeV2(int DashPixels, int GapPixels, int OpacityMillionths,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double DashOffsetPixels = 0);

/// <summary>A text-bound translucent plate with a border and optional left accent, in image pixels.</summary>
public sealed record PresentationBackplateV2(PresentationColor Fill, int OpacityMillionths,
    PresentationColor Border, int Padding, int AccentPixels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresentationPlateStyleV3? Style = null);

/// <summary>Pinned font faces for new presentation styles; absent styles retain the original Plex face.</summary>
public enum PresentationFontFaceV3 { MonoRegular, MonoBold }

/// <summary>Explicit font, tracking and halo in thousandths of an image pixel.</summary>
public sealed record PresentationTextStyleV3(PresentationFontFaceV3 Face, int SizeMilliPixels,
    int LetterSpacingMilliPixels, PresentationColor Color, PresentationColor HaloColor,
    int HaloOpacityMillionths, int HaloWidthMilliPixels);

/// <summary>A body style and optional first-line heading; lines share the block's left edge.</summary>
public sealed record PresentationTextAppearanceV3(PresentationTextStyleV3 Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresentationTextStyleV3? Heading = null);

/// <summary>Border coverage and an optional heading separator independent of the left accent.</summary>
public sealed record PresentationPlateStyleV3(int BorderWidthMilliPixels, int BorderOpacityMillionths,
    bool HeadingRule = false, int RuleOpacityMillionths = 250_000);
/// <summary>A bounded text block using the embedded font at 7 * scale pixels, with pixel inset/spacing.</summary>
public sealed record PresentationTextBlockV1(
    PresentationTextAnchor Anchor,
    PixelPoint Point,
    IReadOnlyList<string> Lines,
    int Scale,
    int Inset,
    int LineSpacing,
    PresentationColor Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresentationBackplateV2? Backplate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresentationTextAppearanceV3? Appearance = null);
/// <summary>A compact cloudy-tile mask whose set bits draw tile borders in row-major order.</summary>
public sealed record PresentationTileMaskV1(
    int Columns,
    int Rows,
    string Encoding,
    ReadOnlyMemory<byte> Bits,
    int LineThickness,
    PresentationColor Color)
{
    public const string RowMajorLsbFirst = "row-major-lsb-first";
}

/// <summary>Bounded, base-pixel-independent drawing facts shared by presentation producers and materializers.</summary>
public sealed record PresentationLayerPayloadV1(
    string SchemaVersion,
    string ContentIdentitySha256,
    string SourceIdentitySha256,
    int WidthPixels,
    int HeightPixels,
    IReadOnlyList<PresentationMarkerV1> Markers,
    IReadOnlyList<PresentationSegmentV1> Segments,
    IReadOnlyList<PresentationEllipseV1> Ellipses,
    IReadOnlyList<PresentationTextBlockV1> TextBlocks,
    PresentationTileMaskV1? TileMask)
{
    public const string PreviousSchemaVersion = "presentation-layer-payload-v1";
    public const string SemanticSchemaVersion = "presentation-layer-payload-v2";
    public const string CurrentSchemaVersion = "presentation-layer-payload-v3";
    public static bool SupportsSchema(string? schema) => schema is CurrentSchemaVersion or SemanticSchemaVersion or PreviousSchemaVersion;
    public const int MaximumMarkers = 10_000;
    public const int MaximumSegments = 50_000;
    public const int MaximumEllipses = 16;
    public const int MaximumTextBlocks = 64;
    public const int MaximumLinesPerBlock = 8;
    public const int MaximumLineCharacters = 64;
    public const int MaximumTileCount = 65_536;

    /// <summary>Validates schema, identity shape, dimensions, collection bounds, and primitive ranges.</summary>
    public void ValidateStructure()
    {
        if (!SupportsSchema(SchemaVersion) || !Sha256(ContentIdentitySha256) || !Sha256(SourceIdentitySha256) ||
            WidthPixels is < 1 or > 65_536 || HeightPixels is < 1 or > 65_536 ||
            (long)WidthPixels * HeightPixels > 268_435_456 ||
            Markers is null || Markers.Count > MaximumMarkers || Segments is null || Segments.Count > MaximumSegments ||
            Ellipses is null || Ellipses.Count > MaximumEllipses || TextBlocks is null || TextBlocks.Count > MaximumTextBlocks)
            throw new ArgumentException("Presentation layer payload structure is invalid.", nameof(PresentationLayerPayloadV1));
        if (Markers.Any(static value => value is null || !Finite(value.Center) || value.Radius is < 0 or > 32) ||
            Segments.Any(static value => value is null || !Finite(value.From) || !Finite(value.To) || value.Thickness is < 1 or > 8) ||
            Ellipses.Any(static value => value is null || !Finite(value.Center) || !double.IsFinite(value.RadiusX) ||
                !double.IsFinite(value.RadiusY) || value.RadiusX <= 0 || value.RadiusY <= 0) ||
            TextBlocks.Any(static value => value is null || !Enum.IsDefined(value.Anchor) || !Finite(value.Point) ||
                value.Scale is < 1 or > 16 || value.Inset is < 0 or > 64 || value.LineSpacing is < 0 or > 16 ||
                value.Lines is null || value.Lines.Count > MaximumLinesPerBlock || value.Lines.Any(static line =>
                    string.IsNullOrWhiteSpace(line) || line.Length > MaximumLineCharacters || line.Any(char.IsControl))))
            throw new ArgumentException("Presentation layer primitive is invalid.", nameof(PresentationLayerPayloadV1));
        if (Segments.Any(value => !ValidStroke(value.Stroke)) || Ellipses.Any(value => !ValidStroke(value.Stroke) || value.Stroke is { DashPixels: > 0 }) ||
            TextBlocks.Any(value => value.Backplate is { } plate && (plate.Padding is < 0 or > 32 ||
                plate.AccentPixels is < 0 or > 8 || plate.OpacityMillionths is < 0 or > 1_000_000)) ||
            SchemaVersion == PreviousSchemaVersion && (Markers.Any(value => value.Crosshair) ||
                Segments.Any(value => value.Stroke is not null) || Ellipses.Any(value => value.Stroke is not null) ||
                TextBlocks.Any(value => value.Backplate is not null)))
            throw new ArgumentException("Presentation style is invalid for its schema.", nameof(PresentationLayerPayloadV1));
        if (Ellipses.Any(value => value.ThicknessMilliPixels is < 1 or > 8_000) ||
            TextBlocks.Any(value => value.Appearance is { } appearance &&
                (!ValidTextStyle(appearance.Body) || appearance.Heading is { } heading && !ValidTextStyle(heading)) ||
                value.Backplate?.Style is { } plate && (plate.BorderWidthMilliPixels is < 0 or > 8_000 ||
                    plate.BorderOpacityMillionths is < 0 or > 1_000_000 || plate.RuleOpacityMillionths is < 0 or > 1_000_000)) ||
            SchemaVersion != CurrentSchemaVersion && (Ellipses.Any(value => value.ThicknessMilliPixels is not null) ||
                TextBlocks.Any(value => value.Appearance is not null || value.Backplate?.Style is not null)))
            throw new ArgumentException("Presentation appearance is invalid for its schema.", nameof(PresentationLayerPayloadV1));
        if (SchemaVersion != PreviousSchemaVersion && (Markers.Any(value => !Bounded(value.Center)) ||
            Segments.Any(value => !Bounded(value.From) || !Bounded(value.To)) ||
            Ellipses.Any(value => !Bounded(value.Center) || value.RadiusX > 131_072 || value.RadiusY > 131_072) ||
            TextBlocks.Any(value => !Bounded(value.Point))))
            throw new ArgumentException("Presentation coordinates exceed the bounded rendering domain.", nameof(PresentationLayerPayloadV1));
        if (TileMask is { } mask)
        {
            int count;
            try { count = checked(mask.Columns * mask.Rows); }
            catch (OverflowException exception) { throw new ArgumentException("Tile mask is invalid.", nameof(PresentationLayerPayloadV1), exception); }
            if (mask.Columns < 1 || mask.Rows < 1 || count > MaximumTileCount ||
                mask.Columns > WidthPixels || mask.Rows > HeightPixels || mask.LineThickness is < 1 or > 8 ||
                mask.Encoding != PresentationTileMaskV1.RowMajorLsbFirst || mask.Bits.Length != (count + 7) / 8 ||
                count % 8 != 0 && (mask.Bits.Span[^1] & ~((1 << (count % 8)) - 1)) != 0)
                throw new ArgumentException("Tile mask is invalid.", nameof(PresentationLayerPayloadV1));
        }
    }

    internal PresentationLayerPayloadV1 Freeze() => this with
    {
        Markers = Array.AsReadOnly(Markers.ToArray()),
        Segments = Array.AsReadOnly(Segments.ToArray()),
        Ellipses = Array.AsReadOnly(Ellipses.ToArray()),
        TextBlocks = Array.AsReadOnly(TextBlocks.Select(static block => block with
        {
            Lines = Array.AsReadOnly(block.Lines.ToArray())
        }).ToArray()),
        TileMask = TileMask is null ? null : TileMask with { Bits = TileMask.Bits.ToArray() }
    };

    private static bool Finite(PixelPoint value) => double.IsFinite(value.X) && double.IsFinite(value.Y);
    private static bool Bounded(PixelPoint value) => Math.Abs(value.X) <= 131_072 && Math.Abs(value.Y) <= 131_072;
    private static bool ValidTextStyle(PresentationTextStyleV3? style) => style is not null &&
        Enum.IsDefined(style.Face) && style.SizeMilliPixels is >= 1_000 and <= 112_000 &&
        style.LetterSpacingMilliPixels is >= 0 and <= 8_000 && style.HaloWidthMilliPixels is >= 0 and <= 16_000 &&
        style.HaloOpacityMillionths is >= 0 and <= 1_000_000;
    private static bool ValidStroke(PresentationStrokeV2? stroke) => stroke is null ||
        stroke.DashPixels is >= 0 and <= 64 && stroke.GapPixels is >= 0 and <= 64 &&
        (stroke.DashPixels == 0) == (stroke.GapPixels == 0) && stroke.OpacityMillionths is >= 0 and <= 1_000_000 &&
        double.IsFinite(stroke.DashOffsetPixels) && stroke.DashOffsetPixels is >= 0 and < 128;
    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

/// <summary>One immutable typed payload plus its runtime enable, blend, and millionths-opacity controls.</summary>
public sealed record PresentationCompositorLayer(
    PresentationLayerPayloadV1 Payload,
    bool Enabled,
    PresentationRasterBlendMode BlendMode,
    int OpacityMillionths);

/// <summary>Rasterizes ordered typed layers into exactly one owned packed output buffer.</summary>
public static class PresentationLayerCompositor
{
    public const string PreviousAlgorithmVersion = "typed-presentation-compositor-v3-plex";
    public const string SemanticAlgorithmVersion = "typed-presentation-compositor-v5-single-coverage";
    public const string AlgorithmVersion = "typed-presentation-compositor-v6-pinned-appearance";

    public static string SelectAlgorithmVersion(IEnumerable<string> versions)
    {
        var values = versions.ToArray();
        return values.All(value => value == PreviousAlgorithmVersion) ? PreviousAlgorithmVersion :
            values.All(value => value is PreviousAlgorithmVersion or SemanticAlgorithmVersion) ? SemanticAlgorithmVersion : AlgorithmVersion;
    }

    // Execution admission, not wire-format limits. Covers 3552x3552 W6 with seven layers;
    // bounds the owned output, aggregate readback and recorded native geometry independently.
    public const int MaximumCompositionPixels = 32 * 1024 * 1024;
    public const int MaximumEnabledLayers = 16;
    public const long MaximumLayerPixels = 128L * 1024 * 1024;
    public const int MaximumLayerPrimitives = 4096;
    public const int MaximumCompositionPrimitives = 20_000;
    public const int MaximumCompositionTextCharacters = 8192;
    public const long MaximumGeometryWork = 32L * 1024 * 1024;

    /// <summary>Composes presentation color, explicitly promoting a Mono8 display base only for enabled v2 layers.</summary>
    public static (ImageLayout Layout, byte[] Pixels) CompositeDisplay(ImageLayout layout, ReadOnlyMemory<byte> immutableBase,
        IReadOnlyList<PresentationCompositorLayer> orderedLayers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedLayers);
        cancellationToken.ThrowIfCancellationRequested();
        if (orderedLayers.Count > 256) throw new ArgumentException("Too many presentation layers.", nameof(orderedLayers));
        var promote = layout.PixelFormat == CameraPixelFormat.Mono8 && orderedLayers.Any(layer =>
            layer is { Enabled: true, OpacityMillionths: > 0 } && layer.Payload.SchemaVersion != PresentationLayerPayloadV1.PreviousSchemaVersion);
        var outputLayout = promote ? new ImageLayout(layout.Width, layout.Height, CameraPixelFormat.Rgb24, checked(layout.Width * 3)) : layout;
        return (outputLayout, CompositeCore(layout, outputLayout, immutableBase, orderedLayers, cancellationToken));
    }

    /// <summary>
    /// Clones the borrowed immutable packed base exactly once and rasterizes ordered enabled layers into that owned
    /// buffer. Supports packed Mono8 and RGB24 and observes cancellation before allocation and during bounded loops.
    /// </summary>
    public static byte[] Composite(
        ImageLayout layout,
        ReadOnlyMemory<byte> immutableBase,
        IReadOnlyList<PresentationCompositorLayer> orderedLayers,
        CancellationToken cancellationToken = default) => CompositeCore(layout, layout, immutableBase, orderedLayers, cancellationToken);

    private static byte[] CompositeCore(ImageLayout inputLayout, ImageLayout layout, ReadOnlyMemory<byte> immutableBase,
        IReadOnlyList<PresentationCompositorLayer> orderedLayers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        inputLayout.Validate();
        ArgumentNullException.ThrowIfNull(orderedLayers);
        if (layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
            layout.StrideBytes != layout.MinimumStrideBytes || inputLayout.StrideBytes != inputLayout.MinimumStrideBytes || orderedLayers.Count > 256)
            throw new ArgumentException("Compositing requires a bounded packed Mono8 or RGB24 frame.", nameof(layout));

        var pixelCount = checked((long)layout.Width * layout.Height);
        if (pixelCount > MaximumCompositionPixels)
            throw new ArgumentException("Presentation dimensions exceed the composition pixel budget.", nameof(layout));
        ImageBuffer.Validate(inputLayout, immutableBase);
        var enabled = new List<PresentationCompositorLayer>();
        var primitives = 0;
        var textCharacters = 0;
        var geometryWork = 0d;
        foreach (var layer in orderedLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || !Enum.IsDefined(layer.BlendMode) || layer.OpacityMillionths is < 0 or > 1_000_000)
                throw new ArgumentException("Compositor layer is invalid.", nameof(orderedLayers));
            if (!layer.Enabled || layer.OpacityMillionths == 0) continue;
            if (enabled.Count == MaximumEnabledLayers || pixelCount * (enabled.Count + 1) > MaximumLayerPixels)
                throw new ArgumentException("Presentation exceeds the enabled-layer work budget.", nameof(orderedLayers));
            var payload = layer.Payload ?? throw new ArgumentException("Layer payload is required.", nameof(orderedLayers));
            payload.ValidateStructure();
            payload = payload.Freeze();
            payload.ValidateStructure();
            if (payload.WidthPixels != layout.Width || payload.HeightPixels != layout.Height)
                throw new ArgumentException("Layer dimensions do not match the base frame.", nameof(orderedLayers));
            var layerPrimitives = payload.Markers.Count + payload.Segments.Count + payload.Ellipses.Count +
                payload.TextBlocks.Sum(static block => block.Lines.Count + (block.Backplate is null ? 0 : 2) + (block.Backplate?.Style?.HeadingRule == true && block.Lines.Count > 1 ? 1 : 0)) +
                (payload.TileMask is { } mask ? checked(mask.Columns * mask.Rows) : 0);
            primitives += layerPrimitives;
            if (layerPrimitives > MaximumLayerPrimitives || primitives > MaximumCompositionPrimitives)
                throw new ArgumentException("Presentation exceeds the recorded primitive budget.", nameof(orderedLayers));
            foreach (var segment in payload.Segments)
                geometryWork += (Math.Abs(segment.From.X - segment.To.X) + Math.Abs(segment.From.Y - segment.To.Y) + 1) * segment.Thickness;
            foreach (var ellipse in payload.Ellipses)
                geometryWork += 4 * (ellipse.RadiusX + ellipse.RadiusY);
            foreach (var marker in payload.Markers)
                geometryWork += 16 * (marker.Radius + 4);
            foreach (var block in payload.TextBlocks)
                foreach (var line in block.Lines)
                {
                    textCharacters += line.Length;
                    geometryWork += (long)line.Length * 7 * block.Scale * 7 * block.Scale;
                }
            if (payload.TileMask is { } tiles)
                geometryWork += 2d * tiles.LineThickness * (tiles.Rows * layout.Width + tiles.Columns * layout.Height);
            if (!double.IsFinite(geometryWork) || geometryWork > MaximumGeometryWork || textCharacters > MaximumCompositionTextCharacters)
                throw new ArgumentException("Presentation exceeds the geometry/text work budget.", nameof(orderedLayers));
            enabled.Add(layer with { Payload = payload });
        }

        // All enabled inputs and aggregate budgets are settled before allocating output or native owners.
        cancellationToken.ThrowIfCancellationRequested();
        var output = new byte[layout.RequiredByteLength];
        if (layout.PixelFormat == inputLayout.PixelFormat) immutableBase.Span.CopyTo(output);
        else
            for (var y = 0; y < layout.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < layout.Width; x++)
                {
                    var offset = (y * layout.Width + x) * 3;
                    output[offset] = output[offset + 1] = output[offset + 2] = immutableBase.Span[y * inputLayout.StrideBytes + x];
                }
            }
        foreach (var layer in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = layer.Payload;
            if (payload.SchemaVersion != PresentationLayerPayloadV1.PreviousSchemaVersion)
            {
                DrawSemanticLayer(output, layout, payload, layer, cancellationToken);
                continue;
            }

            foreach (var segment in payload.Segments)
                DrawLine(output, layout, segment.From, segment.To, segment.Thickness, segment.Color, layer, cancellationToken, segment.Stroke);
            foreach (var ellipse in payload.Ellipses)
                DrawEllipse(output, layout, ellipse, layer, cancellationToken);
            foreach (var marker in payload.Markers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrawMarker(output, layout, marker, layer);
            }
            foreach (var text in payload.TextBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrawTextBlock(output, layout, text, layer, cancellationToken);
            }
            if (payload.TileMask is { } mask) DrawTileMask(output, layout, mask, layer, cancellationToken);
        }
        return output;
    }

    private static void DrawSemanticLayer(byte[] pixels, ImageLayout layout, PresentationLayerPayloadV1 payload,
        PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
        // SVG applies group opacity/blending after rasterizing the layer. Recording first also keeps
        // thick strokes, ellipse scan intersections and crosshair junctions from blending repeatedly.
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, layout.Width, layout.Height), useRTree: true);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        foreach (var segment in payload.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var dash = segment.Stroke is { DashPixels: > 0 } stroke
                ? SKPathEffect.CreateDash([stroke.DashPixels, stroke.GapPixels], (float)stroke.DashOffsetPixels) : null;
            paint.PathEffect = dash;
            paint.Color = Color(segment.Color, segment.Stroke?.OpacityMillionths ?? 1_000_000);
            paint.StrokeWidth = segment.Thickness;
            canvas.DrawLine((float)segment.From.X, (float)segment.From.Y, (float)segment.To.X, (float)segment.To.Y, paint);
            paint.PathEffect = null;
        }
        paint.StrokeWidth = 1;
        foreach (var ellipse in payload.Ellipses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paint.Color = Color(ellipse.Color, ellipse.Stroke?.OpacityMillionths ?? 1_000_000);
            paint.StrokeWidth = (ellipse.ThicknessMilliPixels ?? 1000) / 1000f;
            canvas.DrawOval((float)ellipse.Center.X, (float)ellipse.Center.Y, (float)ellipse.RadiusX, (float)ellipse.RadiusY, paint);
        }
        paint.StrokeWidth = 1;
        foreach (var marker in payload.Markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paint.Color = Color(marker.Color);
            using var builder = new SKPathBuilder();
            var x = (float)marker.Center.X;
            var y = (float)marker.Center.Y;
            if (marker.Crosshair)
            {
                var extent = marker.Radius + 4;
                builder.MoveTo(x - extent, y);
                builder.LineTo(x + extent, y);
                builder.MoveTo(x, y - extent);
                builder.LineTo(x, y + extent);
            }
            if (marker.Radius > 0)
                builder.AddCircle(x, y, marker.Radius);
            using var path = builder.Detach();
            canvas.DrawPath(path, paint);
            if (marker.Radius == 0)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(Round(x), Round(y), 1, 1, paint);
                paint.Style = SKPaintStyle.Stroke;
            }
        }
        foreach (var block in payload.TextBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawSemanticText(canvas, layout, block, cancellationToken);
        }
        if (payload.TileMask is { } mask)
        {
            using var builder = new SKPathBuilder();
            for (var row = 0; row < mask.Rows; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var column = 0; column < mask.Columns; column++)
                {
                    var index = row * mask.Columns + column;
                    if ((mask.Bits.Span[index >> 3] & 1 << (index & 7)) == 0) continue;
                    builder.AddRect(new SKRect((long)column * layout.Width / mask.Columns, (long)row * layout.Height / mask.Rows,
                        (long)(column + 1) * layout.Width / mask.Columns, (long)(row + 1) * layout.Height / mask.Rows));
                }
            }
            using var path = builder.Detach();
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeJoin = SKStrokeJoin.Miter;
            paint.StrokeWidth = mask.LineThickness;
            paint.Color = Color(mask.Color);
            canvas.DrawPath(path, paint);
        }
        using var picture = recorder.EndRecording();
        var bounds = SKRect.Intersect(picture.CullRect, new SKRect(0, 0, layout.Width, layout.Height));
        if (bounds.IsEmpty) return;
        var firstX = Math.Max(0, (int)Math.Floor(bounds.Left));
        var firstY = Math.Max(0, (int)Math.Floor(bounds.Top));
        var throughX = Math.Min(layout.Width, (int)Math.Ceiling(bounds.Right));
        var throughY = Math.Min(layout.Height, (int)Math.Ceiling(bounds.Bottom));
        // One reusable 4 MiB tile, independent of full-frame dimensions. Cancellation is checked
        // between native playbacks and every copied row; all native owners unwind on any failure.
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(new SKImageInfo(Math.Min(1024, layout.Width), Math.Min(1024, layout.Height), SKColorType.Rgba8888, SKAlphaType.Premul));
        using var tile = new SKCanvas(bitmap);
        for (var top = firstY; top < throughY; top += bitmap.Height)
            for (var left = firstX; left < throughX; left += bitmap.Width)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tile.Clear(SKColors.Transparent);
                tile.Save();
                tile.Translate(-left, -top);
                tile.DrawPicture(picture);
                tile.Restore();
                var coverage = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
                var rowWords = bitmap.RowBytes / 4;
                for (var y = 0; y < Math.Min(bitmap.Height, throughY - top); y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = coverage.Slice(y * rowWords, Math.Min(bitmap.Width, throughX - left));
                    for (var x = 0; x < row.Length; x++)
                    {
                        var word = row[x];
                        if (word == 0)
                        {
                            var next = row[x..].IndexOfAnyExcept(0u);
                            if (next < 0) break;
                            x += next;
                            word = row[x];
                        }
                        if (!BitConverter.IsLittleEndian) word = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(word);
                        var alpha = (byte)(word >> 24);
                        if (alpha != 0)
                            Set(pixels, layout, left + x, top + y,
                                new(Unpremultiply((byte)word, alpha), Unpremultiply((byte)(word >> 8), alpha),
                                    Unpremultiply((byte)(word >> 16), alpha)), layer, alpha);
                    }
                }
            }

        static byte Unpremultiply(byte value, byte alpha) => (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);
    }

    private static SKColor Color(PresentationColor value, int opacity = 1_000_000) =>
        new(value.Red, value.Green, value.Blue, (byte)(((long)opacity * 255 + 500_000) / 1_000_000));

    private static void DrawSemanticText(SKCanvas canvas, ImageLayout layout, PresentationTextBlockV1 block,
        CancellationToken cancellationToken)
    {
        using var font = PresentationFont.Create(block.Scale);
        using var paint = new SKPaint { IsAntialias = true };
        if (block.Backplate is { } plate && block.Lines.Count > 0)
        {
            var bounds = PresentationFont.BackplateBounds(block, layout.Width, layout.Height, font);
            paint.Color = new(plate.Fill.Red, plate.Fill.Green, plate.Fill.Blue,
                (byte)(((long)plate.OpacityMillionths * 255 + 500_000) / 1_000_000));
            canvas.DrawRect(bounds, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (plate.Style?.BorderWidthMilliPixels ?? 1000) / 1000f;
            paint.Color = Color(plate.Border, plate.Style?.BorderOpacityMillionths ?? 1_000_000);
            if (paint.StrokeWidth > 0) canvas.DrawRect(bounds, paint);
            if (plate.AccentPixels > 0)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = Color(plate.Border);
                canvas.DrawRect(bounds.Left, bounds.Top, plate.AccentPixels, bounds.Height, paint);
            }
            if (plate.Style?.HeadingRule == true && block.Lines.Count > 1)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                paint.Color = Color(plate.Border, plate.Style.RuleOpacityMillionths);
                var y = PresentationFont.HeadingRuleY(block, layout.Width, layout.Height);
                canvas.DrawLine(bounds.Left + plate.Padding, y, bounds.Right - plate.Padding, y, paint);
            }
        }
        for (var index = 0; index < block.Lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawSemanticTextLine(canvas, layout, block, index, paint);
        }
    }

    private static void DrawSemanticTextLine(SKCanvas canvas, ImageLayout layout, PresentationTextBlockV1 block,
        int index, SKPaint paint)
    {
        using var lineFont = PresentationFont.Create(block, index);
        var style = PresentationFont.LineStyle(block, index);
        var (x, y) = PresentationFont.LineOrigin(block, layout.Width, layout.Height, lineFont, block.Lines[index], index);
        using var path = PresentationFont.LinePath(lineFont, block.Lines[index], x, y,
            (style?.LetterSpacingMilliPixels ?? 0) / 1000f);
        var halo = style is null ? 2 * PresentationFont.Halo(block.Scale) : style.HaloWidthMilliPixels / 1000f;
        if (halo > 0)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = halo;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.Color = style is null ? SKColors.Black : Color(style.HaloColor, style.HaloOpacityMillionths);
            canvas.DrawPath(path, paint);
        }
        paint.Style = SKPaintStyle.Fill;
        paint.Color = Color(style?.Color ?? block.Color);
        canvas.DrawPath(path, paint);
    }

    private static void DrawMarker(byte[] pixels, ImageLayout layout, PresentationMarkerV1 marker, PresentationCompositorLayer layer)
    {
        var extent = marker.Radius + 4;
        if (marker.Center.X < -extent || marker.Center.Y < -extent ||
            marker.Center.X > layout.Width + extent || marker.Center.Y > layout.Height + extent) return;
        if (marker.Crosshair)
        {
            for (var offset = -marker.Radius - 4; offset <= marker.Radius + 4; offset++)
            {
                SetFinite(pixels, layout, marker.Center.X + offset, marker.Center.Y, marker.Color, layer);
                SetFinite(pixels, layout, marker.Center.X, marker.Center.Y + offset, marker.Color, layer);
            }
            if (marker.Radius > 0)
            {
                for (var offset = -marker.Radius; offset <= marker.Radius; offset++)
                {
                    var edge = Math.Sqrt(marker.Radius * marker.Radius - offset * offset);
                    SetFinite(pixels, layout, marker.Center.X + offset, marker.Center.Y + edge, marker.Color, layer);
                    SetFinite(pixels, layout, marker.Center.X + offset, marker.Center.Y - edge, marker.Color, layer);
                    SetFinite(pixels, layout, marker.Center.X + edge, marker.Center.Y + offset, marker.Color, layer);
                    SetFinite(pixels, layout, marker.Center.X - edge, marker.Center.Y + offset, marker.Color, layer);
                }
                return;
            }
        }
        var cx = Round(marker.Center.X); var cy = Round(marker.Center.Y);
        if (marker.Radius == 0) { Set(pixels, layout, cx, cy, marker.Color, layer); return; }
        var count = Math.Max(8, Round(Math.PI * marker.Radius));
        for (var index = 0; index < count; index++)
        {
            var angle = 2 * Math.PI * index / count;
            Set(pixels, layout, cx + Round(marker.Radius * Math.Cos(angle)), cy + Round(marker.Radius * Math.Sin(angle)), marker.Color, layer);
        }
    }

    private static void DrawLine(byte[] pixels, ImageLayout layout, PixelPoint from, PixelPoint to, int thickness,
        PresentationColor color, PresentationCompositorLayer layer, CancellationToken cancellationToken,
        PresentationStrokeV2? stroke = null)
    {
        var origin = from;
        if (!Clip(ref from, ref to, layout.Width, layout.Height)) return;
        var x0 = Round(from.X); var y0 = Round(from.Y); var x1 = Round(to.X); var y1 = Round(to.Y);
        var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1; var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var distance = stroke is { DashPixels: > 0 }
                ? Math.Sqrt((x0 - origin.X) * (x0 - origin.X) + (y0 - origin.Y) * (y0 - origin.Y)) : 0;
            if (stroke is null || stroke.DashPixels == 0 || (distance + stroke.DashOffsetPixels) % (stroke.DashPixels + stroke.GapPixels) < stroke.DashPixels)
                for (var oy = -(thickness - 1) / 2; oy <= thickness / 2; oy++)
                    for (var ox = -(thickness - 1) / 2; ox <= thickness / 2; ox++)
                        Set(pixels, layout, x0 + ox, y0 + oy, color, layer,
                            (byte)((long)(stroke?.OpacityMillionths ?? 1_000_000) * 255 / 1_000_000));
            if (x0 == x1 && y0 == y1) return;
            var doubled = 2 * error;
            if (doubled >= dy) { error += dy; x0 += sx; }
            if (doubled <= dx) { error += dx; y0 += sy; }
        }
    }

    private static void DrawEllipse(byte[] pixels, ImageLayout layout, PresentationEllipseV1 value,
        PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
        if (value.Stroke is { } stroke)
            layer = layer with { OpacityMillionths = (int)((long)layer.OpacityMillionths * stroke.OpacityMillionths / 1_000_000) };
        for (var x = 0; x < layout.Width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = (x - value.Center.X) / value.RadiusX;
            if (Math.Abs(normalized) <= 1)
            {
                var offset = value.RadiusY * Math.Sqrt(Math.Max(0, 1 - normalized * normalized));
                SetFinite(pixels, layout, x, value.Center.Y - offset, value.Color, layer);
                SetFinite(pixels, layout, x, value.Center.Y + offset, value.Color, layer);
            }
        }
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = (y - value.Center.Y) / value.RadiusY;
            if (Math.Abs(normalized) <= 1)
            {
                var offset = value.RadiusX * Math.Sqrt(Math.Max(0, 1 - normalized * normalized));
                SetFinite(pixels, layout, value.Center.X - offset, y, value.Color, layer);
                SetFinite(pixels, layout, value.Center.X + offset, y, value.Color, layer);
            }
        }
    }

    private static void DrawTextBlock(byte[] pixels, ImageLayout layout, PresentationTextBlockV1 value,
        PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
        using var font = PresentationFont.Create(value.Scale);
        if (value.Backplate is { } plate && value.Lines.Count > 0)
        {
            var bounds = PresentationFont.BackplateBounds(value, layout.Width, layout.Height, font);
            bounds.Intersect(new SKRect(0, 0, layout.Width, layout.Height));
            var left = Math.Max(0, (int)bounds.Left);
            var top = Math.Max(0, (int)bounds.Top);
            var right = Math.Min(layout.Width, (int)bounds.Right);
            var bottom = Math.Min(layout.Height, (int)bounds.Bottom);
            for (var y = top; y < bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = left; x < right; x++)
                {
                    var border = x < bounds.Left + Math.Max(1, plate.AccentPixels) || x >= bounds.Right - 1 ||
                        y < bounds.Top + 1 || y >= bounds.Bottom - 1;
                    Set(pixels, layout, x, y, border ? plate.Border : plate.Fill, layer,
                        border ? (byte)255 : (byte)((long)plate.OpacityMillionths * 255 / 1_000_000));
                }
            }
        }
        for (var index = 0; index < value.Lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y) = PresentationFont.LineOrigin(value, layout.Width, layout.Height, font, value.Lines[index], index);
            using var path = PresentationFont.LinePath(font, value.Lines[index], x, y);
            var halo = PresentationFont.Halo(value.Scale);
            DrawLegacyTextPath(pixels, layout, path, halo, value.Color, layer, cancellationToken);
        }
    }

    private static void DrawLegacyTextPath(byte[] pixels, ImageLayout layout, SKPath path, int halo,
        PresentationColor textColor, PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
        var bounds = path.Bounds;
        var left = Math.Max(0, (int)Math.Floor(bounds.Left - halo - 1));
        var top = Math.Max(0, (int)Math.Floor(bounds.Top - halo - 1));
        var right = Math.Min(layout.Width, (int)Math.Ceiling(bounds.Right + halo + 1));
        var bottom = Math.Min(layout.Height, (int)Math.Ceiling(bounds.Bottom + halo + 1));
        if (left >= right || top >= bottom) return;
        using var bitmap = new SKBitmap(new SKImageInfo(right - left, bottom - top, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-left, -top);
            if (halo > 0)
            {
                using var outline = new SKPaint
                {
                    Color = SKColors.Black,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2 * halo,
                    StrokeJoin = SKStrokeJoin.Round
                };
                canvas.DrawPath(path, outline);
            }
            using var fill = new SKPaint
            {
                Color = new SKColor(textColor.Red, textColor.Green, textColor.Blue),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawPath(path, fill);
        }
        for (var row = 0; row < bitmap.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < bitmap.Width; column++)
            {
                var pixel = bitmap.GetPixel(column, row);
                if (pixel.Alpha == 0) continue;
                var color = new PresentationColor(pixel.Red, pixel.Green, pixel.Blue);
                Set(pixels, layout, left + column, top + row, color, layer, pixel.Alpha);
            }
        }
    }

    private static void DrawTileMask(byte[] pixels, ImageLayout layout, PresentationTileMaskV1 mask,
        PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
        for (var row = 0; row < mask.Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y0 = (int)((long)row * layout.Height / mask.Rows); var y1 = (int)((long)(row + 1) * layout.Height / mask.Rows);
            for (var column = 0; column < mask.Columns; column++)
            {
                var index = row * mask.Columns + column;
                if ((mask.Bits.Span[index >> 3] & 1 << (index & 7)) == 0) continue;
                var x0 = (int)((long)column * layout.Width / mask.Columns); var x1 = (int)((long)(column + 1) * layout.Width / mask.Columns);
                for (var t = 0; t < mask.LineThickness && x0 + t < x1 - t && y0 + t < y1 - t; t++)
                {
                    for (var x = x0 + t; x < x1 - t; x++) { Set(pixels, layout, x, y0 + t, mask.Color, layer); Set(pixels, layout, x, y1 - 1 - t, mask.Color, layer); }
                    for (var y = y0 + t; y < y1 - t; y++) { Set(pixels, layout, x0 + t, y, mask.Color, layer); Set(pixels, layout, x1 - 1 - t, y, mask.Color, layer); }
                }
            }
        }
    }

    private static void SetFinite(byte[] pixels, ImageLayout layout, double x, double y, PresentationColor color, PresentationCompositorLayer layer)
    { if (double.IsFinite(x) && double.IsFinite(y) && x is >= int.MinValue and <= int.MaxValue && y is >= int.MinValue and <= int.MaxValue) Set(pixels, layout, Round(x), Round(y), color, layer); }

    private static void Set(byte[] pixels, ImageLayout layout, int x, int y, PresentationColor color,
        PresentationCompositorLayer layer, byte coverage = 255)
    {
        if ((uint)x >= (uint)layout.Width || (uint)y >= (uint)layout.Height) return;
        var channels = layout.PixelFormat == CameraPixelFormat.Mono8 ? 1 : 3; var offset = y * layout.StrideBytes + x * channels;
        if (channels == 1) Blend(ref pixels[offset], color.Red, layer, coverage); else { Blend(ref pixels[offset], color.Red, layer, coverage); Blend(ref pixels[offset + 1], color.Green, layer, coverage); Blend(ref pixels[offset + 2], color.Blue, layer, coverage); }
    }

    private static void Blend(ref byte destination, byte source, PresentationCompositorLayer layer, byte coverage)
    {
        var blended = layer.BlendMode switch
        {
            PresentationRasterBlendMode.Normal => source,
            PresentationRasterBlendMode.Multiply => (destination * source + 127) / 255,
            PresentationRasterBlendMode.Screen => 255 - ((255 - destination) * (255 - source) + 127) / 255,
            PresentationRasterBlendMode.Lighten => Math.Max(destination, source),
            _ => source
        };
        var opacity = (int)((long)layer.OpacityMillionths * coverage / 255);
        destination = (byte)((destination * (1_000_000 - opacity) + blended * opacity + 500_000) / 1_000_000);
    }

    private static bool Clip(ref PixelPoint from, ref PixelPoint to, int width, int height)
    {
        var dx = to.X - from.X; var dy = to.Y - from.Y; var min = 0d; var max = 1d;
        if (!Boundary(-dx, from.X, ref min, ref max) || !Boundary(dx, width - 1d - from.X, ref min, ref max) ||
            !Boundary(-dy, from.Y, ref min, ref max) || !Boundary(dy, height - 1d - from.Y, ref min, ref max)) return false;
        to = new(from.X + max * dx, from.Y + max * dy); from = new(from.X + min * dx, from.Y + min * dy); return true;
    }

    private static bool Boundary(double direction, double distance, ref double minimum, ref double maximum)
    {
        if (Math.Abs(direction) < 1e-15) return distance >= 0;
        var ratio = distance / direction;
        if (direction < 0) { if (ratio > maximum) return false; minimum = Math.Max(minimum, ratio); }
        else { if (ratio < minimum) return false; maximum = Math.Min(maximum, ratio); }
        return minimum <= maximum;
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
