using System.Net;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Transients;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentTransientCandidateTransportTests
{
    [TestMethod]
    public async Task AcceptedAndDuplicateRequireExactAcknowledgementAndAuthenticatedHeaders()
    {
        var submission = TransientDeliveryTestData.Submission();
        var responses = new Queue<HttpResponseMessage>(
        [
            Response(HttpStatusCode.Accepted, TransientDeliveryTestData.Acknowledgement(submission)),
            Response(HttpStatusCode.OK, TransientDeliveryTestData.Acknowledgement(
                submission, TransientCandidateSubmissionDisposition.Duplicate))
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        var factory = new StubHttpClientFactory(handler);
        var transport = CreateTransport(factory);

        var accepted = await transport.SendAsync(submission, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await transport.SendAsync(submission, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateTransportDisposition.Acknowledged, accepted.Disposition);
        Assert.AreEqual(TransientCandidateTransportDisposition.Acknowledged, duplicate.Disposition);
        Assert.AreEqual(SkyMonitorClientOptions.HttpClientName, factory.LastName);
        Assert.AreEqual("device-1", handler.LastHeaders["X-HVO-Device-Id"]);
        Assert.AreEqual("device-key", handler.LastHeaders["X-HVO-Device-Key"]);
        Assert.AreEqual(submission.SubmissionIdentitySha256, handler.LastHeaders["Idempotency-Key"]);
        Assert.IsFalse(handler.LastHeaders.ContainsKey("X-Skip-CentralAuth"));
    }

    [TestMethod]
    public async Task MalformedMismatchedAndRetryableResponsesRetainForRetry()
    {
        var submission = TransientDeliveryTestData.Submission();
        var mismatch = TransientDeliveryTestData.Acknowledgement(submission) with { CandidateId = Guid.NewGuid() };
        var noncanonical = TransientCandidateDeliveryJson.Serialize(
            TransientDeliveryTestData.Acknowledgement(submission));
        noncanonical = [.. noncanonical, (byte)'\n'];
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.Accepted) { Content = new ByteArrayContent("bad-json"u8.ToArray()) },
            Response(HttpStatusCode.Accepted, mismatch),
            Response(HttpStatusCode.OK, TransientDeliveryTestData.Acknowledgement(submission)),
            new(HttpStatusCode.Accepted) { Content = new ByteArrayContent(noncanonical) },
            new(HttpStatusCode.NotFound),
            new(HttpStatusCode.TooManyRequests),
            new(HttpStatusCode.ServiceUnavailable)
        ]);
        var transport = CreateTransport(new StubHttpClientFactory(new CapturingHandler(_ => responses.Dequeue())));

        for (var index = 0; index < 7; index++)
        {
            var result = await transport.SendAsync(submission, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(TransientCandidateTransportDisposition.Retry, result.Disposition);
            Assert.IsNull(result.Acknowledgement);
        }
    }

    [TestMethod]
    public async Task MissingCredentialsAuthenticationFailureAndPermanentContractFailureRemainBlocked()
    {
        var submission = TransientDeliveryTestData.Submission();
        var unavailable = new CameraAgentTransientCandidateTransport(
            new StubIdentityStore(),
            new StubSecretStore(null),
            new StubHttpClientFactory(new CapturingHandler(_ => new(HttpStatusCode.Accepted))),
            Options.Create(new CameraAgentHostOptions()),
            TimeProvider.System);
        var responses = new Queue<HttpResponseMessage>([new(HttpStatusCode.Unauthorized), new(HttpStatusCode.BadRequest)]);
        var transport = CreateTransport(new StubHttpClientFactory(new CapturingHandler(_ => responses.Dequeue())));

        var missing = await unavailable.SendAsync(submission, CancellationToken.None).ConfigureAwait(false);
        var unauthorized = await transport.SendAsync(submission, CancellationToken.None).ConfigureAwait(false);
        var rejected = await transport.SendAsync(submission, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateTransportDisposition.AuthenticationBlocked, missing.Disposition);
        Assert.AreEqual(TransientCandidateTransportDisposition.AuthenticationBlocked, unauthorized.Disposition);
        Assert.AreEqual(TransientCandidateTransportDisposition.Rejected, rejected.Disposition);
    }

    [TestMethod]
    public async Task StalledRequestIsCanceledAtConfiguredDeliveryTimeout()
    {
        var handler = new StalledHandler();
        var transport = CreateTransport(new StubHttpClientFactory(handler), timeoutSeconds: 1);

        var result = await transport.SendAsync(
            TransientDeliveryTestData.Submission(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(TransientCandidateTransportDisposition.Retry, result.Disposition);
        Assert.AreEqual("request-timeout", result.Reason);
        Assert.IsTrue(handler.CancellationObserved);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.BadRequest, TransientCandidateTransportDisposition.AuthenticationBlocked)]
    [DataRow(HttpStatusCode.Unauthorized, TransientCandidateTransportDisposition.AuthenticationBlocked)]
    [DataRow(HttpStatusCode.Forbidden, TransientCandidateTransportDisposition.AuthenticationBlocked)]
    [DataRow(HttpStatusCode.InternalServerError, TransientCandidateTransportDisposition.Retry)]
    public async Task HttpRequestExceptionStatusClassifiesBearerHandlerFailures(
        HttpStatusCode statusCode,
        TransientCandidateTransportDisposition expected)
    {
        var transport = CreateTransport(new StubHttpClientFactory(new CapturingHandler(_ =>
            throw new HttpRequestException("handler failure", inner: null, statusCode))));

        var result = await transport.SendAsync(
            TransientDeliveryTestData.Submission(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Disposition);
        Assert.AreEqual(
            expected == TransientCandidateTransportDisposition.AuthenticationBlocked,
            result.RetryAfter is not null);
    }

    private static CameraAgentTransientCandidateTransport CreateTransport(
        IHttpClientFactory factory,
        DeviceSecrets? secrets = null,
        int timeoutSeconds = 30)
        => new(
            new StubIdentityStore(),
            new StubSecretStore(secrets ?? CreateSecrets()),
            factory,
            Options.Create(new CameraAgentHostOptions
            {
                TransientDetection = new TransientDetectionOptions
                {
                    DeliveryRequestTimeoutSeconds = timeoutSeconds
                }
            }),
            TimeProvider.System);

    private static DeviceSecrets CreateSecrets() => new(
        Guid.NewGuid(), Guid.NewGuid(), "device", "registration", "/heartbeat", "/upload", 30,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(1), "device-key", new CentralIdentityOptions());

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        TransientCandidateSubmissionAcknowledgementV1 acknowledgement)
        => new(status) { Content = new ByteArrayContent(TransientCandidateDeliveryJson.Serialize(acknowledgement)) };

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
        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://central.test") };
        }
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHeaders.Clear();
            foreach (var header in request.Headers)
            {
                LastHeaders[header.Key] = string.Join(",", header.Value);
            }
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
}
