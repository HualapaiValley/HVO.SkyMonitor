using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Positions a text block at an explicit point or image corner.</summary>
public enum PresentationTextAnchor { Point, TopLeft, TopRight, BottomLeft, BottomRight }
/// <summary>Selects deterministic channel compositing; <see cref="Lighten"/> is channel-wise maximum.</summary>
public enum PresentationRasterBlendMode { Normal, Multiply, Screen, Lighten }

/// <summary>An 8-bit red, green, and blue presentation color; Mono8 uses <see cref="Red"/>.</summary>
public readonly record struct PresentationColor(byte Red, byte Green, byte Blue);

/// <summary>A dotted-circle marker in continuous top-left image pixels; radius is 0 through 32 pixels.</summary>
public sealed record PresentationMarkerV1(PixelPoint Center, int Radius, PresentationColor Color);
/// <summary>A clipped line segment in continuous top-left image pixels with thickness 1 through 8.</summary>
public sealed record PresentationSegmentV1(PixelPoint From, PixelPoint To, int Thickness, PresentationColor Color);
/// <summary>An ellipse in continuous top-left image pixels with positive finite radii.</summary>
public sealed record PresentationEllipseV1(PixelPoint Center, double RadiusX, double RadiusY, PresentationColor Color);
/// <summary>A bounded text block using the embedded font at 7 * scale pixels, with pixel inset/spacing.</summary>
public sealed record PresentationTextBlockV1(
    PresentationTextAnchor Anchor,
    PixelPoint Point,
    IReadOnlyList<string> Lines,
    int Scale,
    int Inset,
    int LineSpacing,
    PresentationColor Color);
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
    public const string CurrentSchemaVersion = "presentation-layer-payload-v1";
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
        if (SchemaVersion != CurrentSchemaVersion || !Sha256(ContentIdentitySha256) || !Sha256(SourceIdentitySha256) ||
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
    public const string AlgorithmVersion = "typed-presentation-compositor-v3-plex";

    /// <summary>
    /// Clones the borrowed immutable packed base exactly once and rasterizes ordered enabled layers into that owned
    /// buffer. Supports packed Mono8 and RGB24 and observes cancellation before allocation and during bounded loops.
    /// </summary>
    public static byte[] Composite(
        ImageLayout layout,
        ReadOnlyMemory<byte> immutableBase,
        IReadOnlyList<PresentationCompositorLayer> orderedLayers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageBuffer.Validate(layout, immutableBase);
        ArgumentNullException.ThrowIfNull(orderedLayers);
        if (layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
            layout.StrideBytes != layout.MinimumStrideBytes || orderedLayers.Count > 256)
            throw new ArgumentException("Compositing requires a bounded packed Mono8 or RGB24 frame.", nameof(layout));

        var output = immutableBase.ToArray();
        foreach (var layer in orderedLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || !Enum.IsDefined(layer.BlendMode) || layer.OpacityMillionths is < 0 or > 1_000_000)
                throw new ArgumentException("Compositor layer is invalid.", nameof(orderedLayers));
            if (!layer.Enabled || layer.OpacityMillionths == 0) continue;
            var payload = (layer.Payload ?? throw new ArgumentException("Layer payload is required.", nameof(orderedLayers))).Freeze();
            payload.ValidateStructure();
            if (payload.WidthPixels != layout.Width || payload.HeightPixels != layout.Height)
                throw new ArgumentException("Layer dimensions do not match the base frame.", nameof(orderedLayers));

            foreach (var segment in payload.Segments)
                DrawLine(output, layout, segment.From, segment.To, segment.Thickness, segment.Color, layer, cancellationToken);
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

    private static void DrawMarker(byte[] pixels, ImageLayout layout, PresentationMarkerV1 marker, PresentationCompositorLayer layer)
    {
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
        PresentationColor color, PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
        if (!Clip(ref from, ref to, layout.Width, layout.Height)) return;
        var x0 = Round(from.X); var y0 = Round(from.Y); var x1 = Round(to.X); var y1 = Round(to.Y);
        var dx = Math.Abs(x1 - x0); var sx = x0 < x1 ? 1 : -1; var dy = -Math.Abs(y1 - y0); var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var oy = -(thickness - 1) / 2; oy <= thickness / 2; oy++)
                for (var ox = -(thickness - 1) / 2; ox <= thickness / 2; ox++) Set(pixels, layout, x0 + ox, y0 + oy, color, layer);
            if (x0 == x1 && y0 == y1) return;
            var doubled = 2 * error;
            if (doubled >= dy) { error += dy; x0 += sx; }
            if (doubled <= dx) { error += dx; y0 += sy; }
        }
    }

    private static void DrawEllipse(byte[] pixels, ImageLayout layout, PresentationEllipseV1 value,
        PresentationCompositorLayer layer, CancellationToken cancellationToken)
    {
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
        for (var index = 0; index < value.Lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (x, y) = PresentationFont.LineOrigin(value, layout.Width, layout.Height, font, value.Lines[index], index);
            using var path = PresentationFont.LinePath(font, value.Lines[index], x, y);
            var halo = PresentationFont.Halo(value.Scale);
            var bounds = path.Bounds;
            var left = Math.Max(0, (int)Math.Floor(bounds.Left - halo - 1));
            var top = Math.Max(0, (int)Math.Floor(bounds.Top - halo - 1));
            var right = Math.Min(layout.Width, (int)Math.Ceiling(bounds.Right + halo + 1));
            var bottom = Math.Min(layout.Height, (int)Math.Ceiling(bounds.Bottom + halo + 1));
            if (left >= right || top >= bottom) continue;
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
                    Color = new SKColor(value.Color.Red, value.Color.Green, value.Color.Blue),
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
