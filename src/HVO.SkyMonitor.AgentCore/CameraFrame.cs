using System;

namespace HVO.SkyMonitor.AgentCore;

public enum CameraPixelFormat
{
    Mono8,
    Mono16,
    Rgb24,
    /// <summary>One little-endian unsigned 16-bit sample per RGGB photosite, without demosaicing.</summary>
    BayerRggb16
}

public sealed record CameraFrame(
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    FrameMetadata Metadata,
    int? StrideBytes = null)
{
    /// <summary>Gets the authoritative payload layout when supplied by acquisition or reconstruction.</summary>
    public FrameLayoutDescriptor? Layout { get; init; }
}
