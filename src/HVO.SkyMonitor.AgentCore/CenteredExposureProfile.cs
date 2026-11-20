using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Opt-in centered exposure execution with an installation-local stable camera identity.</summary>
public sealed record CenteredExposureProfile(
    [property: JsonRequired] string CameraId,
    [property: JsonRequired] string ExecutionVersion)
{
    public const string CurrentExecutionVersion = "cameraagent-centered-exposure-v1";

    public bool IsValid() => ExecutionVersion == CurrentExecutionVersion && IsCameraId(CameraId);

    internal static bool IsCameraId(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

/// <summary>Coordinates-free manifest binding for recovery of centered raw publication.</summary>
public sealed record CenteredExposureBindingV1(
    ExposureAttemptKey Source,
    ExposureAttemptKey Target,
    string FrozenRunSha256)
{
    internal bool IsValidFor(ReconstructionDescriptor descriptor) =>
        Source is not null && Target is not null && Source.Camera is not null &&
        Source.Camera == Target.Camera &&
        Source.Camera.AgentId == descriptor.Capture.AgentId &&
        CenteredExposureProfile.IsCameraId(Source.Camera.CameraId) &&
        Source.RunId != Guid.Empty && Source.RunId == Target.RunId &&
        Source.AttemptId != Guid.Empty && Target.AttemptId != Guid.Empty && Source.AttemptId != Target.AttemptId &&
        Source.Ordinal is > 0 and < long.MaxValue && Target.Ordinal == Source.Ordinal + 1 &&
        FrozenRunSha256 is { Length: 64 } && FrozenRunSha256.All(Uri.IsHexDigit) &&
        descriptor.Artifact.Role == FrameArtifactRole.Raw;
}
