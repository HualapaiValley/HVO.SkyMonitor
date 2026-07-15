using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class DerivativeDescriptorFactory
{
    internal static ReconstructionDescriptor Create(
        ReconstructionDescriptor raw,
        Guid artifactId,
        string sourceId,
        ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(product);
        var layout = product.Layout
            ?? throw new InvalidOperationException("CameraAgent frame derivatives require a reconstructable frame layout.");
        return new ReconstructionDescriptor(
            raw.Capture,
            raw.Timing,
            raw.Controls,
            raw.Profiles,
            layout,
            new ArtifactDescriptor(
                artifactId,
                product.Role,
                sourceId,
                product.Variant,
                raw.Timing.ReadoutCompletedUtc,
                product.SourceArtifactIds,
                product.Recipe.Descriptor,
                product.MediaType,
                product.ChecksumSha256))
        {
            CycleEvidence = raw.CycleEvidence
        };
    }
}
