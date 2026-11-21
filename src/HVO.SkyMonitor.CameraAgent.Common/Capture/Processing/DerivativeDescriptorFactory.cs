using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal static class DerivativeDescriptorFactory
{
    internal static async ValueTask<ReconstructionDescriptor> CreateForStorageAsync(
        string storageRoot, ReconstructionDescriptor raw, FrameArtifact artifact, string sourceId,
        ProcessingProduct product, CancellationToken cancellationToken)
    {
        var candidate = Create(raw, artifact.ArtifactId, sourceId, product);
        if (raw.CenteredAcquisition is not { Module.RequestedSetpoint: null })
            return candidate;

        var root = Path.GetFullPath(storageRoot);
        var payloadPath = FileSystemFrameStorageService.GetPayloadPath(root, artifact.Frame.TimestampUtc.ToUniversalTime(),
            artifact.ArtifactId, artifact.Role);
        var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
        if (!File.Exists(sidecarPath))
            return candidate;

        // An older centered derivative may already have an immutable descriptor without acquisition
        // evidence, including when only its staged files survived before SQLite settlement.
        RawIngressFileStore.EnsureNoSymbolicLinks(root, sidecarPath);
        var retained = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(sidecarPath, cancellationToken)
            .ConfigureAwait(false)).Document?.Manifest;
        if (retained?.Descriptor is { CenteredAcquisition: null } original &&
            string.Equals(retained.IdempotencyKey,
                CaptureContractJson.ComputeDescriptorSha256(candidate with { CenteredAcquisition = null }),
                StringComparison.Ordinal))
        {
            return original;
        }

        return candidate;
    }

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
            CenteredAcquisition = raw.CenteredAcquisition,
            Location = raw.Location
        };
    }
}
