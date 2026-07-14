using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>An explicit affine mapping from scene pixels to preview pixels.</summary>
public readonly record struct PreviewTransform(double ScaleX, double ScaleY, double OffsetX = 0, double OffsetY = 0)
{
    /// <summary>Maps one already-projected scene point into preview coordinates.</summary>
    public PixelPoint Apply(PixelPoint point)
        => new(point.X * ScaleX + OffsetX, point.Y * ScaleY + OffsetY);

    /// <summary>Validates finite, non-zero scale values and finite offsets.</summary>
    public void Validate()
    {
        if (!double.IsFinite(ScaleX) || !double.IsFinite(ScaleY) || ScaleX == 0 || ScaleY == 0 ||
            !double.IsFinite(OffsetX) || !double.IsFinite(OffsetY))
        {
            throw new ArgumentOutOfRangeException(nameof(PreviewTransform));
        }
    }
}

/// <summary>Controls bounded marks and optional deterministic labels.</summary>
public sealed record AnnotationOptions
{
    public int MarkRadius { get; init; } = 6;
    public byte MarkValue { get; init; } = byte.MaxValue;
    public byte MarkerValue { get; init; } = 144;
    public bool DrawLabels { get; init; } = true;
    public int MaximumLabelCharacters { get; init; } = 24;
    public int LabelScale { get; init; } = 1;
    public bool DrawImageCircle { get; init; }
    public bool DrawCardinalDirections { get; init; }
    public byte ImageCircleValue { get; init; } = 96;
    public byte CardinalValue { get; init; } = byte.MaxValue;
    public int CardinalScale { get; init; } = 2;
    public byte ConstellationLineValue { get; init; } = 160;
    public byte ConstellationLineRed { get; init; } = 96;
    public byte ConstellationLineGreen { get; init; } = 160;
    public byte ConstellationLineBlue { get; init; } = byte.MaxValue;
    public int ConstellationLineThickness { get; init; } = 1;
    public double ConstellationLineOpacity { get; init; } = 0.8;

    internal void Validate()
    {
        if (MarkRadius is < 0 or > 32 || MaximumLabelCharacters is < 0 or > 256 ||
            LabelScale is < 1 or > 8 || CardinalScale is < 1 or > 8 ||
            ConstellationLineThickness is < 1 or > 8 ||
            !double.IsFinite(ConstellationLineOpacity) || ConstellationLineOpacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AnnotationOptions));
        }
    }
}

/// <summary>Describes the preview anchor actually used for one scene object.</summary>
public sealed record AnnotationAnchor(string ObjectId, PixelPoint ScenePixel, PixelPoint PreviewPixel);

/// <summary>An immutable annotation input already projected into continuous scene pixels.</summary>
public sealed record ProjectedAnnotationObject(
    string Id,
    string DisplayName,
    PixelPoint Pixel,
    bool DrawMark = true,
    bool DrawLabel = true);

/// <summary>An immutable line segment whose endpoints were resolved from the same projected scene.</summary>
public sealed record ProjectedAnnotationSegment(string ConstellationId, PixelPoint FromPixel, PixelPoint ToPixel);

/// <summary>Projection landmarks used for an image-circle border and compass labels.</summary>
public sealed record ProjectedAnnotationOverlay(
    PixelPoint Center,
    double ImageCircleRadius,
    PixelPoint North,
    PixelPoint East,
    PixelPoint South,
    PixelPoint West);

/// <summary>An annotated Mono8 copy and its transformed anchors.</summary>
public sealed record AnnotationResult(
    ReadOnlyMemory<byte> Pixels,
    IReadOnlyList<AnnotationAnchor> Anchors);

/// <summary>Draws annotations using only coordinates already present in projected scene records.</summary>
public static class AnnotationRenderer
{
    public const string AlgorithmVersion = "projected-annotation-raster-v2";

