namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>
/// Parses one gzip header, then feeds raw deflate in bulk while withholding nine wire bytes.
/// At source EOF only the ninth-from-last byte is released, alone; the trailer is never fed to the inflater.
/// The source remains owned by the caller.
/// </summary>
internal sealed class SwpcGzipMemberStream(Stream source) : Stream
{
    private readonly byte[] buffer = new byte[8192];
    private int offset;
    private int length;
    private uint headerCrc = SwpcCrc32.Initial;
    private bool ended;
    private bool loneByteDelivered;
    private bool decoderStarved;

    public bool LoneByteDelivered => loneByteDelivered;
    public bool DecoderStarved => decoderStarved;
    public bool HasExactTrailer => ended && length - offset == 8;
    // Access only after HasExactTrailer establishes that these bytes immediately follow raw deflate.
    public ReadOnlyMemory<byte> Trailer => buffer.AsMemory(length - 8, 8);

    public async ValueTask ReadHeaderAsync(CancellationToken token)
    {
        if (await ReadHeaderByteAsync(token).ConfigureAwait(false) != 0x1f ||
            await ReadHeaderByteAsync(token).ConfigureAwait(false) != 0x8b ||
            await ReadHeaderByteAsync(token).ConfigureAwait(false) != 8)
            throw new InvalidDataException("Invalid gzip header.");
        var flags = await ReadHeaderByteAsync(token).ConfigureAwait(false);
        if ((flags & 0xe0) != 0) throw new InvalidDataException("Reserved gzip flags.");
        await SkipHeaderBytesAsync(6, token).ConfigureAwait(false); // MTIME, XFL and OS.
        if ((flags & 4) != 0)
        {
            var extraLength = await ReadHeaderByteAsync(token).ConfigureAwait(false);
            extraLength |= await ReadHeaderByteAsync(token).ConfigureAwait(false) << 8;
            await SkipHeaderBytesAsync(extraLength, token).ConfigureAwait(false);
        }
        if ((flags & 8) != 0) await ReadTerminatedHeaderAsync(token).ConfigureAwait(false);
        if ((flags & 16) != 0) await ReadTerminatedHeaderAsync(token).ConfigureAwait(false);
        if ((flags & 2) != 0)
        {
            var expected = ~headerCrc & 0xffff;
            var actual = await ReadHeaderByteAsync(token, includeInCrc: false).ConfigureAwait(false);
            actual |= await ReadHeaderByteAsync(token, includeInCrc: false).ConfigureAwait(false) << 8;
            if (actual != expected) throw new InvalidDataException("Invalid gzip header checksum.");
        }
    }

    private async ValueTask<int> ReadHeaderByteAsync(CancellationToken token, bool includeInCrc = true)
    {
        token.ThrowIfCancellationRequested();
        if (offset == length) await FillHeaderAsync(token).ConfigureAwait(false);
        var value = buffer[offset++];
        if (includeInCrc) headerCrc = SwpcCrc32.Append(headerCrc, value);
        return value;
    }

    private async ValueTask SkipHeaderBytesAsync(int count, CancellationToken token)
    {
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (offset == length) await FillHeaderAsync(token).ConfigureAwait(false);
            var take = Math.Min(count, length - offset);
            headerCrc = SwpcCrc32.Append(headerCrc, buffer.AsSpan(offset, take));
            offset += take;
            count -= take;
        }
    }

    private async ValueTask ReadTerminatedHeaderAsync(CancellationToken token)
    {
        for (var index = 0; index < 1024; index++)
            if (await ReadHeaderByteAsync(token).ConfigureAwait(false) == 0) return;
        throw new InvalidDataException("Gzip name or comment exceeds its bound.");
    }

    private async ValueTask FillHeaderAsync(CancellationToken token)
    {
        offset = 0;
        length = await source.ReadAsync(buffer, token).ConfigureAwait(false);
        if (length == 0)
        {
            ended = true;
            throw new InvalidDataException("Incomplete gzip header.");
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty) return 0;
        if (!ended) await FillDeflateAsync(cancellationToken).ConfigureAwait(false);
        var remaining = length - offset;
        if (remaining > 9)
        {
            var take = Math.Min(destination.Length, remaining - 9);
            buffer.AsMemory(offset, take).CopyTo(destination);
            offset += take;
            return take;
        }
        if (ended && remaining == 9)
        {
            destination.Span[0] = buffer[offset++];
            loneByteDelivered = true;
            return 1;
        }
        // A finished raw inflater never requests this EOF. Reaching it proves decoder starvation.
        decoderStarved = true;
        return 0;
    }

    private async ValueTask FillDeflateAsync(CancellationToken token)
    {
        var remaining = length - offset;
        buffer.AsSpan(offset, remaining).CopyTo(buffer);
        offset = 0;
        length = remaining;
        // Aggregate fragmented delivery into the same fixed bulk buffer, always retaining nine bytes.
        while (length < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
            {
                ended = true;
                break;
            }
            length += read;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] destination, int start, int count) =>
        ReadAsync(destination.AsMemory(start, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
