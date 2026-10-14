using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Consumes the already source-bound retained scene; never rebuilds capture geometry.</summary>
internal static class CuratedDeepSkyAnnotation
{
    public static ProjectedSceneV1? FindScene(ProcessingArtifact primary, IEnumerable<ProcessingArtifact?> auxiliarySources)
    {
        var sources = auxiliarySources.Where(static source => source?.SchemaVersion == ProjectedSceneV1.DeepSkySchemaVersion).ToArray();
        if (sources.Length == 0) return null;
        if (sources.Length != 1) throw new InvalidDataException("One source-bound curated scene is required.");
        var source = sources[0]!;
        var scene = ProjectedSceneJson.Parse(source.Payload).Scene
            ?? throw new InvalidDataException("Curated scene is invalid.");
        if (source.Role != FrameArtifactRole.Metadata ||
            source.ContentIdentitySha256 != scene.SceneIdentitySha256 ||
            primary.CaptureId != scene.Source.CaptureId)
            throw new InvalidDataException("Curated scene does not match the annotated capture.");
        return scene;
    }

    public static IEnumerable<ProcessingAlgorithmIdentity> Algorithms(ProjectedSceneV1? scene) => scene is null
        ? []
        : [new("curated-dso-overlay", PresentationDeepSkyLayerProducer.ProducerVersion),
           new("curated-dso-compositor", PresentationLayerCompositor.AlgorithmVersion)];

    public static int LegendBaseline(MetadataCornerOverlay? metadata) => metadata is { TopLeft.Count: > 0 }
        ? metadata.Inset + metadata.TopLeft.Count * 7 * metadata.Scale +
            (metadata.TopLeft.Count - 1) * metadata.LineSpacing + 10
        : 4;

    // The legacy renderer uses 6x7 bitmap labels at these exact anchors. Reserve them without changing its output.
    public static IReadOnlyList<ResolvedFootprintBounds> ReservedLabels(IReadOnlyList<ProjectedAnnotationObject> objects,
        PreviewTransform transform, AnnotationOptions options)
    {
        if (!options.DrawLabels || options.MaximumLabelCharacters == 0) return [];
        return objects.Where(static item => item.DrawLabel).Select(item =>
        {
            var anchor = transform.Apply(item.Pixel);
            var roundedX = Math.Round(anchor.X, MidpointRounding.AwayFromZero);
            var clearance = options.MarkRadius;
            if (item.FootprintParts is { Count: > 0 } parts)
            {
                var points = parts.SelectMany(static part => part.Points).Select(point =>
                {
                    var pixel = transform.Apply(point);
                    var dx = pixel.X - anchor.X;
                    var dy = pixel.Y - anchor.Y;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    var factor = length > 0 ? (length + AnnotationRenderer.ResolvedFootprintPaddingPixels) / length : 1;
                    return new PixelPoint(anchor.X + dx * factor, anchor.Y + dy * factor);
                }).ToArray();
                if (points.Length > 0 && points.All(static point => double.IsFinite(point.X) && double.IsFinite(point.Y)))
                {
                    var minX = points.Min(static point => point.X);
                    var minY = points.Min(static point => point.Y);
                    var maxX = points.Max(static point => point.X);
                    var maxY = points.Max(static point => point.Y);
                    if (Math.Max(maxX - minX, maxY - minY) / 2 > options.MarkRadius)
                        clearance = Math.Max(clearance, (int)Math.Ceiling(maxX - roundedX));
                }
            }
            var x = roundedX + clearance + 2;
            var y = Math.Round(anchor.Y, MidpointRounding.AwayFromZero) - 3 * options.LabelScale;
            return new ResolvedFootprintBounds(x - 1, y - 1,
                x + Math.Min(item.DisplayName.Length, options.MaximumLabelCharacters) * 6 * options.LabelScale,
                y + 7 * options.LabelScale + 1);
        }).ToArray();
    }
}
