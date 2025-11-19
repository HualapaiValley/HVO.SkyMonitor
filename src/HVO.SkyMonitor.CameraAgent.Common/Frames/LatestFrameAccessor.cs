using System;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Frames;

public interface ILatestFrameAccessor
{
    bool TryGetSnapshot([NotNullWhen(true)] out LatestFrameSnapshot? snapshot);
    void Update(CameraFrame frame);
}

public sealed record LatestFrameSnapshot(
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData);

public sealed class LatestFrameAccessor : ILatestFrameAccessor
{
    private LatestFrameSnapshot? _snapshot;

    public bool TryGetSnapshot([NotNullWhen(true)] out LatestFrameSnapshot? snapshot)
    {
        snapshot = Volatile.Read(ref _snapshot);
        return snapshot is not null;
    }

    public void Update(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var copy = frame.PixelData.ToArray();
        var snapshot = new LatestFrameSnapshot(
            frame.TimestampUtc,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            copy);
        Volatile.Write(ref _snapshot, snapshot);
    }
}
