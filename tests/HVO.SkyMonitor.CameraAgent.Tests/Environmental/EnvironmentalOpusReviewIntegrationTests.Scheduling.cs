using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalOpusReviewIntegrationTests
{
    [TestMethod]
    public async Task PeriodicProviderRecoversWhenTheInitialScheduleReadFails()
    {
        var root = Root();
        try
        {
            var clock = new ExposureTestTimeProvider(Epoch);
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            using var handler = new OnlineWeatherSourceTests.FakeHandler(_ => OnlineWeatherSourceTests.Response(200, EnvironmentalAdapterReviewUnitTests.Payload));
            using var client = new WeatherProviderClient(new HttpClient(handler), clock);
            using var provider = new ServiceCollection().AddSingleton(client).BuildServiceProvider();
            var source = OnlineConfiguration();
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new() { Enabled = true, Sources = [source] }
            });
            using var coordinator = new EnvironmentalAcquisitionCoordinator(OnlineFactory(provider), new Publisher(store, root),
                new Locations(), options, clock, store);
            var reads = 0;
            var state = new Mock<IEnvironmentalAcquisitionStateStore>(MockBehavior.Strict);
            state.Setup(value => value.ReadSourceStatesAsync(root, It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((_, token) => Interlocked.Increment(ref reads) == 1
                    ? ValueTask.FromException<IReadOnlyList<EnvironmentalSourceRuntimeState>>(new IOException("injected schedule read failure"))
                    : store.ReadSourceStatesAsync(root, token));
            state.Setup(value => value.UpdateSourceScheduleAsync(root, It.IsAny<EnvironmentalSourceDescriptor>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns<string, EnvironmentalSourceDescriptor, DateTimeOffset, CancellationToken>((_, descriptor, due, token) => store.UpdateSourceScheduleAsync(root, descriptor, due, token));
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(coordinator, state.Object, store, telemetry, clock, options,
                NullLogger<EnvironmentalAcquisitionService>.Instance);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await WaitFor(() => Task.FromResult(Volatile.Read(ref reads) >= 1 && clock.ActiveTimers >= 2)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromSeconds(1));
                await WaitFor(async () => (await store.ReadAttemptsAsync(root, 10, CancellationToken.None).ConfigureAwait(false)).Count == 1).ConfigureAwait(false);
                Assert.AreEqual(1, handler.Calls);
                Assert.IsTrue(reads >= 2);
            }
            finally { await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task ProviderMinimumPeriodDoesNotSkipARefreshWhenSchedulingLatencyShrinks()
    {
        var root = Root();
        try
        {
            var clock = new ExposureTestTimeProvider(Epoch.AddMilliseconds(100));
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var requestTimes = new List<DateTimeOffset>();
            using var handler = new OnlineWeatherSourceTests.FakeHandler(_ =>
            { requestTimes.Add(clock.GetUtcNow()); return OnlineWeatherSourceTests.Response(200, EnvironmentalAdapterReviewUnitTests.Payload); });
            using var client = new WeatherProviderClient(new HttpClient(handler), clock, store, root);
            using var provider = new ServiceCollection().AddSingleton(client).BuildServiceProvider();
            var source = OnlineConfiguration();
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new() { Enabled = true, Sources = [source] }
            });
            using var coordinator = new EnvironmentalAcquisitionCoordinator(OnlineFactory(provider), new Publisher(store, root),
                new Locations(), options, clock, store);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
                (await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, Epoch).ConfigureAwait(false)).Disposition);
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(coordinator, store, store, telemetry, clock, options,
                NullLogger<EnvironmentalAcquisitionService>.Instance);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await WaitFor(() => Task.FromResult(clock.ActiveTimers >= 2)).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMilliseconds(299_900));
                await Task.Delay(30).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await WaitFor(() => Task.FromResult(handler.Calls == 2)).ConfigureAwait(false);
                Assert.IsTrue(requestTimes[1] - requestTimes[0] >= TimeSpan.FromSeconds(300), "Jitter handling must never violate provider minimum request spacing.");
            }
            finally { await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task FiniteSlowSqmReadIsCancelledByTheRealCaptureBudgetAndCannotReuseAnOldFact()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var source = new EnvironmentalSourceConfiguration
            {
                Id = "sqm",
                Type = "SkyQualityMeter",
                Kind = EnvironmentalObservationKind.SkyQuality,
                Triggers = [EnvironmentalAcquisitionTrigger.BeforeCapture],
                StaleAfterSeconds = 300,
                ValidForSeconds = 900,
                Options = JsonSerializer.SerializeToElement(new EnvironmentalDeviceOptions("sqm"), JsonOptions)
            };
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new() { Enabled = true, BeforeCaptureWaitMilliseconds = 250, SourceTimeoutMilliseconds = 5000, Sources = [source] }
            });
            var device = new FiniteSlowDevice();
            using var provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(device).BuildServiceProvider();
            using var coordinator = new EnvironmentalAcquisitionCoordinator(new(provider, EnvironmentalAdapterPipelineTests.Registrations()),
                new Publisher(store, root), new Locations(), options, clock, store);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
                (await coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.BeforeCapture, Epoch).ConfigureAwait(false)).Disposition);
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(coordinator, store, store, telemetry, clock, options,
                NullLogger<EnvironmentalAcquisitionService>.Instance);
            var bridge = new EnvironmentalCaptureTriggerBridge(coordinator, service, store, options);
            await bridge.BeforeCaptureAsync(Epoch.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(device.Cancelled);
            Assert.IsFalse(device.SlowReadCompleted);
            Assert.AreEqual(EnvironmentalAcquisitionDisposition.TimedOut,
                (await store.ReadSourceStatesAsync(root, CancellationToken.None).ConfigureAwait(false)).Single().LastDisposition);
            var associations = new EnvironmentalAssociationService(store, store, options, clock, acquisitionStates: store);
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing,
                (await associations.AssociateAsync(Guid.NewGuid(), 1, Epoch.AddSeconds(1), Epoch.AddSeconds(2), null,
                    [source.Kind], CancellationToken.None).ConfigureAwait(false)).Single().Status);
        }
        finally { Delete(root); }
    }

    private static EnvironmentalSourceConfiguration OnlineConfiguration() => new()
    {
        Id = "weather",
        Type = "OnlineWeather",
        Kind = EnvironmentalObservationKind.AirTemperature,
        ScheduleEpochUtc = Epoch,
        Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
        PeriodSeconds = 300,
        StaleAfterSeconds = 5400,
        ValidForSeconds = 7200,
        Options = JsonSerializer.SerializeToElement(new OnlineWeatherSourceOptions(WeatherProvider.Nws, "KIGM"), JsonOptions)
    };
    private static EnvironmentalSourceFactory OnlineFactory(IServiceProvider provider)
        => new(provider, [new("OnlineWeather", typeof(OnlineWeatherSource), typeof(OnlineWeatherSourceOptions))]);
    private static async Task WaitFor(Func<Task<bool>> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        { if (await predicate().ConfigureAwait(false)) return; await Task.Delay(10).ConfigureAwait(false); }
        Assert.Fail("Expected bounded hosted service progress.");
    }
    private sealed class FiniteSlowDevice : IEnvironmentalDevice
    {
        private int calls;
        public bool Cancelled { get; private set; }
        public bool SlowReadCompleted { get; private set; }
        public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
        {
            if (++calls > 1)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); SlowReadCompleted = true; }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new(Encoding.ASCII.GetBytes(EnvironmentalHardwareAdapterTests.Sqm), receivedAtUtc, true, ReceivedAtUtc: receivedAtUtc);
        }
    }
}
