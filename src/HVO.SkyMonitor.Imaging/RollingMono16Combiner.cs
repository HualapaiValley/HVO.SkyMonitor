using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Immutable result of a rolling Mono16 arithmetic mean.</summary>
public sealed record RollingCombinationResult(ReadOnlyMemory<byte> PixelData, IReadOnlyList<Guid> SourceArtifactIds, TimeSpan TotalIntegration);

/// <summary>Maintains a bounded compatible Mono16 frame window and emits an average after every frame.</summary>
public sealed class RollingMono16Combiner
{
    private readonly int _capacity;
    private readonly Queue<(FrameArtifact Artifact, TimeSpan Exposure)> _frames = new();
    private int _width;
    private int _height;

    /// <summary>Creates a rolling combiner using the newest compatible frame count.</summary>
    public RollingMono16Combiner(int capacity)
    {
        if (capacity is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>Adds a raw Mono16 artifact, resetting the window when layout changes, and returns the warm-up/full average.</summary>
    public RollingCombinationResult Add(FrameArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame;
        if (artifact.Role is not FrameArtifactRole.Raw and not FrameArtifactRole.Calibrated || frame.PixelFormat != CameraPixelFormat.Mono16 ||
            frame.PixelData.Length != checked(frame.Width * frame.Height * 2))
        {
            throw new ArgumentException("A tightly packed Mono16 raw or calibrated artifact is required.", nameof(artifact));
        }

        if (_frames.Count > 0 && (_width != frame.Width || _height != frame.Height))
        {
            _frames.Clear();
        }

        _width = frame.Width;
        _height = frame.Height;
        _frames.Enqueue((artifact, frame.Metadata.Exposure));
        if (_frames.Count > _capacity)
        {
            _frames.Dequeue();
        }

        var totals = new ulong[checked(frame.Width * frame.Height)];
        foreach (var (input, _) in _frames)
        {
            var data = input.Frame.PixelData.Span;
            for (var pixel = 0; pixel < totals.Length; pixel++)
            {
                totals[pixel] += (ushort)(data[pixel * 2] | data[pixel * 2 + 1] << 8);
            }
        }

        var output = new byte[frame.PixelData.Length];
        for (var pixel = 0; pixel < totals.Length; pixel++)
        {
            var average = (ushort)(totals[pixel] / (ulong)_frames.Count);
            output[pixel * 2] = (byte)average;
            output[pixel * 2 + 1] = (byte)(average >> 8);
        }

        return new RollingCombinationResult(output, _frames.Select(item => item.Artifact.ArtifactId).ToArray(),
            TimeSpan.FromTicks(_frames.Sum(item => item.Exposure.Ticks)));
    }
}
