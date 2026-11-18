using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Conservative presentation filtering; original scenes and raw evidence remain unchanged.</summary>
public static class HorizonObstructionAnnotation
{
    public static bool IsPartClear(ResolvedFootprintPart part, Func<PixelPoint, bool> clear,
        Func<PixelPoint, PixelPoint, bool> segmentClear)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(clear);
        ArgumentNullException.ThrowIfNull(segmentClear);
        if (!part.Points.All(clear)) return false;
        var count = part.Closed ? part.Points.Count : part.Points.Count - 1;
        for (var i = 0; i < count; i++)
            if (!segmentClear(part.Points[i], part.Points[(i + 1) % part.Points.Count])) return false;
        return true;
    }

    public static (IReadOnlyList<ProjectedAnnotationObject> Objects, IReadOnlyList<ProjectedAnnotationSegment> Segments) Filter(
        HorizonObstructionSceneEligibility eligibility,
        IReadOnlyList<ProjectedAnnotationObject> objects, IReadOnlyList<ProjectedAnnotationSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(segments);
        if (objects.Count > ProjectedSceneJson.MaximumObjectCount || segments.Count > ProjectedSceneJson.MaximumSegmentCount)
            throw new ArgumentOutOfRangeException(nameof(objects));
        return (objects.Where(item => eligibility.IsClear(item.Pixel) &&
                (item.FootprintParts is null || item.FootprintParts.All(part => IsPartClear(part, eligibility.IsClear, eligibility.IsSegmentClear)))).ToArray(),
            segments.Where(segment => eligibility.IsSegmentClear(segment.FromPixel, segment.ToPixel)).ToArray());
    }

    public static (IReadOnlyList<ProjectedAnnotationObject> Objects, IReadOnlyList<ProjectedAnnotationSegment> Segments) Filter(
        HorizonObstructionEvaluator evaluator, ProjectedSceneImageTransformV1 transform,
        IReadOnlyList<ProjectedAnnotationObject> objects, IReadOnlyList<ProjectedAnnotationSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(segments);
        if (objects.Count > ProjectedSceneJson.MaximumObjectCount || segments.Count > ProjectedSceneJson.MaximumSegmentCount)
            throw new ArgumentOutOfRangeException(nameof(objects));
        ProjectedSceneImageTransform.Validate(transform, evaluator.Projection.WidthPixels, evaluator.Projection.HeightPixels);
        if (!evaluator.IsCompatible) return ([], []);
        var projector = ProjectorFactory.Create(evaluator.Projection);
        AltAzPoint? Direction(PixelPoint pixel)
        {
            if (pixel.X < 0 || pixel.Y < 0 || pixel.X > transform.OutputWidthPixels || pixel.Y > transform.OutputHeightPixels)
                return null;
            return projector.Unproject(ProjectedSceneImageTransform.Inverse(transform, pixel));
        }
        bool Clear(PixelPoint pixel) => Direction(pixel) is { } direction &&
            evaluator.Evaluate(direction) == HorizonObstructionEligibility.Clear;
        bool SegmentClear(PixelPoint from, PixelPoint to) => Direction(from) is { } a && Direction(to) is { } b &&
            evaluator.IsSegmentClear(a, b);
        return (objects.Where(item => Clear(item.Pixel) &&
                (item.FootprintParts is null || item.FootprintParts.All(part => IsPartClear(part, Clear, SegmentClear)))).ToArray(),
            segments.Where(segment => SegmentClear(segment.FromPixel, segment.ToPixel)).ToArray());
    }
}
