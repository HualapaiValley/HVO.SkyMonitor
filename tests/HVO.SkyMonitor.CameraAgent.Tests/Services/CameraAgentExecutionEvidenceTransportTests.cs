using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Evidence;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>
/// The authenticated evidence transport: its own routes and media type, device-credential headers, strict contract
/// parsing, bounded response reads, and HTTP failure classification.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentExecutionEvidenceTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NegotiationAndSubmissionUseTheirOwnRoutesCredentialsAndFraming()
    {
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        var responses = new Queue<HttpResponseMessage>(
        [
            Response(GraphExecutionEvidenceJson.Serialize(new ExecutionEvidenceNegotiationResponseV1(
                ExecutionEvidenceNegotiationResponseV1.CurrentSchemaVersion,
                ExecutionEvidenceNegotiationDisposition.Supported,
                GraphExecutionEvidenceSchemaVersions.Supported,
                ExecutionEvidenceLimitsV1.Current,
                Now,
                GraphExecutionEvidenceSchemaVersions.Current))),
            Response(GraphExecutionEvidenceJson.Serialize(CreateFeedback(origin)))
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        var factory = new StubHttpClientFactory(handler);
        var transport = CreateTransport(factory);

        Assert.IsTrue(await transport.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false));
        var negotiation = await transport.NegotiateAsync(
            new(
                ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion,
                origin,
                GraphExecutionEvidenceSchemaVersions.Supported),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Completed, negotiation.Disposition);
        Assert.AreEqual(
            GraphExecutionEvidenceSchemaVersions.Current, negotiation.Response!.SelectedSchemaVersion);
        Assert.AreEqual("/api/device/execution-evidence/negotiate", handler.LastPath);
        Assert.AreEqual("application/json", handler.LastContentType);

        var payloads = new[]
        {
            ExecutionEvidenceTestFactory.SealExecution(origin, 1, 1).Payload.ToArray(),
            ExecutionEvidenceTestFactory.SealExecution(origin, 2, 2).Payload.ToArray()
        };
        var submit = await transport.SubmitAsync(origin.IdentitySha256, payloads, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Completed, submit.Disposition);
        Assert.AreEqual(origin.IdentitySha256, submit.Feedback!.OriginIdentitySha256);
        Assert.AreEqual("/api/device/execution-evidence", handler.LastPath);
        Assert.AreEqual(ExecutionEvidenceBatchCodec.MediaType, handler.LastContentType);
        Assert.AreEqual("device-1", handler.LastHeaders["X-HVO-Device-Id"]);
        Assert.AreEqual("device-key", handler.LastHeaders["X-HVO-Device-Key"]);
        Assert.AreEqual("1", handler.LastHeaders["X-Skip-CentralAuth"]);
        Assert.AreEqual(origin.IdentitySha256, handler.LastHeaders["X-HVO-Evidence-Origin"]);
        Assert.AreEqual(SkyMonitorClientOptions.HttpClientName, factory.LastName);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, factory.Client!.Timeout);

        // The framing must be exactly what the receiver decodes; every envelope byte survives.
        var decoded = ExecutionEvidenceBatchCodec.Decode(
            handler.LastBody!, GraphExecutionEvidenceLimits.MaximumResyncUnits);
        Assert.AreEqual(2, decoded.Count);
        CollectionAssert.AreEqual(payloads[0], decoded[0].ToArray());
        CollectionAssert.AreEqual(payloads[1], decoded[1].ToArray());
    }

    [TestMethod]
    public async Task MissingCredentialsBlockTheLaneWithoutASingleRequest()
    {
        var handler = new CapturingHandler(_ => new(HttpStatusCode.OK));
        var transport = CreateTransport(new StubHttpClientFactory(handler), credentialsAvailable: false);

        Assert.IsFalse(await transport.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false));
        var negotiation = await transport.NegotiateAsync(
            new(
                ExecutionEvidenceNegotiationRequestV1.CurrentSchemaVersion,
                ExecutionEvidenceTestFactory.CreateOrigin(),
                GraphExecutionEvidenceSchemaVersions.Supported),
            CancellationToken.None).ConfigureAwait(false);
        var submit = await transport.SubmitAsync(
            ExecutionEvidenceTestFactory.CreateOrigin().IdentitySha256, [], CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(ExecutionEvidenceTransportDisposition.AuthenticationBlocked, negotiation.Disposition);
        Assert.AreEqual(
            ExecutionEvidenceExportReasonCodes.TransportUnconfigured, negotiation.ReasonCode);
        Assert.AreEqual(TimeSpan.FromMinutes(5), negotiation.RetryAfter);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.AuthenticationBlocked, submit.Disposition);
        Assert.IsNull(handler.LastPath);
    }

    [TestMethod]
    public async Task ADisabledLaneReportsNoAvailableTransportEvenWithCredentials()
    {
        var transport = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => new(HttpStatusCode.OK))),
            exportEnabled: false);
        Assert.IsFalse(await transport.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false));

        var central = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => new(HttpStatusCode.OK))),
            centralEnabled: false);
        Assert.IsFalse(await central.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task HttpFailuresAreClassifiedAndRetryAfterIsHonoured()
    {
        var retryDelta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryDelta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(19));
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.Unauthorized),
            new(HttpStatusCode.BadRequest),
            new(HttpStatusCode.RequestEntityTooLarge),
            new(HttpStatusCode.UnsupportedMediaType),
            retryDelta,
            new(HttpStatusCode.InternalServerError)
        ]);
        var transport = CreateTransport(
            new StubHttpClientFactory(new CapturingHandler(_ => responses.Dequeue())));
        var origin = ExecutionEvidenceTestFactory.CreateOrigin().IdentitySha256;

        var unauthorized = await transport.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        var badRequest = await transport.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        var tooLarge = await transport.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        var mediaType = await transport.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        var throttled = await transport.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        var serverError = await transport.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ExecutionEvidenceTransportDisposition.AuthenticationBlocked, unauthorized.Disposition);
        Assert.AreEqual(TimeSpan.FromMinutes(5), unauthorized.RetryAfter);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Rejected, badRequest.Disposition);
        Assert.AreEqual("http-400", badRequest.ReasonCode);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Rejected, tooLarge.Disposition);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Rejected, mediaType.Disposition);
        Assert.AreEqual(TimeSpan.FromSeconds(19), throttled.RetryAfter);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Retry, serverError.Disposition);
    }

    [TestMethod]
    public async Task InvalidOversizedTimedOutAndUnavailableResponsesStayRetryable()
    {
        var origin = ExecutionEvidenceTestFactory.CreateOrigin().IdentitySha256;
        var invalid = CreateTransport(new StubHttpClientFactory(new CapturingHandler(
            _ => Response("null"u8.ToArray()))));
        var invalidResult = await invalid.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Retry, invalidResult.Disposition);

        var oversized = CreateTransport(new StubHttpClientFactory(new CapturingHandler(
            _ => Response(new byte[GraphExecutionEvidenceLimits.MaximumFeedbackBytes + 1]))));
        var oversizedResult = await oversized.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Retry, oversizedResult.Disposition);
        Assert.AreEqual(GraphExecutionEvidenceReasonCodes.PayloadTooLarge, oversizedResult.ReasonCode);

        var stalled = new StalledHandler();
        var timeout = CreateTransport(new StubHttpClientFactory(stalled), timeoutSeconds: 1);
        var timedOut = await timeout.SubmitAsync(origin, [], CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ExecutionEvidenceTransportDisposition.Retry, timedOut.Disposition);
        Assert.AreEqual("request-timeout", timedOut.ReasonCode);
        Assert.IsTrue(stalled.CancellationObserved);

        var unavailable = CreateTransport(new StubHttpClientFactory(new CapturingHandler(
            _ => throw new HttpRequestException("unavailable"))));
        var unavailableResult = await unavailable.SubmitAsync(origin, [], CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(
            ExecutionEvidenceExportReasonCodes.TransportUnavailable, unavailableResult.ReasonCode);
    }

    private static CameraAgentExecutionEvidenceTransport CreateTransport(
        IHttpClientFactory factory,
        bool credentialsAvailable = true,
        bool exportEnabled = true,
        bool centralEnabled = true,
        int timeoutSeconds = 30)
        => new(
            new StubIdentityStore(),
            new StubSecretStore(credentialsAvailable ? CreateSecrets() : null),
            factory,
            Options.Create(new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions
                {
                    Mode = centralEnabled ? CentralIntegrationMode.Enabled : CentralIntegrationMode.Disabled
                },
                ExecutionEvidenceExport = new ExecutionEvidenceExportOptions
                {
                    Enabled = exportEnabled,
                    RequestTimeoutSeconds = timeoutSeconds
                }
            }),
            new TestTimeProvider(Now));

    private static ExecutionEvidenceFeedbackV1 CreateFeedback(ExecutionEvidenceOriginV1 origin)
        => new(
            ExecutionEvidenceFeedbackV1.CurrentSchemaVersion,
            origin.IdentitySha256,
            Now,
            0,
            [],
            ImmutableArray<ExecutionEvidenceFactV1>.Empty,
            new(ExecutionEvidenceRetentionV1.CurrentSchemaVersion, 0, Now.AddDays(1), 100));

    private static HttpResponseMessage Response(byte[] payload)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

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

    private sealed class StubIdentityStore : IDeviceIdentityStore
    {
        public Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceIdentity("device-1", "VERIFY", DateTimeOffset.UnixEpoch));
    }

    private sealed class StubSecretStore(DeviceSecrets? secrets) : IDeviceSecretStore
    {
        public Task<DeviceSecrets?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(secrets);

        public Task SaveAsync(DeviceSecrets value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

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

        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
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
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return response(request);
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
