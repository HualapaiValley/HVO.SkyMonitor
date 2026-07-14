using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

public enum FrameByteOrder
{
    NotApplicable,
    LittleEndian,
    BigEndian
}

public enum FrameSamplePacking
{
    ByteAligned,
    Packed
}

public enum ColorFilterArrayPattern
{
    None,
    Rggb
}

/// <summary>Stable agent, rig, sequence, and capture identity assigned before optional processing.</summary>
public sealed record CaptureIdentityDescriptor(
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] string RigId,
    [property: JsonRequired] long CaptureSequence,
    [property: JsonRequired] Guid CaptureId);

/// <summary>UTC capture boundaries. Durable ingress is the time bytes became locally recoverable.</summary>
public sealed record CaptureTimingDescriptor(
    [property: JsonRequired] DateTimeOffset RequestedStartUtc,
    [property: JsonRequired] DateTimeOffset ExposureStartedUtc,
    [property: JsonRequired] DateTimeOffset ExposureEndedUtc,
    [property: JsonRequired] DateTimeOffset ReadoutCompletedUtc,
    [property: JsonRequired] DateTimeOffset DurableIngressUtc);

/// <summary>Requested and effective camera controls at capture time.</summary>
public sealed record CaptureControlDescriptor(
    [property: JsonRequired] TimeSpan RequestedExposure,
    [property: JsonRequired] TimeSpan EffectiveExposure,
    [property: JsonRequired] double RequestedGain,
    [property: JsonRequired] double EffectiveGain,
    [property: JsonRequired] double? RequestedOffset,
    [property: JsonRequired] double? EffectiveOffset,
    [property: JsonRequired] double? TemperatureSetpointC,
    [property: JsonRequired] double? EffectiveTemperatureC);

/// <summary>Immutable name, version, and SHA-256 identity of a capture-time profile.</summary>
public sealed record ProfileIdentityDescriptor(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string Sha256);

/// <summary>Capture-time identities needed to interpret and reproduce an artifact.</summary>
public sealed record CaptureProfileSet(
    [property: JsonRequired] ProfileIdentityDescriptor Rig,
    [property: JsonRequired] ProfileIdentityDescriptor Calibration,
    [property: JsonRequired] ProfileIdentityDescriptor Mask,
    [property: JsonRequired] ProfileIdentityDescriptor Sensor,
    [property: JsonRequired] ProfileIdentityDescriptor Processing);

/// <summary>Complete byte layout needed to wrap and interpret one frame payload.</summary>
public sealed record FrameLayoutDescriptor(
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] int StrideBytes,
    [property: JsonRequired] CameraPixelFormat PixelFormat,
    [property: JsonRequired] FrameByteOrder ByteOrder,
    [property: JsonRequired] int SampleDepthBits,
    [property: JsonRequired] int ContainerDepthBits,
    [property: JsonRequired] FrameSamplePacking Packing,
    [property: JsonRequired] ColorFilterArrayPattern CfaPattern,
    [property: JsonRequired] double? BlackLevel,
    [property: JsonRequired] double? WhiteLevel,
    [property: JsonRequired] long ByteLength);

/// <summary>Canonical descriptive identity of a recipe; it contains no executable behavior.</summary>
public sealed record RecipeIdentityDescriptor(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string SemanticVersion,
    [property: JsonRequired] string ImplementationVersion,
    [property: JsonRequired] JsonElement Options,
    [property: JsonRequired] string OptionsSha256)
{
    public static RecipeIdentityDescriptor Create(
        string name,
        string semanticVersion,
        string implementationVersion,
        JsonElement options)
        => new(
            name,
            semanticVersion,
            implementationVersion,
            CaptureContractJson.Canonicalize(options),
            CaptureContractJson.ComputeCanonicalJsonSha256(options));
}

