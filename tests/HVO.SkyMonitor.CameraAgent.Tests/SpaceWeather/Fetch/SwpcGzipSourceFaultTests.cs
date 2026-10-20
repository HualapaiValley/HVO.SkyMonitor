using System.Globalization;
using System.IO.Compression;
using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcGzipSourceFaultTests
{
    private static int Bound => SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales) + 1;

    [TestMethod]
    [DataRow("single", false, false)]
    [DataRow("single", false, true)]
    [DataRow("single", true, false)]
    [DataRow("single", true, true)]
    [DataRow("one-byte", false, false)]
    [DataRow("one-byte", false, true)]
    [DataRow("one-byte", true, false)]
    [DataRow("one-byte", true, true)]
    [DataRow("mixed", false, false)]
    [DataRow("mixed", false, true)]
    [DataRow("mixed", true, false)]
    [DataRow("mixed", true, true)]
    public async Task SourceInvalidDataDuringDrainPropagatesOnce(string delivery, bool declared, bool once)
    {
        var wire = RejectedWire("completion", 2 * Bound + 32768);
        var rejection = await RejectionPointAsync(wire, delivery).ConfigureAwait(false);
        Assert.IsTrue(wire.Length >= rejection + 2 * Bound + 8192);
        var faultAt = rejection + 7 * 8192;
        Assert.IsTrue(faultAt > rejection, "The injected fault must be strictly inside the drain.");
        var expected = new InvalidDataException("source drain fault");
        var content = Content(wire, delivery, expected, faultAt, once);
        content.Headers.ContentLength = declared ? wire.Length + 1 : null;
        var (result, error) = await ObserveAsync(Fetch(content)).ConfigureAwait(false);
        Record($"R{(once ? "" : "'")}/{declared}/{delivery}", content, rejection, result, error);
        AssertFault(content, rejection, faultAt, expected, error);
        Assert.AreEqual(7 * 8192, content.BytesRead - rejection);
    }

    [TestMethod]
    [DataRow("header-first", "invalid-data", "single")]
    [DataRow("header-first", "invalid-data", "one-byte")]
    [DataRow("header-first", "invalid-data", "mixed")]
    [DataRow("header-first", "invalid-operation", "single")]
    [DataRow("header-first", "invalid-operation", "one-byte")]
    [DataRow("header-first", "invalid-operation", "mixed")]
    [DataRow("header-partial", "invalid-data", "single")]
    [DataRow("header-partial", "invalid-data", "one-byte")]
    [DataRow("header-partial", "invalid-data", "mixed")]
    [DataRow("header-partial", "invalid-operation", "single")]
    [DataRow("header-partial", "invalid-operation", "one-byte")]
    [DataRow("header-partial", "invalid-operation", "mixed")]
    [DataRow("inflater", "invalid-data", "single")]
    [DataRow("inflater", "invalid-data", "one-byte")]
    [DataRow("inflater", "invalid-data", "mixed")]
    [DataRow("inflater", "invalid-operation", "single")]
    [DataRow("inflater", "invalid-operation", "one-byte")]
    [DataRow("inflater", "invalid-operation", "mixed")]
    public async Task UnexpectedSourceFaultDuringDecodingPropagatesUnchanged(string stage, string kind, string delivery)
    {
        var wire = ValidWire();
        var faultAt = FaultPoint(stage);
        Exception expected = kind == "invalid-data" ? new InvalidDataException("source decode fault")
            : new InvalidOperationException("unexpected source decode fault");
        var content = Content(wire, delivery, expected, faultAt, once: true);
        var (result, error) = await ObserveAsync(Fetch(content)).ConfigureAwait(false);
        Record($"{(kind == "invalid-data" ? stage == "inflater" ? "D" : "H" : "X")}/{stage}/{delivery}",
            content, null, result, error);
        AssertFault(content, null, faultAt, expected, error);
    }

    [TestMethod]
    [DataRow("single")]
    [DataRow("one-byte")]
    [DataRow("mixed")]
    public async Task OtherUnexpectedSourceFaultDuringDrainPropagatesUnchanged(string delivery)
    {
        var wire = RejectedWire("completion", 2 * Bound + 32768);
        var rejection = await RejectionPointAsync(wire, delivery).ConfigureAwait(false);
        var expected = new InvalidOperationException("unexpected source drain fault");
        var faultAt = rejection + 8192;
        var content = Content(wire, delivery, expected, faultAt, once: true);
        var (result, error) = await ObserveAsync(Fetch(content)).ConfigureAwait(false);
        Record($"X/drain/{delivery}", content, rejection, result, error);
        AssertFault(content, rejection, faultAt, expected, error);
    }

    [TestMethod]
    [DataRow("wrong-id2", false, "single")]
    [DataRow("wrong-id2", false, "one-byte")]
    [DataRow("wrong-id2", false, "mixed")]
    [DataRow("wrong-id2", true, "single")]
    [DataRow("wrong-id2", true, "one-byte")]
    [DataRow("wrong-id2", true, "mixed")]
    [DataRow("reserved-flag", false, "single")]
    [DataRow("reserved-flag", false, "one-byte")]
    [DataRow("reserved-flag", false, "mixed")]
    [DataRow("reserved-flag", true, "single")]
    [DataRow("reserved-flag", true, "one-byte")]
    [DataRow("reserved-flag", true, "mixed")]
    [DataRow("invalid-deflate", false, "single")]
    [DataRow("invalid-deflate", false, "one-byte")]
    [DataRow("invalid-deflate", false, "mixed")]
    [DataRow("invalid-deflate", true, "single")]
    [DataRow("invalid-deflate", true, "one-byte")]
    [DataRow("invalid-deflate", true, "mixed")]
    public async Task ClientDecodingFaultStillDrainsExactlyOnce(string shape, bool truncated, string delivery)
    {
        var wire = RejectedWire(shape, 20000);
        var rejection = await RejectionPointAsync(wire, delivery).ConfigureAwait(false);
        var content = Content(wire, delivery);
        content.Headers.ContentLength = wire.Length + (truncated ? 1 : 0);
        var result = await Fetch(content).ConfigureAwait(false);
        Assert.AreEqual(truncated ? SwpcTransportFailure.NetworkError : SwpcTransportFailure.None, result.Failure,
            $"{shape}/{truncated}/{delivery}");
        Assert.AreEqual(!truncated, result.BodyUndecodable);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.IsFalse(result.Oversize);
        Assert.AreEqual(wire.Length, content.BytesRead, "The rejection must drain through EOF.");
        AssertDrainBound(content, rejection);
        // Settle EOF with one zero read, without a further probe.
        Assert.AreEqual(1, content.ZeroReadCalls, "Exactly one EOF read settles the rejection.");
    }

    [TestMethod]
    [DataRow("header-first", "io", "single")]
    [DataRow("header-first", "io", "one-byte")]
    [DataRow("header-first", "io", "mixed")]
    [DataRow("header-first", "http-io", "single")]
    [DataRow("header-first", "http-io", "one-byte")]
    [DataRow("header-first", "http-io", "mixed")]
    [DataRow("header-first", "deadline", "single")]
    [DataRow("header-first", "deadline", "one-byte")]
    [DataRow("header-first", "deadline", "mixed")]
    [DataRow("header-first", "caller", "single")]
    [DataRow("header-first", "caller", "one-byte")]
    [DataRow("header-first", "caller", "mixed")]
    [DataRow("inflater", "io", "single")]
    [DataRow("inflater", "io", "one-byte")]
    [DataRow("inflater", "io", "mixed")]
    [DataRow("inflater", "http-io", "single")]
    [DataRow("inflater", "http-io", "one-byte")]
    [DataRow("inflater", "http-io", "mixed")]
    [DataRow("inflater", "deadline", "single")]
    [DataRow("inflater", "deadline", "one-byte")]
    [DataRow("inflater", "deadline", "mixed")]
    [DataRow("inflater", "caller", "single")]
    [DataRow("inflater", "caller", "one-byte")]
    [DataRow("inflater", "caller", "mixed")]
    [DataRow("drain", "io", "single")]
    [DataRow("drain", "io", "one-byte")]
    [DataRow("drain", "io", "mixed")]
    [DataRow("drain", "http-io", "single")]
    [DataRow("drain", "http-io", "one-byte")]
    [DataRow("drain", "http-io", "mixed")]
    [DataRow("drain", "deadline", "single")]
    [DataRow("drain", "deadline", "one-byte")]
    [DataRow("drain", "deadline", "mixed")]
    [DataRow("drain", "caller", "single")]
    [DataRow("drain", "caller", "one-byte")]
    [DataRow("drain", "caller", "mixed")]
    public async Task SourceTransportAndCancellationKeepTheirOutcomes(string stage, string kind, string delivery)
    {
        var wire = stage == "drain" ? RejectedWire("completion", 2 * Bound + 32768) : ValidWire();
        int? rejection = stage == "drain" ? await RejectionPointAsync(wire, delivery).ConfigureAwait(false) : null;
        var faultAt = rejection + 8192 ?? FaultPoint(stage);
        var content = Content(wire, delivery, kind switch
        {
            "io" => new IOException("source I/O fault"),
            "http-io" => new HttpIOException(HttpRequestError.ResponseEnded),
            _ => null
        }, faultAt, once: true, pending: kind is "deadline" or "caller");
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        using var caller = new CancellationTokenSource();
        var fetch = Fetch(content, clock, caller.Token);
        if (kind is "deadline" or "caller")
        {
            await content.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.IsFalse(content.ReaderDisposed);
            if (kind == "deadline") clock.Advance(SwpcSource.RequestTimeout);
            else await caller.CancelAsync().ConfigureAwait(false);
        }
        if (kind == "caller")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => fetch).ConfigureAwait(false);
            Assert.AreEqual(caller.Token, error.CancellationToken);
        }
        else
        {
            var result = await fetch.ConfigureAwait(false);
            Assert.AreEqual(kind == "deadline" ? SwpcTransportFailure.Timeout : SwpcTransportFailure.NetworkError, result.Failure);
            Assert.IsNull(result.StatusCode);
            Assert.IsTrue(result.Body.IsEmpty);
        }
        Assert.AreEqual(faultAt, content.BytesRead);
        Assert.AreEqual(0, content.ReadsAfterThrow);
        AssertDrainBound(content, rejection);
        Assert.IsTrue(content.ReaderDisposed);
    }

    private static int FaultPoint(string stage) => stage switch
    {
        "header-first" => 0,
        "header-partial" => 4,
        "inflater" => 10000,
        _ => throw new AssertFailedException(stage)
    };

    private static byte[] ValidWire()
    {
        var bytes = new byte[32768];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)index;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.NoCompression, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static byte[] RejectedWire(string shape, int size)
    {
        var wire = new byte[size];
        SwpcTestData.EmptyGzipMember.CopyTo(wire, 0);
        switch (shape)
        {
            case "completion": break;
            case "wrong-id2": wire[1] = 0; break;
            case "reserved-flag": wire[3] = 32; break;
            case "invalid-deflate": wire[10] = 7; break;
            default: throw new AssertFailedException(shape);
        }
        return wire;
    }

    private static SwpcContent Content(byte[] wire, string delivery, Exception? failure = null, int faultAt = 0,
        bool once = false, bool pending = false) => new(wire, failure: failure, afterBytes: faultAt,
            faultOnce: once, limitAtOutcome: true, pending: pending,
            chunk: delivery == "one-byte" ? 1 : int.MaxValue,
            chunks: delivery == "mixed" ? [2, 11, 1, 7, 3, 19] : null);

    private static async Task<int> RejectionPointAsync(byte[] wire, string delivery)
    {
        // Fault-free control uses the unchanged framing/hold-back protocol and the client's bounded ReadAsync shape.
        using var content = Content(wire, delivery);
        var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var sourceDisposal = source.ConfigureAwait(false);
        var member = new SwpcGzipMemberStream(source);
        await using var memberDisposal = member.ConfigureAwait(false);
        try
        {
            await member.ReadHeaderAsync(CancellationToken.None).ConfigureAwait(false);
            var decoder = new DeflateStream(member, CompressionMode.Decompress, leaveOpen: true);
            await using var decoderDisposal = decoder.ConfigureAwait(false);
            var bytes = new byte[Bound];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await decoder.ReadAsync(bytes.AsMemory(length)).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            Assert.IsTrue(length < Bound);
            Assert.IsTrue(!member.LoneByteDelivered || member.DecoderStarved || !member.HasExactTrailer);
        }
        catch (InvalidDataException) { }
        return content.BytesRead;
    }

    private static void AssertFault(SwpcContent content, int? rejection, int faultAt, Exception expected, Exception? error)
    {
        AssertDrainBound(content, rejection);
        Assert.AreSame(expected, error, "The original source exception must propagate unchanged.");
        Assert.AreEqual(faultAt, content.BytesAtThrow);
        Assert.AreEqual(faultAt, content.BytesRead);
        Assert.AreEqual(1, content.FaultCount, "Do not encounter a repeated source fault.");
        Assert.AreEqual(0, content.ReadsAfterThrow, "Never read again after a source exception.");
        Assert.IsTrue(content.ReaderDisposed);
    }

    private static void AssertDrainBound(SwpcContent content, int? rejection)
    {
        var discarded = rejection.HasValue ? content.BytesRead - rejection.Value : 0;
        Assert.IsTrue(discarded >= 0 && discarded <= Bound, $"Discarded {discarded}; bound {Bound}.");
    }

    private static void Record(string name, SwpcContent content, int? rejection, SwpcHttpResponse? result, Exception? error) =>
        Console.WriteLine($"REPRO {name}: outcome={error?.GetType().Name ?? (result!.BodyUndecodable ? "BodyUndecodable" : result.Failure.ToString())}; " +
            $"rejection={rejection?.ToString(CultureInfo.InvariantCulture) ?? "none"}; afterRejection={(rejection.HasValue ? content.BytesRead - rejection.Value : 0)}; " +
            $"bytes={content.BytesRead}; readsAfterThrow={content.ReadsAfterThrow}; faults={content.FaultCount}");

    private static async Task<(SwpcHttpResponse? Result, Exception? Error)> ObserveAsync(Task<SwpcHttpResponse> fetch)
    {
        try { return (await fetch.ConfigureAwait(false), null); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return (null, error); }
    }

    private static async Task<SwpcHttpResponse> Fetch(SwpcContent content, ManualTimeProvider? clock = null,
        CancellationToken token = default)
    {
        content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        using var handler = new SwpcStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await new SwpcClient(http, clock ?? new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent)
            .FetchAsync(SwpcProduct.Scales, null, token).ConfigureAwait(false);
    }
}
