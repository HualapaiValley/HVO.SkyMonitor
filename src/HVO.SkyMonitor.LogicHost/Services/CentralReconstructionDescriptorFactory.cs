using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal static class CentralReconstructionDescriptorFactory
{
    public static ReconstructionDescriptor Create(CentralFrame frame, CentralArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ReconstructionState != CentralReconstructionState.Complete || frame.Timing is null ||
            frame.Control is null || artifact.Layout is null || artifact.Recipe is null ||
            frame.RigId is null || frame.CaptureSequence is null)
        {
            throw new InvalidOperationException("The central artifact is not reconstructable.");
        }

        var profiles = frame.Profiles.ToDictionary(profile => profile.Kind);
        using var options = JsonDocument.Parse(artifact.Recipe.OptionsJson);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(frame.AgentId, frame.RigId, frame.CaptureSequence.Value, frame.FrameId),
            new CaptureTimingDescriptor(
                frame.Timing.RequestedStartUtc,
                frame.Timing.ExposureStartedUtc,
                frame.Timing.ExposureEndedUtc,
                frame.Timing.ReadoutCompletedUtc,
                frame.Timing.DurableIngressUtc)
            {
                SetpointAppliedUtc = frame.Timing.SetpointAppliedUtc
            },
            new CaptureControlDescriptor(
                TimeSpan.FromTicks(frame.Control.RequestedExposureTicks),
                TimeSpan.FromTicks(frame.Control.EffectiveExposureTicks),
                frame.Control.RequestedGain,
                frame.Control.EffectiveGain,
                frame.Control.RequestedOffset,
                frame.Control.EffectiveOffset,
                frame.Control.TemperatureSetpointC,
                frame.Control.EffectiveTemperatureC),
            new CaptureProfileSet(
                ToIdentity(profiles[CentralProfileKind.Rig]),
                ToIdentity(profiles[CentralProfileKind.Calibration]),
                ToIdentity(profiles[CentralProfileKind.Mask]),
                ToIdentity(profiles[CentralProfileKind.Sensor]),
                ToIdentity(profiles[CentralProfileKind.Processing])),
            CreateLayout(artifact.Layout),
            new ArtifactDescriptor(
                artifact.ArtifactId,
                artifact.Role,
                artifact.SourceId!,
                artifact.Variant!,
                artifact.CreatedUtc!.Value,
                artifact.Sources.OrderBy(source => source.Ordinal).Select(source => source.SourceArtifactId).ToArray(),
                new RecipeIdentityDescriptor(
                    artifact.Recipe.Name,
                    artifact.Recipe.SemanticVersion,
                    artifact.Recipe.ImplementationVersion,
                    options.RootElement.Clone(),
                    artifact.Recipe.OptionsSha256),
                artifact.MediaType,
                artifact.ChecksumSha256));
        if (frame.CycleEvidenceJson is not null)
        {
            descriptor = descriptor with
            {
                CycleEvidence = JsonSerializer.Deserialize<CaptureCycleEvidence>(frame.CycleEvidenceJson)
            };
        }
        var location = frame.Location
            ?? throw new InvalidOperationException("The central frame location evidence was not loaded.");
        descriptor = descriptor with
        {
            Location = new CaptureLocationProvenance(
                location.LocationId,
                location.Version,
                location.Source,
                location.HorizontalAccuracyMeters,
                location.EffectiveFromUtc,
                location.EffectiveUntilUtc)
        };
        return descriptor;
    }

    internal static StructuredProcessingProductDescriptorV1 CreateStructured(CentralArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ReconstructionState != CentralReconstructionState.Complete ||
            artifact.StructuredProduct is not { } product)
        {
            throw new InvalidOperationException("The central structured product is not reconstructable.");
        }
        var descriptor = StructuredProcessingProductManifestJson.ParseDescriptor(product.DescriptorJson);
        var validation = descriptor.Validate();
        var persistedRecipe = artifact.Recipe;
        var expectedRecipe = descriptor.Artifact.Recipe;
        var persistedSources = artifact.Sources.OrderBy(static source => source.Ordinal).ToArray();
        if (!validation.IsValid || descriptor.Artifact.ArtifactId != artifact.ArtifactId ||
            descriptor.Artifact.ChecksumSha256 != artifact.ChecksumSha256 ||
            descriptor.Artifact.Role != artifact.Role || descriptor.Artifact.MediaType != artifact.MediaType ||
            descriptor.Artifact.SourceId != artifact.SourceId || descriptor.Artifact.Variant != artifact.Variant ||
            descriptor.Artifact.CreatedUtc != artifact.CreatedUtc || descriptor.ByteLength != artifact.ByteLength ||
            descriptor.OutputIdentitySha256 != product.OutputIdentitySha256 ||
            descriptor.ContentIdentitySha256 != product.ContentIdentitySha256 ||
            descriptor.Kind.ToString() != product.ProductKind ||
            descriptor.ProductSchemaVersion != product.ProductSchemaVersion ||
            descriptor.TotalIntegrationTicks != product.TotalIntegrationTicks ||
            JsonSerializer.Serialize(descriptor.Algorithms) != product.AlgorithmsJson ||
            JsonSerializer.Serialize(descriptor.Compatibility) != product.CompatibilityJson ||
            persistedRecipe is null || persistedRecipe.Name != expectedRecipe.Name ||
            persistedRecipe.SemanticVersion != expectedRecipe.SemanticVersion ||
            persistedRecipe.ImplementationVersion != expectedRecipe.ImplementationVersion ||
            !string.Equals(persistedRecipe.OptionsSha256, expectedRecipe.OptionsSha256, StringComparison.OrdinalIgnoreCase) ||
            persistedRecipe.OptionsJson != JsonSerializer.Serialize(CaptureContractJson.Canonicalize(expectedRecipe.Options)) ||
            persistedSources.Length != descriptor.Artifact.SourceArtifactIds.Count ||
            persistedSources.Where((source, ordinal) => source.Ordinal != ordinal ||
                source.SourceArtifactId != descriptor.Artifact.SourceArtifactIds[ordinal]).Any())
        {
            throw new InvalidDataException("The persisted structured product descriptor does not match its artifact.");
        }
        return descriptor;
    }

    internal static FrameLayoutDescriptor CreateLayout(CentralArtifactLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new FrameLayoutDescriptor(
            layout.Width,
            layout.Height,
            layout.StrideBytes,
            Enum.Parse<CameraPixelFormat>(layout.PixelFormat),
            Enum.Parse<FrameByteOrder>(layout.ByteOrder),
            layout.SampleDepthBits,
            layout.ContainerDepthBits,
            Enum.Parse<FrameSamplePacking>(layout.Packing),
            Enum.Parse<ColorFilterArrayPattern>(layout.CfaPattern),
            layout.BlackLevel,
            layout.WhiteLevel,
            layout.ByteLength)
        {
            StoredCodeTransform = layout.StoredCodeTransform is null
                ? null
                : Enum.Parse<FrameStoredCodeTransform>(layout.StoredCodeTransform),
            LevelCodeSpace = layout.LevelCodeSpace is null
                ? null
                : Enum.Parse<FrameLevelCodeSpace>(layout.LevelCodeSpace),
            Readout = layout.NativeWidth is null
                ? null
                : new FrameReadoutDescriptor(
                    layout.NativeWidth.Value,
                    layout.NativeHeight!.Value,
                    layout.RoiX!.Value,
                    layout.RoiY!.Value,
                    layout.RoiWidth!.Value,
                    layout.RoiHeight!.Value,
                    layout.BinX!.Value,
                    layout.BinY!.Value,
                    Enum.Parse<FrameBinningAlgorithm>(layout.BinningAlgorithm!),
                    layout.CfaOriginX,
                    layout.CfaOriginY)
        };
    }

    internal static string ComputeOutputIdentity(CentralArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Recipe is not { } recipe || string.IsNullOrWhiteSpace(artifact.Variant))
        {
            throw new InvalidOperationException("The central artifact output identity is not reconstructable.");
        }
        using var options = JsonDocument.Parse(recipe.OptionsJson);
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(new RecipeIdentityDescriptor(
            recipe.Name,
            recipe.SemanticVersion,
            recipe.ImplementationVersion,
            options.RootElement.Clone(),
            recipe.OptionsSha256));
        return ProcessingIdentity.CreateOutputIdentity(
            artifact.Role,
            artifact.Variant,
            recipeIdentity.IdentitySha256,
            artifact.Sources.OrderBy(static source => source.Ordinal)
                .Select(static source => source.SourceArtifactId)
                .ToArray());
    }

    private static ProfileIdentityDescriptor ToIdentity(CentralCaptureProfile profile)
        => new(profile.Name, profile.Version, profile.Sha256);
}