    /// <summary>Composes the existing monochrome annotation mask over packed RGB24 without altering source pixels.</summary>
    public static AnnotationResult AnnotateRgb24WithSegments(
        ReadOnlyMemory<byte> preview,
        int width,
        int height,
        IEnumerable<ProjectedAnnotationObject> objects,
        IEnumerable<ProjectedAnnotationSegment> segments,
        PreviewTransform transform,
        AnnotationOptions? options = null,
        ProjectedAnnotationOverlay? projectionOverlay = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(segments);
        if (preview.Length != checked(width * height * 3))
        {
            throw new ArgumentException("Preview dimensions do not match its RGB24 pixel buffer.", nameof(preview));
        }

        transform.Validate();
        options ??= new AnnotationOptions();
        options.Validate();
        var pixels = preview.ToArray();
        DrawRgbSegments(pixels, width, height, segments, transform, options, cancellationToken);
        var overlay = AnnotateMono8WithSegments(
            new byte[checked(width * height)], width, height, objects, [], transform, options, projectionOverlay,
            cancellationToken);
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            if (pixel % width == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var value = overlay.Pixels.Span[pixel];
            if (value == 0)
            {
                continue;
            }
            pixels[pixel * 3] = Math.Max(pixels[pixel * 3], value);
            pixels[pixel * 3 + 1] = Math.Max(pixels[pixel * 3 + 1], value);
            pixels[pixel * 3 + 2] = Math.Max(pixels[pixel * 3 + 2], value);
        }
        return new AnnotationResult(pixels, overlay.Anchors);
    }

    /// <summary>Annotates projected objects and constellation segments without recalculating scene geometry.</summary>
    public static AnnotationResult AnnotateMono8WithSegments(
        ReadOnlyMemory<byte> preview,
        int width,
        int height,
        IEnumerable<ProjectedAnnotationObject> objects,
        IEnumerable<ProjectedAnnotationSegment> segments,
        PreviewTransform transform,
        AnnotationOptions? options = null,
        ProjectedAnnotationOverlay? projectionOverlay = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(segments);
        transform.Validate();
        options ??= new AnnotationOptions();
        options.Validate();
        var pixels = preview.ToArray();
        DrawMonoSegments(pixels, width, height, segments, transform, options, cancellationToken);
        var result = AnnotateMono8(pixels, width, height, objects, transform, options, cancellationToken);
        pixels = result.Pixels.ToArray();

        if (projectionOverlay is not null)
        {
            DrawProjectionOverlay(pixels, width, height, transform, projectionOverlay, options, cancellationToken);
        }

        return new AnnotationResult(pixels, result.Anchors);
    }

