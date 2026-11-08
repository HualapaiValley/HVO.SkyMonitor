using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class EnvironmentalAdapterReviewIntegrationTests
{
    [TestMethod]
    public async Task PriorityChangePreservesTheCapturedPolicyAndOnlyChangesNewCaptures()
    {
        var root = Root();
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            var local = EnvironmentalAdapterReviewUnitTests.Fact("local", EnvironmentalObservationKind.AirTemperature, 20);
            var provider = EnvironmentalAdapterReviewUnitTests.Fact("provider", local.Value.Kind, 21, EnvironmentalObservationSourceKind.Imported);
            await store.CommitLocalAsync(root, local, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, provider, CancellationToken.None).ConfigureAwait(false);
            var sources = new[] { EnvironmentalAdapterReviewUnitTests.Configuration("local", local.Value.Kind),
                EnvironmentalAdapterReviewUnitTests.Configuration("provider", local.Value.Kind, "OnlineWeather") };
            IOptions<CameraAgentHostOptions> Configure(string[] priority) => Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = root, EnvironmentalAcquisition = new() { Enabled = true, Sources = sources, WeatherSourcePriority = priority } });
            var original = new EnvironmentalAssociationService(store, store, Configure(["local", "provider"]), TimeProvider.System, acquisitionStates: store);
            var capture = Guid.NewGuid();
            var first = (await original.AssociateAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null, [local.Value.Kind], CancellationToken.None).ConfigureAwait(false))[0];
            var changed = new EnvironmentalAssociationService(store, store, Configure(["provider", "local"]), TimeProvider.System, acquisitionStates: store);
            var recovered = await changed.ReadCompletedAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null, [local.Value.Kind], CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(first.AssociationIdentitySha256, recovered[0].AssociationIdentitySha256);
            var retry = (await changed.AssociateAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null, [local.Value.Kind], CancellationToken.None).ConfigureAwait(false))[0];
            Assert.AreEqual(first.AssociationIdentitySha256, retry.AssociationIdentitySha256);
            Assert.HasCount(1, await store.ReadAssociationsAsync(root, capture, CancellationToken.None).ConfigureAwait(false));
            var next = (await changed.AssociateAsync(Guid.NewGuid(), 2, Epoch, Epoch.AddSeconds(1), null, [local.Value.Kind], CancellationToken.None).ConfigureAwait(false))[0];
            Assert.AreEqual("provider", (await store.ReadLocalDetailAsync(root, next.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false))!.Fact.Source.SourceId);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task PrefixCollisionDoesNotSuppressAnIndependentCaptureReading()
    {
        var root = Root();
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            var fact = EnvironmentalAdapterReviewUnitTests.Fact("station-temperature", EnvironmentalObservationKind.AirTemperature, 20);
            await store.CommitLocalAsync(root, fact, CancellationToken.None).ConfigureAwait(false);
            var humidity = EnvironmentalAdapterReviewUnitTests.Configuration("station", EnvironmentalObservationKind.RelativeHumidity);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new()
                { Enabled = true, Sources = [humidity, EnvironmentalAdapterReviewUnitTests.Configuration("station-temperature", fact.Value.Kind)] }
            });
            await store.RecordAttemptAsync(root, humidity.ToDescriptor(), new("station", EnvironmentalAcquisitionTrigger.Periodic,
                EnvironmentalAcquisitionDisposition.Failed, "device-disconnected", null, Epoch, Epoch), null, null, CancellationToken.None).ConfigureAwait(false);
            var service = new EnvironmentalAssociationService(store, store, options, TimeProvider.System, acquisitionStates: store);
            var result = await service.AssociateAsync(Guid.NewGuid(), 1, Epoch, Epoch.AddSeconds(1), null, [fact.Value.Kind], CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, result[0].Status);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealBeforeCaptureBridgeFencesTimeoutAndPreservesHostCancellation(bool hostCanceled)
    {
        var root = Root();
        try
        {
            var clock = new ExposureTestTimeProvider(Epoch);
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var configuration = new EnvironmentalSourceConfiguration
            {
                Id = "sqm",
                Type = "SkyQualityMeter",
                Kind = EnvironmentalObservationKind.SkyQuality,
                Required = false,
                Triggers = [EnvironmentalAcquisitionTrigger.BeforeCapture],
                Options = JsonSerializer.SerializeToElement(new EnvironmentalDeviceOptions("sqm"), JsonOptions)
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new()
                { Enabled = true, BeforeCaptureWaitMilliseconds = 30, SourceTimeoutMilliseconds = 5000, Sources = [configuration] }
            });
            using var provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(new SlowAfterFirstDevice()).BuildServiceProvider();
            var factory = new EnvironmentalSourceFactory(provider, EnvironmentalAdapterPipelineTests.Registrations());
            using var coordinator = new EnvironmentalAcquisitionCoordinator(factory, new Publisher(store, root), new LocationStore(), options, clock, store);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch).ConfigureAwait(false)).Disposition);
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(coordinator, store, store, telemetry, clock, options, NullLogger<EnvironmentalAcquisitionService>.Instance);
            var bridge = new EnvironmentalCaptureTriggerBridge(coordinator, service, store, options);
            using var cancellation = new CancellationTokenSource();
            if (hostCanceled) await cancellation.CancelAsync().ConfigureAwait(false);
            if (hostCanceled)
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await bridge.BeforeCaptureAsync(Epoch.AddSeconds(1), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            else await bridge.BeforeCaptureAsync(Epoch.AddSeconds(1), cancellation.Token).ConfigureAwait(false);
            var state = (await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false)).Single();
            Assert.AreEqual(hostCanceled ? EnvironmentalAcquisitionDisposition.Produced : EnvironmentalAcquisitionDisposition.TimedOut, state.LastDisposition);
            if (!hostCanceled)
            {
                var association = new EnvironmentalAssociationService(store, store, options, clock, acquisitionStates: store);
                var result = await association.AssociateAsync(Guid.NewGuid(), 1, Epoch.AddSeconds(1), Epoch.AddSeconds(2), null,
                    [EnvironmentalObservationKind.SkyQuality], CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, result[0].Status);
            }
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task PeriodicProviderRestartKeepsIndependentReadingCadences()
    {
        var root = Root();
        try
        {
            var clock = new ExposureTestTimeProvider(Epoch);
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var configurations = new[] { OnlineConfiguration("temperature", EnvironmentalObservationKind.AirTemperature, 300),
                OnlineConfiguration("humidity", EnvironmentalObservationKind.RelativeHumidity, 3600) };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new()
                { Enabled = true, MaximumConcurrency = 1, Sources = configurations, WeatherSourcePriority = ["temperature", "humidity"] }
            });
            using var handler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(200, EnvironmentalAdapterReviewUnitTests.Payload));
            using (var client = new WeatherProviderClient(new HttpClient(handler), clock, store, root))
            using (var provider = new ServiceCollection().AddSingleton(client).BuildServiceProvider())
            using (var coordinator = new EnvironmentalAcquisitionCoordinator(Factory(provider), new Publisher(store, root), new LocationStore(), options, clock, store))
            using (var telemetry = new EnvironmentalAcquisitionTelemetry())
            using (var service = new EnvironmentalAcquisitionService(coordinator, store, store, telemetry, clock, options, NullLogger<EnvironmentalAcquisitionService>.Instance))
            {
                await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await WaitFor(async () => (await store.ReadAttemptsAsync(root, 100, CancellationToken.None).ConfigureAwait(false)).Count >= 1).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(300));
                await WaitFor(async () =>
                {
                    var states = await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false);
                    return states.Any(s => s.SourceId == "temperature" && s.NextPollUtc == Epoch.AddSeconds(600)) &&
                        states.Any(s => s.SourceId == "humidity" && s.NextPollUtc == Epoch.AddSeconds(3600));
                }).ConfigureAwait(false);
                await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            clock.Advance(TimeSpan.FromSeconds(60));
            var priorCount = (await store.ReadAttemptsAsync(root, 100, CancellationToken.None).ConfigureAwait(false)).Count;
            using var restartedHandler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(200, EnvironmentalAdapterReviewUnitTests.Payload));
            using var restartedClient = new WeatherProviderClient(new HttpClient(restartedHandler), clock, store, root);
            using var restartedProvider = new ServiceCollection().AddSingleton(restartedClient).BuildServiceProvider();
            using var restartedCoordinator = new EnvironmentalAcquisitionCoordinator(Factory(restartedProvider), new Publisher(store, root), new LocationStore(), options, clock, store);
            using var restartedTelemetry = new EnvironmentalAcquisitionTelemetry();
            using var restarted = new EnvironmentalAcquisitionService(restartedCoordinator, store, store, restartedTelemetry, clock, options, NullLogger<EnvironmentalAcquisitionService>.Instance);
            await restarted.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await WaitFor(() => Task.FromResult(clock.ActiveTimers >= 3)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(240));
                await WaitFor(async () => (await store.ReadAttemptsAsync(root, 100, CancellationToken.None).ConfigureAwait(false)).Count > priorCount).ConfigureAwait(false);
                Assert.AreEqual(1, restartedHandler.Calls);
            }
            finally { await restarted.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { Delete(root); }
    }

    private static EnvironmentalSourceConfiguration OnlineConfiguration(string id, EnvironmentalObservationKind kind, int period)
        => new()
        {
            Id = id,
            Type = "OnlineWeather",
            Kind = kind,
            Required = false,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
            ScheduleEpochUtc = Epoch,
            PeriodSeconds = period,
            StaleAfterSeconds = 5400,
            ValidForSeconds = 7200,
            Options = JsonSerializer.SerializeToElement(new OnlineWeatherSourceOptions(WeatherProvider.Nws, "KIGM"), JsonOptions)
        };
    private static EnvironmentalSourceFactory Factory(IServiceProvider provider)
        => new(provider, [new("OnlineWeather", typeof(OnlineWeatherSource), typeof(OnlineWeatherSourceOptions))]);
    private static async Task WaitFor(Func<Task<bool>> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            if (await predicate().ConfigureAwait(false)) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.Fail("Expected bounded asynchronous service progress.");
    }
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private static readonly DateTimeOffset Epoch = EnvironmentalHardwareAdapterTests.Epoch;
    private static string Root() => Path.Combine(Path.GetTempPath(), "hvo-review-" + Guid.NewGuid().ToString("N"));
    private static void Delete(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
    private sealed class SlowAfterFirstDevice : IEnvironmentalDevice
    {
        private int count;
        public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
        {
            if (++count > 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new(Encoding.UTF8.GetBytes(EnvironmentalHardwareAdapterTests.Sqm), receivedAtUtc, false, ReceivedAtUtc: receivedAtUtc);
        }
    }
}
