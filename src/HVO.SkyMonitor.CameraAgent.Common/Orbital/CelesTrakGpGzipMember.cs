using System.Buffers.Binary;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Framing primitives for decoding exactly one in-memory gzip member: an explicit header parser, the IEEE CRC-32 the
/// trailer carries, and a raw-deflate input that proves where the inflater stopped. The final eight wire bytes are the
/// trailer and are never parsed as header or fed to the inflater.
/// </summary>
internal static class CelesTrakGpGzipMember
{
    public const int TrailerLength = 8;
    private const int MaximumTerminatedFieldLength = 1024;

    // Initialized once; no code writes this private table after initialization.
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>
    /// Parses one RFC 1952 header confined to <paramref name="header"/>, which excludes the trailer. Rejects reserved
    /// flags, a header that runs past its span, a name or comment longer than its bound, and a wrong header CRC.
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < 10 || header[0] != 0x1F || header[1] != 0x8B || header[2] != 0x08)
        {
            return false;
        }
        var flags = header[3];
        if ((flags & 0xE0) != 0)
        {
            return false;
        }
        var position = 10; // ID1, ID2, CM, FLG, MTIME, XFL and OS.
        if ((flags & 0x04) != 0)
        {
            if (header.Length - position < 2)
            {
                return false;
            }
            var extraLength = header[position] | (header[position + 1] << 8);
            position += 2;
            if (header.Length - position < extraLength)
            {
                return false;
            }
            position += extraLength;
        }
        if ((flags & 0x08) != 0 && !TrySkipTerminated(header, ref position))
        {
            return false;
        }
        if ((flags & 0x10) != 0 && !TrySkipTerminated(header, ref position))
        {
            return false;
        }
        if ((flags & 0x02) != 0)
        {
            if (header.Length - position < 2)
            {
                return false;
            }
            var expected = ComputeCrc32(header[..position]) & 0xFFFF;
            if ((header[position] | (header[position + 1] << 8)) != expected)
            {
                return false;
            }
            position += 2;
        }
        length = position;
        return true;
    }

    /// <summary>Reflected IEEE CRC-32, eight bytes per step (slicing-by-8) so a 32 MiB body stays cheap to verify.</summary>
    public static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<uint> table = CrcTable;
        var state = uint.MaxValue;
        while (bytes.Length >= 8)
        {
            var low = BinaryPrimitives.ReadUInt32LittleEndian(bytes) ^ state;
            var high = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            state = table[(7 * 256) + (int)(low & 0xFF)] ^ table[(6 * 256) + (int)((low >> 8) & 0xFF)] ^
                table[(5 * 256) + (int)((low >> 16) & 0xFF)] ^ table[(4 * 256) + (int)(low >> 24)] ^
                table[(3 * 256) + (int)(high & 0xFF)] ^ table[(2 * 256) + (int)((high >> 8) & 0xFF)] ^
                table[256 + (int)((high >> 16) & 0xFF)] ^ table[(int)(high >> 24)];
            bytes = bytes[8..];
        }
        foreach (var value in bytes)
        {
            state = table[(int)((state ^ value) & 0xFF)] ^ (state >> 8);
        }
        return ~state;
    }

    private static bool TrySkipTerminated(ReadOnlySpan<byte> header, ref int position)
    {
        var field = header[position..];
        var terminator = field[..Math.Min(field.Length, MaximumTerminatedFieldLength)].IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }
        position += terminator + 1;
        return true;
    }

    // Slice k maps a byte to its CRC contribution k positions further from the end of an eight-byte step.
    private static uint[] CreateCrcTable()
    {
        var table = new uint[8 * 256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320u);
            }
            table[index] = value;
        }
        for (var index = 256; index < table.Length; index++)
        {
            var previous = table[index - 256];
            table[index] = (previous >> 8) ^ table[previous & 0xFF];
        }
        return table;
    }

    /// <summary>
    /// Supplies the raw deflate bytes between the header and the trailer, withholding the last of them until every
    /// earlier byte has been requested. A raw inflater reads only after consuming all input it holds, so a request for
    /// that byte proves nothing earlier was left over, and any request after it proves the stream did not finish.
    /// </summary>
    public sealed class DeflateInput(byte[] wire, int start, int length) : Stream
    {
        private int consumed;

        /// <summary>The inflater asked for the final deflate byte, alone, after taking every earlier one.</summary>
        public bool FinalByteDelivered { get; private set; }

        /// <summary>The inflater asked for input after the final deflate byte: the member is truncated.</summary>
        public bool Starved { get; private set; }

        public override int Read(Span<byte> destination)
        {
            if (destination.IsEmpty)
            {
                return 0;
            }
            var remaining = length - consumed;
            if (remaining > 1)
            {
                var take = Math.Min(destination.Length, remaining - 1);
                wire.AsSpan(start + consumed, take).CopyTo(destination);
                consumed += take;
                return take;
            }
            if (remaining == 1)
            {
                destination[0] = wire[start + consumed];
                consumed++;
                FinalByteDelivered = true;
                return 1;
            }
            // A finished raw inflater never asks for this EOF.
            Starved = true;
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
