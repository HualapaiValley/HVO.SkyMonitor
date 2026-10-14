using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public static class CameraAgentCombinedSpanProjector
{
    public static TimeSpan? Project(CameraAgentCombinedLineage lineage, CameraAgentProductDetail product,
        IReadOnlyList<CameraAgentGalleryCapture> captures, Guid endpointCaptureId)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(captures);
        if (product.Product.ArtifactId != lineage.ArtifactId || product.Product.CaptureId != endpointCaptureId ||
            product.SourcesTruncated || lineage.SourceCount is < 1 or > 8 ||
            lineage.SourceCount != lineage.SourceArtifactIds.Count || product.Product.SourceCount != lineage.SourceCount ||
            product.Sources.Count != lineage.SourceCount || captures.Count != lineage.SourceCount ||
            lineage.SourceArtifactIds.Distinct().Count() != lineage.SourceCount)
            return null;
        DateTimeOffset? first = null, previous = null;
        long? previousSequence = null;
        for (var index = 0; index < lineage.SourceCount; index++)
        {
            var source = product.Sources[index];
            var capture = captures[index];
            if (source.Ordinal != index || source.ArtifactId != lineage.SourceArtifactIds[index] ||
                source.Role is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated) ||
                source.CaptureId != capture.CaptureId || source.CaptureSequence != capture.CaptureSequence ||
                capture.Detail is not { EvidenceAvailability: "Available", Timing: { } timing } ||
                timing.ExposureStartedUtc != capture.ExposureStartedUtc ||
                previous is { } prior && timing.ExposureStartedUtc < prior ||
                previousSequence is { } sequence && capture.CaptureSequence <= sequence)
                return null;
            first ??= timing.ExposureStartedUtc;
            previous = timing.ExposureStartedUtc;
            previousSequence = capture.CaptureSequence;
        }
        return captures[^1].CaptureId == endpointCaptureId ? previous - first : null;
    }
}