/// <summary>Artifact identity, role, variant, ordered lineage, and producing recipe.</summary>
public sealed record ArtifactDescriptor(
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] FrameArtifactRole Role,
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] string Variant,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] IReadOnlyList<Guid> SourceArtifactIds,
    [property: JsonRequired] RecipeIdentityDescriptor Recipe,
    [property: JsonRequired] string MediaType,
    [property: JsonRequired] string ChecksumSha256);

/// <summary>Transport-neutral descriptor sufficient to reconstruct one captured artifact.</summary>
public sealed record ReconstructionDescriptor(
    [property: JsonRequired] CaptureIdentityDescriptor Capture,
    [property: JsonRequired] CaptureTimingDescriptor Timing,
    [property: JsonRequired] CaptureControlDescriptor Controls,
    [property: JsonRequired] CaptureProfileSet Profiles,
    [property: JsonRequired] FrameLayoutDescriptor Layout,
    [property: JsonRequired] ArtifactDescriptor Artifact)
{
    public CaptureContractValidationResult Validate()
        => ReconstructionDescriptorValidator.Validate(this);
}

internal static class ReconstructionDescriptorValidator
{
    public static CaptureContractValidationResult Validate(ReconstructionDescriptor? descriptor)
    {
        if (descriptor?.Capture is null || string.IsNullOrWhiteSpace(descriptor.Capture.AgentId) ||
            string.IsNullOrWhiteSpace(descriptor.Capture.RigId) || descriptor.Capture.CaptureId == Guid.Empty)
        {
            return Failure(CaptureContractReasonCodes.InvalidIdentity, "descriptor.capture");
        }
        if (descriptor.Capture.CaptureSequence < 1)
        {
            return Failure(CaptureContractReasonCodes.InvalidCaptureSequence, "descriptor.capture.captureSequence");
        }
        if (descriptor.Artifact is not null && descriptor.Capture.CaptureId == descriptor.Artifact.ArtifactId)
        {
            return Failure(CaptureContractReasonCodes.InvalidIdentity, "descriptor.artifact.artifactId");
        }
        if (descriptor.Artifact?.SourceArtifactIds?.Contains(descriptor.Capture.CaptureId) == true)
        {
            return Failure(CaptureContractReasonCodes.InvalidLineage, "descriptor.artifact.sourceArtifactIds");
        }
        if (descriptor.Timing is null || !IsUtc(descriptor.Timing.RequestedStartUtc) || !IsUtc(descriptor.Timing.ExposureStartedUtc) ||
            !IsUtc(descriptor.Timing.ExposureEndedUtc) || !IsUtc(descriptor.Timing.ReadoutCompletedUtc) ||
            !IsUtc(descriptor.Timing.DurableIngressUtc) ||
            descriptor.Timing.RequestedStartUtc > descriptor.Timing.ExposureStartedUtc ||
            descriptor.Timing.ExposureStartedUtc > descriptor.Timing.ExposureEndedUtc ||
            descriptor.Timing.ExposureEndedUtc > descriptor.Timing.ReadoutCompletedUtc ||
            descriptor.Timing.ReadoutCompletedUtc > descriptor.Timing.DurableIngressUtc)
        {
            return Failure(CaptureContractReasonCodes.InvalidTimingOrder, "descriptor.timing");
        }
        if (descriptor.Controls is null || descriptor.Controls.RequestedExposure < TimeSpan.Zero || descriptor.Controls.EffectiveExposure < TimeSpan.Zero ||
            !double.IsFinite(descriptor.Controls.RequestedGain) || !double.IsFinite(descriptor.Controls.EffectiveGain) ||
            !IsFinite(descriptor.Controls.RequestedOffset) || !IsFinite(descriptor.Controls.EffectiveOffset) ||
            !IsFinite(descriptor.Controls.TemperatureSetpointC) || !IsFinite(descriptor.Controls.EffectiveTemperatureC))
        {
            return Failure(CaptureContractReasonCodes.InvalidControls, "descriptor.controls");
        }

        var profileResult = ValidateProfiles(descriptor.Profiles);
        if (!profileResult.IsValid)
        {
            return profileResult;
        }
        var layoutResult = ValidateLayout(descriptor.Layout);
        if (!layoutResult.IsValid)
        {
            return layoutResult;
        }
        return ValidateArtifact(descriptor.Artifact);
    }

