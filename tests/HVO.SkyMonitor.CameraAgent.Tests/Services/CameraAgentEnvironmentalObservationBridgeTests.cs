using System.Collections.Concurrent;
using System.Net;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentEnvironmentalObservationBridgeTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MatchingAcknowledgementIsAcceptedAndMalformedOrMismatchedAcknowledgementRetries()
    {
        var observation = CreateObservation();
        var matching = Acknowledgement(observation);
        var responses = new Queue<HttpResponseMessage>(
        [
            Response(HttpStatusCode.Accepted, EnvironmentalObservationDeliveryJson.Serialize(matching)),
            Response(HttpStatusCode.OK, "not-json"u8.ToArray()),
            Response(HttpStatusCode.OK, EnvironmentalObservationDeliveryJson.Serialize(
                matching with { ObservationId = Guid.NewGuid() })),
            Response(HttpStatusCode.OK, EnvironmentalObservationDeliveryJson.Serialize(matching))
        ]);
        var handler = new StubHandler(_ => responses.Dequeue());
        var bridge = CreateBridge(handler);

        var accepted = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var malformed = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var mismatched = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var statusMismatch = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Acknowledged, accepted.Disposition);
        Assert.AreEqual(matching, accepted.Acknowledgement);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Retry, malformed.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Retry, mismatched.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Retry, statusMismatch.Disposition);
        Assert.AreEqual("/api/device/environmental-observations", handler.LastRequestUri?.AbsolutePath);
        Assert.IsTrue(handler.SawSkipAuthenticationHeader);
    }

    [TestMethod]
    public async Task HttpResponsesHaveBoundedTerminalQuarantineAuthenticationAndRetryClassification()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.BadRequest),
            new(HttpStatusCode.RequestEntityTooLarge),
            new(HttpStatusCode.Conflict),
            new(HttpStatusCode.Unauthorized),
            new(HttpStatusCode.NotFound),
            new(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) }
            },
            new((HttpStatusCode)425),
            new(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Epoch.AddSeconds(30)) }
            }
        ]);
        var bridge = CreateBridge(new StubHandler(_ => responses.Dequeue()));
        var observation = CreateObservation();

        var badRequest = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var oversized = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var conflict = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var unauthorized = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var notFound = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var rateLimited = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var tooEarly = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);
        var unavailable = await bridge.SendAsync(observation, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Quarantine, badRequest.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Quarantine, oversized.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Terminal, conflict.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.AuthenticationBlocked, unauthorized.Disposition);
        Assert.IsTrue(unauthorized.RetryAfter <= TimeSpan.FromMinutes(5));
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Terminal, notFound.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Retry, rateLimited.Disposition);
        Assert.IsNull(rateLimited.RetryAfter);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Retry, tooEarly.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Retry, unavailable.Disposition);
        Assert.AreEqual(TimeSpan.FromSeconds(30), unavailable.RetryAfter);
    }

    [TestMethod]
    public async Task FrozenHistoricalTargetsAreSentForCentralValidation()
    {
        var observation = CreateObservation();
        var historical = observation with
        {
            Target = observation.Target with { SiteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") }
        };
        var reprovisioned = historical with { Target = historical.Target with { AgentId = Guid.NewGuid() } };
        var acknowledgements = new Queue<EnvironmentalObservationAcknowledgement>(
            [Acknowledgement(historical), Acknowledgement(reprovisioned)]);
        var handler = new StubHandler(_ => Response(
            HttpStatusCode.Accepted,
            EnvironmentalObservationDeliveryJson.Serialize(acknowledgements.Dequeue())));
        var bridge = CreateBridge(handler);

        var historicalResult = await bridge.SendAsync(historical, CancellationToken.None).ConfigureAwait(false);
        var reprovisionedResult = await bridge.SendAsync(reprovisioned, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Acknowledged, historicalResult.Disposition);
        Assert.AreEqual(EnvironmentalObservationTransportDisposition.Acknowledged, reprovisionedResult.Disposition);
        Assert.AreEqual(2, handler.SendCount);
    }

    [TestMethod]
    public void DeliveryRetryDelayIsExponentialAndBounded()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), EnvironmentalObservationDeliveryService.CalculateRetryDelay(
            1, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)));
        Assert.AreEqual(TimeSpan.FromSeconds(20), EnvironmentalObservationDeliveryService.CalculateRetryDelay(
            3, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)));
        Assert.AreEqual(TimeSpan.FromMinutes(1), EnvironmentalObservationDeliveryService.CalculateRetryDelay(
            30, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1)));
    }

    [TestMethod]
    public async Task DeliveryHealthExposesOnlyBoundedQueueState()
    {
        var state = new EnvironmentalObservationDeliveryState();
        state.Update(
            new EnvironmentalObservationOutboxSnapshot(
                3, 300, 1, 100, 0, 1, 1, 1, 2, Epoch.AddMinutes(-1), Epoch),
            EnvironmentalObservationDeliveryAvailability.Unhealthy,
            "terminal");
        var check = new EnvironmentalObservationDeliveryHealthCheck(
            state,
            Options.Create(new CameraAgentHostOptions()),
            new FixedTimeProvider(Epoch));

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        string[] expectedKeys =
        [
            "Availability", "PendingCount", "PendingBytes", "LeasedCount", "RetryCount",
            "QuarantineCount", "TerminalCount", "OverflowCount", "OldestAgeSeconds"
        ];
        CollectionAssert.AreEquivalent(
            expectedKeys,
            result.Data.Keys.ToArray());
    }

    [TestMethod]
    public void DeliveryTelemetryUsesFixedLabelsAndExposesQueueGauges()
    {
        const string sensitive = "secret-device-site-value-url";
        var instruments = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var tagValues = new ConcurrentBag<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EnvironmentalObservationDeliveryTelemetry.MeterName)
                {
                    instruments.TryAdd(instrument.Name, 0);
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => CaptureTags(tags, tagValues));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => CaptureTags(tags, tagValues));
        listener.Start();
        var state = new EnvironmentalObservationDeliveryState();
        state.Update(
            new EnvironmentalObservationOutboxSnapshot(2, 200, 2, 200, 0, 1, 0, 0, 0, Epoch, Epoch),
            EnvironmentalObservationDeliveryAvailability.Degraded,
            "retry");
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, new FixedTimeProvider(Epoch));

        telemetry.RecordSend(
            new EnvironmentalObservationTransportResult(
                EnvironmentalObservationTransportDisposition.Retry,
                sensitive),
            TimeSpan.FromMilliseconds(5));
        listener.RecordObservableInstruments();

        string[] expectedGauges =
        [
            "skymonitor.environment.edge.pending",
            "skymonitor.environment.edge.pending.bytes",
            "skymonitor.environment.edge.oldest.age"
        ];
        foreach (var gauge in expectedGauges)
        {
            Assert.IsTrue(instruments.ContainsKey(gauge), $"Missing telemetry instrument '{gauge}'.");
        }
        Assert.IsTrue(tagValues.Contains("other", StringComparer.Ordinal));
        Assert.IsFalse(tagValues.Any(value => value.Contains(sensitive, StringComparison.Ordinal)));
    }

    private static CameraAgentEnvironmentalObservationBridge CreateBridge(StubHandler handler)
    {
        var observation = CreateObservation();
        var secrets = new DeviceSecrets(
            observation.Target.AgentId!.Value,
            observation.Target.SiteId,
            "test",
            "registration-token",
            "/api/device/heartbeat",
            60,
            Epoch,
            Epoch.AddDays(1),
            "device-key",
            new CentralIdentityOptions());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://logic.test") };
        return new CameraAgentEnvironmentalObservationBridge(
            new StubIdentityStore(),
            new StubSecretStore(secrets),
            new StubHttpClientFactory(client),
            Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = "test",
                EnvironmentalDelivery = new EnvironmentalObservationDeliveryOptions()
            }),
            new FixedTimeProvider(Epoch));
    }

    private static EnvironmentalObservationV1 CreateObservation()
    {
        using var document = JsonDocument.Parse("{}");
        var parameters = document.RootElement.Clone();
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new EnvironmentalObservationTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
            new EnvironmentalObservationSource(
                "test-provider",
                "weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch,
            null,
            null,
            Epoch.AddMinutes(-1),
            Epoch.AddMinutes(5),
            Epoch.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good),
            []);
    }

    private static EnvironmentalObservationAcknowledgement Acknowledgement(EnvironmentalObservationV1 observation)
        => new(
            EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
            observation.ObservationId,
            EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation),
            EnvironmentalObservationJson.ComputeContentSha256(observation),
            Epoch.AddMinutes(1),
            EnvironmentalObservationDeliveryDisposition.Accepted);

    private static HttpResponseMessage Response(HttpStatusCode statusCode, byte[] content)
        => new(statusCode) { Content = new ByteArrayContent(content) };

    private static void CaptureTags(ReadOnlySpan<KeyValuePair<string, object?>> tags, ConcurrentBag<string> values)
    {
        foreach (var tag in tags)
        {
            if (tag.Value is not null)
            {
                values.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
            }
        }
    }

    private sealed class StubIdentityStore : IDeviceIdentityStore
    {
        public Task<DeviceIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceIdentity("device-1", "VERIFY", Epoch));
    }

    private sealed class StubSecretStore(DeviceSecrets? secrets) : IDeviceSecretStore
    {
        public Task<DeviceSecrets?> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(secrets);
        public Task SaveAsync(DeviceSecrets value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public bool SawSkipAuthenticationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            LastRequestUri = request.RequestUri;
            SawSkipAuthenticationHeader = request.Headers.Contains("X-Skip-CentralAuth");
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
