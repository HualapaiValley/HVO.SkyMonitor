using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.CameraAgent.Common.Options;

/// <summary>Local-only background video settings; automation definitions choose hourly and/or sunrise-day windows.</summary>
public sealed class TimeLapseOptions : IValidatableObject
{
    public bool Enabled { get; init; }
    public string? RigProfileSha256 { get; init; }
    [Range(128, 1280)]
    public int MaximumDimension { get; init; } = 1280;
    public TimeLapseTimingOptions Timing { get; init; } = new();
    public TimeLapseFrameOptions Frames { get; init; } = new();
    public FFmpegOptions Encoder { get; init; } = new();
    [Range(1024L, 1024L * 1024 * 1024 * 1024)]
    public long MaximumPublishedBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    [Range(0L, 1024L * 1024 * 1024 * 1024)]
    public long MinimumFreeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Enabled && (RigProfileSha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)))
            yield return new("An enabled time-lapse preset requires an exact rig SHA-256 identity.", [nameof(RigProfileSha256)]);
        if (Timing is null || Timing.Compression is not (180 or 300) || Timing.MaximumHoldSeconds is < 1 or > 300 ||
            Timing.FallbackCadenceSeconds < 1 || Timing.FallbackCadenceSeconds > Timing.MaximumHoldSeconds)
            yield return new("Invalid time-lapse timing policy.", [nameof(Timing)]);
        if (Frames is null || Frames.StackCount is not (1 or 3) || Frames.JpegQuality is < 1 or > 100 || Frames.MaximumSaturatedMillionths is < 0 or > 1_000_000)
            yield return new("Invalid time-lapse frame policy.", [nameof(Frames)]);
        if (Encoder is null || !Path.IsPathFullyQualified(Encoder.ExecutablePath) || !Path.IsPathFullyQualified(Encoder.ProbePath))
            yield return new("Time-lapse executable paths must be absolute host settings.", [nameof(Encoder)]);
    }

    internal string Target => "time-lapse:" + Identity(RigProfileSha256, MaximumDimension, Timing, Frames);

    private static string Identity(string? rig, int dimension, TimeLapseTimingOptions timing, TimeLapseFrameOptions frames)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
    {
        version = "hvo-cameraagent-timelapse-preset-v1", RigProfileSha256 = rig?.ToUpperInvariant(),
        MaximumDimension = dimension, Timing = timing, Frames = frames,
        renderer = TimeLapseFrameRenderer.RecipeIdentity(frames), FFmpegTimeLapseEncoder.ProfileVersion
    });

    internal static bool Matches(CameraAgentTimeLapsePreset preset) => preset.Rig is not null &&
        !string.IsNullOrWhiteSpace(preset.AgentId) && preset.AgentId.Length <= 128 &&
        preset.MaximumDimension is >= 128 and <= 1280 && preset.Frames is not null && preset.Timing is not null &&
        preset.IdentitySha256 == Identity(RigProjectionContextFactory.CreateProfileHashSha256(preset.Rig),
            preset.MaximumDimension, preset.Timing, preset.Frames);

    internal CameraAgentTimeLapsePreset Freeze(CameraModuleConfig configuration) =>
        new(Target[11..], configuration.AgentId ?? throw new InvalidOperationException("Capture identity is not configured."),
            configuration.DeploymentLocation is null && !configuration.DeploymentLocationRedacted ? configuration.Observatory : null,
            configuration.Rig, Frames, Timing, MaximumDimension);
}
