using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcGzipRejectionDrainTests
{
    [TestMethod]
    [DataRow("a-empty-padded", "single")]
    [DataRow("a-empty-padded", "one-byte")]
    [DataRow("a-empty-padded", "mixed")]
    [DataRow("b-trailing-garbage", "single")]
    [DataRow("b-trailing-garbage", "one-byte")]
    [DataRow("b-trailing-garbage", "mixed")]
    [DataRow("c-wrong-id2", "single")]
    [DataRow("c-wrong-id2", "one-byte")]
    [DataRow("c-wrong-id2", "mixed")]
    [DataRow("c-reserved-flag", "single")]
    [DataRow("c-reserved-flag", "one-byte")]
    [DataRow("c-reserved-flag", "mixed")]
    [DataRow("d-invalid-deflate", "single")]
    [DataRow("d-invalid-deflate", "one-byte")]
    [DataRow("d-invalid-deflate", "mixed")]
    public async Task DeclaredTruncationWinsOnEveryGzipRejection(string shape, string delivery)
    {
        var wire = RejectedWire(shape);
        var content = Content(wire, delivery);
        content.Headers.ContentLength = shape == "a-empty-padded" ? 9000 : wire.Length + 1;
        var result = await Fetch(content).ConfigureAwait(false);
        Console.WriteLine($"{shape}/{delivery}: Failure={result.Failure}, BodyUndecodable={result.BodyUndecodable}, bytesRead={content.BytesRead}");
        Assert.AreEqual(SwpcTransportFailure.NetworkError, result.Failure, $"{shape}/{delivery}");
        Assert.IsNull(result.StatusCode);
        Assert.IsFalse(result.BodyUndecodable);
        Assert.IsFalse(result.Oversize);
        Assert.IsTrue(result.Body.IsEmpty);
    }

    [TestMethod]
    [DataRow(true, "single")]
    [DataRow(true, "one-byte")]
    [DataRow(true, "mixed")]
    [DataRow(false, "single")]
    [DataRow(false, "one-byte")]
    [DataRow(false, "mixed")]
    public async Task PaddedMemberWithMatchingOrAbsentLengthIsUndecodable(bool declared, string delivery)
    {
        var wire = RejectedWire("a-empty-padded");
        var content = Content(wire, delivery);
        content.Headers.ContentLength = declared ? wire.Length : null;
        var result = await Fetch(content).ConfigureAwait(false);
        AssertUndecodable(result);
        Assert.AreEqual(wire.Length, content.BytesRead);
        Assert.IsTrue(content.ReaderDisposed);
    }

    [TestMethod]
    [DataRow("a-empty-padded", "io")]
    [DataRow("a-empty-padded", "http-io")]
    [DataRow("a-empty-padded", "deadline")]
    [DataRow("a-empty-padded", "caller")]
    [DataRow("c-wrong-id2", "io")]
    [DataRow("c-wrong-id2", "http-io")]
    [DataRow("c-wrong-id2", "deadline")]
    [DataRow("c-wrong-id2", "caller")]
    [DataRow("d-invalid-deflate", "io")]
    [DataRow("d-invalid-deflate", "http-io")]
    [DataRow("d-invalid-deflate", "deadline")]
    [DataRow("d-invalid-deflate", "caller")]
    public async Task TransportOutcomesSurviveMidDrain(string rejection, string outcome)
    {
        var prefix = rejection == "c-wrong-id2" ? 8192 : 8202;
        var wire = new byte[prefix + 16384];
        RejectedWire(rejection).CopyTo(wire, 0);
        var failAt = prefix + 8192;
        var content = new SwpcContent(wire, afterBytes: failAt,
            pending: outcome is "deadline" or "caller", failure: outcome switch
            {
                "io" => new IOException("drain failed"),
                "http-io" => new HttpIOException(HttpRequestError.ResponseEnded),
                _ => null
            });
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        using var caller = new CancellationTokenSource();
        var fetch = Fetch(content, clock, caller.Token);
        if (outcome is "deadline" or "caller")
        {
            await content.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.IsFalse(content.ReaderDisposed, "The drain must keep its wire source open.");
            if (outcome == "deadline") clock.Advance(SwpcSource.RequestTimeout);
            else await caller.CancelAsync().ConfigureAwait(false);
        }
        if (outcome == "caller")
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => fetch).ConfigureAwait(false);
            Assert.AreEqual(caller.Token, error.CancellationToken);
        }
        else
        {
            var result = await fetch.ConfigureAwait(false);
            Assert.AreEqual(outcome == "deadline" ? SwpcTransportFailure.Timeout : SwpcTransportFailure.NetworkError, result.Failure);
            Assert.IsNull(result.StatusCode);
            Assert.IsFalse(result.BodyUndecodable);
            Assert.IsTrue(result.Body.IsEmpty);
        }
        Assert.AreEqual(failAt, content.BytesRead, "One drain buffer must arrive before the injected outcome.");
        Assert.IsTrue(content.ReaderDisposed);
    }

    [TestMethod]
    [DataRow("a-empty-padded", true)]
    [DataRow("a-empty-padded", false)]
    [DataRow("c-wrong-id2", true)]
    [DataRow("c-wrong-id2", false)]
    [DataRow("d-invalid-deflate", true)]
    [DataRow("d-invalid-deflate", false)]
    public async Task DrainStopsAtBoundWithoutAnExtraRead(string rejection, bool extraWire)
    {
        var prefix = rejection == "c-wrong-id2" ? 8192 : 8202;
        var bound = SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales) + 1;
        var stop = prefix + bound;
        var wire = new byte[stop + (extraWire ? 4096 : 0)];
        RejectedWire(rejection).CopyTo(wire, 0);
        var content = new SwpcContent(wire, afterBytes: stop, failure: new AssertFailedException("Read after drain bound."));
        content.Headers.ContentLength = wire.Length + 1;
        var result = await Fetch(content).ConfigureAwait(false);
        AssertUndecodable(result);
        Assert.AreEqual(stop, content.BytesRead, "The only further wire allowance is cap+1.");
        Assert.AreEqual((rejection == "c-wrong-id2" ? 1 : 2) + (bound + 8191) / 8192, content.ReadCalls);
        Assert.AreEqual(1, content.LastReadRequest, "The last request is the one-byte remaining allowance.");
        Assert.IsTrue(content.ReaderDisposed);
    }

    [TestMethod]
    public async Task OversizeWithDeclaredTruncationNeverEntersDrain()
    {
        var maximum = SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales);
        var compressed = SwpcTestData.Gzip(new byte[maximum * 2]);
        var wire = new byte[8202 + maximum];
        compressed.CopyTo(wire, 0);
        var content = new SwpcContent(wire, afterBytes: 8202, failure: new AssertFailedException("Oversize entered the drain."));
        content.Headers.ContentLength = wire.Length + 1;
        var result = await Fetch(content).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.None, result.Failure);
        Assert.IsTrue(result.Oversize);
        Assert.IsFalse(result.BodyUndecodable);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.AreEqual(8202, content.BytesRead);
        Assert.AreEqual(2, content.ReadCalls);
        Assert.IsTrue(content.ReaderDisposed);
    }

    private static void AssertUndecodable(SwpcHttpResponse result)
    {
        Assert.AreEqual(SwpcTransportFailure.None, result.Failure);
        Assert.AreEqual(200, result.StatusCode);
        Assert.IsTrue(result.BodyUndecodable);
        Assert.IsFalse(result.Oversize);
        Assert.IsTrue(result.Body.IsEmpty);
    }

    private static byte[] RejectedWire(string shape)
    {
        var member = SwpcTestData.EmptyGzipMember;
        var wire = new byte[shape is "c-wrong-id2" or "c-reserved-flag" ? 4096 : 8202];
        member.CopyTo(wire, 0);
        switch (shape)
        {
            case "a-empty-padded": break;
            case "b-trailing-garbage":
                SwpcTestData.Gzip([1, 2, 3]).CopyTo(wire, 0);
                wire.AsSpan(64).Fill(0x41);
                break;
            case "c-wrong-id2": wire[1] = 0; break;
            case "c-reserved-flag": wire[3] = 32; break;
            case "d-invalid-deflate": wire[10] = 7; break; // BFINAL=1, reserved BTYPE=3.
            default: throw new AssertFailedException(shape);
        }
        return wire;
    }

    private static SwpcContent Content(byte[] wire, string delivery)
    {
        var content = new SwpcContent(wire, chunk: delivery == "one-byte" ? 1 : int.MaxValue,
            chunks: delivery == "mixed" ? [2, 11, 1, 7, 3, 19] : null);
        if (!content.Headers.Contains("Content-Encoding")) content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        return content;
    }

    private static async Task<SwpcHttpResponse> Fetch(SwpcContent content, ManualTimeProvider? clock = null,
        CancellationToken token = default)
    {
        if (!content.Headers.Contains("Content-Encoding")) content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        using var handler = new SwpcStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await new SwpcClient(http, clock ?? new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent)
            .FetchAsync(SwpcProduct.Scales, null, token).ConfigureAwait(false);
    }
}
