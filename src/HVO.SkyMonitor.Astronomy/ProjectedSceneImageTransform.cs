namespace HVO.SkyMonitor.Astronomy;

/// <summary>Transformed emitted-image geometry detached from optical projection metadata.</summary>
public sealed record ProjectedSceneGeometrySnapshot(
    IReadOnlyList<ProjectedCelestialObject> Objects,
    IReadOnlyList<ProjectedConstellationSegment> Segments);

/// <summary>Applies the canonical emitted-image transform exactly once to continuous pixel-edge coordinates.</summary>
public static class ProjectedSceneImageTransform
{
    public static PixelPoint Apply(ProjectedSceneImageTransformV1 transform, PixelPoint source)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Validate(transform, transform.SourceWidthPixels, transform.SourceHeightPixels);
        if (!double.IsFinite(source.X) || !double.IsFinite(source.Y) || !IsIdentity(transform) &&
            (source.X < transform.CropX || source.X > transform.CropX + transform.CropWidth ||
             source.Y < transform.CropY || source.Y > transform.CropY + transform.CropHeight))
            throw new ArgumentOutOfRangeException(nameof(source), "Source point is outside the configured crop.");
        var width = (double)transform.CropWidth / transform.BinX;
        var height = (double)transform.CropHeight / transform.BinY;
        var x = (source.X - transform.CropX) / transform.BinX;
        var y = (source.Y - transform.CropY) / transform.BinY;
        if (transform.HorizontalMirror) x = width - x;
        if (transform.VerticalMirror) y = height - y;
        return transform.Rotation switch
        {
            ProjectedSceneQuarterRotation.Degrees0 => new(x, y),
            ProjectedSceneQuarterRotation.Degrees90 => new(height - y, x),
            ProjectedSceneQuarterRotation.Degrees180 => new(width - x, height - y),
            ProjectedSceneQuarterRotation.Degrees270 => new(y, width - x),
            _ => throw new ArgumentOutOfRangeException(nameof(transform))
        };
    }

    public static PixelPoint Inverse(ProjectedSceneImageTransformV1 transform, PixelPoint output)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Validate(transform, transform.SourceWidthPixels, transform.SourceHeightPixels);
        if (!double.IsFinite(output.X) || !double.IsFinite(output.Y) || !IsIdentity(transform) &&
            (output.X < 0 || output.X > transform.OutputWidthPixels || output.Y < 0 || output.Y > transform.OutputHeightPixels))
            throw new ArgumentOutOfRangeException(nameof(output));
        var width = (double)transform.CropWidth / transform.BinX;
        var height = (double)transform.CropHeight / transform.BinY;
        var (x, y) = transform.Rotation switch
        {
            ProjectedSceneQuarterRotation.Degrees0 => (output.X, output.Y),
            ProjectedSceneQuarterRotation.Degrees90 => (output.Y, height - output.X),
            ProjectedSceneQuarterRotation.Degrees180 => (width - output.X, height - output.Y),
            ProjectedSceneQuarterRotation.Degrees270 => (width - output.Y, output.X),
            _ => throw new ArgumentOutOfRangeException(nameof(transform))
        };
        if (transform.HorizontalMirror) x = width - x;
        if (transform.VerticalMirror) y = height - y;
        return new(transform.CropX + x * transform.BinX, transform.CropY + y * transform.BinY);
    }

    public static ProjectedSceneGeometrySnapshot CreateGeometrySnapshot(
        VisibleScene source,
        ProjectedSceneImageTransformV1 transform)
    {
        ArgumentNullException.ThrowIfNull(source);
        Validate(transform, source.Request.Projection.WidthPixels, source.Request.Projection.HeightPixels);
        var objects = source.Objects
            .Where(item => ContainsCrop(transform, item.Pixel))
            .Select(item => item with { Pixel = Apply(transform, item.Pixel) })
            .ToArray();
        var segments = new List<ProjectedConstellationSegment>();
        foreach (var group in source.Segments.GroupBy(
            static segment => (segment.ConstellationId, segment.FromObjectId, segment.ToObjectId)))
        {
            var partIndex = 0;
            foreach (var segment in group.OrderBy(static segment => segment.PartIndex))
            {
                if (!TryClipToCrop(transform, segment.FromPixel, segment.ToPixel, out var from, out var to) ||
                    DistanceSquared(from, to) <= 1e-18)
                    continue;
                segments.Add(segment with
                {
                    FromPixel = Apply(transform, from),
                    ToPixel = Apply(transform, to),
                    PartIndex = partIndex++
                });
            }
        }
        return new(objects, segments);
    }

    public static void Validate(ProjectedSceneImageTransformV1 transform, int sourceWidthPixels, int sourceHeightPixels)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.SchemaVersion != ProjectedSceneImageTransformV1.CurrentSchemaVersion ||
            transform.SourceWidthPixels != sourceWidthPixels || transform.SourceHeightPixels != sourceHeightPixels ||
            transform.SourceWidthPixels is < 1 or > ProjectedSceneJson.MaximumDimensionPixels ||
            transform.SourceHeightPixels is < 1 or > ProjectedSceneJson.MaximumDimensionPixels ||
            (long)transform.SourceWidthPixels * transform.SourceHeightPixels > ProjectedSceneJson.MaximumPixelArea ||
            transform.CropX < 0 || transform.CropY < 0 || transform.CropWidth < 1 || transform.CropHeight < 1 ||
            (long)transform.CropX + transform.CropWidth > transform.SourceWidthPixels ||
            (long)transform.CropY + transform.CropHeight > transform.SourceHeightPixels ||
            transform.BinX < 1 || transform.BinY < 1 || transform.CropWidth % transform.BinX != 0 ||
            transform.CropHeight % transform.BinY != 0 || !Enum.IsDefined(transform.Rotation))
            throw new ArgumentException("Emitted-image transform is invalid.", nameof(transform));
        var binnedWidth = transform.CropWidth / transform.BinX;
        var binnedHeight = transform.CropHeight / transform.BinY;
        var swapsAxes = transform.Rotation is ProjectedSceneQuarterRotation.Degrees90 or ProjectedSceneQuarterRotation.Degrees270;
        var expectedWidth = swapsAxes ? binnedHeight : binnedWidth;
        var expectedHeight = swapsAxes ? binnedWidth : binnedHeight;
        if (transform.OutputWidthPixels != expectedWidth || transform.OutputHeightPixels != expectedHeight ||
            transform.OutputWidthPixels > ProjectedSceneJson.MaximumDimensionPixels ||
            transform.OutputHeightPixels > ProjectedSceneJson.MaximumDimensionPixels ||
            (long)transform.OutputWidthPixels * transform.OutputHeightPixels > ProjectedSceneJson.MaximumPixelArea)
            throw new ArgumentException("Emitted-image output dimensions are inconsistent.", nameof(transform));
    }

    internal static bool IsIdentity(ProjectedSceneImageTransformV1 transform) =>
        transform.CropX == 0 && transform.CropY == 0 &&
        transform.CropWidth == transform.SourceWidthPixels && transform.CropHeight == transform.SourceHeightPixels &&
        transform.BinX == 1 && transform.BinY == 1 && !transform.HorizontalMirror && !transform.VerticalMirror &&
        transform.Rotation == ProjectedSceneQuarterRotation.Degrees0;

    private static bool ContainsCrop(ProjectedSceneImageTransformV1 transform, PixelPoint point) =>
        point.X >= transform.CropX && point.X <= transform.CropX + transform.CropWidth &&
        point.Y >= transform.CropY && point.Y <= transform.CropY + transform.CropHeight;

    private static bool TryClipToCrop(
        ProjectedSceneImageTransformV1 transform,
        PixelPoint from,
        PixelPoint to,
        out PixelPoint clippedFrom,
        out PixelPoint clippedTo)
    {
        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var minimum = 0d;
        var maximum = 1d;
        if (!VisibleSceneBuilder.ClipBoundary(-deltaX, from.X - transform.CropX, ref minimum, ref maximum) ||
            !VisibleSceneBuilder.ClipBoundary(deltaX, transform.CropX + transform.CropWidth - from.X, ref minimum, ref maximum) ||
            !VisibleSceneBuilder.ClipBoundary(-deltaY, from.Y - transform.CropY, ref minimum, ref maximum) ||
            !VisibleSceneBuilder.ClipBoundary(deltaY, transform.CropY + transform.CropHeight - from.Y, ref minimum, ref maximum))
        {
            clippedFrom = default;
            clippedTo = default;
            return false;
        }
        clippedFrom = new(from.X + minimum * deltaX, from.Y + minimum * deltaY);
        clippedTo = new(from.X + maximum * deltaX, from.Y + maximum * deltaY);
        return minimum <= maximum;
    }

    private static double DistanceSquared(PixelPoint left, PixelPoint right) =>
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2);
}
