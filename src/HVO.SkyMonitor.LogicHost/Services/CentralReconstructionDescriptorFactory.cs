using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;

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
            new FrameLayoutDescriptor(
                artifact.Layout.Width,
                artifact.Layout.Height,
                artifact.Layout.StrideBytes,
                Enum.Parse<CameraPixelFormat>(artifact.Layout.PixelFormat),
                Enum.Parse<FrameByteOrder>(artifact.Layout.ByteOrder),
                artifact.Layout.SampleDepthBits,
                artifact.Layout.ContainerDepthBits,
                Enum.Parse<FrameSamplePacking>(artifact.Layout.Packing),
                Enum.Parse<ColorFilterArrayPattern>(artifact.Layout.CfaPattern),
                artifact.Layout.BlackLevel,
                artifact.Layout.WhiteLevel,
                artifact.Layout.ByteLength)
            {
                StoredCodeTransform = artifact.Layout.StoredCodeTransform is null
                    ? null
                    : Enum.Parse<FrameStoredCodeTransform>(artifact.Layout.StoredCodeTransform),
                LevelCodeSpace = artifact.Layout.LevelCodeSpace is null
                    ? null
                    : Enum.Parse<FrameLevelCodeSpace>(artifact.Layout.LevelCodeSpace),
                Readout = artifact.Layout.NativeWidth is null
                    ? null
                    : new FrameReadoutDescriptor(
                        artifact.Layout.NativeWidth.Value,
                        artifact.Layout.NativeHeight!.Value,
                        artifact.Layout.RoiX!.Value,
                        artifact.Layout.RoiY!.Value,
                        artifact.Layout.RoiWidth!.Value,
                        artifact.Layout.RoiHeight!.Value,
                        artifact.Layout.BinX!.Value,
                        artifact.Layout.BinY!.Value,
                        Enum.Parse<FrameBinningAlgorithm>(artifact.Layout.BinningAlgorithm!),
                        artifact.Layout.CfaOriginX,
                        artifact.Layout.CfaOriginY)
            },
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
        if (frame.Location is { } location)
        {
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
        }
        else if (frame.LocationEvidenceState != CentralCaptureLocationEvidenceState.LegacyIncomplete)
        {
            throw new InvalidOperationException("The central frame location evidence was not loaded.");
        }
        return descriptor;
    }

    private static ProfileIdentityDescriptor ToIdentity(CentralCaptureProfile profile)
        => new(profile.Name, profile.Version, profile.Sha256);
}
