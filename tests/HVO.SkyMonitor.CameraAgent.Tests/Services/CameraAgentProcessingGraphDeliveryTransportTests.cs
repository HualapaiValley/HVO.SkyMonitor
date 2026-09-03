using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentProcessingGraphDeliveryTransportTests
{
    [TestMethod]
    public async Task SuccessfulPullAndFactUseDeviceCredentialsAndStrictContracts()
    {
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var fact = CreateFact(now);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(new ProcessingGraphProposalPollResponseV1(
                ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Current,
                "assignment-current",
                now)),
            JsonResponse(new ProcessingGraphFactAcknowledgementV1(
                ProcessingGraphDeliverySchemaVersions.Current,
                fact.FactId,
                ProcessingGraphFactAcknowledgementDisposition.Recorded,
                now))
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        var factory = new StubHttpClientFactory(handler);
        var transport = CreateTransport(factory, new TestTimeProvider(now));

        var pull = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var acknowledgement = await transport.SendFactAsync(fact, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Acknowledged, pull.Disposition);
        Assert.AreEqual(ProcessingGraphProposalPollDisposition.Current, pull.Response!.Disposition);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Acknowledged, acknowledgement.Disposition);
        Assert.AreEqual(fact.FactId, acknowledgement.Acknowledgement!.FactId);
        Assert.AreEqual("/api/device/processing-graphs/facts", handler.LastPath);
        Assert.AreEqual("device-1", handler.LastHeaders["X-HVO-Device-Id"]);
        Assert.AreEqual("device-key", handler.LastHeaders["X-HVO-Device-Key"]);
        Assert.AreEqual("1", handler.LastHeaders["X-Skip-CentralAuth"]);
        Assert.AreEqual("application/json", handler.LastContentType);
        Assert.AreEqual(SkyMonitorClientOptions.HttpClientName, factory.LastName);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, factory.Client!.Timeout);
    }

    [TestMethod]
    public async Task MissingCredentialsInvalidPayloadsAndInstallationMismatchAreClassified()
    {
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var blocked = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => new(HttpStatusCode.OK))),
            new TestTimeProvider(now),
            credentialsAvailable: false);
        var missingPull = await blocked.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var missingFact = await blocked.SendFactAsync(CreateFact(now), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, missingPull.Disposition);
        Assert.AreEqual(TimeSpan.FromMinutes(5), missingPull.RetryAfter);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, missingFact.Disposition);

        var proposal = new ProcessingGraphDeliveryProposalV1(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new ProcessingGraphDefinition(ProcessingGraphSchemaVersions.Current, "test", "1", [], []),
            now,
            now.AddHours(1));
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(new ProcessingGraphProposalPollResponseV1(
                ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Proposed,
                "proposal-available",
                now,
                proposal)),
            new(HttpStatusCode.OK) { Content = new ByteArrayContent("null"u8.ToArray()) },
            new(HttpStatusCode.OK) { Content = new ByteArrayContent("null"u8.ToArray()) }
        ]);
        var transport = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => responses.Dequeue())),
            new TestTimeProvider(now));

        var mismatch = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var invalidPoll = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var invalidFact = await transport.SendFactAsync(CreateFact(now), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Rejected, mismatch.Disposition);
        Assert.AreEqual("installation-mismatch", mismatch.ReasonCode);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Retry, invalidPoll.Disposition);
        Assert.AreEqual("invalid-response", invalidPoll.ReasonCode);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Retry, invalidFact.Disposition);
        Assert.AreEqual("invalid-acknowledgement", invalidFact.ReasonCode);
    }

    [TestMethod]
    public async Task HttpFailuresHonorAuthenticationPermanenceAndRetryAfterBounds()
    {
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var retryDelta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryDelta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
        var retryDate = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        retryDate.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(23));
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.Unauthorized),
            new(HttpStatusCode.Forbidden),
            new(HttpStatusCode.BadRequest),
            new(HttpStatusCode.Conflict),
            new(HttpStatusCode.NotFound),
            retryDelta,
            retryDate,
            new(HttpStatusCode.InternalServerError)
        ]);
        var transport = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => responses.Dequeue())),
            new TestTimeProvider(now));

        var unauthorized = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var forbidden = await transport.SendFactAsync(CreateFact(now), CancellationToken.None).ConfigureAwait(false);
        var badRequest = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var conflict = await transport.SendFactAsync(CreateFact(now), CancellationToken.None).ConfigureAwait(false);
        var notFound = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var delta = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        var date = await transport.SendFactAsync(CreateFact(now), CancellationToken.None).ConfigureAwait(false);
        var serverError = await transport.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, unauthorized.Disposition);
        Assert.AreEqual(TimeSpan.FromMinutes(5), unauthorized.RetryAfter);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, forbidden.Disposition);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Rejected, badRequest.Disposition);
        Assert.AreEqual("http-400", badRequest.ReasonCode);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Rejected, conflict.Disposition);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Rejected, notFound.Disposition);
        Assert.AreEqual(TimeSpan.FromSeconds(17), delta.RetryAfter);
        Assert.AreEqual(TimeSpan.FromSeconds(23), date.RetryAfter);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Retry, serverError.Disposition);
    }

    [TestMethod]
    public async Task TimeoutTransportFailureAndOversizedBodiesRemainRetryable()
    {
        var now = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var stalled = new StalledHandler();
        var timeout = CreateTransport(
            new StubHttpClientFactory(stalled), new TestTimeProvider(now), timeoutSeconds: 1);
        var timedOut = await timeout.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Retry, timedOut.Disposition);
        Assert.AreEqual("request-timeout", timedOut.ReasonCode);
        Assert.IsTrue(stalled.CancellationObserved);

        var unavailable = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => throw new HttpRequestException("unavailable"))),
            new TestTimeProvider(now));
        Assert.AreEqual(
            "transport-unavailable",
            (await unavailable.SendFactAsync(CreateFact(now), CancellationToken.None).ConfigureAwait(false)).ReasonCode);

        var oversized = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[ProcessingGraphJson.MaximumDocumentBytes + 64 * 1024 + 1])
            })),
            new TestTimeProvider(now));
        var result = await oversized.PullAsync(CreatePoll(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphDeliveryTransportDisposition.Retry, result.Disposition);
        Assert.AreEqual("invalid-response", result.ReasonCode);
    }

    private static CameraAgentProcessingGraphDeliveryTransport CreateTransport(
        IHttpClientFactory factory,
        TimeProvider timeProvider,
        bool credentialsAvailable = true,
        int timeoutSeconds = 30)
        => new(
            new StubIdentityStore(),
            new StubSecretStore(credentialsAvailable ? CreateSecrets() : null),
            factory,
            Options.Create(new CameraAgentHostOptions
            {
                ProcessingGraphDelivery = new ProcessingGraphDeliveryOptions
                {
                    RequestTimeoutSeconds = timeoutSeconds
                }
            }),
            timeProvider);

    private static ProcessingGraphProposalPollRequestV1 CreatePoll()
        => new(
            ProcessingGraphDeliverySchemaVersions.Current,
            "device-1",
            null,
            null,
            null,
            ProcessingGraphAgentCapabilities.Create([BuiltInProcessingRecipes.EncodedPreview]));

    private static ProcessingGraphDeliveryFactV1 CreateFact(DateTimeOffset now)
        => new(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ProcessingGraphDeliveryFactKind.Accepted,
            now,
            new string('4', 64),
            new string('5', 64),
            new string('6', 64),
            new string('7', 64),
            "accepted");

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
        };

    private static DeviceSecrets CreateSecrets()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "device",
            "registration",
            "/heartbeat",
            30,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddYears(1),
            "device-key",
            new CentralIdentityOptions());

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    private sealed class StubIdentityStore : IDeviceIdentityStore
    {
        public Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceIdentity("device-1", "VERIFY", DateTimeOffset.UnixEpoch));
    }

    private sealed class StubSecretStore(DeviceSecrets? secrets) : IDeviceSecretStore
    {
        public Task<DeviceSecrets?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(secrets);

        public Task SaveAsync(DeviceSecrets value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastName { get; private set; }

        public HttpClient? Client { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            Client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://central.test") };
            return Client;
        }
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? LastPath { get; private set; }

        public string? LastContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastHeaders.Clear();
            foreach (var header in request.Headers)
            {
                LastHeaders[header.Key] = string.Join(",", header.Value);
            }
            LastPath = request.RequestUri?.AbsolutePath;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(response(request));
        }
    }

    private sealed class StalledHandler : HttpMessageHandler
    {
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The stalled request unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
