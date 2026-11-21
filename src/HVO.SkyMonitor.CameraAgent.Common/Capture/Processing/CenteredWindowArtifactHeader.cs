using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// This projection is independent of storage paths and file contents. The same
// header is used to freeze custody and to attach authenticated payload bytes.
internal static class CenteredWindowArtifactHeader
{
    internal static ProcessingArtifact FromRaw(ReconstructionDescriptor descriptor) =>
        Create(descriptor.Capture, descriptor.Artifact, descriptor,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor), descriptor.Controls.EffectiveExposure,
            descriptor.Capture.CaptureSequence, ProcessingProductKind.PixelData, null, null,
            descriptor.Timing.ExposureEndedUtc);

    internal static ProcessingArtifact FromOutput(DurableProcessingOutput output)
    {
        var descriptor = output.Descriptor;
        var ended = descriptor is null ? (DateTimeOffset?)null : ProcessingArtifact.ResolveObservationEndedUtc(
            descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc, output.TotalIntegration);
        var header = Create(output.Capture, output.Artifact, descriptor, output.Compatibility, output.TotalIntegration,
            output.CaptureSequence, output.ProductManifest?.Kind ?? ProcessingProductKind.PixelData,
            output.ProductSchemaVersion, output.ContentIdentitySha256, ended);
        if (header.RecipeIdentitySha256 != output.RecipeIdentitySha256)
            throw new InvalidDataException("The committed centered producer differs from its descriptor.");
        return header;
    }

    private static ProcessingArtifact Create(CaptureIdentityDescriptor capture, ArtifactDescriptor artifact,
        ReconstructionDescriptor? descriptor, ProcessingCompatibilityIdentity compatibility, TimeSpan integration,
        long sequence, ProcessingProductKind kind, string? schema, string? content, DateTimeOffset? ended) =>
        new(artifact.ArtifactId, artifact.Role, artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256, artifact.MediaType, descriptor?.Layout,
            ReadOnlyMemory<byte>.Empty, artifact.CreatedUtc, integration, compatibility, sequence, artifact.SourceArtifactIds,
            descriptor?.Timing.ExposureStartedUtc, ended, descriptor is null ? null : new(
                descriptor.Controls.EffectiveGain, descriptor.Controls.EffectiveOffset, descriptor.Controls.EffectiveTemperatureC))
        {
            CaptureId = capture.CaptureId,
            DescriptorIdentitySha256 = descriptor is null ? null : CaptureContractJson.ComputeDescriptorSha256(descriptor),
            ContentIdentitySha256 = content,
            RecipeDescriptor = artifact.Recipe,
            ProductKind = kind,
            SchemaVersion = schema
        };
}
