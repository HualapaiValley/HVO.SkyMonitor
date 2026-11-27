using Amazon.Runtime;
using Amazon.S3.Model;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

/// <summary>Owns one GET response for the duration of streaming. The per-read deadline also covers a stalled body.</summary>
internal sealed class SiloS3ResponseStream(GetObjectResponse response, SiloStoreDescriptor store, TimeSpan timeout) : Stream
{
    private Stream Source => response.ResponseStream;

    public override bool CanRead => Source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            return await Source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.StoreClass == SiloStoreClass.Archive && SiloS3ObjectStore.IsTlsFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw SiloArchiveStore.TlsFailure();
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException or AmazonClientException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SiloStoreUnavailableException(store.StoreId, "read exact version body", writeMayHaveLanded: false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
