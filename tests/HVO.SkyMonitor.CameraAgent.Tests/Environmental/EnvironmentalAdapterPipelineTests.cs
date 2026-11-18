using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class EnvironmentalAdapterPipelineTests
{
    [TestMethod]
    [DataRow("LocalWeatherStation", SimulatedEnvironmentalDeviceMode.Normal, "normal")]
    [DataRow("LocalWeatherStation", SimulatedEnvironmentalDeviceMode.Stale, "normal")]
    [DataRow("LocalWeatherStation", SimulatedEnvironmentalDeviceMode.Disconnected, "normal")]
    [DataRow("InfraredSky", SimulatedEnvironmentalDeviceMode.Normal, "normal")]
    [DataRow("InfraredSky", SimulatedEnvironmentalDeviceMode.Faulted, "normal")]
    [DataRow("SkyQualityMeter", SimulatedEnvironmentalDeviceMode.Normal, "normal")]
    [DataRow("SkyQualityMeter", SimulatedEnvironmentalDeviceMode.Normal, "saturated")]
    [DataRow("SkyQualityMeter", SimulatedEnvironmentalDeviceMode.Normal, "slow")]
    [DataRow("SkyQualityMeter", SimulatedEnvironmentalDeviceMode.Timeout, "normal")]
    [DataRow("SkyQualityMeter", SimulatedEnvironmentalDeviceMode.Disconnected, "normal")]
    public async Task ConfiguredAdaptersPersistAttemptsHistoryAndCaptureAssociations(string type, SimulatedEnvironmentalDeviceMode mode, string scenario)
    {
        var source = EnvironmentalHardwareAdapterTests.Source(type, mode);
        var payload = type switch
        {
            "LocalWeatherStation" => EnvironmentalHardwareAdapterTests.Station,
            "InfraredSky" => EnvironmentalHardwareAdapterTests.Infrared,
            _ => EnvironmentalHardwareAdapterTests.Sqm
        };
        if (scenario == "saturated") payload = payload.Replace("21.10m", "0.00m", StringComparison.Ordinal);
        if (scenario == "slow") payload = payload.Replace("0000001.000s", "0000120.000s", StringComparison.Ordinal);
        var produces = mode == SimulatedEnvironmentalDeviceMode.Normal && scenario != "saturated";
        var root = Path.Combine(Path.GetTempPath(), "hvo-adapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new EnvironmentalSourceConfiguration
            {
                Id = source.Descriptor.Id,
                Type = type,
                Kind = source.Descriptor.Kind,
                Required = false,
                Triggers = [EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionTrigger.Periodic],
                PeriodSeconds = 60,
                StaleAfterSeconds = 300,
                ValidForSeconds = 900,
                Options = JsonSerializer.SerializeToElement(EnvironmentalHardwareAdapterTests.Options(payload, mode), SerializerOptions())
            };
            var hostOptions = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                { Enabled = true, SourceTimeoutMilliseconds = 100, Sources = [configuration] }
            });
            using var store = new SqliteEnvironmentalObservationOutbox();
            var services = new ServiceCollection();
            services.AddSingleton<IEnvironmentalObservationPublisher>(new Publisher(store, root));
            using var provider = services.BuildServiceProvider();
            var factory = new EnvironmentalSourceFactory(provider, Registrations());
            var captureId = Guid.NewGuid();
            using (var coordinator = new EnvironmentalAcquisitionCoordinator(factory, provider.GetRequiredService<IEnvironmentalObservationPublisher>(),
                new LocationStore(), hostOptions, TimeProvider.System, store))
            {
                var result = await coordinator.AcquireSourceAsync(configuration.Id, EnvironmentalAcquisitionTrigger.BeforeCapture,
                    EnvironmentalHardwareAdapterTests.Epoch, 1, captureId).ConfigureAwait(false);
                Assert.AreEqual(produces ? EnvironmentalAcquisitionDisposition.Produced :
                    mode == SimulatedEnvironmentalDeviceMode.Timeout ? EnvironmentalAcquisitionDisposition.TimedOut : EnvironmentalAcquisitionDisposition.Missing,
                    result.Disposition);
            }
            // Restart against the same durable store: no interrupted read can manufacture a fact.
            using var restarted = new EnvironmentalAcquisitionCoordinator(factory, provider.GetRequiredService<IEnvironmentalObservationPublisher>(),
                new LocationStore(), hostOptions, TimeProvider.System, store);
            var attempts = await store.ReadAttemptsAsync(root, 100, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, attempts);
            Assert.AreEqual(captureId, attempts[0].CaptureId);
            var history = await store.ReadLocalPageAsync(root, null, 100, null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(produces ? 1 : 0, history.Items.Count);
            var association = new EnvironmentalAssociationService(store, store, hostOptions, TimeProvider.System);
            var resultSet = await association.AssociateAsync(captureId, 1, EnvironmentalHardwareAdapterTests.Epoch,
                EnvironmentalHardwareAdapterTests.Epoch.AddSeconds(1), null, [configuration.Kind], CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(produces ? LocalEnvironmentalAssociationStatus.Fresh : LocalEnvironmentalAssociationStatus.Missing, resultSet[0].Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ImportedAndLocalFactsCoexistInDurableHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-weather-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            using var handler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(200,
                """{"properties":{"timestamp":"2026-11-08T06:00:00Z","temperature":{"value":20,"unitCode":"wmoUnit:degC"}}}"""));
            using var client = new WeatherProviderClient(new HttpClient(handler), new OnlineWeatherSourceTests.Clock());
            var imported = await OnlineWeatherSourceTests.Create(client).AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
            var local = await EnvironmentalHardwareAdapterTests.Source("LocalWeatherStation").AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, imported.Fact!, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, local.Fact!, CancellationToken.None).ConfigureAwait(false);
            var history = await store.ReadLocalPageAsync(root, EnvironmentalObservationKind.AirTemperature, 100, null, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, history.Items);
            Assert.IsTrue(history.Items.Any(r => r.Fact.Source.Kind == EnvironmentalObservationSourceKind.Imported));
            Assert.IsTrue(history.Items.Any(r => r.Fact.Source.Kind == EnvironmentalObservationSourceKind.Simulated));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task InfraredLineageUsesDeliveredSourceIdentitiesAfterTargetEnrichment()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-lineage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            var inputs = new EnvironmentalHardwareAdapterTests.RecordingPublisher();
            var source = new InfraredSkySource(EnvironmentalHardwareAdapterTests.Descriptor("InfraredSky", EnvironmentalObservationKind.CloudCover),
                EnvironmentalHardwareAdapterTests.Options(EnvironmentalHardwareAdapterTests.Infrared), publisher: inputs);
            var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            var delivered = new List<EnvironmentalObservationV1>();
            foreach (var input in inputs.Facts)
                delivered.Add((await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false)).DeliveryObservation!);
            var derived = await store.CommitLocalAsync(root, result.Fact!, target, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(derived.DeliveryObservation);
            foreach (var reference in derived.DeliveryObservation.Lineage)
                Assert.IsTrue(delivered.Any(input => input.ObservationId == reference.ObservationId &&
                    EnvironmentalObservationJson.ComputeSourceIdentitySha256(input) == reference.SourceIdentitySha256));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("LocalWeatherStation")]
    [DataRow("InfraredSky")]
    [DataRow("SkyQualityMeter")]
    public async Task RestartAfterInterruptedPollCanAcquireFreshSample(string type)
    {
        var source = EnvironmentalHardwareAdapterTests.Source(type);
        var payload = type == "LocalWeatherStation" ? EnvironmentalHardwareAdapterTests.Station :
            type == "InfraredSky" ? EnvironmentalHardwareAdapterTests.Infrared : EnvironmentalHardwareAdapterTests.Sqm;
        var root = Path.Combine(Path.GetTempPath(), "hvo-reconnect-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            using var provider = new ServiceCollection().BuildServiceProvider();
            var factory = new EnvironmentalSourceFactory(provider, Registrations());
            var publisher = new Publisher(store, root);
            IOptions<CameraAgentHostOptions> Configure(SimulatedEnvironmentalDeviceMode mode) => Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    Sources = [new EnvironmentalSourceConfiguration
                    {
                        Id = source.Descriptor.Id, Type = type, Kind = source.Descriptor.Kind, Required = false,
                        Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
                        Options = JsonSerializer.SerializeToElement(EnvironmentalHardwareAdapterTests.Options(payload, mode), SerializerOptions())
                    }]
                }
            });
            using (var interrupted = new EnvironmentalAcquisitionCoordinator(factory, publisher, new LocationStore(),
                Configure(SimulatedEnvironmentalDeviceMode.Timeout), TimeProvider.System, store))
            {
                using var cancellation = new CancellationTokenSource(20);
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await interrupted.AcquireSourceAsync(source.Descriptor.Id, EnvironmentalAcquisitionTrigger.Periodic,
                        EnvironmentalHardwareAdapterTests.Epoch, cancellationToken: cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            }
            Assert.AreEqual(0, (await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
            using var restarted = new EnvironmentalAcquisitionCoordinator(factory, publisher, new LocationStore(),
                Configure(SimulatedEnvironmentalDeviceMode.Normal), TimeProvider.System, store);
            var recovered = await restarted.AcquireSourceAsync(source.Descriptor.Id, EnvironmentalAcquisitionTrigger.Periodic,
                EnvironmentalHardwareAdapterTests.Epoch.AddSeconds(1)).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, recovered.Disposition);
            Assert.AreEqual(1, (await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProviderRetryAfterSurvivesClientRestartWithoutNetworkRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-weather-backoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            var clock = new OnlineWeatherSourceTests.Clock();
            using (var limitedHandler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(429)))
            using (var limited = new WeatherProviderClient(new HttpClient(limitedHandler), clock, store, root))
            {
                var source = OnlineWeatherSourceTests.Create(limited);
                var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("provider-rate-limited", result.Reason);
            }
            clock.Now = clock.Now.AddSeconds(300);
            using var handler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(500));
            using var restarted = new WeatherProviderClient(new HttpClient(handler), clock, store, root);
            var retry = await OnlineWeatherSourceTests.Create(restarted).AcquireAsync(
                EnvironmentalHardwareAdapterTests.Context() with { ObservedAtUtc = clock.Now }, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(retry.Fact);
            Assert.AreEqual(0, handler.Calls);
            var states = await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalHardwareAdapterTests.Epoch.AddSeconds(600), states.Single().NextPollUtc);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static EnvironmentalSourceRegistration[] Registrations() =>
    [
        new("LocalWeatherStation", typeof(LocalWeatherStationSource), typeof(EnvironmentalDeviceOptions)),
        new("InfraredSky", typeof(InfraredSkySource), typeof(EnvironmentalDeviceOptions)),
        new("SkyQualityMeter", typeof(SkyQualityMeterSource), typeof(EnvironmentalDeviceOptions))
    ];
    private static JsonSerializerOptions SerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }
    private sealed class Publisher(SqliteEnvironmentalObservationOutbox store, string root) : IEnvironmentalObservationPublisher
    {
        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        {
            await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
            return new(EnvironmentalObservationPublishDisposition.Enqueued, null);
        }
    }
    private sealed class LocationStore : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => EnvironmentalHardwareAdapterTests.Context().DeploymentLocation;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken) => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }
}
