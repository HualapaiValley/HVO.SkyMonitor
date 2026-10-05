namespace HVO.SkyMonitor.Video.FFmpeg;

/// <summary>Host-owned configuration, never accepted from a media request or transported in a processing recipe.</summary>
public sealed record FFmpegOptions
{
    public string ExecutablePath { get; init; } = "/usr/bin/ffmpeg";
    public string ProbePath { get; init; } = "/usr/bin/ffprobe";
    public string ScratchDirectory { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
    public long MaximumScratchBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long MaximumOutputBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumAddressSpaceBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    // CUDA initializes a large virtual mapping even for a small NVENC frame. This is not resident RAM.
    public long MaximumNvidiaAddressSpaceBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    public long MaximumResidentBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    internal void Validate()
    {
        if (!Path.IsPathFullyQualified(ExecutablePath) || !Path.IsPathFullyQualified(ProbePath) ||
            !Path.IsPathFullyQualified(ScratchDirectory) || Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromHours(1) ||
            MaximumOutputBytes is < 1024 or > 32L * 1024 * 1024 * 1024 ||
            MaximumScratchBytes < MaximumOutputBytes || MaximumScratchBytes > 128L * 1024 * 1024 * 1024 ||
            MaximumAddressSpaceBytes is < 256L * 1024 * 1024 or > 64L * 1024 * 1024 * 1024 ||
            MaximumNvidiaAddressSpaceBytes is < 256L * 1024 * 1024 or > 64L * 1024 * 1024 * 1024 ||
            MaximumResidentBytes < 128L * 1024 * 1024 || MaximumResidentBytes > Math.Min(MaximumAddressSpaceBytes, MaximumNvidiaAddressSpaceBytes))
            throw new ArgumentException("Invalid bounded FFmpeg configuration.");
    }
}

/// <summary>The qualified profiles are deliberately closed; requests cannot inject FFmpeg switches.</summary>
public enum TimeLapseEncoderProfile
{
    Software,
    Nvidia
}

public sealed record FFmpegCapability(bool Available, string? ReasonCode, string? Version, string? BinarySha256,
    string? ProbeBinarySha256 = null);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Every media failure requires a stable operator-facing reason code.")]
public sealed class TimeLapseEncodingException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}
