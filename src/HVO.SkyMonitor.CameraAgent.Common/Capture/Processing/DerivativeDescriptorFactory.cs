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
        var referenceCalibration = string.Equals(
            product.Recipe.Descriptor.Name,
            BuiltInProcessingRecipes.ReferenceCalibration,
            StringComparison.Ordinal);
        return new ReconstructionDescriptor(
            raw.Capture,
            raw.Timing,
            raw.Controls,
            raw.Profiles with
            {
                Calibration = referenceCalibration
                    ? new ProfileIdentityDescriptor(
                        "reference-calibration-profile",
                        ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                        product.Compatibility.Calibration)
                    : raw.Profiles.Calibration with { Sha256 = product.Compatibility.Calibration },
                Mask = referenceCalibration
                    ? new ProfileIdentityDescriptor("calibration-defect-mask", "1.0.0", product.Compatibility.Mask)
                    : raw.Profiles.Mask with { Sha256 = product.Compatibility.Mask },
                Processing = raw.Profiles.Processing with { Sha256 = product.Compatibility.ProcessingProfile }
            },
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
            CycleEvidence = raw.CycleEvidence,
            Location = raw.Location
        };
    }
}
