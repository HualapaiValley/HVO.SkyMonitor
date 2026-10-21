using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcGzipPrimitiveTests
{
    [TestMethod]
    [DataRow("", 0x00000000u)]
    [DataRow("a", 0xe8b7be43u)]
    [DataRow("123456789", 0xcbf43926u)]
    public void CrcMatchesKnownIeeeVectors(string text, uint expected)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Assert.AreEqual(expected, SwpcCrc32.Compute(bytes));
        var incremental = SwpcCrc32.Initial;
        foreach (var value in bytes) incremental = SwpcCrc32.Append(incremental, value);
        Assert.AreEqual(expected, ~incremental);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(9)]
    [DataRow(65536)]
    public void CrcMatchesMemberTrailers(int length)
    {
        var decoded = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
        // Empty uses the canonical member; nonempty members and their trailers come from GZipStream.
        var wire = SwpcTestData.Gzip(decoded);
        Assert.IsTrue(wire.Length >= 20);
        Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(wire.Length - 8)), SwpcCrc32.Compute(decoded));
        var split = decoded.Length / 2;
        var state = SwpcCrc32.Append(SwpcCrc32.Initial, decoded.AsSpan(0, split));
        Assert.AreEqual(SwpcCrc32.Compute(decoded), ~SwpcCrc32.Append(state, decoded.AsSpan(split)));
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task HoldbackRequiresLoneReadAndExactlyEightTrailerBytes(string delivery)
    {
        var wire = SwpcTestData.Gzip([1, 2, 3]);
        using var content = Content(wire, delivery);
        using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var member = new SwpcGzipMemberStream(source);
        await member.ReadHeaderAsync(CancellationToken.None).ConfigureAwait(false);
        var buffer = new byte[8192];
        var bulk = await member.ReadAsync(buffer).ConfigureAwait(false);
        Assert.AreEqual(wire.Length - 10 - 9, bulk, "Fragmentation must still produce a bulk delivery.");
        Assert.IsFalse(member.LoneByteDelivered, "The isolated byte has not been requested.");
        Assert.IsFalse(member.HasExactTrailer, "Nine undelivered bytes are not an eight-byte trailer.");
        Assert.IsFalse(member.DecoderStarved);
        Assert.AreEqual(1, await member.ReadAsync(buffer).ConfigureAwait(false));
        Assert.IsTrue(member.LoneByteDelivered);
        Assert.IsTrue(member.HasExactTrailer);
        Assert.IsFalse(member.DecoderStarved);
        CollectionAssert.AreEqual(wire[^8..], member.Trailer.ToArray());
        Assert.AreEqual(0, await member.ReadAsync(Memory<byte>.Empty).ConfigureAwait(false));
        Assert.IsFalse(member.DecoderStarved, "An empty destination cannot starve a decoder.");
        Assert.AreEqual(0, await member.ReadAsync(buffer).ConfigureAwait(false));
        Assert.IsTrue(member.DecoderStarved, "Requesting EOF after the isolated byte proves starvation.");
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task RawInflaterFinishesOnTheIsolatedByteWithoutRequestingEof(string delivery)
    {
        foreach (var length in new[] { 0, 7, 8192, 65536 })
        {
            var decoded = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
            using var content = Content(SwpcTestData.Gzip(decoded), delivery);
            using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var member = new SwpcGzipMemberStream(source);
            await member.ReadHeaderAsync(CancellationToken.None).ConfigureAwait(false);
            using var decoder = new DeflateStream(member, CompressionMode.Decompress, leaveOpen: true);
            using var output = new MemoryStream();
            // Exercise the same ReadAsync protocol as the bounded client, including the first zero read.
            var buffer = new byte[8192];
            while (true)
            {
                var read = await decoder.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            Assert.IsTrue(member.LoneByteDelivered, $"length={length}/{delivery}");
            Assert.IsFalse(member.DecoderStarved, $"length={length}/{delivery}");
            Assert.IsTrue(member.HasExactTrailer, $"length={length}/{delivery}");
            CollectionAssert.AreEqual(decoded, output.ToArray(), $"length={length}/{delivery}");
        }
    }

    private static SwpcContent Content(byte[] wire, string delivery) => new(wire,
        chunk: delivery == "one-byte" ? 1 : int.MaxValue,
        chunks: delivery == "mixed" ? [2, 11, 1, 7, 3, 19] : null);
}
