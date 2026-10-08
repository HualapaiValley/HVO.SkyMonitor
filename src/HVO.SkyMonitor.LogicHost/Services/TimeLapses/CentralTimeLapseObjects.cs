using System.Security.Cryptography;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

/// <summary>Large MP4s use bounded objects within the existing provider-neutral put limit. No new provider API.</summary>
internal sealed class CentralTimeLapseObjects(IObjectStore objects, CentralObjectStorageNames names)
{
    internal const int ChunkBytes = 32 * 1024 * 1024;

    internal async Task<IReadOnlyList<CentralTimeLapseChunk>> PutAsync(CentralTimeLapseLease authority, Guid jobId, EncodedTimeLapse encoded, CancellationToken token)
    {
        await using var input = encoded.OpenRead();
        if (input.Length != encoded.Evidence.PayloadBytes) throw new InvalidDataException("Unpublished video length changed.");
        var chunks = new List<CentralTimeLapseChunk>();
        var buffer = GC.AllocateUninitializedArray<byte>((int)Math.Min(input.Length, ChunkBytes));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        while (total < input.Length)
        {
            var count = (int)Math.Min(buffer.Length, input.Length - total);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            var sha = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count)));
            var key = Key(authority.JobId, authority.Attempt, jobId, encoded.Evidence.PayloadSha256, chunks.Count, sha);
            var chunk = new CentralTimeLapseChunk(key, count, sha);
            try { _ = await ReadChunkAsync(chunk, token).ConfigureAwait(false); }
            catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
            {
                await using var content = new MemoryStream(buffer, 0, count, writable: false);
                await objects.PutAsync(names.ArtifactBucket, key, content, count, "application/octet-stream", token).ConfigureAwait(false);
            }
            // A provider acknowledgement alone is not video readiness. Read the committed generation and verify bytes.
            _ = await ReadChunkAsync(chunk, token).ConfigureAwait(false);
            chunks.Add(chunk);
            total += count;
        }
        if (total != encoded.Evidence.PayloadBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(encoded.Evidence.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unpublished video checksum changed.");
        return chunks;
    }

    internal async Task VerifyAsync(CentralTimeLapseProduct product, CancellationToken token)
    {
        await using var stream = await OpenAsync(product, token).ConfigureAwait(false);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (!sha.Equals(product.Encoding.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained video payload checksum changed.");
    }

    /// <summary>
    /// An attempt cleans only earlier attempts of its own authority, excluding committed hourly/daily chunks.
    /// A stalled older worker can never delete a newer worker's output, even after losing its SQL lease.
    /// </summary>
    internal async Task RemoveUnpublishedAsync(CentralTimeLapseLease authority, IReadOnlySet<string> retainedKeys, CancellationToken token,
        bool includeCurrentAttempt = false)
    {
        var count = 0;
        for (var attempt = 1; attempt < authority.Attempt + (includeCurrentAttempt ? 1 : 0); attempt++)
        {
            var prefix = FormattableString.Invariant($"time-lapses/{authority.JobId:N}/{attempt:D2}/");
            await foreach (var item in objects.ListAsync(names.ArtifactBucket, prefix, token).ConfigureAwait(false))
            {
                if (++count > 4096 || !item.Key.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Unpublished video cleanup exceeds its job boundary.");
                if (!retainedKeys.Contains(item.Key)) await objects.DeleteAsync(names.ArtifactBucket, item.Key, token).ConfigureAwait(false);
            }
        }
    }

    internal async Task<Stream> OpenAsync(CentralTimeLapseProduct product, CancellationToken token)
    {
        Validate(product);
        foreach (var chunk in product.Chunks)
        {
            var stat = await objects.StatAsync(names.ArtifactBucket, chunk.Key, token).ConfigureAwait(false);
            if (stat.ContentLength != chunk.Bytes) throw new InvalidDataException("A retained video chunk has the wrong length.");
        }
        return new ChunkStream(this, product.Chunks, product.Encoding.PayloadBytes);
    }

    private async Task<byte[]> ReadChunkAsync(CentralTimeLapseChunk chunk, CancellationToken token)
    {
        if (chunk.Bytes is < 1 or > ChunkBytes) throw new InvalidDataException("Invalid bounded video chunk.");
        var stat = await objects.StatAsync(names.ArtifactBucket, chunk.Key, token).ConfigureAwait(false);
        if (stat.ContentLength != chunk.Bytes) throw new InvalidDataException("Video chunk length mismatch.");
        var bytes = GC.AllocateUninitializedArray<byte>((int)chunk.Bytes);
        await objects.ReadAsync(names.ArtifactBucket, chunk.Key, stat.Generation, async (source, cancellationToken) =>
        {
            await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var tail = new byte[1];
            if (await source.ReadAsync(tail, cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Video chunk exceeds declared length.");
        }, token).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(chunk.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Video chunk checksum mismatch.");
        return bytes;
    }

    internal static void Validate(CentralTimeLapseProduct product)
    {
        if (product.JobId == Guid.Empty || product.PublicationJobId == Guid.Empty || product.PublicationAttempt is < 1 or > 3 ||
            product.Encoding.PayloadSha256 is not { Length: 64 } ||
            !product.Encoding.PayloadSha256.All(char.IsAsciiHexDigit) ||
            product.Encoding.PayloadBytes is < 1 or > 32L * 1024 * 1024 * 1024 || product.Chunks.Count is < 1 or > 1024 ||
            product.Chunks.Sum(static chunk => chunk.Bytes) != product.Encoding.PayloadBytes ||
            product.Chunks.Where((chunk, index) => chunk.Bytes is < 1 or > ChunkBytes ||
                index < product.Chunks.Count - 1 && chunk.Bytes != ChunkBytes ||
                chunk.Sha256 is not { Length: 64 } || !chunk.Sha256.All(char.IsAsciiHexDigit) ||
                chunk.Key != Key(product.PublicationJobId, product.PublicationAttempt, product.JobId, product.Encoding.PayloadSha256, index, chunk.Sha256)).Any())
            throw new InvalidDataException("Invalid immutable video chunk manifest.");
    }

    private static string Key(Guid publicationJobId, int attempt, Guid jobId, string payloadSha256, int index, string chunkSha256)
        => FormattableString.Invariant($"time-lapses/{publicationJobId:N}/{attempt:D2}/{jobId:N}/{payloadSha256}/{index:D4}-{chunkSha256}.part");

    /// <summary>Seekable range view; verifies a complete bounded chunk before releasing any bytes from it.</summary>
    private sealed class ChunkStream(CentralTimeLapseObjects owner, IReadOnlyList<CentralTimeLapseChunk> chunks, long length) : Stream
    {
        private long _position;
        private int _cachedIndex = -1;
        private byte[]? _cached;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0 || _position >= length) return 0;
            var index = checked((int)(_position / ChunkBytes));
            if (_cachedIndex != index)
            {
                _cached = null;
                _cached = await owner.ReadChunkAsync(chunks[index], cancellationToken).ConfigureAwait(false);
                _cachedIndex = index;
            }
            var offset = (int)(_position % ChunkBytes);
            var count = Math.Min(buffer.Length, _cached!.Length - offset);
            _cached.AsMemory(offset, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0 || position > length) throw new IOException("Video range is outside the retained payload.");
            return _position = position;
        }
        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            _cached = null;
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous video reads.");
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
