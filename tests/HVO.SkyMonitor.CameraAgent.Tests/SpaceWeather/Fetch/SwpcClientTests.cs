using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcClientTests
{
    [TestMethod]
    [DataRow(SwpcProduct.Alerts, false, false)]
    [DataRow(SwpcProduct.Scales, true, false)]
    [DataRow(SwpcProduct.KpForecast, false, true)]
    [DataRow(SwpcProduct.ThreeDayForecast, true, true)]
    public async Task RequestHasExactlyTheApprovedUriAndHeaders(SwpcProduct product, bool etag, bool modified)
    {
        const string tag = "W/\"stored\"";
        const string last = "Tue, 03 Feb 2026 00:59:00 GMT";
        var handler = Handler(new SwpcContent([]));
        using var http = Http(handler);
        var client = new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent);
        var result = await client.FetchAsync(product, new(etag ? tag : null, modified ? last : null), CancellationToken.None).ConfigureAwait(false);
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(SwpcSource.RequestUri(product), request.Uri);
        var expected = new List<(string, string)>
        {
            ("User-Agent", SwpcSource.UserAgent), ("Accept", SwpcSource.MediaType(product)), ("Accept-Encoding", "gzip")
        };
        if (etag) expected.Add(("If-None-Match", tag));
        if (modified) expected.Add(("If-Modified-Since", last));
        CollectionAssert.AreEquivalent(expected.ToArray(), request.Headers);
        Assert.AreEqual(etag || modified, result.ValidatorsSent);
    }

    [TestMethod]
    public async Task NullOrInvalidValidatorsAreNotSentAndCallsDoNotCache()
    {
        var handler = new SwpcStubHandler((_, _) =>
        {
            var response = Response(new SwpcContent([]));
            response.Headers.TryAddWithoutValidation("ETag", "\"new\"");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=ignored");
            return Task.FromResult(response);
        });
        using var http = Http(handler);
        var client = new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent);
        foreach (var validators in new SwpcRequestValidators?[] { null, new("bad\r\nCookie: injected", new string('x', 129)) })
        {
            var result = await client.FetchAsync(SwpcProduct.Alerts, validators, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(result.ValidatorsSent);
        }
        Assert.HasCount(2, handler.Requests);
        Assert.IsTrue(handler.Requests.All(r => r.Headers.Length == 3));
    }

    [TestMethod]
    [DataRow(301)]
    [DataRow(404)]
    [DataRow(304)]
    [DataRow(429)]
    [DataRow(503)]
    [DataRow(999)]
    public async Task Non200StatusNeverOpensTheBodyOrFollowsLocation(int status)
    {
        var content = new SwpcContent([], failure: new AssertFailedException("Body read."));
        var (result, handler) = await Fetch(content, status: status, configure: response =>
        {
            response.Headers.TryAddWithoutValidation("Location", "https://example.invalid/");
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "br");
            response.Content.Headers.ContentLength = long.MaxValue;
        }).ConfigureAwait(false);
        Assert.AreEqual(status, result.StatusCode);
        Assert.AreEqual(SwpcTransportFailure.None, result.Failure);
        Assert.IsFalse(content.Opened);
        Assert.IsTrue(content.Disposed);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    public async Task IdentityReadsAreBoundedAtExactlyCapPlusOne(bool declared, int extra)
    {
        var cap = SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales);
        var bytes = Enumerable.Repeat((byte)'x', cap + extra).ToArray();
        var content = new SwpcContent(bytes, chunk: 97);
        var (result, _) = await Fetch(content, product: SwpcProduct.Scales, configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "identity");
            if (declared) response.Content.Headers.ContentLength = bytes.Length;
        }).ConfigureAwait(false);
        Assert.AreEqual(extra > 0, result.Oversize);
        Assert.IsFalse(result.BodyUndecodable);
        Assert.AreEqual(declared && extra > 0 ? 0 : cap + extra, content.BytesRead);
        if (extra == 0) CollectionAssert.AreEqual(bytes, result.Body.ToArray());
        else Assert.IsTrue(result.Body.IsEmpty);
    }

    [TestMethod]
    public async Task UnboundedChunkedStreamStopsAtCapPlusOne()
    {
        var content = new SwpcContent([], unbounded: true);
        var (result, _) = await Fetch(content, product: SwpcProduct.Scales).ConfigureAwait(false);
        Assert.IsTrue(result.Oversize);
        Assert.AreEqual(SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales) + 1, content.BytesRead);
        Assert.IsTrue(result.Body.IsEmpty);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(100)]
    public async Task GzipDecodingStopsAtTheDecodedCap(int extra)
    {
        var cap = SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales);
        var bytes = new byte[cap + extra];
        var wire = SwpcTestData.Gzip(bytes);
        var content = new SwpcContent(wire, chunk: 37);
        var (result, _) = await Fetch(content, product: SwpcProduct.Scales, configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            response.Content.Headers.ContentLength = wire.Length;
        }).ConfigureAwait(false);
        Assert.AreEqual(extra > 0, result.Oversize);
        Assert.IsFalse(result.BodyUndecodable);
        if (extra == 0) CollectionAssert.AreEqual(bytes, result.Body.ToArray());
        else Assert.IsTrue(result.Body.IsEmpty);
    }

    [TestMethod]
    public async Task GzipBombStopsWithoutReadingTheRemainingCompressedBody()
    {
        var cap = SwpcSource.MaximumDecodedBytes(SwpcProduct.Scales);
        var bytes = new byte[cap * 100];
        // Deterministic, poorly compressible tail makes excessive decoder read-ahead observable.
        for (var index = cap + 1; index < bytes.Length; index += 32)
        {
            var hash = SHA256.HashData(BitConverter.GetBytes(index));
            hash.AsSpan(0, Math.Min(hash.Length, bytes.Length - index)).CopyTo(bytes.AsSpan(index));
        }
        var content = new SwpcContent(SwpcTestData.Gzip(bytes), chunk: 31);
        var (result, _) = await Fetch(content, product: SwpcProduct.Scales,
            configure: response => response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip")).ConfigureAwait(false);
        Assert.IsTrue(result.Oversize);
        Assert.IsFalse(result.BodyUndecodable);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.IsTrue(content.BytesRead < cap + 1 + 8192, "Only cap+1 decoded bytes plus decoder read-ahead may be consumed.");
    }

    [TestMethod]
    [DataRow("br")]
    [DataRow("deflate")]
    [DataRow("gzip, gzip")]
    [DataRow("gzip, identity")]
    [DataRow("")]
    public async Task UnsupportedCodingDoesNotOpenTheBodyOrJudgeSize(string coding)
    {
        var content = new SwpcContent([]);
        var (result, _) = await Fetch(content, configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", coding);
            response.Content.Headers.ContentLength = long.MaxValue;
        }).ConfigureAwait(false);
        Assert.IsTrue(result.BodyUndecodable);
        Assert.IsFalse(result.Oversize);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.IsFalse(content.Opened);
    }

    [TestMethod]
    public async Task RepeatedCodingIsUndecodableWithoutReading()
    {
        var content = new SwpcContent([]);
        var (result, _) = await Fetch(content, configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        }).ConfigureAwait(false);
        Assert.IsTrue(result.BodyUndecodable);
        Assert.IsFalse(content.Opened);
    }

    [TestMethod]
    [DataRow("plain")]
    [DataRow("empty")]
    [DataRow("crc")]
    [DataRow("truncated")]
    public async Task InvalidGzipHasNoBodyOrSizeVerdict(string shape)
    {
        var valid = SwpcTestData.Gzip(Encoding.UTF8.GetBytes("payload"));
        var wire = shape switch
        {
            "plain" => Encoding.UTF8.GetBytes("not gzip"),
            "empty" => [],
            "truncated" => valid[..^4],
            _ => valid
        };
        if (shape == "crc") wire[^8] ^= 0xff;
        var (result, _) = await Fetch(new SwpcContent(wire), configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/html");
        }).ConfigureAwait(false);
        Assert.IsTrue(result.BodyUndecodable);
        Assert.IsFalse(result.Oversize);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual("text/html", result.MediaType, "T1 leaves the content-type decision to policy.");
    }

    [TestMethod]
    [DataRow("application/json", "application/json")]
    [DataRow("application/json; charset=UTF-8", "application/json")]
    [DataRow("application/json\t; charset=UTF-8", "application/json")]
    [DataRow("\u001capplication/json\u001f", "application/json")]
    [DataRow("application/json\u0085", "application/json")]
    [DataRow("\u00a0text/plain\u00a0;q=1", "text/plain")]
    [DataRow("application/json\u2003", "application/json")]
    [DataRow("application/json; charset=café", "application/json")]
    [DataRow("TEXT/Plain", "TEXT/Plain")]
    [DataRow("café", null)]
    [DataRow("bad\tvalue", null)]
    [DataRow("application/\u00a0json", null)]
    [DataRow("", null)]
    [DataRow(" \t\u001c ", null)]
    [DataRow(";charset=UTF-8", null)]
    [DataRow("\rapplication/json\r", "application/json")]
    [DataRow("\napplication/json\n", "application/json")]
    [DataRow("\vapplication/json\v", "application/json")]
    [DataRow("\fapplication/json\f", "application/json")]
    [DataRow("\u001dapplication/json\u001d", "application/json")]
    [DataRow("\u001eapplication/json\u001e", "application/json")]
    [DataRow(" application/json ", "application/json")]
    [DataRow("\u1680application/json\u1680", "application/json")]
    [DataRow("\u2000application/json\u2000", "application/json")]
    [DataRow("\u2001application/json\u2001", "application/json")]
    [DataRow("\u2002application/json\u2002", "application/json")]
    [DataRow("\u2004application/json\u2004", "application/json")]
    [DataRow("\u2005application/json\u2005", "application/json")]
    [DataRow("\u2006application/json\u2006", "application/json")]
    [DataRow("\u2007application/json\u2007", "application/json")]
    [DataRow("\u2008application/json\u2008", "application/json")]
    [DataRow("\u2009application/json\u2009", "application/json")]
    [DataRow("\u200aapplication/json\u200a", "application/json")]
    [DataRow("\u2028application/json\u2028", "application/json")]
    [DataRow("\u2029application/json\u2029", "application/json")]
    [DataRow("\u202fapplication/json\u202f", "application/json")]
    [DataRow("\u205fapplication/json\u205f", "application/json")]
    [DataRow("\u3000application/json\u3000", "application/json")]
    [DataRow("\u180eapplication/json\u180e", null)]
    [DataRow("\u200bapplication/json\u200b", null)]
    [DataRow("\ufeffapplication/json\ufeff", null)]
    [DataRow("application/json;charset=UTF-8;q=1", "application/json")]
    [DataRow("application/json; a=1; b=2;", "application/json")]
    [DataRow(";;application/json", null)]
    [DataRow("application/ json", "application/ json")]
    public async Task ContentTypeIsOneBoundedTrimmedMediaToken(string raw, string? expected)
    {
        var (result, _) = await Fetch(new SwpcContent([]), configure: response =>
        {
            Assert.IsTrue(response.Content.Headers.TryAddWithoutValidation("Content-Type", raw));
            Assert.AreEqual(raw, response.Content.Headers.NonValidated["Content-Type"].Single(), "The injected field must reach raw extraction unchanged.");
        }).ConfigureAwait(false);
        Assert.AreEqual(expected, result.MediaType);
    }

    [TestMethod]
    public async Task MediaTypeBoundAppliesAfterParametersAndWhitespaceAreRemoved()
    {
        var token = new string('m', 256);
        var parameters = "; " + new string('p', 1000);
        var (bounded, _) = await Fetch(new SwpcContent([]), configure: response =>
            response.Content.Headers.TryAddWithoutValidation("Content-Type", token + parameters)).ConfigureAwait(false);
        Assert.AreEqual(token, bounded.MediaType);
        var (over, _) = await Fetch(new SwpcContent([]), configure: response =>
            response.Content.Headers.TryAddWithoutValidation("Content-Type", token + "m" + parameters)).ConfigureAwait(false);
        Assert.IsNull(over.MediaType);
        var (trimmed, _) = await Fetch(new SwpcContent([]), configure: response =>
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "\t" + token + "\u00a0")).ConfigureAwait(false);
        Assert.AreEqual(token, trimmed.MediaType);
    }

    [TestMethod]
    [DataRow("application/json\t; charset=UTF-8", SwpcProduct.Scales)]
    [DataRow("\u001capplication/json\u001f", SwpcProduct.Scales)]
    [DataRow("\u00a0text/plain\u00a0;q=1", SwpcProduct.ThreeDayForecast)]
    [DataRow("application/json; charset=café", SwpcProduct.Scales)]
    [DataRow("café", SwpcProduct.Scales)]
    [DataRow("\rapplication/json\n", SwpcProduct.Scales)]
    [DataRow("\v\fapplication/json\u001d\u001e", SwpcProduct.Scales)]
    [DataRow("\u1680application/json\u205f;q=1", SwpcProduct.Scales)]
    [DataRow("\u3000text/plain\u2029", SwpcProduct.ThreeDayForecast)]
    [DataRow("\u180eapplication/json", SwpcProduct.Scales)]
    [DataRow("application/json\u200b", SwpcProduct.Scales)]
    [DataRow("\ufeffapplication/json", SwpcProduct.Scales)]
    [DataRow("application/json;charset=UTF-8;q=1", SwpcProduct.Scales)]
    [DataRow("text/plain; a=1; b=2", SwpcProduct.ThreeDayForecast)]
    [DataRow(";;application/json", SwpcProduct.Scales)]
    [DataRow("application/ json", SwpcProduct.Scales)]
    [DataRow("mmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmm;q=1", SwpcProduct.Scales)]
    public async Task MediaTypeTokenPreservesS1VerdictOnTheRawHeader(string raw, SwpcProduct product)
    {
        var fixture = product == SwpcProduct.ThreeDayForecast ? "3-day-forecast.txt" : "noaa-scales.json";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1",
            "capture-20261019T1857Z", fixture)).ConfigureAwait(false);
        var (result, _) = await Fetch(new SwpcContent(bytes), product: product, configure: response =>
        {
            Assert.IsTrue(response.Content.Headers.TryAddWithoutValidation("Content-Type", raw));
            Assert.AreEqual(raw, response.Content.Headers.NonValidated["Content-Type"].Single(), "The parser oracle must see the injected field unchanged.");
        }).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.None, result.Failure);
        CollectionAssert.AreEqual(bytes, result.Body.ToArray());
        if (product == SwpcProduct.ThreeDayForecast)
        {
            var expected = SwpcThreeDayForecastParser.Parse(bytes, raw);
            var actual = SwpcThreeDayForecastParser.Parse(result.Body.Span, result.MediaType);
            Assert.AreEqual(expected.RejectionReason, actual.RejectionReason);
            Assert.IsTrue(actual.Accepted);
        }
        else
        {
            var expected = SwpcScalesParser.Parse(bytes, raw);
            var actual = SwpcScalesParser.Parse(result.Body.Span, result.MediaType);
            Assert.AreEqual(expected.RejectionReason, actual.RejectionReason);
            if (raw == "application/json\t; charset=UTF-8")
            {
                Assert.IsTrue(actual.Accepted, "B1 must fully accept the committed valid scales body.");
                Assert.IsEmpty(actual.Rejections);
                Assert.HasCount(5, actual.Records);
            }
        }
    }

    [TestMethod]
    public async Task HeaderBoundsAndRepeatedFieldsAreHandledWithoutTypedParsing()
    {
        var (bounded, _) = await Fetch(new SwpcContent([]), configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", new string('m', 256));
            response.Headers.TryAddWithoutValidation("ETag", new string('e', 1024));
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", new string('l', 128));
            response.Headers.TryAddWithoutValidation("Retry-After", "Tue, 03 Feb 2026 01:02:03 GMT");
        }).ConfigureAwait(false);
        Assert.AreEqual(256, bounded.MediaType!.Length);
        Assert.AreEqual(1024, bounded.EntityTag!.Length);
        Assert.AreEqual(128, bounded.LastModified!.Length);
        Assert.AreEqual("Tue, 03 Feb 2026 01:02:03 GMT", bounded.RetryAfter);
        var (over, _) = await Fetch(new SwpcContent([]), configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", new string('m', 257));
            response.Headers.TryAddWithoutValidation("ETag", new string('e', 1025));
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", new string('l', 129));
            response.Headers.TryAddWithoutValidation("Retry-After", new string('1', 30));
        }).ConfigureAwait(false);
        Assert.IsNull(over.MediaType);
        Assert.IsNull(over.EntityTag);
        Assert.IsNull(over.LastModified);
        Assert.IsNull(over.RetryAfter);
        var (repeated, _) = await Fetch(new SwpcContent([]), configure: response =>
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain");
            response.Headers.TryAddWithoutValidation("ETag", "\"one\"");
            response.Headers.TryAddWithoutValidation("ETag", "\"two\"");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", "one");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", "two");
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
        }).ConfigureAwait(false);
        Assert.IsNull(repeated.MediaType);
        Assert.IsNull(repeated.EntityTag);
        Assert.IsNull(repeated.LastModified);
        Assert.IsNull(repeated.RetryAfter);
        var (invalid, _) = await Fetch(new SwpcContent([]), configure: response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "\"café\"");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", "bad\tvalue");
        }).ConfigureAwait(false);
        Assert.IsNull(invalid.EntityTag);
        Assert.IsNull(invalid.LastModified);
        Assert.IsNull(invalid.MediaType);
        Assert.IsNull(invalid.RetryAfter);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeadlineCoversPendingHeadersAndBodies(bool bodyPending)
    {
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        var content = new SwpcContent([], pending: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SwpcStubHandler(async (_, token) =>
        {
            started.TrySetResult();
            if (!bodyPending) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return Response(content);
        });
        using var http = Http(handler);
        var fetch = new SwpcClient(http, clock, SwpcSource.UserAgent).FetchAsync(SwpcProduct.Alerts, null, CancellationToken.None).AsTask();
        await (bodyPending ? content.ReadStarted.Task : started.Task).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        clock.Advance(SwpcSource.RequestTimeout);
        var result = await fetch.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.Timeout, result.Failure);
        Assert.IsNull(result.StatusCode);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.AreEqual(SwpcTestData.Now, result.StartedUtc);
        Assert.AreEqual(SwpcTestData.Now + SwpcSource.RequestTimeout, result.CompletedUtc);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("io")]
    [DataRow("tls")]
    [DataRow("socket")]
    [DataRow("cancellation")]
    [DataRow("http-io")]
    [DataRow("handler-timeout")]
    public async Task TransportExceptionsReturnOnlyTransportFacts(string shape)
    {
        var handler = new SwpcStubHandler((_, _) => throw TransportException(shape));
        using var http = Http(handler);
        var result = await new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent)
            .FetchAsync(SwpcProduct.Alerts, new("\"tag\"", null), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(shape == "handler-timeout" ? SwpcTransportFailure.Timeout : SwpcTransportFailure.NetworkError, result.Failure);
        Assert.IsNull(result.StatusCode);
        Assert.IsTrue(result.Body.IsEmpty);
        Assert.IsNull(result.EntityTag);
        Assert.IsTrue(result.ValidatorsSent);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TruncatedConnectionIsNetworkFailureIncludingGzip(bool gzip)
    {
        var bytes = gzip ? SwpcTestData.Gzip(new byte[100]) : new byte[100];
        var (result, _) = await Fetch(new SwpcContent(bytes), configure: response =>
        {
            if (gzip) response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            response.Content.Headers.ContentLength = bytes.Length + 10;
        }).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.NetworkError, result.Failure);
        Assert.IsNull(result.StatusCode);
        var (readFailure, _) = await Fetch(new SwpcContent([], failure: new IOException("truncated")), configure: response =>
        {
            if (gzip) response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        }).ConfigureAwait(false);
        Assert.AreEqual(SwpcTransportFailure.NetworkError, readFailure.Failure);
        Assert.IsFalse(readFailure.BodyUndecodable);
    }

    [TestMethod]
    [DataRow("deadline")]
    [DataRow("transport")]
    [DataRow("success")]
    [DataRow("headers")]
    [DataRow("body")]
    [DataRow("pre-cancelled")]
    public async Task CallerCancellationAlwaysThrowsAndWinsRaces(string race)
    {
        var clock = new ManualTimeProvider(SwpcTestData.Now);
        using var caller = new CancellationTokenSource();
        var content = new SwpcContent([], pending: race == "body");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SwpcStubHandler(async (_, token) =>
        {
            started.TrySetResult();
            if (race is "headers" or "body")
            {
                if (race == "headers") await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Response(content);
            }
            await caller.CancelAsync().ConfigureAwait(false);
            if (race == "deadline") clock.Advance(SwpcSource.RequestTimeout);
            if (race is "transport" or "deadline") throw new HttpRequestException("racing failure");
            return Response(content);
        });
        using var http = Http(handler);
        if (race == "pre-cancelled") await caller.CancelAsync().ConfigureAwait(false);
        var fetch = new SwpcClient(http, clock, SwpcSource.UserAgent).FetchAsync(SwpcProduct.Alerts, null, caller.Token).AsTask();
        if (race is "headers" or "body")
        {
            await (race == "body" ? content.ReadStarted.Task : started.Task).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await caller.CancelAsync().ConfigureAwait(false);
        }
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        Assert.AreEqual(caller.Token, error.CancellationToken);
        if (race == "pre-cancelled") Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task UnexpectedExceptionsPropagate()
    {
        var handler = new SwpcStubHandler((_, _) => throw new InvalidOperationException("programming defect"));
        using var http = Http(handler);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new SwpcClient(http,
            new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent).FetchAsync(SwpcProduct.Alerts, null, CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TimesComeOnlyFromTheInjectedClockAndAreUtc()
    {
        var clock = new ManualTimeProvider(SwpcTestData.Now.ToOffset(TimeSpan.FromHours(-7)));
        var handler = new SwpcStubHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            var response = Response(new SwpcContent([]));
            response.Headers.Date = DateTimeOffset.MinValue;
            return Task.FromResult(response);
        });
        using var http = Http(handler);
        var result = await new SwpcClient(http, clock, SwpcSource.UserAgent).FetchAsync(SwpcProduct.Alerts, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcProduct.Alerts, result.Product);
        Assert.AreEqual(SwpcTestData.Now, result.StartedUtc);
        Assert.AreEqual(SwpcTestData.Now.AddSeconds(2), result.CompletedUtc);
        Assert.AreEqual(TimeSpan.Zero, result.StartedUtc.Offset);
        Assert.AreEqual(TimeSpan.Zero, result.CompletedUtc.Offset);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("bad\r\nCookie: injected")]
    [DataRow("café")]
    [DataRow("Unbalanced (comment")]
    [DataRow("bad/1.0 @@@")]
    public void ConstructorRejectsInvalidUserAgents(string? value)
    {
        using var http = Http(Handler(new SwpcContent([])));
        Assert.ThrowsExactly<ArgumentException>(() => new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), value!));
        Assert.ThrowsExactly<ArgumentException>(() => new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), new string('x', 257)));
    }

    [TestMethod]
    [DataRow("Cookie")]
    [DataRow("Authorization")]
    [DataRow("User-Agent")]
    [DataRow("If-None-Match")]
    public void ConstructorRejectsEveryDefaultHeader(string header)
    {
        using var http = Http(Handler(new SwpcContent([])));
        http.DefaultRequestHeaders.TryAddWithoutValidation(header, "value");
        Assert.ThrowsExactly<ArgumentException>(() => new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent));
    }

    [TestMethod]
    public void ConstructorRejectsNullDependencies()
    {
        using var http = Http(Handler(new SwpcContent([])));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SwpcClient(null!, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SwpcClient(http, null!, SwpcSource.UserAgent));
    }

    [TestMethod]
    [DataRow("s23-retry-after-forms.json", 1, SwpcTransportFailure.None, 429)]
    [DataRow("s22-gone-client-error-network.json", 2, SwpcTransportFailure.NetworkError, null)]
    public void ScenarioStepsConstructTransportFactsWithoutHttp(string file, int number, SwpcTransportFailure failure, int? status)
    {
        using var scenario = SwpcTestData.Scenario(file);
        var step = scenario.RootElement.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetInt32() == number);
        var raw = step.GetProperty("response");
        var now = SwpcTestData.Time(step.GetProperty("atUtc").GetString()!);
        var response = new SwpcHttpResponse(SwpcProduct.Alerts, now, now,
            SwpcTestData.Text(raw, "outcome") == "network-error" ? SwpcTransportFailure.NetworkError : SwpcTransportFailure.None,
            raw.TryGetProperty("status", out var code) ? code.GetInt32() : null,
            mediaType: SwpcTestData.Text(raw, "contentType"), entityTag: SwpcTestData.Text(raw, "etag"),
            lastModified: SwpcTestData.Text(raw, "lastModified"), retryAfter: SwpcTestData.Text(raw, "retryAfter"),
            body: Encoding.UTF8.GetBytes(SwpcTestData.Text(raw, "body") ?? ""));
        Assert.AreEqual(failure, response.Failure);
        Assert.AreEqual(status, response.StatusCode);
        Assert.AreEqual(now, response.StartedUtc);
        Assert.AreEqual(SwpcTestData.Text(raw, "retryAfter"), response.RetryAfter);
    }

    [TestMethod]
    public void ResponseRecordKeepsStatusAndFailureConsistent()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SwpcHttpResponse(SwpcProduct.Alerts,
            SwpcTestData.Now, SwpcTestData.Now, SwpcTransportFailure.None, null));
        Assert.ThrowsExactly<ArgumentException>(() => new SwpcHttpResponse(SwpcProduct.Alerts,
            SwpcTestData.Now, SwpcTestData.Now, SwpcTransportFailure.Timeout, 200));
        Assert.ThrowsExactly<ArgumentException>(() => new SwpcHttpResponse(SwpcProduct.Alerts,
            SwpcTestData.Now, SwpcTestData.Now, SwpcTransportFailure.None, 200, oversize: true, bodyUndecodable: true));
    }

    private static Exception TransportException(string shape) => shape switch
    {
        "http" => new HttpRequestException("transport"),
        "io" => new IOException("transport"),
        "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, "transport"),
        "tls" => new AuthenticationException("transport"),
        "socket" => new SocketException((int)SocketError.ConnectionReset),
        "handler-timeout" => new OperationCanceledException("timeout", new TimeoutException()),
        _ => new OperationCanceledException("unexplained")
    };

    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    private static SwpcStubHandler Handler(SwpcContent content, int status = 200, Action<HttpResponseMessage>? configure = null) =>
        new((_, _) =>
        {
            var response = Response(content, status);
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
    private static HttpResponseMessage Response(SwpcContent content, int status = 200) => new((HttpStatusCode)status) { Content = content };
    private static async Task<(SwpcHttpResponse Result, SwpcStubHandler Handler)> Fetch(SwpcContent content,
        int status = 200, SwpcProduct product = SwpcProduct.Alerts, Action<HttpResponseMessage>? configure = null)
    {
        var handler = Handler(content, status, configure);
        using var http = Http(handler);
        var result = await new SwpcClient(http, new ManualTimeProvider(SwpcTestData.Now), SwpcSource.UserAgent)
            .FetchAsync(product, null, CancellationToken.None).ConfigureAwait(false);
        return (result, handler);
    }
}
