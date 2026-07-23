using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal static class GalleryEvidenceClassifier
{
    internal static GalleryEvidenceOrigin Classify(ArtifactManifestV2 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var sourceId = manifest.Descriptor.Artifact.SourceId;
        if (manifest.Scene is not null && string.Equals(sourceId, "VirtualSky", StringComparison.Ordinal))
        {
            return GalleryEvidenceOrigin.Simulated;
        }
        if (string.Equals(sourceId, "RandomImage", StringComparison.Ordinal))
        {
            return GalleryEvidenceOrigin.DeveloperFixture;
        }
        return GalleryEvidenceOrigin.Unknown;
    }
}
