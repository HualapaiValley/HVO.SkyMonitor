using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class CelesTrakGpClientTests
{
    private const string Secret = "SECRET-HEADER-VALUE";
    private static readonly byte[] SmallJson = Encoding.UTF8.GetBytes("[{\"NORAD_CAT_ID\":1}]");

    [TestMethod]
    [DataRow(100)]
    [DataRow(101)]
    [DataRow(201)]
    [DataRow(202)]
    [DataRow(203)]
    [DataRow(204)]
    [DataRow(206)]
    [DataRow(300)]
    [DataRow(301)]
    [DataRow(302)]
    [DataRow(303)]
    [DataRow(304)]
    [DataRow(307)]
    [DataRow(308)]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(408)]
    [DataRow(410)]
    [DataRow(418)]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    [DataRow(504)]
    [DataRow(599)]
    public async Task EveryNon200StatusIsAProviderHaltWithoutReadingTheBody(int status)
    {
        var content = new TrackingContent(SmallJson);
        var (result, handler) = await FetchAsync(content, (HttpStatusCode)status,
            configure: static response => response.Headers.TryAddWithoutValidation("Location", "https://example.invalid/")).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.HttpStatusHalt, result.Disposition);
        Assert.AreEqual(status, result.HttpStatusCode);
        Assert.AreEqual("orbital.non-200-response", result.ReasonCode);
        Assert.AreEqual($"status={status}", result.Detail);
        Assert.IsNull(result.RawBytes);
        Assert.IsNull(result.RawSha256);
        Assert.AreEqual(0, result.WireLengthBytes);
        Assert.IsNull(result.ContentEncoding);
        Assert.IsFalse(content.BodyRead, "A non-200 body is never read.");
        Assert.HasCount(1, handler.Requests, "No retry and no redirect is followed.");
    }

    [TestMethod]
    public async Task HaltStatusTakesPrecedenceOverEveryResponseCheck()
    {
        var content = new TrackingContent(new byte[100]);
        var (result, _) = await FetchAsync(content, HttpStatusCode.TooManyRequests, contentType: "text/html",
            contentEncoding: "br", contentLength: long.MaxValue / 2).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.HttpStatusHalt, result.Disposition);
        Assert.AreEqual("status=429", result.Detail);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(99)]
    [DataRow(600)]
    [DataRow(999)]
    public async Task StatusOutsideTheRfc9110RangeIsARejectedResponseNotAHalt(int status)
    {
        var content = new TrackingContent(SmallJson);
        var (result, handler) = await FetchAsync(content, (HttpStatusCode)status).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual(status, result.HttpStatusCode);
        Assert.AreEqual("orbital.invalid-http-status", result.ReasonCode);
        Assert.AreEqual($"status={status}", result.Detail);
        Assert.IsNull(result.RawBytes);
        Assert.IsNull(result.RawSha256);
        Assert.IsFalse(content.BodyRead, "An invalid-status body is never read.");
        Assert.HasCount(1, handler.Requests, "No retry is made.");
    }

    [TestMethod]
    [DataRow(100)]
    [DataRow(599)]
    public async Task BoundaryValidStatusesStillHaltAndFitTheHaltContract(int status)
    {
        var (result, _) = await FetchAsync(new TrackingContent(SmallJson), (HttpStatusCode)status).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.HttpStatusHalt, result.Disposition);
        var halt = new OrbitalProviderHalt(CelesTrakGpTestData.RetrievedUtc, result.HttpStatusCode!.Value, result.ReasonCode);
        new OrbitalRefreshStatus(OrbitalRefreshStatus.CurrentVersion, CelesTrakGpTestData.RetrievedUtc, 1, true, null, null,
            CelesTrakGpTestData.RetrievedUtc, null, null, null, null, null, halt, null, "halted",
            OrbitalRefreshRecoveryAction.ResumeProvider).Validate();
    }

    [TestMethod]
    public async Task RequestIsOneUnconditionalGetOfTheApprovedUriWithExactlyThreeHeaders()
    {
        var (result, handler) = await FetchAsync(new TrackingContent(SmallJson)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, result.Disposition);
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual(CelesTrakGpSource.RequestUri, request.RequestUri);
        Assert.AreEqual("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON", request.RequestUri!.OriginalString);
        Assert.IsFalse(request.HasContent);
        CollectionAssert.AreEquivalent(
            new[] { ("User-Agent", CelesTrakGpTestData.UserAgent), ("Accept", "application/json"), ("Accept-Encoding", "gzip") },
            request.Headers.ToArray());
    }

    [TestMethod]
    public async Task EachFetchIsExactlyOneNewRequestAndNothingIsCachedOrConditional()
    {
        var handler = new StubHttpHandler(() => CelesTrakGpTestData.Response(HttpStatusCode.OK, new TrackingContent(SmallJson),
            configure: static response =>
            {
                response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc");
            }));
        var client = CelesTrakGpTestData.CreateClient(handler);

        var first = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);
        var second = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, first.Disposition);
        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, second.Disposition);
        Assert.HasCount(2, handler.Requests);
        foreach (var request in handler.Requests)
        {
            Assert.IsFalse(request.Headers.Any(static h => h.Name is "If-None-Match" or "If-Modified-Since" or "Cookie" or
                "Authorization" or "Proxy-Authorization" or "Range"));
            Assert.HasCount(3, request.Headers);
        }
    }

    [TestMethod]
    public async Task RetrievedIdentityBodyCarriesExactBytesHashLengthsAndUtcTimes()
    {
        var raw = CelesTrakGpTestData.ReadFixture(CelesTrakGpTestData.ValidFixture);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 19, 10, 28, 18, TimeSpan.FromHours(-7)));
        var handler = new StubHttpHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(CelesTrakGpTestData.Response(HttpStatusCode.OK, new TrackingContent(raw, chunkBytes: 97),
                contentLength: raw.Length));
        });

        var result = await CelesTrakGpTestData.CreateClient(handler, clock).FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, result.Disposition);
        Assert.AreEqual(200, result.HttpStatusCode);
        CollectionAssert.AreEqual(raw, result.RawBytes);
        Assert.AreEqual(CelesTrakGpTestData.Sha256(raw), result.RawSha256);
        Assert.AreEqual(raw.Length, result.WireLengthBytes);
        Assert.IsNull(result.ContentEncoding);
        Assert.AreEqual("orbital.retrieved", result.ReasonCode);
        Assert.AreEqual($"status=200 raw-bytes={raw.Length} wire-bytes={raw.Length}", result.Detail);
        Assert.AreEqual(CelesTrakGpTestData.RetrievedUtc, result.StartedUtc);
        Assert.AreEqual(CelesTrakGpTestData.RetrievedUtc.AddSeconds(2), result.CompletedUtc);
        Assert.AreEqual(TimeSpan.Zero, result.StartedUtc.Offset);
        Assert.AreEqual(TimeSpan.Zero, result.CompletedUtc.Offset);
    }

    [TestMethod]
    public async Task ProviderDateHeaderNeverBecomesProvenanceTime()
    {
        var raw = CelesTrakGpTestData.ReadFixture(CelesTrakGpTestData.ValidFixture);
        var clock = new ManualTimeProvider(CelesTrakGpTestData.RetrievedUtc);
        var (result, _) = await FetchAsync(new TrackingContent(raw), timeProvider: clock, configure: static response =>
        {
            response.Headers.Date = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero);
        }).ConfigureAwait(false);

        Assert.AreEqual(CelesTrakGpTestData.RetrievedUtc, result.CompletedUtc);
        Assert.AreEqual("Mon, 19 Oct 2026 17:00:00 GMT", result.LastModified);
        var candidate = CelesTrakGpNormalizer.CreateCandidate(result);
        Assert.IsNull(candidate.RejectionReasonCode, candidate.Detail);
        var source = candidate.Snapshot!.Manifest.Sources.Single();
        Assert.AreEqual(CelesTrakGpTestData.RetrievedUtc, source.RetrievedUtc);
        Assert.IsNull(source.ProviderGeneratedUtc);
        Assert.AreEqual("Mon, 19 Oct 2026 17:00:00 GMT", source.LastModified);
    }

    [TestMethod]
    [DataRow("application/json")]
    [DataRow("APPLICATION/JSON")]
    [DataRow("application/json; charset=utf-8")]
    [DataRow("application/json;charset=UTF-8")]
    [DataRow("Application/Json; Charset=\"utf-8\"")]
    public async Task JsonMediaTypeIsAccepted(string contentType)
    {
        var (result, _) = await FetchAsync(new TrackingContent(SmallJson), contentType: contentType).ConfigureAwait(false);
        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, result.Disposition, result.Detail);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("text/html")]
    [DataRow("text/plain; charset=utf-8")]
    [DataRow("text/json")]
    [DataRow("application/octet-stream")]
    [DataRow("application/problem+json")]
    [DataRow("application/jsonl")]
    [DataRow("application/*")]
    [DataRow("*/*")]
    [DataRow("application/json; charset=iso-8859-1")]
    [DataRow("application/json; charset=utf-16")]
    [DataRow("application/json; profile=gp")]
    [DataRow("application/json; charset=utf-8; profile=gp")]
    [DataRow("application/json, text/html")]
    [DataRow("application/json;")]
    [DataRow("")]
    [DataRow("json")]
    public async Task OtherMediaTypesAreRejectedBeforeTheBodyIsRead(string? contentType)
    {
        var content = new TrackingContent(SmallJson);
        var (result, _) = await FetchAsync(content, contentType: contentType).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual(200, result.HttpStatusCode);
        Assert.AreEqual("orbital.media-type", result.ReasonCode);
        Assert.AreEqual("content-type-not-application-json", result.Detail);
        Assert.IsNull(result.RawBytes);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    public async Task DuplicateContentTypeHeadersAreRejected()
    {
        var content = new TrackingContent(SmallJson);
        var (result, _) = await FetchAsync(content, configure: static response =>
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json")).ConfigureAwait(false);

        Assert.AreEqual("orbital.media-type", result.ReasonCode);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("identity", "identity")]
    [DataRow("IDENTITY", "identity")]
    [DataRow("gzip", "gzip")]
    [DataRow("GZip", "gzip")]
    [DataRow(" gzip ", "gzip")]
    public async Task IdentityOrGzipContentEncodingIsAccepted(string? header, string? expected)
    {
        var wire = expected == "gzip" ? CelesTrakGpTestData.Gzip(SmallJson) : SmallJson;
        var (result, _) = await FetchAsync(new TrackingContent(wire), contentEncoding: header).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, result.Disposition, result.Detail);
        Assert.AreEqual(expected, result.ContentEncoding);
        CollectionAssert.AreEqual(SmallJson, result.RawBytes);
        Assert.AreEqual(wire.Length, result.WireLengthBytes);
    }

    [TestMethod]
    [DataRow("br")]
    [DataRow("deflate")]
    [DataRow("compress")]
    [DataRow("x-gzip")]
    [DataRow("zstd")]
    [DataRow("gzip, identity")]
    [DataRow("gzip,gzip")]
    [DataRow("identity, gzip")]
    [DataRow("gzip;q=1")]
    public async Task OtherContentEncodingsAreRejectedBeforeTheBodyIsRead(string header)
    {
        var content = new TrackingContent(CelesTrakGpTestData.Gzip(SmallJson));
        var (result, _) = await FetchAsync(content, contentEncoding: header).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.content-encoding", result.ReasonCode);
        Assert.AreEqual("content-encoding-not-identity-or-gzip", result.Detail);
        Assert.IsNull(result.ContentEncoding);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    public async Task RepeatedContentEncodingHeadersAreRejected()
    {
        var content = new TrackingContent(CelesTrakGpTestData.Gzip(SmallJson));
        var (result, _) = await FetchAsync(content, contentEncoding: "gzip", configure: static response =>
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip")).ConfigureAwait(false);

        Assert.AreEqual("orbital.content-encoding", result.ReasonCode);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    public async Task DeclaredLengthOverTheWireBoundIsRejectedBeforeTheBodyIsRead()
    {
        var content = new TrackingContent(new byte[101]);
        var (result, _) = await FetchAsync(content, contentLength: 101, settings: Settings(wire: 100)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.wire-bound", result.ReasonCode);
        Assert.AreEqual("declared-bytes=101 limit=100", result.Detail);
        Assert.AreEqual(0, result.WireLengthBytes);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    public async Task DeclaredLengthOverTheDefaultBoundIsRejectedWithoutAllocatingIt()
    {
        var content = new TrackingContent([]);
        var (result, _) = await FetchAsync(content, contentLength: CelesTrakGpSource.MaximumWireBytes + 1L).ConfigureAwait(false);

        Assert.AreEqual("orbital.wire-bound", result.ReasonCode);
        Assert.AreEqual($"declared-bytes={CelesTrakGpSource.MaximumWireBytes + 1L} limit={CelesTrakGpSource.MaximumWireBytes}",
            result.Detail);
        Assert.IsFalse(content.BodyRead);
    }

    [TestMethod]
    [DataRow(int.MaxValue)]
    [DataRow(7)]
    [DataRow(1)]
    public async Task UndeclaredBodyOverTheWireBoundIsRejectedAtOneByteOver(int chunkBytes)
    {
        var (result, _) = await FetchAsync(new TrackingContent(new byte[1000], chunkBytes), settings: Settings(wire: 100)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.wire-bound", result.ReasonCode);
        Assert.AreEqual("read-bytes>100", result.Detail);
        Assert.AreEqual(101, result.WireLengthBytes);
        Assert.IsNull(result.RawBytes);
    }

    [TestMethod]
    [DataRow(false, int.MaxValue)]
    [DataRow(false, 1)]
    [DataRow(true, int.MaxValue)]
    [DataRow(true, 3)]
    public async Task BodyExactlyAtTheWireBoundIsAccepted(bool declared, int chunkBytes)
    {
        var body = Enumerable.Repeat((byte)'x', 100).ToArray();
        var (result, _) = await FetchAsync(new TrackingContent(body, chunkBytes), contentLength: declared ? 100 : null,
            settings: Settings(wire: 100)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, result.Disposition, result.Detail);
        Assert.AreEqual(100, result.WireLengthBytes);
        CollectionAssert.AreEqual(body, result.RawBytes);
    }

    [TestMethod]
    [DataRow(50, 40)]
    [DataRow(40, 50)]
    [DataRow(1, 0)]
    public async Task DeclaredLengthThatDisagreesWithTheBodyIsRejected(int declared, int bodyLength)
    {
        var (result, _) = await FetchAsync(new TrackingContent(new byte[bodyLength]), contentLength: declared,
            settings: Settings(wire: 100)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.length-mismatch", result.ReasonCode);
        Assert.AreEqual($"declared-bytes={declared} read-bytes={bodyLength}", result.Detail);
        Assert.AreEqual(bodyLength, result.WireLengthBytes);
    }

    [TestMethod]
    public async Task GzipBombIsStoppedAtTheDefaultRawBound()
    {
        var bomb = CelesTrakGpTestData.Gzip(new byte[33 * 1024 * 1024]);
        Assert.IsLessThan(CelesTrakGpSource.MaximumWireBytes / 100, bomb.Length);

        var (result, _) = await FetchAsync(new TrackingContent(bomb), contentEncoding: "gzip").ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.raw-bound", result.ReasonCode);
        Assert.AreEqual($"decoded-bytes>{CelesTrakGpSource.MaximumRawBytes}", result.Detail);
        Assert.AreEqual(bomb.Length, result.WireLengthBytes);
        Assert.AreEqual("gzip", result.ContentEncoding);
        Assert.IsNull(result.RawBytes);
    }

    [TestMethod]
    public async Task GzipIsDecodedUpToExactlyTheConfiguredRawBound()
    {
        var exact = Enumerable.Repeat((byte)'[', 1000).ToArray();
        var over = Enumerable.Repeat((byte)'[', 1001).ToArray();

        var (accepted, _) = await FetchAsync(new TrackingContent(CelesTrakGpTestData.Gzip(exact)), contentEncoding: "gzip",
            settings: Settings(raw: 1000)).ConfigureAwait(false);
        var (rejected, _) = await FetchAsync(new TrackingContent(CelesTrakGpTestData.Gzip(over)), contentEncoding: "gzip",
            settings: Settings(raw: 1000)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, accepted.Disposition, accepted.Detail);
        CollectionAssert.AreEqual(exact, accepted.RawBytes);
        Assert.AreEqual(CelesTrakGpTestData.Sha256(exact), accepted.RawSha256);
        Assert.AreEqual("orbital.raw-bound", rejected.ReasonCode);
        Assert.AreEqual("decoded-bytes>1000", rejected.Detail);
    }

    [TestMethod]
    public async Task GzipWhoseTrailerUnderstatesTheSizeStillStopsAtTheRawBound()
    {
        var wire = CelesTrakGpTestData.Gzip(new byte[5000]);
        wire[^4] = 1;
        wire[^3] = 0;
        wire[^2] = 0;
        wire[^1] = 0;

        var (result, _) = await FetchAsync(new TrackingContent(wire), contentEncoding: "gzip", settings: Settings(raw: 1000)).ConfigureAwait(false);

        Assert.AreEqual("orbital.raw-bound", result.ReasonCode);
    }

    [TestMethod]
    [DataRow("plain-json-labelled-gzip", "gzip-header")]
    [DataRow("too-short", "gzip-header")]
    [DataRow("wrong-magic", "gzip-header")]
    [DataRow("wrong-method", "gzip-header")]
    [DataRow("corrupt-crc", "gzip-")]
    [DataRow("corrupt-deflate", "gzip-")]
    [DataRow("truncated-trailer", "gzip-")]
    [DataRow("truncated-deflate", "gzip-")]
    [DataRow("trailing-garbage", "gzip-")]
    [DataRow("second-member", "gzip-size-mismatch")]
    public async Task InvalidGzipIsRejected(string shape, string detailPrefix)
    {
        var payload = CelesTrakGpTestData.ReadFixture(CelesTrakGpTestData.ValidFixture);
        var valid = CelesTrakGpTestData.Gzip(payload);
        var wire = shape switch
        {
            "plain-json-labelled-gzip" => payload,
            "too-short" => valid[..17],
            "wrong-magic" => Mutate(valid, 1, 0x8C),
            "wrong-method" => Mutate(valid, 2, 0x07),
            "corrupt-crc" => Mutate(valid, valid.Length - 8, (byte)(valid[^8] ^ 0xFF)),
            "corrupt-deflate" => Mutate(valid, valid.Length / 2, (byte)(valid[valid.Length / 2] ^ 0x5A)),
            "truncated-trailer" => valid[..^4],
            "truncated-deflate" => valid[..(valid.Length / 2)],
            "trailing-garbage" => [.. valid, .. "GARBAGE!"u8.ToArray()],
            "second-member" => [.. valid, .. CelesTrakGpTestData.Gzip("[]"u8.ToArray())],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        var (result, _) = await FetchAsync(new TrackingContent(wire), contentEncoding: "gzip").ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition, result.Detail);
        Assert.AreEqual("orbital.gzip-invalid", result.ReasonCode, result.Detail);
        Assert.StartsWith(detailPrefix, result.Detail);
        Assert.AreEqual(wire.Length, result.WireLengthBytes);
        Assert.IsNull(result.RawBytes);
    }

    [TestMethod]
    public void GzipDecodingObservesCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var wire = CelesTrakGpTestData.Gzip(SmallJson);

        Assert.ThrowsExactly<OperationCanceledException>(() => CelesTrakGpClient.DecodeGzip(wire, 1000, cancelled.Token));
    }

    [TestMethod]
    [DataRow("identity-undeclared")]
    [DataRow("identity-declared-zero")]
    [DataRow("gzip-zero-bytes")]
    [DataRow("gzip-declared-zero")]
    [DataRow("gzip-empty-member")]
    public async Task EmptyBodiesAreRejected(string shape)
    {
        // A well-formed gzip member whose content is empty: header, final empty fixed block, CRC32 0 and ISIZE 0.
        byte[] emptyMember = [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0x03, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0, 0];
        var (wire, encoding, declared) = shape switch
        {
            "identity-undeclared" => (Array.Empty<byte>(), (string?)null, (long?)null),
            "identity-declared-zero" => ([], null, 0L),
            "gzip-zero-bytes" => ([], "gzip", null),
            "gzip-declared-zero" => ([], "gzip", 0L),
            "gzip-empty-member" => (emptyMember, "gzip", emptyMember.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var (result, _) = await FetchAsync(new TrackingContent(wire), contentEncoding: encoding,
            contentLength: declared).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.empty-body", result.ReasonCode, result.Detail);
        Assert.AreEqual("raw-bytes=0", result.Detail);
        Assert.AreEqual(wire.Length, result.WireLengthBytes);
    }

    [TestMethod]
    public async Task IdentityBodyOverTheRawBoundIsRejected()
    {
        var (result, _) = await FetchAsync(new TrackingContent(new byte[150]), settings: Settings(wire: 200, raw: 100)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.ResponseRejected, result.Disposition);
        Assert.AreEqual("orbital.raw-bound", result.ReasonCode);
        Assert.AreEqual("raw-bytes>100", result.Detail);
        Assert.AreEqual(150, result.WireLengthBytes);
    }

    [TestMethod]
    public async Task InternalDeadlineWhileWaitingForHeadersIsATimeoutNotCancellation()
    {
        var clock = new ManualTimeProvider(CelesTrakGpTestData.RetrievedUtc);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpHandler(async (_, token) =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });
        var client = CelesTrakGpTestData.CreateClient(handler, clock, Settings(timeout: TimeSpan.FromSeconds(12)));

        var fetch = client.FetchAsync(CancellationToken.None).AsTask();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromMilliseconds(11_999));
        Assert.IsFalse(fetch.IsCompleted, "The deadline has not passed.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await fetch.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TimedOut, result.Disposition);
        Assert.AreEqual("orbital.request-timeout", result.ReasonCode);
        Assert.AreEqual("timeout-ms=12000", result.Detail);
        Assert.IsNull(result.HttpStatusCode);
        Assert.IsNull(result.RawBytes);
        Assert.AreEqual(CelesTrakGpTestData.RetrievedUtc, result.StartedUtc);
        Assert.AreEqual(CelesTrakGpTestData.RetrievedUtc.AddSeconds(12), result.CompletedUtc);
    }

    [TestMethod]
    public async Task InternalDeadlineCoversTheBodyRead()
    {
        var clock = new ManualTimeProvider(CelesTrakGpTestData.RetrievedUtc);
        var content = new TrackingContent(SmallJson, holdReads: true);
        var handler = new StubHttpHandler(() => CelesTrakGpTestData.Response(HttpStatusCode.OK, content,
            configure: static response => response.Headers.TryAddWithoutValidation("ETag", "\"slow\"")));
        var client = CelesTrakGpTestData.CreateClient(handler, clock);

        var fetch = client.FetchAsync(CancellationToken.None).AsTask();
        await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        clock.Advance(CelesTrakGpSource.MaximumRequestTimeout);
        var result = await fetch.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TimedOut, result.Disposition);
        Assert.AreEqual("orbital.request-timeout", result.ReasonCode);
        Assert.AreEqual("timeout-ms=30000", result.Detail);
        Assert.AreEqual(200, result.HttpStatusCode);
        Assert.AreEqual("\"slow\"", result.EntityTag);
        Assert.IsNull(result.RawBytes);
    }

    [TestMethod]
    public async Task CallerCancellationWhileWaitingIsRethrownAndNotReportedAsAnOutcome()
    {
        var clock = new ManualTimeProvider(CelesTrakGpTestData.RetrievedUtc);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpHandler(async (_, token) =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });
        using var caller = new CancellationTokenSource();
        var fetch = CelesTrakGpTestData.CreateClient(handler, clock).FetchAsync(caller.Token).AsTask();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await caller.CancelAsync().ConfigureAwait(false);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        Assert.AreEqual(caller.Token, exception.CancellationToken);
    }

    [TestMethod]
    public async Task CallerCancellationDuringTheBodyReadIsRethrown()
    {
        var content = new TrackingContent(SmallJson, holdReads: true);
        var handler = new StubHttpHandler(() => CelesTrakGpTestData.Response(HttpStatusCode.OK, content));
        using var caller = new CancellationTokenSource();
        var fetch = CelesTrakGpTestData.CreateClient(handler).FetchAsync(caller.Token).AsTask();
        await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await caller.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task PreCancelledCallerSendsNothing()
    {
        var handler = new StubHttpHandler(() => CelesTrakGpTestData.Response(HttpStatusCode.OK, new TrackingContent(SmallJson)));
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CelesTrakGpTestData.CreateClient(handler).FetchAsync(caller.Token).AsTask()).ConfigureAwait(false);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task HttpClientTimeoutIsReportedAsATimeoutDistinctFromTheInternalDeadline()
    {
        var handler = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        });
        var client = new CelesTrakGpClient(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) },
            TimeProvider.System, CelesTrakGpTestData.DefaultSettings);

        var result = await client.FetchAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TimedOut, result.Disposition);
        Assert.AreEqual("orbital.http-client-timeout", result.ReasonCode);
        Assert.Contains("TimeoutException", result.Detail);
    }

    [TestMethod]
    [DataRow("tls", "orbital.transport-tls")]
    [DataRow("authentication-exception", "orbital.transport-tls")]
    [DataRow("unknown-with-authentication-inner", "orbital.transport-tls")]
    [DataRow("user-authentication", "orbital.transport-authentication")]
    [DataRow("proxy-tunnel", "orbital.transport-authentication")]
    [DataRow("connection", "orbital.transport-connection")]
    [DataRow("name-resolution", "orbital.transport-connection")]
    [DataRow("socket", "orbital.transport-connection")]
    [DataRow("unknown-with-socket-inner", "orbital.transport-connection")]
    [DataRow("protocol", "orbital.transport-protocol")]
    [DataRow("invalid-response", "orbital.transport-protocol")]
    [DataRow("response-ended", "orbital.transport-protocol")]
    [DataRow("version-negotiation", "orbital.transport-protocol")]
    [DataRow("http-io", "orbital.transport-protocol")]
    [DataRow("limit", "orbital.transport-limit")]
    [DataRow("io", "orbital.transport-io")]
    [DataRow("unknown", "orbital.transport-failure")]
    public async Task TransportFailuresAreClassifiedWithoutExceptionMessages(string shape, string reasonCode)
    {
        var handler = new StubHttpHandler((_, _) => throw CreateTransportException(shape));

        var result = await CelesTrakGpTestData.CreateClient(handler).FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TransportFailure, result.Disposition);
        Assert.AreEqual(reasonCode, result.ReasonCode);
        Assert.IsNull(result.HttpStatusCode);
        Assert.IsNull(result.RawBytes);
        Assert.DoesNotContain(Secret, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("celestrak.org", result.Detail, StringComparison.Ordinal);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Detail));
    }

    [TestMethod]
    public async Task UnexplainedCancellationBeforeHeadersIsATransportFailureNotATimeout()
    {
        // A cancellation that neither the caller, the internal deadline nor HttpClient.Timeout requested (for example
        // an inner handler aborting) passes through HttpClient unchanged. It is not evidence of a timeout.
        var handler = new StubHttpHandler((_, _) => throw CreateTransportException("handler-cancellation"));

        var result = await CelesTrakGpTestData.CreateClient(handler).FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TransportFailure, result.Disposition, result.Detail);
        Assert.AreEqual("orbital.transport-cancelled", result.ReasonCode);
        Assert.AreEqual("OperationCanceledException", result.Detail);
        Assert.IsNull(result.HttpStatusCode);
        Assert.DoesNotContain(Secret, result.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public void TransportDetailNamesTypesAndErrorCodesOnly()
    {
        var exception = new HttpRequestException(HttpRequestError.ConnectionError, Secret,
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.AreEqual("HttpRequestException:ConnectionError <- SocketException:ConnectionRefused",
            CelesTrakGpClient.DescribeException(exception));
    }

    [TestMethod]
    public void TransportDetailIsBoundedToFourExceptionsDeep()
    {
        Exception exception = new IOException(Secret);
        for (var i = 0; i < 10; i++)
        {
            exception = new IOException(Secret, exception);
        }

        Assert.AreEqual("IOException <- IOException <- IOException <- IOException",
            CelesTrakGpClient.DescribeException(exception));
    }

    [TestMethod]
    [DataRow("io", "orbital.transport-io")]
    [DataRow("http-io", "orbital.transport-protocol")]
    [DataRow("socket", "orbital.transport-connection")]
    [DataRow("handler-cancellation", "orbital.transport-cancelled")]
    public async Task BodyReadFailuresAreTransportFailuresAfterTheStatusWasSeen(string shape, string reasonCode)
    {
        var content = new TrackingContent(SmallJson, readFailure: CreateTransportException(shape));
        var (result, _) = await FetchAsync(content).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TransportFailure, result.Disposition);
        Assert.AreEqual(reasonCode, result.ReasonCode);
        Assert.AreEqual(200, result.HttpStatusCode);
        Assert.IsNull(result.RawBytes);
        Assert.DoesNotContain(Secret, result.Detail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UnexpectedExceptionsAreNotDisguisedAsProviderOutcomes()
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("programming error"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CelesTrakGpTestData.CreateClient(handler).FetchAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ValidatorsAreCapturedForEveryResponseAndSanitized()
    {
        var (halt, _) = await FetchAsync(new TrackingContent(SmallJson), HttpStatusCode.NotModified, configure: static response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "W/\"304-tag\"");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", "Mon, 19 Oct 2026 17:00:00 GMT");
        }).ConfigureAwait(false);
        var (dropped, _) = await FetchAsync(new TrackingContent(SmallJson), configure: static response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "\"café\"");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", new string('M', 129));
        }).ConfigureAwait(false);
        var (duplicate, _) = await FetchAsync(new TrackingContent(SmallJson), configure: static response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "\"one\"");
            response.Headers.TryAddWithoutValidation("ETag", "\"two\"");
        }).ConfigureAwait(false);
        var (bounded, _) = await FetchAsync(new TrackingContent(SmallJson), configure: static response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "\"" + new string('e', 1022) + "\"");
            response.Content.Headers.TryAddWithoutValidation("Last-Modified", new string('M', 128));
        }).ConfigureAwait(false);

        Assert.AreEqual("W/\"304-tag\"", halt.EntityTag);
        Assert.AreEqual("Mon, 19 Oct 2026 17:00:00 GMT", halt.LastModified);
        Assert.IsNull(dropped.EntityTag);
        Assert.IsNull(dropped.LastModified);
        Assert.IsNull(duplicate.EntityTag);
        Assert.AreEqual(1024, bounded.EntityTag!.Length);
        Assert.AreEqual(128, bounded.LastModified!.Length);
    }

    [TestMethod]
    public void ConstructorRejectsNullDependencies()
    {
        using var handler = new StubHttpHandler(() => throw new InvalidOperationException());
        using var httpClient = new HttpClient(handler);
        var settings = CelesTrakGpTestData.DefaultSettings;

        Assert.ThrowsExactly<ArgumentNullException>(() => new CelesTrakGpClient(null!, TimeProvider.System, settings));
        Assert.ThrowsExactly<ArgumentNullException>(() => new CelesTrakGpClient(httpClient, null!, settings));
        Assert.ThrowsExactly<ArgumentNullException>(() => new CelesTrakGpClient(httpClient, TimeProvider.System, null!));
    }

    [TestMethod]
    [DataRow("timeout-zero")]
    [DataRow("timeout-negative")]
    [DataRow("timeout-infinite")]
    [DataRow("timeout-over")]
    [DataRow("wire-zero")]
    [DataRow("wire-negative")]
    [DataRow("wire-over")]
    [DataRow("raw-zero")]
    [DataRow("raw-negative")]
    [DataRow("raw-over")]
    public void ConstructorRejectsOutOfRangeBounds(string shape)
    {
        var defaults = CelesTrakGpTestData.DefaultSettings;
        var settings = shape switch
        {
            "timeout-zero" => defaults with { RequestTimeout = TimeSpan.Zero },
            "timeout-negative" => defaults with { RequestTimeout = TimeSpan.FromMilliseconds(-1) },
            "timeout-infinite" => defaults with { RequestTimeout = Timeout.InfiniteTimeSpan },
            "timeout-over" => defaults with { RequestTimeout = CelesTrakGpSource.MaximumRequestTimeout + TimeSpan.FromTicks(1) },
            "wire-zero" => defaults with { MaximumWireBytes = 0 },
            "wire-negative" => defaults with { MaximumWireBytes = -1 },
            "wire-over" => defaults with { MaximumWireBytes = CelesTrakGpSource.MaximumWireBytes + 1 },
            "raw-zero" => defaults with { MaximumRawBytes = 0 },
            "raw-negative" => defaults with { MaximumRawBytes = -1 },
            "raw-over" => defaults with { MaximumRawBytes = CelesTrakGpSource.MaximumRawBytes + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        using var httpClient = new HttpClient(new StubHttpHandler(() => throw new InvalidOperationException()));

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new CelesTrakGpClient(httpClient, TimeProvider.System, settings));
        Assert.AreEqual("settings", exception.ParamName);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("blank")]
    [DataRow("control")]
    [DataRow("line-break")]
    [DataRow("non-ascii")]
    [DataRow("unbalanced-comment")]
    [DataRow("separator")]
    [DataRow("too-long")]
    public void ConstructorRejectsMalformedUserAgents(string shape)
    {
        var userAgent = shape switch
        {
            "null" => null,
            "empty" => string.Empty,
            "blank" => "   ",
            "control" => "HVO.SkyMonitor/1.0\u0001",
            "line-break" => "HVO.SkyMonitor/1.0\r\nCookie: a=b",
            "non-ascii" => "HVO.SkyMonitor/1.0 café",
            "unbalanced-comment" => "HVO.SkyMonitor/1.0 (contact",
            "separator" => "HVO.SkyMonitor/1.0 @@@",
            "too-long" => "HVO.SkyMonitor/" + new string('1', 250),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        using var httpClient = new HttpClient(new StubHttpHandler(() => throw new InvalidOperationException()));

        var exception = Assert.ThrowsExactly<ArgumentException>(() => new CelesTrakGpClient(httpClient, TimeProvider.System,
            CelesTrakGpTestData.DefaultSettings with { UserAgent = userAgent! }));
        Assert.AreEqual("settings", exception.ParamName);
    }

    [TestMethod]
    [DataRow("HVO.SkyMonitor/1.0")]
    [DataRow("HVO.SkyMonitor.CameraAgent/0.9.1-dev (+https://example.invalid/contact)")]
    [DataRow("a")]
    public void ConstructorAcceptsWellFormedUserAgentsAndInclusiveBounds(string userAgent)
    {
        using var httpClient = new HttpClient(new StubHttpHandler(() => throw new InvalidOperationException()));
        var settings = new CelesTrakGpClientSettings(CelesTrakGpSource.MaximumRequestTimeout, 1, 1, userAgent);

        _ = new CelesTrakGpClient(httpClient, TimeProvider.System, settings);
        _ = new CelesTrakGpClient(httpClient, TimeProvider.System, settings with
        {
            RequestTimeout = TimeSpan.FromTicks(1),
            MaximumWireBytes = CelesTrakGpSource.MaximumWireBytes,
            MaximumRawBytes = CelesTrakGpSource.MaximumRawBytes
        });
        Assert.IsTrue(CelesTrakGpClient.IsValidUserAgent("U/" + new string('1', 254)));
        Assert.IsFalse(CelesTrakGpClient.IsValidUserAgent("U/" + new string('1', 255)));
    }

    [TestMethod]
    [DataRow("If-None-Match", "\"v1\"")]
    [DataRow("Cookie", "session=abc")]
    [DataRow("Authorization", "Bearer abc")]
    [DataRow("User-Agent", "Other/1.0")]
    public void ConstructorRejectsHttpClientsWithDefaultRequestHeaders(string name, string value)
    {
        using var httpClient = new HttpClient(new StubHttpHandler(() => throw new InvalidOperationException()));
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(name, value);

        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => new CelesTrakGpClient(httpClient, TimeProvider.System, CelesTrakGpTestData.DefaultSettings));
        Assert.AreEqual("httpClient", exception.ParamName);
    }

    private static async Task<(OrbitalFetchResult Result, StubHttpHandler Handler)> FetchAsync(TrackingContent content,
        HttpStatusCode status = HttpStatusCode.OK, string? contentType = "application/json", string? contentEncoding = null,
        long? contentLength = null, CelesTrakGpClientSettings? settings = null, TimeProvider? timeProvider = null,
        Action<HttpResponseMessage>? configure = null)
    {
        var handler = new StubHttpHandler(() =>
            CelesTrakGpTestData.Response(status, content, contentType, contentEncoding, contentLength, configure));
        var result = await CelesTrakGpTestData.CreateClient(handler, timeProvider, settings).FetchAsync(CancellationToken.None).ConfigureAwait(false);
        return (result, handler);
    }

    private static CelesTrakGpClientSettings Settings(int? wire = null, int? raw = null, TimeSpan? timeout = null) =>
        CelesTrakGpTestData.DefaultSettings with
        {
            MaximumWireBytes = wire ?? CelesTrakGpSource.MaximumWireBytes,
            MaximumRawBytes = raw ?? CelesTrakGpSource.MaximumRawBytes,
            RequestTimeout = timeout ?? CelesTrakGpSource.MaximumRequestTimeout
        };

    private static byte[] Mutate(byte[] source, int index, byte value)
    {
        var copy = source.ToArray();
        copy[index] = value;
        return copy;
    }

    private static Exception CreateTransportException(string shape) => shape switch
    {
        "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, Secret, new AuthenticationException(Secret)),
        "authentication-exception" => new AuthenticationException(Secret),
        "unknown-with-authentication-inner" => new HttpRequestException(Secret, new AuthenticationException(Secret)),
        "user-authentication" => new HttpRequestException(HttpRequestError.UserAuthenticationError, Secret),
        "proxy-tunnel" => new HttpRequestException(HttpRequestError.ProxyTunnelError, Secret),
        "connection" => new HttpRequestException(HttpRequestError.ConnectionError, Secret,
            new SocketException((int)SocketError.ConnectionRefused)),
        "name-resolution" => new HttpRequestException(HttpRequestError.NameResolutionError, Secret,
            new SocketException((int)SocketError.HostNotFound)),
        "socket" => new SocketException((int)SocketError.ConnectionReset),
        "unknown-with-socket-inner" => new HttpRequestException(Secret, new SocketException((int)SocketError.TimedOut)),
        "protocol" => new HttpRequestException(HttpRequestError.HttpProtocolError, Secret),
        "invalid-response" => new HttpRequestException(HttpRequestError.InvalidResponse, Secret),
        "response-ended" => new HttpRequestException(HttpRequestError.ResponseEnded, Secret),
        "version-negotiation" => new HttpRequestException(HttpRequestError.VersionNegotiationError, Secret),
        "http-io" => new HttpIOException(HttpRequestError.ResponseEnded, Secret),
        "limit" => new HttpRequestException(HttpRequestError.ConfigurationLimitExceeded, Secret),
        "io" => new IOException(Secret),
        "unknown" => new HttpRequestException(Secret),
        "handler-cancellation" => new OperationCanceledException(Secret),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };
}
