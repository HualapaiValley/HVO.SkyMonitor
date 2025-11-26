using System;

namespace HVO.SkyMonitor.AgentCore;

public enum CameraPixelFormat
{
    Mono8,
    Mono16,
    Rgb24
}

public sealed record CameraFrame(
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    FrameMetadata Metadata);
