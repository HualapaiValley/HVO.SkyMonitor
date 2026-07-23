using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public enum CameraAgentArtifactReadStatus
{
    Found,
    NotFound,
    Conflict,
    Gone,
    TooLarge,
    UnsupportedMediaType,
    Unavailable
}

public sealed record CameraAgentArtifactContentResult(
    CameraAgentArtifactReadStatus Status,
    CameraAgentArtifactContentStream? Content = null);

public sealed record CameraAgentArtifactPreviewResult(
    CameraAgentArtifactReadStatus Status,
    ReadOnlyMemory<byte> Content = default,
    string? ChecksumSha256 = null,
    int? Width = null,
    int? Height = null);

public interface ICameraAgentArtifactService
{
    ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(
        Guid artifactId,
        CancellationToken cancellationToken);

    ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(
        Guid artifactId,
        CancellationToken cancellationToken);
}

public sealed class CameraAgentArtifactContentStream : Stream
{
    private readonly Stream _stream;
    private int _disposed;

    internal CameraAgentArtifactContentStream(
        Stream stream,
        Guid artifactId,
        Guid captureId,
        FrameArtifactRole role,
        string mediaType,
        long byteLength,
        string checksumSha256,
        string fileName,
        ReconstructionDescriptor? descriptor)
    {
        _stream = stream;
        ArtifactId = artifactId;
        CaptureId = captureId;
        Role = role;
        MediaType = mediaType;
        ByteLength = byteLength;
        ChecksumSha256 = checksumSha256;
        FileName = fileName;
        Descriptor = descriptor;
    }

    public Guid ArtifactId { get; }

    public Guid CaptureId { get; }

    public FrameArtifactRole Role { get; }

    public string MediaType { get; }

    public long ByteLength { get; }

    public string ChecksumSha256 { get; }

    public string FileName { get; }

    internal ReconstructionDescriptor? Descriptor { get; }

    public override bool CanRead => _stream.CanRead;

    public override bool CanSeek => _stream.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public override void Flush() => _stream.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _stream.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _stream.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