    private static CaptureContractValidationResult ValidateProfiles(CaptureProfileSet? profiles)
    {
        if (profiles is null || !IsValidProfile(profiles.Rig) || !IsValidProfile(profiles.Calibration) ||
            !IsValidProfile(profiles.Mask) || !IsValidProfile(profiles.Sensor) || !IsValidProfile(profiles.Processing))
        {
            return Failure(CaptureContractReasonCodes.InvalidProfile, "descriptor.profiles");
        }
        return CaptureContractValidationResult.Success;
    }

    private static CaptureContractValidationResult ValidateLayout(FrameLayoutDescriptor? layout)
    {
        if (layout is null || layout.Width < 1 || layout.Height < 1)
        {
            return Failure(CaptureContractReasonCodes.InvalidDimensions, "descriptor.layout");
        }
        if (!Enum.IsDefined(layout.PixelFormat))
        {
            return Failure(CaptureContractReasonCodes.UnsupportedFormat, "descriptor.layout.pixelFormat");
        }

        var (bytesPerPixel, sampleDepth, containerDepth, byteOrder, cfa) = layout.PixelFormat switch
        {
            CameraPixelFormat.Mono8 => (1, 8, 8, FrameByteOrder.NotApplicable, ColorFilterArrayPattern.None),
            CameraPixelFormat.Mono16 => (2, 16, 16, FrameByteOrder.LittleEndian, ColorFilterArrayPattern.None),
            CameraPixelFormat.Rgb24 => (3, 8, 8, FrameByteOrder.NotApplicable, ColorFilterArrayPattern.None),
            CameraPixelFormat.BayerRggb16 => (2, 16, 16, FrameByteOrder.LittleEndian, ColorFilterArrayPattern.Rggb),
            _ => (0, 0, 0, FrameByteOrder.NotApplicable, ColorFilterArrayPattern.None)
        };
        if (layout.SampleDepthBits != sampleDepth || layout.ContainerDepthBits != containerDepth)
        {
            return Failure(CaptureContractReasonCodes.InvalidSampleDepth, "descriptor.layout.sampleDepthBits");
        }
        var byteOrderIsValid = containerDepth > 8
            ? layout.ByteOrder is FrameByteOrder.LittleEndian or FrameByteOrder.BigEndian
            : layout.ByteOrder == byteOrder;
        if (!byteOrderIsValid)
        {
            return Failure(CaptureContractReasonCodes.InvalidByteOrder, "descriptor.layout.byteOrder");
        }
        if (layout.Packing != FrameSamplePacking.ByteAligned)
        {
            return Failure(CaptureContractReasonCodes.InvalidPacking, "descriptor.layout.packing");
        }
        if (layout.CfaPattern != cfa)
        {
            return Failure(CaptureContractReasonCodes.InvalidCfa, "descriptor.layout.cfaPattern");
        }
        var maximumLevel = Math.Pow(2, layout.SampleDepthBits) - 1;
        if (!IsFinite(layout.BlackLevel) || !IsFinite(layout.WhiteLevel) ||
            layout.BlackLevel is < 0 || layout.WhiteLevel is < 0 ||
            layout.BlackLevel > maximumLevel || layout.WhiteLevel > maximumLevel ||
            layout.BlackLevel.HasValue && layout.WhiteLevel.HasValue && layout.BlackLevel > layout.WhiteLevel)
        {
            return Failure(CaptureContractReasonCodes.InvalidLevels, "descriptor.layout");
        }

        var minimumStride = (long)layout.Width * bytesPerPixel;
        var expectedLength = (long)layout.StrideBytes * layout.Height;
        if (layout.StrideBytes < minimumStride || layout.ByteLength != expectedLength)
        {
            return Failure(CaptureContractReasonCodes.InvalidStride, "descriptor.layout.strideBytes");
        }
        return CaptureContractValidationResult.Success;
    }

