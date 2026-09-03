using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DeviceEnvironmentalObservationApiTests
{
    [TestMethod]
    public async Task PostCommitResponseLossAndCameraAgentRestartConvergeThroughDurableWorker()
    {
        var fixture = HVO.SkyMonitor.CameraAgent.IntegrationTests.AssemblyHooks.Fixture;
        var observation = CreateObservation(
            fixture.ObservatoryId,
            fixture.DevicePublicId,
            Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"environment-delivery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var deliveryOptions = new EnvironmentalObservationDeliveryOptions
        {
            BatchSize = 1,
            RetryInitialDelaySeconds = 1,
            RetryMaximumDelaySeconds = 5,
            MaximumAttempts = 3
        };
        using var client = fixture.CreateHostClient();
        var transport = new PostCommitLossTransport(
            client,
            fixture.DeviceId,
            "cameraagent-integration-key");
        try
        {
            using (var firstOutbox = new SqliteEnvironmentalObservationOutbox(time))
            using (var firstTelemetry = new EnvironmentalObservationDeliveryTelemetry(
                new EnvironmentalObservationDeliveryState(),
                time))
            {
                await firstOutbox.EnqueueAsync(root, observation, CancellationToken.None).ConfigureAwait(false);
                var firstService = CreateDeliveryService(
                    transport, firstOutbox, firstTelemetry, time, root, deliveryOptions);
                await firstService.DrainBatchAsync(root, deliveryOptions, CancellationToken.None).ConfigureAwait(false);
                (await firstOutbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).RetryCount.Should().Be(1);
            }

            time.Advance(TimeSpan.FromSeconds(1));
            using (var restartedOutbox = new SqliteEnvironmentalObservationOutbox(time))
            using (var restartedTelemetry = new EnvironmentalObservationDeliveryTelemetry(
                new EnvironmentalObservationDeliveryState(),
                time))
            {
                var restartedService = CreateDeliveryService(
                    transport, restartedOutbox, restartedTelemetry, time, root, deliveryOptions);
                await restartedService.DrainBatchAsync(root, deliveryOptions, CancellationToken.None).ConfigureAwait(false);
                (await restartedOutbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount.Should().Be(0);
            }

            transport.FirstAcknowledgement.Should().NotBeNull();
            transport.LastAcknowledgement.Should().NotBeNull();
            transport.FirstAcknowledgement!.Disposition.Should().Be(EnvironmentalObservationDeliveryDisposition.Accepted);
            transport.LastAcknowledgement!.Disposition.Should().Be(EnvironmentalObservationDeliveryDisposition.Duplicate);
            transport.LastAcknowledgement.ReceivedAtUtc.Should().Be(transport.FirstAcknowledgement.ReceivedAtUtc);
            (await fixture.CountEnvironmentalObservationsAsync(observation.ObservationId).ConfigureAwait(false))
                .Should().Be(1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static EnvironmentalObservationDeliveryService CreateDeliveryService(
        IEnvironmentalObservationTransport transport,
        IEnvironmentalObservationOutbox outbox,
        EnvironmentalObservationDeliveryTelemetry telemetry,
        TimeProvider timeProvider,
        string root,
        EnvironmentalObservationDeliveryOptions deliveryOptions)
        => new(
            transport,
            NullEnvironmentalObservationTargetResolver.Instance,
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            new EnvironmentalObservationDeliveryState(),
            telemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalDelivery = deliveryOptions
            }),
            timeProvider,
            NullLogger<EnvironmentalObservationDeliveryService>.Instance);

    private static EnvironmentalObservationV1 CreateObservation(Guid siteId, Guid agentId, Guid observationId)
    {
        using var document = JsonDocument.Parse("{}");
        var parameters = document.RootElement.Clone();
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            observationId,
            new EnvironmentalObservationTarget(siteId, agentId),
            new EnvironmentalObservationSource(
                "api-provider",
                "weather",
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            DateTimeOffset.UnixEpoch,
            null,
            null,
            DateTimeOffset.UnixEpoch.AddMinutes(-1),
            DateTimeOffset.UnixEpoch.AddMinutes(5),
            DateTimeOffset.UnixEpoch.AddMinutes(3),
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationUnit.Percent,
                45,
                null,
                EnvironmentalObservationQuality.Good),
            []);
    }

    private sealed class NullEnvironmentalObservationTargetResolver : IEnvironmentalObservationTargetResolver
    {
        public static NullEnvironmentalObservationTargetResolver Instance { get; } = new();

        public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<EnvironmentalObservationResolvedTarget?>(null);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class PostCommitLossTransport(HttpClient client, string deviceId, string deviceKey)
        : IEnvironmentalObservationTransport
    {
        private int _attempt;
        public EnvironmentalObservationAcknowledgement? FirstAcknowledgement { get; private set; }
        public EnvironmentalObservationAcknowledgement? LastAcknowledgement { get; private set; }

        public async ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
        {
            using var content = new ByteArrayContent(EnvironmentalObservationDeliveryJson.Serialize(
                new EnvironmentalObservationDeliveryEnvelope(
                    EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
                    deviceId,
                    deviceKey,
                    observation)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(
                new Uri("/api/device/environmental-observations", UriKind.Relative),
                content,
                cancellationToken).ConfigureAwait(false);
            response.IsSuccessStatusCode.Should().BeTrue();
            var parsed = EnvironmentalObservationDeliveryJson.ParseAcknowledgement(
                await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            parsed.Validation.IsValid.Should().BeTrue();
            var acknowledgement = parsed.Value!;
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                FirstAcknowledgement = acknowledgement;
                throw new HttpRequestException("Simulated response loss after central commit.");
            }
            LastAcknowledgement = acknowledgement;
            return new(
                EnvironmentalObservationTransportDisposition.Acknowledged,
                acknowledgement.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted
                    ? "accepted"
                    : "duplicate",
                acknowledgement);
        }
    }
}
