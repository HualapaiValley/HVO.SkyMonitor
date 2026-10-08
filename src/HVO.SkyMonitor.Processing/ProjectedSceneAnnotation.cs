using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Maps a canonical projected scene to annotation objects, so every host annotating the same scene artifact draws
/// the same marks, outlines and labels.
/// </summary>
public static class ProjectedSceneAnnotation
{
    /// <summary>
    /// Returns one annotation object per scene object, carrying its resolved outline when one exists, followed by one
    /// per resolved footprint whose centre object the scene omits. A disc whose centre is below the horizon or outside
    /// the crop is still visible through its limb, so it keeps its outline and label rather than disappearing.
    /// </summary>
    public static IReadOnlyList<ProjectedAnnotationObject> CreateObjects(ProjectedSceneV1 scene, double maximumLabelMagnitude)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var footprints = scene.ResolvedFootprints ?? [];
        var parts = footprints.ToDictionary(static item => item.Id, static item => item.Parts, StringComparer.Ordinal);
        var objects = scene.Objects.Select(item =>
        {
            var annotate = IsNamed(item.Id, item.DisplayName) &&
                (item.Kind == CelestialObjectKind.SolarSystemBody || item.Magnitude <= maximumLabelMagnitude);
            return new ProjectedAnnotationObject(item.Id, item.DisplayName, item.Pixel, annotate, annotate,
                parts.GetValueOrDefault(item.Id));
        }).ToList();
        if (footprints.Count == 0) return objects;
        var present = scene.Objects.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var footprint in footprints.Where(item => !present.Contains(item.Id)))
        {
            var annotate = IsNamed(footprint.Id, footprint.DisplayName);
            objects.Add(new ProjectedAnnotationObject(footprint.Id, footprint.DisplayName,
                FootprintAnchor(footprint, scene.ImageTransform), annotate, annotate, footprint.Parts));
        }
        return objects;
    }

    /// <summary>
    /// The anchor of a footprint without a centre object: its projected centre while that lies in the emitted image,
    /// so the outline is padded along true limb radii, otherwise the centre of its visible bounds, so the label stays
    /// beside the part of the disc that is drawn.
    /// </summary>
    public static PixelPoint FootprintAnchor(ProjectedResolvedFootprint footprint, ProjectedSceneImageTransformV1 transform)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        ArgumentNullException.ThrowIfNull(transform);
        if (footprint.CenterPixel is { } center && double.IsFinite(center.X) && double.IsFinite(center.Y) &&
            center.X >= 0 && center.X <= transform.OutputWidthPixels &&
            center.Y >= 0 && center.Y <= transform.OutputHeightPixels)
            return center;
        return new PixelPoint((footprint.Bounds.MinX + footprint.Bounds.MaxX) / 2,
            (footprint.Bounds.MinY + footprint.Bounds.MaxY) / 2);
    }

    private static bool IsNamed(string id, string displayName) =>
        !string.IsNullOrWhiteSpace(displayName) && !string.Equals(id, displayName, StringComparison.Ordinal);
}
