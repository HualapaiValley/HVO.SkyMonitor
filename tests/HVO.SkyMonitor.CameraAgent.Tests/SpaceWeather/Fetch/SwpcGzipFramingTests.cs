using System.Buffers.Binary;
using System.Net;
using System.IO.Compression;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcGzipFramingTests
{
    [TestMethod]
    [DataRow("a-empty-minus-two", "single")]
    [DataRow("a-empty-minus-two", "one-byte")]
    [DataRow("a-empty-minus-two", "mixed")]
    [DataRow("b-forged-suffix", "single")]
    [DataRow("b-forged-suffix", "one-byte")]
    [DataRow("b-forged-suffix", "mixed")]
    [DataRow("c-empty-prefix", "single")]
    [DataRow("c-empty-prefix", "one-byte")]
    [DataRow("c-empty-prefix", "mixed")]
    [DataRow("d-duplicate-trailer", "single")]
    [DataRow("d-duplicate-trailer", "one-byte")]
    [DataRow("d-duplicate-trailer", "mixed")]
    [DataRow("e-trailing-magic-byte", "single")]
    [DataRow("e-trailing-magic-byte", "one-byte")]
    [DataRow("e-trailing-magic-byte", "mixed")]
    [DataRow("f-two-members", "single")]
    [DataRow("f-two-members", "one-byte")]
    [DataRow("f-two-members", "mixed")]
    [DataRow("g-truncated-deflate-forged-size", "single")]
    [DataRow("g-truncated-deflate-forged-size", "one-byte")]
    [DataRow("g-truncated-deflate-forged-size", "mixed")]
    [DataRow("h-wrong-crc", "single")]
    [DataRow("h-wrong-crc", "one-byte")]
    [DataRow("h-wrong-crc", "mixed")]
    public async Task MalformedMemberIsRejected(string shape, string delivery)
    {
        var payload = Encoding.ASCII.GetBytes("payload");
        var valid = SwpcTestData.Gzip(payload);
        var empty = SwpcTestData.EmptyGzipMember;
        Assert.AreEqual(20, empty.Length);
        var suffix = new byte[8];
        "GARB"u8.CopyTo(suffix);
        BinaryPrimitives.WriteUInt32LittleEndian(suffix.AsSpan(4), (uint)payload.Length);
        var wire = shape switch
        {
            "a-empty-minus-two" => empty[..^2],
            "b-forged-suffix" => valid.Concat(suffix).ToArray(),
            "c-empty-prefix" => empty.Concat(valid).ToArray(),
            "d-duplicate-trailer" => valid.Concat(valid[^8..]).ToArray(),
            "e-trailing-magic-byte" => valid.Append((byte)0x1f).ToArray(),
            "f-two-members" => valid.Concat(valid).ToArray(),
            "g-truncated-deflate-forged-size" => TruncatedStoredBlock(),
            "h-wrong-crc" => valid,
            _ => throw new AssertFailedException(shape)
        };
        if (shape == "h-wrong-crc") wire[^8] ^= 0xff;
        var result = await Fetch(wire, delivery).ConfigureAwait(false);
        Console.WriteLine($"{shape} / {delivery}: {(result.BodyUndecodable ? "rejected" : "accepted")}; decoded={result.Body.Length}; failure={result.Failure}");
        Assert.AreEqual(SwpcTransportFailure.None, result.Failure, $"{shape}/{delivery}");
        Assert.IsTrue(result.BodyUndecodable, $"{shape}/{delivery} must reject malformed framing.");
        Assert.IsFalse(result.Oversize, $"{shape}/{delivery}");
        Assert.IsTrue(result.Body.IsEmpty, $"{shape}/{delivery}");
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task CompleteMembersAndOptionalHeadersDecode(string delivery)
    {
        foreach (var length in new[] { 0, 1, 7, 127, 8192, 65536 })
        {
            var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
            await AssertDecoded(SwpcTestData.Gzip(payload), payload, delivery, $"size={length}").ConfigureAwait(false);
        }
        byte[] small = [1, 2, 3];
        foreach (var shape in new[] { "extra", "extra-max", "name", "comment", "fhcrc", "all", "name-1024", "comment-1024", "ignored-fields" })
            await AssertDecoded(WithHeader(small, shape), small, delivery, shape).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("name-1025", "single")]
    [DataRow("name-1025", "one-byte")]
    [DataRow("name-1025", "mixed")]
    [DataRow("comment-1025", "single")]
    [DataRow("comment-1025", "one-byte")]
    [DataRow("comment-1025", "mixed")]
    [DataRow("wrong-fhcrc", "single")]
    [DataRow("wrong-fhcrc", "one-byte")]
    [DataRow("wrong-fhcrc", "mixed")]
    [DataRow("reserved-32", "single")]
    [DataRow("reserved-32", "one-byte")]
    [DataRow("reserved-32", "mixed")]
    [DataRow("reserved-64", "single")]
    [DataRow("reserved-64", "one-byte")]
    [DataRow("reserved-64", "mixed")]
    [DataRow("reserved-128", "single")]
    [DataRow("reserved-128", "one-byte")]
    [DataRow("reserved-128", "mixed")]
    [DataRow("wrong-cm", "single")]
    [DataRow("wrong-cm", "one-byte")]
    [DataRow("wrong-cm", "mixed")]
    [DataRow("wrong-id2", "single")]
    [DataRow("wrong-id2", "one-byte")]
    [DataRow("wrong-id2", "mixed")]
    [DataRow("wrong-isize", "single")]
    [DataRow("wrong-isize", "one-byte")]
    [DataRow("wrong-isize", "mixed")]
    [DataRow("short-member", "single")]
    [DataRow("short-member", "one-byte")]
    [DataRow("short-member", "mixed")]
    [DataRow("trailing-00", "single")]
    [DataRow("trailing-00", "one-byte")]
    [DataRow("trailing-00", "mixed")]
    [DataRow("trailing-1f", "single")]
    [DataRow("trailing-1f", "one-byte")]
    [DataRow("trailing-1f", "mixed")]
    [DataRow("trailing-8b", "single")]
    [DataRow("trailing-8b", "one-byte")]
    [DataRow("trailing-8b", "mixed")]
    [DataRow("extra-truncated", "single")]
    [DataRow("extra-truncated", "one-byte")]
    [DataRow("extra-truncated", "mixed")]
    [DataRow("name-unterminated", "single")]
    [DataRow("name-unterminated", "one-byte")]
    [DataRow("name-unterminated", "mixed")]
    [DataRow("comment-unterminated", "single")]
    [DataRow("comment-unterminated", "one-byte")]
    [DataRow("comment-unterminated", "mixed")]
    [DataRow("fhcrc-truncated", "single")]
    [DataRow("fhcrc-truncated", "one-byte")]
    [DataRow("fhcrc-truncated", "mixed")]
    [DataRow("trailer-missing", "single")]
    [DataRow("trailer-missing", "one-byte")]
    [DataRow("trailer-missing", "mixed")]
    [DataRow("non-final-block", "single")]
    [DataRow("non-final-block", "one-byte")]
    [DataRow("non-final-block", "mixed")]
    public async Task InvalidHeaderTrailerOrSuffixIsRejected(string shape, string delivery)
    {
        ArgumentNullException.ThrowIfNull(shape);
        byte[] payload = [1, 2, 3];
        var wire = WithHeader(payload, shape);
        switch (shape)
        {
            case "wrong-cm": wire[2] = 0; break;
            case "wrong-id2": wire[1] = 0; break;
            case "reserved-32": wire[3] |= 32; break;
            case "reserved-64": wire[3] |= 64; break;
            case "reserved-128": wire[3] |= 128; break;
            case "wrong-isize": wire[^4] ^= 1; break;
            case "short-member": wire = wire[..17]; break;
            case "trailing-00": wire = wire.Append((byte)0).ToArray(); break;
            case "trailing-1f": wire = wire.Append((byte)0x1f).ToArray(); break;
            case "trailing-8b": wire = wire.Append((byte)0x8b).ToArray(); break;
            case "extra-truncated": wire[3] = 4; wire = wire[..11]; break;
            case "name-unterminated": wire[3] = 8; wire = wire[..10].Concat(new byte[] { 65, 65 }).ToArray(); break;
            case "comment-unterminated": wire[3] = 16; wire = wire[..10].Concat(new byte[] { 65, 65 }).ToArray(); break;
            case "fhcrc-truncated": wire[3] = 2; wire = wire[..11]; break;
            case "trailer-missing": wire = wire[..^8]; break;
            case "non-final-block": wire[10] &= 0xfe; break;
        }
        var response = await Fetch(wire, delivery).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.None, response.Failure, $"{shape}/{delivery}");
        Assert.IsTrue(response.BodyUndecodable, $"{shape}/{delivery} must reject.");
        Assert.IsFalse(response.Oversize, $"{shape}/{delivery}");
        Assert.IsTrue(response.Body.IsEmpty, $"{shape}/{delivery}");
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task IncompleteDeflateWithMatchingTrailerIsRejected(string delivery)
    {
        byte[] partial = [65, 65, 65];
        // Stored block promises 32 decoded bytes. Only three and a genuine trailer for those three arrive.
        byte[] headerAndBlock = [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 255, 1, 32, 0, 223, 255];
        var wire = headerAndBlock.Concat(partial).Concat(SwpcTestData.Gzip(partial)[^8..]).ToArray();
        var response = await Fetch(wire, delivery).ConfigureAwait(false);
        Assert.IsTrue(response.BodyUndecodable, $"starved/{delivery}");
        Assert.IsTrue(response.Body.IsEmpty);
        Assert.AreEqual(SwpcTransportFailure.None, response.Failure);
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task StoredAndCompressedBlocksAcrossReadBoundariesDecode(string delivery)
    {
        foreach (var length in new[] { 1, 2, 8172, 8182, 8191, 8192, 8193, 65535, 65536 })
        {
            var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
            foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest })
            {
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, level, leaveOpen: true)) gzip.Write(payload);
                await AssertDecoded(output.ToArray(), payload, delivery, $"{level}/size={length}").ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task DeclaredConnectionTruncationWinsOverFraming(string delivery)
    {
        var valid = SwpcTestData.Gzip([1, 2, 3]);
        for (var length = 0; length < valid.Length; length++)
        {
            var response = await Fetch(valid[..length], delivery, valid.Length).ConfigureAwait(false);
            Assert.AreEqual(SwpcTransportFailure.NetworkError, response.Failure, $"length={length}/{delivery}");
            Assert.IsFalse(response.BodyUndecodable);
            Assert.IsTrue(response.Body.IsEmpty);
        }
    }

    [TestMethod]
    [DataRow("io")]
    [DataRow("http-io")]
    [DataRow("deadline")]
    [DataRow("caller")]
    public async Task TransportAndCancellationOutcomesSurviveInsideDeflateReads(string shape)
    {
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        using var caller = new CancellationTokenSource();
        var content = new SwpcContent(SwpcTestData.Gzip([1, 2, 3]), chunk: 11, afterBytes: 11,
            pending: shape is "deadline" or "caller", failure: shape switch
            {
                "io" => new IOException("wire failed"),
                "http-io" => new HttpIOException(HttpRequestError.ResponseEnded),
                _ => null
            });
        content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        using var handler = new SwpcStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var fetch = new SwpcClient(http, clock, SwpcSource.UserAgent)
            .FetchAsync(SwpcProduct.Scales, null, caller.Token).AsTask();
        if (shape is "deadline" or "caller")
        {
            await content.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (shape == "deadline") clock.Advance(SwpcSource.RequestTimeout);
            else await caller.CancelAsync().ConfigureAwait(false);
        }
        if (shape == "caller")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => fetch).ConfigureAwait(false);
            Assert.AreEqual(caller.Token, error.CancellationToken);
        }
        else
        {
            var response = await fetch.ConfigureAwait(false);
            Assert.AreEqual(shape == "deadline" ? SwpcTransportFailure.Timeout : SwpcTransportFailure.NetworkError, response.Failure);
            Assert.IsNull(response.StatusCode);
            Assert.IsFalse(response.BodyUndecodable);
            Assert.IsTrue(response.Body.IsEmpty);
        }
    }

    private static async Task AssertDecoded(byte[] wire, byte[] expected, string delivery, string shape)
    {
        var response = await Fetch(wire, delivery).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.None, response.Failure, $"{shape}/{delivery}");
        Assert.IsFalse(response.BodyUndecodable, $"{shape}/{delivery}");
        Assert.IsFalse(response.Oversize, $"{shape}/{delivery}");
        CollectionAssert.AreEqual(expected, response.Body.ToArray(), $"{shape}/{delivery}");
    }

    private static byte[] WithHeader(byte[] payload, string shape)
    {
        var valid = SwpcTestData.Gzip(payload);
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
        if (all || shape.StartsWith("name", StringComparison.Ordinal))
        {
            header[3] |= 8;
            header.AddRange(Enumerable.Repeat((byte)65, shape == "name-1025" ? 1024 : shape == "name-1024" ? 1023 : 5));
            header.Add(0);
        }
        if (all || shape.StartsWith("comment", StringComparison.Ordinal))
        {
            header[3] |= 16;
            header.AddRange(Enumerable.Repeat((byte)66, shape == "comment-1025" ? 1024 : shape == "comment-1024" ? 1023 : 7));
            header.Add(0);
        }
        if (shape == "ignored-fields")
        {
            header[3] |= 1; // FTEXT is permitted.
            for (var i = 4; i < 10; i++) header[i] = 255;
        }
        if (all || shape is "fhcrc" or "wrong-fhcrc")
        {
            header[3] |= 2;
            // Independent header CRC from a GZipStream-produced trailer.
            var crc = SwpcTestData.Gzip(header.ToArray())[^8..];
            header.Add((byte)(crc[0] ^ (shape == "wrong-fhcrc" ? 1 : 0)));
            header.Add(crc[1]);
        }
        return header.Concat(valid[10..]).ToArray();
    }

    private static byte[] TruncatedStoredBlock()
    {
        // Final stored block declares 32 bytes but only 12 arrive. Its final four bytes forge ISIZE=12.
        byte[] wire = [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 255, 1, 32, 0, 223, 255,
            65, 65, 65, 65, 65, 65, 65, 65, 12, 0, 0, 0];
        return wire;
    }

    private static async Task<SwpcHttpResponse> Fetch(byte[] wire, string delivery, long? declaredLength = null)
    {
        var content = new SwpcContent(wire, chunk: delivery == "one-byte" ? 1 : int.MaxValue,
            chunks: delivery == "mixed" ? [2, 11, 1, 7, 3, 19] : null);
        content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        content.Headers.ContentLength = declaredLength;
        using var handler = new SwpcStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent)
            .FetchAsync(SwpcProduct.Scales, null, CancellationToken.None).ConfigureAwait(false);
    }
}
