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
    /// the crop is still visible through its limb, so it keeps its outline and label rather than disappearing. Deep-sky
    /// footprints belong to the separate deep-sky layer and are never drawn here.
    /// </summary>
    public static IReadOnlyList<ProjectedAnnotationObject> CreateObjects(ProjectedSceneV1 scene, double maximumLabelMagnitude)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var footprints = SolarSystemFootprints(scene);
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
    /// Returns the scene's solar-system footprints. A projected-scene-v3 scene also carries deep-sky ellipses, which
    /// only the opt-in deep-sky layer draws, so the star, Sun and Moon annotation of a v3 scene matches its v2 form.
    /// </summary>
    public static IReadOnlyList<ProjectedResolvedFootprint> SolarSystemFootprints(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.ResolvedFootprints is { } footprints
            ? footprints.Where(static item => item.SourceKind == ResolvedFootprintSourceKind.SolarSystemBody).ToArray()
            : [];
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
