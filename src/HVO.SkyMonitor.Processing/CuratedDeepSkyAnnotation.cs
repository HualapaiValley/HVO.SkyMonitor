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
}
