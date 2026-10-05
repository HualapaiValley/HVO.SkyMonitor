using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Video.FFmpeg;

/// <summary>Verified packet proof; daily assembly can prove stream-copy equality rather than merely video duration.</summary>
public sealed record TimeLapsePacket(long PresentationTick, long DecodeTick, long DurationTicks, string Sha256);

public sealed record TimeLapseMediaProof(
    int Width, int Height, long DurationTicks, string StreamIdentitySha256,
    IReadOnlyList<TimeLapsePacket> Packets);

public sealed record TimeLapseEncodingEvidence(
    string ProfileVersion,
    TimeLapseEncoderProfile Profile,
    FFmpegCapability Capability,
    string TimelineIdentitySha256,
    string EncodingIdentitySha256,
    IReadOnlyDictionary<int, string> EncodedImageChecksums,
    string PayloadSha256,
    long PayloadBytes,
    TimeLapseMediaProof Media);

/// <summary>
/// An unpublished verified temporary. Hosts stream it into their own immutable stores before disposing it.
/// It never confers publication authority and knows nothing about upload, devices, SQL or local journals.
/// </summary>
public sealed class EncodedTimeLapse : IAsyncDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _slot;
    private bool _disposed;

    internal EncodedTimeLapse(string directory, TimeLapseEncodingEvidence evidence, FileStream lease, SemaphoreSlim slot)
    {
        _directory = directory;
        Evidence = evidence;
        _lease = lease;
        _slot = slot;
    }

    public TimeLapseEncodingEvidence Evidence { get; }

    public FileStream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PhysicalRoot.Open(_directory).Resolve("video.mp4");
        DurableSync.RequireRegularFile(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { TimeLapseWorkspace.Delete(_directory); }
        finally { _lease.Dispose(); _slot.Release(); }
        return ValueTask.CompletedTask;
    }
}
