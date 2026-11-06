using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ArtifactIngestService
{
    internal static (string Schema, string Identity, IReadOnlyList<StructuredSourceFact> Sources) ValidateAstrometryPayload(
        StructuredProcessingProductDescriptorV1 descriptor, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        try
        {
            var raw = descriptor.SourceCapture;
            var sources = descriptor.Artifact.SourceArtifactIds;
            if (descriptor.Artifact.MediaType == MeasuredAstrometryJson.ContextMediaType)
            {
                var context = MeasuredAstrometryJson.ParseContext(payload);
                var input = MeasuredAstrometryJson.ContextInput(context);
                ValidateAstrometryFrame(raw, context.Frame, context.ReadoutIdentitySha256);
                var expectedProducer = ProcessingIdentity.CreateRecipeIdentity(new(RetainedAstrometryProducts.ContextRecipe,
                    "1.0.0", RetainedAstrometryProducts.Version, ProcessingOperationKind.Analyzer),
                    System.Text.Json.JsonSerializer.SerializeToElement(new { contextIdentitySha256 = input.IdentitySha256 }));
                var prior = context.PreviousAccepted?.Frame.SourceArtifactId;
                if (sources.Count != (prior is not null && prior != raw.Artifact.ArtifactId ? 4 : 3) ||
                    sources[0] != raw.Artifact.ArtifactId || sources[1] != context.MeasuredArtifactId ||
                    sources.Count == 4 && sources[3] != prior || descriptor.ContentIdentitySha256 != input.IdentitySha256 ||
                    ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256 != expectedProducer.IdentitySha256)
                    throw new ArgumentException("Context lineage mismatch.");
                return (context.SchemaVersion, input.IdentitySha256!, sources.Select((_, i) => i switch
                {
                    0 => new StructuredSourceFact(FrameArtifactRole.Raw, raw.Artifact.Variant,
                        ProcessingIdentity.CreateRecipeIdentity(raw.Artifact.Recipe).IdentitySha256),
                    1 => new StructuredSourceFact(FrameArtifactRole.Metadata, RecipeIdentitySha256: context.MeasuredRecipeIdentitySha256),
                    3 => new StructuredSourceFact(FrameArtifactRole.Raw),
                    _ => new StructuredSourceFact(FrameArtifactRole.Metadata)
                }).ToArray());
            }
            if (descriptor.Artifact.MediaType != MeasuredAstrometryJson.MediaType) throw new ArgumentException("Unsupported astrometry media type.");
            var solution = MeasuredAstrometryJson.Parse(payload);
            ValidateAstrometryFrame(raw, solution.Assessment.Frame, solution.ReadoutIdentitySha256);
            var producer = descriptor.Artifact.Recipe;
            if (sources.Count != 4 || sources[0] != raw.Artifact.ArtifactId || sources[1] != solution.MeasuredArtifactId ||
                !BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.MeasuredAstrometry, out var definition) ||
                producer.Name != definition!.Name || producer.SemanticVersion != definition.SemanticVersion ||
                producer.ImplementationVersion != definition.ImplementationVersion ||
                !string.Equals(solution.RecipeIdentitySha256, ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Solution lineage mismatch.");
            return (solution.SchemaVersion, solution.SolutionIdentitySha256,
                [new(FrameArtifactRole.Raw, raw.Artifact.Variant, ProcessingIdentity.CreateRecipeIdentity(raw.Artifact.Recipe).IdentitySha256),
                    new(FrameArtifactRole.Metadata, RecipeIdentitySha256: solution.MeasuredRecipeIdentitySha256),
                    new(FrameArtifactRole.Metadata), new(FrameArtifactRole.Metadata)]);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new ArtifactIntegrityException("Astrometry payload or capture lineage does not match its manifest.", exception);
        }
    }

    private static void ValidateAstrometryFrame(ReconstructionDescriptor raw, AstrometricFrameContext frame, string readoutIdentity)
    {
        if (frame.CaptureId != raw.Capture.CaptureId || frame.SourceArtifactId != raw.Artifact.ArtifactId ||
            !string.Equals(frame.SourceDescriptorSha256, CaptureContractJson.ComputeDescriptorSha256(raw), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(frame.SourcePayloadSha256, raw.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase) ||
            frame.ExposureStartUtc != raw.Timing.ExposureStartedUtc || frame.ExposureEndUtc != ProcessingArtifact.ResolveObservationEndedUtc(
                raw.Timing.ExposureStartedUtc, raw.Timing.ExposureEndedUtc, raw.Controls.EffectiveExposure) ||
            !string.Equals(readoutIdentity, MeasuredAstrometryJson.ComputeReadoutIdentity(raw.Layout), StringComparison.OrdinalIgnoreCase) ||
            raw.Location is { } location && !string.Equals(location.IdentitySha256, frame.ObserverIdentitySha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Astrometry raw descriptor mismatch.");
    }
}
