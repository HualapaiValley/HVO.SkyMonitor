using System;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Frames;

public interface ILatestFrameAccessor
{
    bool TryGetSnapshot([NotNullWhen(true)] out LatestFrameSnapshot? snapshot);
    bool TryGetSnapshot(FrameArtifactRole role, [NotNullWhen(true)] out LatestFrameSnapshot? snapshot);
    void Update(CameraFrame frame);
    void Update(FrameArtifactRole role, CameraFrame frame);
    void Update(FrameArtifact artifact);
}

public sealed record LatestFrameSnapshot(
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    FrameMetadata? Metadata = null,
    string? RecipeVersion = null,
    int? SourceArtifactCount = null);

public sealed class LatestFrameAccessor : ILatestFrameAccessor
{
    private LatestFrameSnapshot? _previewSnapshot;
    private LatestFrameSnapshot? _rawSnapshot;
    private LatestFrameSnapshot? _combinedSnapshot;

    public bool TryGetSnapshot([NotNullWhen(true)] out LatestFrameSnapshot? snapshot)
    {
        snapshot = Volatile.Read(ref _previewSnapshot);
        return snapshot is not null;
    }

    public bool TryGetSnapshot(FrameArtifactRole role, [NotNullWhen(true)] out LatestFrameSnapshot? snapshot)
    {
        snapshot = role switch
        {
            FrameArtifactRole.Raw => Volatile.Read(ref _rawSnapshot),
            FrameArtifactRole.Combined => Volatile.Read(ref _combinedSnapshot),
            FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview => Volatile.Read(ref _previewSnapshot),
            _ => null
        };
        return snapshot is not null;
    }

    public void Update(CameraFrame frame)
        => Update(FrameArtifactRole.Preview, frame);

    public void Update(FrameArtifactRole role, CameraFrame frame)
        => Update(role, frame, null, null);

    public void Update(FrameArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Update(artifact.Role, artifact.Frame, artifact.RecipeVersion, artifact.SourceArtifactIds?.Count);
    }

    private void Update(FrameArtifactRole role, CameraFrame frame, string? recipeVersion, int? sourceArtifactCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var copy = frame.PixelData.ToArray();
        var snapshot = new LatestFrameSnapshot(
            frame.TimestampUtc,
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            copy,
            frame.Metadata,
            recipeVersion,
            sourceArtifactCount);
        switch (role)
        {
            case FrameArtifactRole.Raw:
                Volatile.Write(ref _rawSnapshot, snapshot);
                break;
            case FrameArtifactRole.Combined:
                Volatile.Write(ref _combinedSnapshot, snapshot);
                break;
            case FrameArtifactRole.Preview:
            case FrameArtifactRole.AnnotatedPreview:
                Volatile.Write(ref _previewSnapshot, snapshot);
                break;
        }
    }
}