    private static CaptureContractValidationResult ValidateArtifact(ArtifactDescriptor? artifact)
    {
        if (artifact is null || artifact.ArtifactId == Guid.Empty)
        {
            return Failure(CaptureContractReasonCodes.InvalidIdentity, "descriptor.artifact.artifactId");
        }
        if (string.IsNullOrWhiteSpace(artifact.SourceId))
        {
            return Failure(CaptureContractReasonCodes.InvalidIdentity, "descriptor.artifact.sourceId");
        }
        if (!Enum.IsDefined(artifact.Role))
        {
            return Failure(CaptureContractReasonCodes.InvalidArtifactRole, "descriptor.artifact.role");
        }
        if (string.IsNullOrWhiteSpace(artifact.Variant))
        {
            return Failure(CaptureContractReasonCodes.InvalidArtifactVariant, "descriptor.artifact.variant");
        }
        if (!IsUtc(artifact.CreatedUtc))
        {
            return Failure(CaptureContractReasonCodes.InvalidTimingOrder, "descriptor.artifact.createdUtc");
        }
        if (artifact.SourceArtifactIds is null || artifact.SourceArtifactIds.Any(static id => id == Guid.Empty) ||
            artifact.SourceArtifactIds.Contains(artifact.ArtifactId) ||
            artifact.SourceArtifactIds.Count != artifact.SourceArtifactIds.Distinct().Count())
        {
            return Failure(CaptureContractReasonCodes.InvalidLineage, "descriptor.artifact.sourceArtifactIds");
        }
        if (artifact.Role == FrameArtifactRole.Raw && artifact.SourceArtifactIds.Count != 0 ||
            artifact.Role != FrameArtifactRole.Raw && artifact.SourceArtifactIds.Count == 0)
        {
            return Failure(CaptureContractReasonCodes.InvalidLineage, "descriptor.artifact.sourceArtifactIds");
        }
        if (artifact.Recipe is null || string.IsNullOrWhiteSpace(artifact.Recipe.Name) ||
            string.IsNullOrWhiteSpace(artifact.Recipe.SemanticVersion) ||
            string.IsNullOrWhiteSpace(artifact.Recipe.ImplementationVersion) ||
            artifact.Recipe.Options.ValueKind is JsonValueKind.Undefined)
        {
            return Failure(CaptureContractReasonCodes.InvalidRecipe, "descriptor.artifact.recipe");
        }
        if (!IsSha256(artifact.Recipe.OptionsSha256) ||
            !string.Equals(artifact.Recipe.OptionsSha256, CaptureContractJson.ComputeCanonicalJsonSha256(artifact.Recipe.Options), StringComparison.OrdinalIgnoreCase))
        {
            return Failure(CaptureContractReasonCodes.RecipeHashMismatch, "descriptor.artifact.recipe.optionsSha256");
        }
        if (string.IsNullOrWhiteSpace(artifact.MediaType))
        {
            return Failure(CaptureContractReasonCodes.InvalidArtifactRole, "descriptor.artifact.mediaType");
        }
        if (!IsSha256(artifact.ChecksumSha256))
        {
            return Failure(CaptureContractReasonCodes.InvalidChecksum, "descriptor.artifact.checksumSha256");
        }
        return CaptureContractValidationResult.Success;
    }

    private static bool IsValidProfile(ProfileIdentityDescriptor? profile)
        => profile is not null && !string.IsNullOrWhiteSpace(profile.Name) &&
           !string.IsNullOrWhiteSpace(profile.Version) && IsSha256(profile.Sha256);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool IsFinite(double? value) => !value.HasValue || double.IsFinite(value.Value);

    private static CaptureContractValidationResult Failure(string reasonCode, string path)
        => CaptureContractValidationResult.Failure(reasonCode, path);
}
