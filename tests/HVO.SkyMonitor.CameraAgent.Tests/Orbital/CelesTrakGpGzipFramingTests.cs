using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class CelesTrakGpGzipFramingTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("[{\"NORAD_CAT_ID\":25544}]");

    // Each shape's final four bytes equal, or are forged to equal, the decoded length the old ISIZE-only check compared.
    [TestMethod]
    [DataRow("trailing-bytes-forged-isize", "gzip-trailing-bytes")]
    [DataRow("duplicate-trailer", "gzip-trailing-bytes")]
    [DataRow("trailing-magic-byte", "gzip-trailing-bytes")]
    [DataRow("second-member", "gzip-trailing-bytes")]
    [DataRow("empty-member-then-member", "gzip-trailing-bytes")]
    [DataRow("truncated-member-forged-isize", "gzip-truncated")]
    [DataRow("truncated-deflate-genuine-trailer", "gzip-truncated")]
    [DataRow("empty-member-minus-two", "gzip-truncated")]
    [DataRow("trailer-missing", "gzip-truncated")]
    [DataRow("non-final-block", "gzip-truncated")]
    [DataRow("bad-crc-matching-isize", "gzip-crc-mismatch")]
    [DataRow("bad-isize", "gzip-size-mismatch")]
    public void MalformedMemberFramingIsRejected(string shape, string detailPrefix)
    {
        var wire = Malformed(shape);

        var outcome = CelesTrakGpClient.DecodeGzip(wire, 1000, CancellationToken.None);

        Assert.AreEqual("orbital.gzip-invalid", outcome.ReasonCode, $"{shape}: {outcome.Detail}");
        Assert.StartsWith(detailPrefix, outcome.Detail, shape);
        Assert.IsNull(outcome.Bytes, shape);
    }

    [TestMethod]
    public void TruncatedMemberForgesTheLengthTheOldCheckCompared()
    {
        // The shape's premise: a GZipStream decode of the whole wire yields exactly the length its last four bytes name.
        var wire = Malformed("truncated-member-forged-isize");
        using var lenient = new GZipStream(new MemoryStream(wire), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        lenient.CopyTo(decoded);

        Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(wire.Length - 4)), (uint)decoded.Length);
        Assert.AreEqual("gzip-truncated", CelesTrakGpClient.DecodeGzip(wire, 1000, CancellationToken.None).Detail);
    }

    [TestMethod]
    [DataRow("trailing-bytes-forged-isize")]
    [DataRow("second-member")]
    [DataRow("truncated-member-forged-isize")]
    public void OversizedDecodeIsReportedAsTheRawBoundBeforeAnyFramingVerdict(string shape)
    {
        var wire = Malformed(shape);

        var outcome = CelesTrakGpClient.DecodeGzip(wire, 2, CancellationToken.None);

        Assert.AreEqual("orbital.raw-bound", outcome.ReasonCode, outcome.Detail);
        Assert.AreEqual("decoded-bytes>2", outcome.Detail);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(1000)]
    [DataRow(65536)]
    public void MemberDecodingToExactlyTheCapIsAcceptedAndOneByteOverIsRejected(int cap)
    {
        var exact = Enumerable.Range(0, cap).Select(static i => (byte)(i * 31)).ToArray();
        var over = Enumerable.Range(0, cap + 1).Select(static i => (byte)(i * 31)).ToArray();

        var accepted = CelesTrakGpClient.DecodeGzip(CelesTrakGpTestData.Gzip(exact), cap, CancellationToken.None);
        var rejected = CelesTrakGpClient.DecodeGzip(CelesTrakGpTestData.Gzip(over), cap, CancellationToken.None);

        Assert.IsNull(accepted.ReasonCode, accepted.Detail);
        CollectionAssert.AreEqual(exact, accepted.Bytes);
        Assert.AreEqual("orbital.raw-bound", rejected.ReasonCode, rejected.Detail);
        Assert.AreEqual(FormattableString.Invariant($"decoded-bytes>{cap}"), rejected.Detail);
        Assert.IsNull(rejected.Bytes);
    }

    [TestMethod]
    public void EmptyMemberDecodesToNoBytes()
    {
        byte[] empty = [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0x03, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];

        var outcome = CelesTrakGpClient.DecodeGzip(empty, 1000, CancellationToken.None);

        Assert.IsNull(outcome.ReasonCode, outcome.Detail);
        Assert.IsEmpty(outcome.Bytes!);
    }

    [TestMethod]
    public void StoredAndCompressedBlocksAcrossInputBufferBoundariesDecode()
    {
        // The inflater pulls input in 8 KiB reads, so lengths around that size move the deflate end across a read.
        foreach (var length in new[] { 1, 2, 7, 8172, 8182, 8191, 8192, 8193, 16384, 65535, 65536, 200_000 })
        {
            var payload = Enumerable.Range(0, length).Select(static i => (byte)((i * 37) ^ (i >> 7))).ToArray();
            foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal,
                CompressionLevel.SmallestSize })
            {
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, level, leaveOpen: true))
                {
                    gzip.Write(payload);
                }
                var outcome = CelesTrakGpClient.DecodeGzip(output.ToArray(), 1 << 20, CancellationToken.None);

                Assert.IsNull(outcome.ReasonCode, $"{level}/size={length}: {outcome.Detail}");
                CollectionAssert.AreEqual(payload, outcome.Bytes, $"{level}/size={length}");
            }
        }
    }

    [TestMethod]
    [DataRow("plain")]
    [DataRow("extra")]
    [DataRow("extra-max")]
    [DataRow("name")]
    [DataRow("comment")]
    [DataRow("fhcrc")]
    [DataRow("all")]
    [DataRow("name-1024")]
    [DataRow("comment-1024")]
    [DataRow("ignored-fields")]
    public void OptionalHeaderFieldsAreParsedAndSkipped(string shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var outcome = CelesTrakGpClient.DecodeGzip(WithHeader(Payload, shape), 1000, CancellationToken.None);

        Assert.IsNull(outcome.ReasonCode, $"{shape}: {outcome.Detail}");
        CollectionAssert.AreEqual(Payload, outcome.Bytes, shape);
    }

    [TestMethod]
    [DataRow("wrong-id1")]
    [DataRow("wrong-id2")]
    [DataRow("wrong-cm")]
    [DataRow("reserved-32")]
    [DataRow("reserved-64")]
    [DataRow("reserved-128")]
    [DataRow("wrong-fhcrc")]
    [DataRow("name-1025")]
    [DataRow("comment-1025")]
    [DataRow("extra-overruns-trailer")]
    [DataRow("name-unterminated-before-trailer")]
    [DataRow("comment-unterminated-before-trailer")]
    [DataRow("fhcrc-overruns-trailer")]
    public void InvalidHeaderIsRejected(string shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var wire = WithHeader(Payload, shape);
        switch (shape)
        {
            case "wrong-id1": wire[0] = 0x1E; break;
            case "wrong-id2": wire[1] = 0x8C; break;
            case "wrong-cm": wire[2] = 0x07; break;
            case "reserved-32": wire[3] |= 32; break;
            case "reserved-64": wire[3] |= 64; break;
            case "reserved-128": wire[3] |= 128; break;
            // The header's own bytes may not run into the final eight, which are always the trailer.
            case "extra-overruns-trailer": wire = [.. wire[..3], 4, .. wire[4..10], 0xFF, 0x00, .. new byte[8]]; break;
            case "name-unterminated-before-trailer": wire = [.. wire[..3], 8, .. wire[4..10], 65, 65, 65, 0, 0, 0, 0, 0]; break;
            case "comment-unterminated-before-trailer": wire = [.. wire[..3], 16, .. wire[4..10], 66, 66, 66, 0, 0, 0, 0, 0]; break;
            case "fhcrc-overruns-trailer": wire = [.. wire[..3], 2, .. wire[4..10], .. new byte[8]]; break;
        }

        var outcome = CelesTrakGpClient.DecodeGzip(wire, 1000, CancellationToken.None);

        Assert.AreEqual("orbital.gzip-invalid", outcome.ReasonCode, $"{shape}: {outcome.Detail}");
        Assert.AreEqual("gzip-header", outcome.Detail, shape);
        Assert.IsNull(outcome.Bytes, shape);
    }

    [TestMethod]
    public void CorruptDeflateIsRejectedAsAStreamFault()
    {
        // BTYPE=11 is reserved; the inflater rejects it on the first block header.
        byte[] wire = [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0x07, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];

        var outcome = CelesTrakGpClient.DecodeGzip(wire, 1000, CancellationToken.None);

        Assert.AreEqual("orbital.gzip-invalid", outcome.ReasonCode, outcome.Detail);
        Assert.AreEqual("gzip-stream", outcome.Detail);
    }

    private static byte[] Malformed(string shape)
    {
        var valid = CelesTrakGpTestData.Gzip(Payload);
        byte[] empty = [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0x03, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        var forgedSuffix = new byte[8];
        "GARB"u8.CopyTo(forgedSuffix);
        BinaryPrimitives.WriteUInt32LittleEndian(forgedSuffix.AsSpan(4), (uint)Payload.Length);
        switch (shape)
        {
            case "trailing-bytes-forged-isize": return [.. valid, .. forgedSuffix];
            case "duplicate-trailer": return [.. valid, .. valid[^8..]];
            case "trailing-magic-byte": return [.. valid, 0x1F];
            case "second-member": return [.. valid, .. CelesTrakGpTestData.Gzip("[]"u8.ToArray())];
            // The empty first member contributes nothing, so the second member's ISIZE equals the whole decode.
            case "empty-member-then-member": return [.. empty, .. valid];
            case "truncated-member-forged-isize": return TruncatedStoredBlock();
            case "truncated-deflate-genuine-trailer":
                {
                    // A final stored block promises 32 bytes; three arrive, followed by a genuine trailer for those three.
                    byte[] partial = [65, 65, 65];
                    return [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0x01, 0x20, 0x00, 0xDF, 0xFF, .. partial,
                        .. CelesTrakGpTestData.Gzip(partial)[^8..]];
                }
            case "empty-member-minus-two": return empty[..^2];
            case "trailer-missing": return valid[..^8];
            case "non-final-block":
                {
                    // A stored block for the whole payload with BFINAL clear: the decode completes but the stream does not.
                    var stored = new List<byte> { 0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0x00 };
                    stored.AddRange([(byte)Payload.Length, 0, (byte)~Payload.Length, 0xFF]);
                    stored.AddRange(Payload);
                    stored.AddRange(valid[^8..]);
                    return [.. stored];
                }
            case "bad-crc-matching-isize": return Mutate(valid, valid.Length - 8, (byte)(valid[^8] ^ 0x01));
            case "bad-isize": return Mutate(valid, valid.Length - 4, (byte)(valid[^4] ^ 0x01));
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static byte[] TruncatedStoredBlock()
    {
        // A final stored block declares 32 bytes but only 12 follow, and those 12 end by forging ISIZE=12.
        return [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0x01, 0x20, 0x00, 0xDF, 0xFF,
            65, 65, 65, 65, 65, 65, 65, 65, 12, 0, 0, 0];
    }

    private static byte[] WithHeader(byte[] payload, string shape)
    {
        var valid = CelesTrakGpTestData.Gzip(payload);
        var header = valid[..10].ToList();
        var all = shape == "all";
        if (all || shape is "extra" or "extra-max")
        {
            header[3] |= 4;
            var length = shape == "extra-max" ? 65535 : 19;
            header.Add((byte)length);
            header.Add((byte)(length >> 8));
            header.AddRange(Enumerable.Repeat((byte)42, length));
        }
        if (all || shape.StartsWith("name-10", StringComparison.Ordinal) || shape == "name")
        {
            header[3] |= 8;
            header.AddRange(Enumerable.Repeat((byte)65, shape == "name-1025" ? 1024 : shape == "name-1024" ? 1023 : 5));
            header.Add(0);
        }
        if (all || shape.StartsWith("comment-10", StringComparison.Ordinal) || shape == "comment")
        {
            header[3] |= 16;
            header.AddRange(Enumerable.Repeat((byte)66, shape == "comment-1025" ? 1024 : shape == "comment-1024" ? 1023 : 7));
            header.Add(0);
        }
        if (shape == "ignored-fields")
        {
            header[3] |= 1; // FTEXT is permitted.
            for (var i = 4; i < 10; i++)
            {
                header[i] = 255;
            }
        }
        if (all || shape is "fhcrc" or "wrong-fhcrc")
        {
            header[3] |= 2;
            // An independent header CRC: the low half of the CRC-32 a GZipStream writes for the header bytes.
            var crc = CelesTrakGpTestData.Gzip([.. header])[^8..];
            header.Add((byte)(crc[0] ^ (shape == "wrong-fhcrc" ? 1 : 0)));
            header.Add(crc[1]);
        }
        return [.. header, .. valid[10..]];
    }

    private static byte[] Mutate(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }
}
