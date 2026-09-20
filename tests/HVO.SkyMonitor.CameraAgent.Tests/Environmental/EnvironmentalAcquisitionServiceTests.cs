using System.Collections.Concurrent;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalAcquisitionServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Retention_WhenFirstAttemptFails_RetriesWithoutReportingOutage()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(Path.GetTempPath(), $"hvo-environmental-{Guid.NewGuid():N}")
        });
        var factory = new EnvironmentalSourceFactory(services, []);
        using var coordinator = new EnvironmentalAcquisitionCoordinator(
            factory,
            Mock.Of<IEnvironmentalObservationPublisher>(),
            Mock.Of<IDeploymentLocationStore>(),
            options,
            TimeProvider.System);
        var retention = new FailOnceRetentionStore();
        var logger = new RecordingLogger<EnvironmentalAcquisitionService>();
        using var telemetry = new EnvironmentalAcquisitionTelemetry();
        using var service = new EnvironmentalAcquisitionService(
            coordinator,
            Mock.Of<IEnvironmentalAcquisitionStateStore>(),
            retention,
            telemetry,
            TimeProvider.System,
            options,
            logger);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await retention.Retried.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, retention.AttemptCount);
        Assert.IsFalse(logger.Events.Any(static entry => entry.EventId == 2526));
    }

    [TestMethod]
    public async Task PeriodicStartup_WhenEveryAttemptIsInitiallyBusy_ConvergesWithoutUnexpectedFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-environmental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var sourceConfigurations = StartupSourceKinds.Select((kind, index) => Source(kind, index)).ToArray();
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    MaximumConcurrency = 4,
                    QueueCapacity = 32,
                    SourceTimeoutMilliseconds = 5_000,
                    Sources = sourceConfigurations
                }
            });
            var factory = new EnvironmentalSourceFactory(
                services,
                [new EnvironmentalSourceRegistration(
                    "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
            var publisher = new ConcurrentPublisher();
            var stateStore = new FirstBusyPerSourceStateStore(StartupSourceKinds.Length);
            using var coordinator = new EnvironmentalAcquisitionCoordinator(
                factory,
                publisher,
                new FixedDeploymentLocationStore(Location()),
                options,
                TimeProvider.System,
                stateStore);
            var logger = new RecordingLogger<EnvironmentalAcquisitionService>();
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(
                coordinator,
                stateStore,
                new NoopRetentionStore(),
                telemetry,
                TimeProvider.System,
                options,
                logger);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await stateStore.Completed.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(StartupSourceKinds.Length, publisher.PublishCount);
            Assert.AreEqual(StartupSourceKinds.Length, stateStore.Receipts.Count);
            Assert.AreEqual(StartupSourceKinds.Length * 2, stateStore.AttemptCount);
            Assert.IsTrue(stateStore.Receipts.All(static receipt =>
                receipt.Disposition == EnvironmentalAcquisitionDisposition.Produced));
            Assert.IsFalse(logger.Events.Any(static entry => entry.EventId == 2522));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PeriodicStartup_WithSharedSqlitePersistenceAndRetention_HasNoUnexpectedFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-environmental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    MaximumConcurrency = 4,
                    QueueCapacity = 32,
                    SourceTimeoutMilliseconds = 5_000,
                    Sources = StartupSourceKinds.Select((kind, index) => Source(kind, index)).ToArray()
                }
            });
            using var store = new SqliteEnvironmentalObservationOutbox();
            var factory = new EnvironmentalSourceFactory(
                services,
                [new EnvironmentalSourceRegistration(
                    "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
            using var coordinator = new EnvironmentalAcquisitionCoordinator(
                factory,
                new StorePublisher(store, root),
                new FixedDeploymentLocationStore(Location()),
                options,
                TimeProvider.System,
                store);
            var logger = new RecordingLogger<EnvironmentalAcquisitionService>();
            using var telemetry = new EnvironmentalAcquisitionTelemetry();
            using var service = new EnvironmentalAcquisitionService(
                coordinator, store, store, telemetry, TimeProvider.System, options, logger);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForAttemptsAsync(store, root, StartupSourceKinds.Length).ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

            var attempts = await store.ReadAttemptsAsync(root, 100, CancellationToken.None).ConfigureAwait(false);
            var snapshot = await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(StartupSourceKinds.Length, attempts);
            Assert.AreEqual(StartupSourceKinds.Length, snapshot.StoredCount);
            Assert.IsFalse(logger.Events.Any(static entry => entry.EventId == 2522));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForAttemptsAsync(
        SqliteEnvironmentalObservationOutbox store,
        string root,
        int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var attempts = await store.ReadAttemptsAsync(root, 100, CancellationToken.None).ConfigureAwait(false);
            if (attempts.Count >= count)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for {count} environmental startup attempts.");
    }

    private static readonly EnvironmentalObservationKind[] StartupSourceKinds =
    [
        EnvironmentalObservationKind.AirTemperature,
        EnvironmentalObservationKind.RelativeHumidity,
        EnvironmentalObservationKind.AtmosphericPressure,
        EnvironmentalObservationKind.WindSpeed,
        EnvironmentalObservationKind.WindDirection,
        EnvironmentalObservationKind.WindGust,
        EnvironmentalObservationKind.PrecipitationRate,
        EnvironmentalObservationKind.RainState,
        EnvironmentalObservationKind.SkyBrightness,
        EnvironmentalObservationKind.SkyQuality,
        EnvironmentalObservationKind.CloudCover,
        EnvironmentalObservationKind.CameraSensorTemperature
    ];

    private static EnvironmentalSourceConfiguration Source(EnvironmentalObservationKind kind, int index)
    {
        var isBoolean = kind == EnvironmentalObservationKind.RainState;
        return new EnvironmentalSourceConfiguration
        {
            Id = $"startup-{kind}",
            Type = "VirtualEnvironment",
            Kind = kind,
            Required = true,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
            ScheduleEpochUtc = Epoch,
            PeriodSeconds = 30,
            EveryNthCapture = 3,
            ValidForSeconds = 120,
            StaleAfterSeconds = 45,
            RigId = kind == EnvironmentalObservationKind.CameraSensorTemperature ? "rig-1" : null,
            Options = CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(
                300 + index,
                Epoch,
                isBoolean ? null : NumericValue(kind, index),
                isBoolean ? false : null))
        };
    }

    private static double NumericValue(EnvironmentalObservationKind kind, int index)
        => kind switch
        {
            EnvironmentalObservationKind.RelativeHumidity => 45,
            EnvironmentalObservationKind.AtmosphericPressure => 101_325,
            EnvironmentalObservationKind.WindDirection => 180,
            EnvironmentalObservationKind.PrecipitationRate => 0,
            EnvironmentalObservationKind.SkyBrightness or EnvironmentalObservationKind.SkyQuality => 21,
            EnvironmentalObservationKind.CloudCover => 0.2,
            _ => 10 + index
        };

    private static DeploymentLocationSnapshot Location()
        => DeploymentLocationSnapshot.Create(
            "location", 1, "test", null, Epoch.AddDays(-1), null,
            35.5599378, -113.9119818, 520, "America/Phoenix");

    private sealed class FailOnceRetentionStore : ILocalEnvironmentalRetentionStore
    {
        private readonly TaskCompletionSource _retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public Task Retried => _retried.Task;

        public ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
            string root,
            DateTimeOffset recordedBeforeUtc,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
            {
                return ValueTask.FromException<LocalEnvironmentalRetentionResult>(
                    new IOException("Injected transient retention failure."));
            }
            _retried.TrySetResult();
            return ValueTask.FromResult(new LocalEnvironmentalRetentionResult(0, 0, 0, 0));
        }
    }

    private sealed class ConcurrentPublisher : IEnvironmentalObservationPublisher
    {
        private int _publishCount;
        public int PublishCount => Volatile.Read(ref _publishCount);

        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _publishCount);
            return ValueTask.FromResult(new EnvironmentalObservationPublishResult(
                EnvironmentalObservationPublishDisposition.Enqueued, null));
        }
    }

    private sealed class StorePublisher(SqliteEnvironmentalObservationOutbox store, string root)
        : IEnvironmentalObservationPublisher
    {
        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            var result = await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
            return new EnvironmentalObservationPublishResult(
                result.Disposition == LocalEnvironmentalObservationCommitDisposition.Committed
                    ? EnvironmentalObservationPublishDisposition.Enqueued
                    : EnvironmentalObservationPublishDisposition.Duplicate,
                null);
        }
    }

    private sealed class FirstBusyPerSourceStateStore(int expectedReceipts) : IEnvironmentalAcquisitionStateStore
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<EnvironmentalAcquisitionReceipt> _receipts = new();
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public ConcurrentQueue<EnvironmentalAcquisitionReceipt> Receipts => _receipts;
        public Task Completed => _completed.Task;

        public ValueTask RecordAttemptAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            EnvironmentalAcquisitionReceipt receipt,
            long? captureSequence,
            Guid? captureId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attemptCount);
            if (_calls.AddOrUpdate(source.Id, 1, static (_, current) => current + 1) == 1)
            {
                return ValueTask.FromException(new SqliteException(
                    "Injected startup writer contention.", SQLitePCL.raw.SQLITE_BUSY, SQLitePCL.raw.SQLITE_BUSY));
            }
            _receipts.Enqueue(receipt);
            if (_receipts.Count == expectedReceipts)
            {
                _completed.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RecordCaptureRegimeAsync(
            string root, long captureSequence, Guid captureId, CaptureSolarRegime regime,
            DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);

        public ValueTask UpdateSourceScheduleAsync(
            string root, EnvironmentalSourceDescriptor source, DateTimeOffset nextPollUtc,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(
            string root, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<EnvironmentalSourceRuntimeState>>([]);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(
            string root, int maximumResults, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>>([]);
    }

    private sealed class NoopRetentionStore : ILocalEnvironmentalRetentionStore
    {
        public ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
            string root, DateTimeOffset recordedBeforeUtc, int maximumResults,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new LocalEnvironmentalRetentionResult(0, 0, 0, 0));
    }

    private sealed class FixedDeploymentLocationStore(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; } = active;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed, CancellationToken cancellationToken) => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, int EventId, Exception? Exception)> Events { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Enqueue((logLevel, eventId.Id, exception));
    }
}