    /// <summary>Legacy projection adapter retained for existing processing-step compatibility.</summary>
    public static byte[] AnnotateMono8(
        ReadOnlyMemory<byte> preview,
        int width,
        int height,
        IImageProjector projector,
        IEnumerable<AltAzPoint> directions)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(directions);
        int length;
        try
        {
            length = checked(width * height);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(width), exception, "Preview dimensions overflow the addressable buffer size.");
        }

        if (width <= 0 || height <= 0 || preview.Length != length)
        {
            throw new ArgumentException("Preview dimensions do not match its pixel buffer.", nameof(preview));
        }

        var pixels = preview.ToArray();
        foreach (var direction in directions)
        {
            if (projector.Project(direction) is { } point)
            {
                SetPixel(pixels, width, height, Round(point.X), Round(point.Y), byte.MaxValue);
            }
        }

        return pixels;
    }

    /// <summary>Copies a packed Mono8 preview and draws clipped cross marks and optional compact labels.</summary>
    public static AnnotationResult AnnotateMono8(
        ReadOnlyMemory<byte> preview,
        int width,
        int height,
        IEnumerable<ProjectedCelestialObject> objects,
        PreviewTransform transform,
        AnnotationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return AnnotateMono8(preview, width, height,
            objects.Select(static item => new ProjectedAnnotationObject(item.Id, item.DisplayName, item.Pixel)),
            transform, options, cancellationToken);
    }

    /// <summary>Copies a packed Mono8 preview and annotates persisted projected-object metadata.</summary>
    public static AnnotationResult AnnotateMono8(
        ReadOnlyMemory<byte> preview,
        int width,
        int height,
        IEnumerable<ProjectedAnnotationObject> objects,
        PreviewTransform transform,
        AnnotationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(objects);
        transform.Validate();
        options ??= new AnnotationOptions();
        options.Validate();
        int length;
        try
        {
            length = checked(width * height);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(width), exception, "Preview dimensions overflow the addressable buffer size.");
        }

        if (width <= 0 || height <= 0 || preview.Length != length)
        {
            throw new ArgumentException("Preview dimensions do not match its pixel buffer.", nameof(preview));
        }

        var pixels = preview.ToArray();
        var anchors = new List<AnnotationAnchor>();
        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchor = transform.Apply(item.Pixel);
            if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y))
            {
                continue;
            }

            anchors.Add(new AnnotationAnchor(item.Id, item.Pixel, anchor));
            var x = Round(anchor.X);
            var y = Round(anchor.Y);
            if (item.DrawMark)
            {
                DrawDottedCircle(pixels, width, height, x, y, options.MarkRadius, options.MarkerValue);
            }

            if (options.DrawLabels && item.DrawLabel && options.MaximumLabelCharacters > 0)
            {
                DrawLabel(pixels, width, height, x + options.MarkRadius + 2, y - 3 * options.LabelScale,
                    item.DisplayName.AsSpan(0, Math.Min(item.DisplayName.Length, options.MaximumLabelCharacters)),
                    options.MarkValue, options.LabelScale);
            }
        }

        return new AnnotationResult(pixels, anchors);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void SetPixel(byte[] pixels, int width, int height, int x, int y, byte value)
    {
        if ((uint)x < (uint)width && (uint)y < (uint)height)
        {
            pixels[y * width + x] = value;
        }
    }

    private static void SetPixelMaximum(byte[] pixels, int width, int height, int x, int y, byte value)
    {
        if ((uint)x < (uint)width && (uint)y < (uint)height)
        {
            var index = y * width + x;
            pixels[index] = Math.Max(pixels[index], value);
        }
    }

    private static void DrawDottedCircle(
        byte[] pixels, int width, int height, int centerX, int centerY, int radius, byte value)
    {
        if (radius == 0)
        {
            SetPixelMaximum(pixels, width, height, centerX, centerY, value);
            return;
        }

        var pointCount = Math.Max(8, (int)Math.Round(Math.PI * radius));
        for (var point = 0; point < pointCount; point++)
        {
            var angle = 2 * Math.PI * point / pointCount;
            SetPixelMaximum(pixels, width, height,
                centerX + Round(radius * Math.Cos(angle)),
                centerY + Round(radius * Math.Sin(angle)), value);
        }
    }

    private static void DrawProjectionOverlay(
        byte[] pixels,
        int width,
        int height,
        PreviewTransform transform,
        ProjectedAnnotationOverlay overlay,
        AnnotationOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var center = transform.Apply(overlay.Center);
        if (options.DrawImageCircle)
        {
            DrawEllipse(pixels, width, height, center,
                Math.Abs(overlay.ImageCircleRadius * transform.ScaleX),
                Math.Abs(overlay.ImageCircleRadius * transform.ScaleY), options.ImageCircleValue, cancellationToken);
        }

        if (!options.DrawCardinalDirections)
        {
            return;
        }

        DrawCardinal(pixels, width, height, center, transform.Apply(overlay.North), 'N', options);
        DrawCardinal(pixels, width, height, center, transform.Apply(overlay.East), 'E', options);
        DrawCardinal(pixels, width, height, center, transform.Apply(overlay.South), 'S', options);
        DrawCardinal(pixels, width, height, center, transform.Apply(overlay.West), 'W', options);
    }

    private static void DrawEllipse(
        byte[] pixels,
        int width,
        int height,
        PixelPoint center,
        double radiusX,
        double radiusY,
        byte value,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
            !double.IsFinite(radiusX) || !double.IsFinite(radiusY) || radiusX <= 0 || radiusY <= 0)
        {
            return;
        }

        for (var x = 0; x < width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = (x - center.X) / radiusX;
            if (Math.Abs(normalized) <= 1)
            {
                var offset = radiusY * Math.Sqrt(Math.Max(0, 1 - normalized * normalized));
                SetPixelMaximumIfFinite(pixels, width, height, x, center.Y - offset, value);
                SetPixelMaximumIfFinite(pixels, width, height, x, center.Y + offset, value);
            }
        }

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = (y - center.Y) / radiusY;
            if (Math.Abs(normalized) <= 1)
            {
                var offset = radiusX * Math.Sqrt(Math.Max(0, 1 - normalized * normalized));
                SetPixelMaximumIfFinite(pixels, width, height, center.X - offset, y, value);
                SetPixelMaximumIfFinite(pixels, width, height, center.X + offset, y, value);
            }
        }
    }

    private static void SetPixelMaximumIfFinite(
        byte[] pixels, int width, int height, double x, double y, byte value)
    {
        if (double.IsFinite(x) && double.IsFinite(y) &&
            x is >= int.MinValue and <= int.MaxValue && y is >= int.MinValue and <= int.MaxValue)
        {
            SetPixelMaximum(pixels, width, height, Round(x), Round(y), value);
        }
    }

    private static void DrawCardinal(
        byte[] pixels, int width, int height, PixelPoint center, PixelPoint target, char label, AnnotationOptions options)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y))
        {
            return;
        }

        var scale = options.CardinalScale;
        var halfWidth = 3 * scale;
        var halfHeight = 4 * scale;
        var deltaX = target.X - center.X;
        var deltaY = target.Y - center.Y;
        var factor = 1d;
        if (deltaX < 0)
        {
            factor = Math.Min(factor, (halfWidth - center.X) / deltaX);
        }
        else if (deltaX > 0)
        {
            factor = Math.Min(factor, (width - halfWidth - 1 - center.X) / deltaX);
        }
        if (deltaY < 0)
        {
            factor = Math.Min(factor, (halfHeight - center.Y) / deltaY);
        }
        else if (deltaY > 0)
        {
            factor = Math.Min(factor, (height - halfHeight - 1 - center.Y) / deltaY);
        }

        factor = Math.Clamp(factor, 0, 1);
        var x = Round(center.X + deltaX * factor) - 2 * scale;
        var y = Round(center.Y + deltaY * factor) - 3 * scale;
        Span<char> text = stackalloc char[1];
        text[0] = label;
        DrawLabel(pixels, width, height, x, y, text, options.CardinalValue, scale);
    }

    private static void DrawMonoSegments(
        byte[] pixels,
        int width,
        int height,
        IEnumerable<ProjectedAnnotationSegment> segments,
        PreviewTransform transform,
        AnnotationOptions options,
        CancellationToken cancellationToken)
    {
        bool[]? mask = null;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTransformAndClip(segment, transform, width, height, out var from, out var to))
            {
                mask ??= new bool[checked(width * height)];
                DrawLine(from, to, options.ConstellationLineThickness,
                    (x, y) => SetMask(mask, width, height, x, y));
            }
        }
        if (mask is null)
        {
            return;
        }
        for (var index = 0; index < mask.Length; index++)
        {
            if (index % width == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (mask[index])
            {
                pixels[index] = Blend(pixels[index], options.ConstellationLineValue, options.ConstellationLineOpacity);
            }
        }
    }

    private static void DrawRgbSegments(
        byte[] pixels,
        int width,
        int height,
        IEnumerable<ProjectedAnnotationSegment> segments,
        PreviewTransform transform,
        AnnotationOptions options,
        CancellationToken cancellationToken)
    {
        bool[]? mask = null;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryTransformAndClip(segment, transform, width, height, out var from, out var to))
            {
                mask ??= new bool[checked(width * height)];
                DrawLine(from, to, options.ConstellationLineThickness,
                    (x, y) => SetMask(mask, width, height, x, y));
            }
        }
        if (mask is null)
        {
            return;
        }
        for (var pixel = 0; pixel < mask.Length; pixel++)
        {
            if (pixel % width == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (mask[pixel])
            {
                var index = pixel * 3;
                pixels[index] = Blend(pixels[index], options.ConstellationLineRed, options.ConstellationLineOpacity);
                pixels[index + 1] = Blend(pixels[index + 1], options.ConstellationLineGreen, options.ConstellationLineOpacity);
                pixels[index + 2] = Blend(pixels[index + 2], options.ConstellationLineBlue, options.ConstellationLineOpacity);
            }
        }
    }

    private static bool TryTransformAndClip(
        ProjectedAnnotationSegment segment,
        PreviewTransform transform,
        int width,
        int height,
        out PixelPoint from,
        out PixelPoint to)
    {
        from = transform.Apply(segment.FromPixel);
        to = transform.Apply(segment.ToPixel);
        if (!double.IsFinite(from.X) || !double.IsFinite(from.Y) ||
            !double.IsFinite(to.X) || !double.IsFinite(to.Y))
        {
            return false;
        }

        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var minimum = 0d;
        var maximum = 1d;
        if (!ClipBoundary(-deltaX, from.X, ref minimum, ref maximum) ||
            !ClipBoundary(deltaX, width - 1d - from.X, ref minimum, ref maximum) ||
            !ClipBoundary(-deltaY, from.Y, ref minimum, ref maximum) ||
            !ClipBoundary(deltaY, height - 1d - from.Y, ref minimum, ref maximum))
        {
            return false;
        }

        to = new PixelPoint(from.X + maximum * deltaX, from.Y + maximum * deltaY);
        from = new PixelPoint(from.X + minimum * deltaX, from.Y + minimum * deltaY);
        return true;
    }

    private static bool ClipBoundary(double direction, double distance, ref double minimum, ref double maximum)
    {
        if (Math.Abs(direction) < 1e-15)
        {
            return distance >= 0;
        }
        var ratio = distance / direction;
        if (direction < 0)
        {
            if (ratio > maximum) return false;
            minimum = Math.Max(minimum, ratio);
        }
        else
        {
            if (ratio < minimum) return false;
            maximum = Math.Min(maximum, ratio);
        }
        return minimum <= maximum;
    }

    private static void SetMask(bool[] mask, int width, int height, int x, int y)
    {
        if ((uint)x < (uint)width && (uint)y < (uint)height)
        {
            mask[y * width + x] = true;
        }
    }

    private static byte Blend(byte source, byte value, double opacity)
        => (byte)Math.Clamp(Round(source + (value - source) * opacity), byte.MinValue, byte.MaxValue);

    private static void DrawLine(
        PixelPoint from,
        PixelPoint to,
        int thickness,
        Action<int, int> setPixel)
    {
        var x0 = Round(from.X);
        var y0 = Round(from.Y);
        var x1 = Round(to.X);
        var y1 = Round(to.Y);
        var deltaX = Math.Abs(x1 - x0);
        var stepX = x0 < x1 ? 1 : -1;
        var deltaY = -Math.Abs(y1 - y0);
        var stepY = y0 < y1 ? 1 : -1;
        var error = deltaX + deltaY;
        while (true)
        {
            var minimumOffset = -(thickness - 1) / 2;
            var maximumOffset = thickness / 2;
            for (var offsetY = minimumOffset; offsetY <= maximumOffset; offsetY++)
            {
                for (var offsetX = minimumOffset; offsetX <= maximumOffset; offsetX++)
                {
                    setPixel(x0 + offsetX, y0 + offsetY);
                }
            }
            if (x0 == x1 && y0 == y1)
            {
                return;
            }

            var doubled = 2 * error;
            if (doubled >= deltaY)
            {
                error += deltaY;
                x0 += stepX;
            }

            if (doubled <= deltaX)
            {
                error += deltaX;
                y0 += stepY;
            }
        }
    }

    private static void DrawLabel(
        byte[] pixels, int width, int height, int x, int y, ReadOnlySpan<char> text, byte value, int scale)
    {
        for (var characterIndex = 0; characterIndex < text.Length; characterIndex++)
        {
            var rows = GlyphRows(char.ToUpperInvariant(text[characterIndex]));
            for (var row = 0; row < rows.Length; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    if ((rows[row] & (1 << (4 - column))) == 0)
                    {
                        continue;
                    }
                    for (var scaleY = 0; scaleY < scale; scaleY++)
                    {
                        for (var scaleX = 0; scaleX < scale; scaleX++)
                        {
                            SetPixel(pixels, width, height,
                                x + characterIndex * 6 * scale + column * scale + scaleX,
                                y + row * scale + scaleY, value);
                        }
                    }
                }
            }
        }
    }

    private static ReadOnlySpan<byte> GlyphRows(char value) => value switch
    {
        'A' => [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        'B' => [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
        'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        'D' => [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
        'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
        'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
        'G' => [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
        'H' => [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        'I' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x1F],
        'J' => [0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0E],
        'K' => [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
        'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
        'M' => [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
        'N' => [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
        'O' => [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        'P' => [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
        'Q' => [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
        'R' => [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
        'S' => [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
        'T' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
        'U' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        'V' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
        'W' => [0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A],
        'X' => [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
        'Y' => [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
        'Z' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
        '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        '1' => [0x04, 0x0C, 0x14, 0x04, 0x04, 0x04, 0x1F],
        '2' => [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
        '3' => [0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E],
        '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        '5' => [0x1F, 0x10, 0x10, 0x1E, 0x01, 0x01, 0x1E],
        '6' => [0x0E, 0x10, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x01, 0x0E],
        '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
        '.' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
        _ => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]
    };
}
