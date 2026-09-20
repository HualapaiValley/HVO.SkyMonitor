using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalAcquisitionCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task OnDemandAcquisitionPublishesAndConcurrentTriggerCoalesces()
    {
        var publisher = new RecordingPublisher();
        using var coordinator = CreateCoordinator(publisher, delayMilliseconds: 1_000, timeoutMilliseconds: 5_000);

        var first = coordinator.AcquireSourceAsync(
            "temperature",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false);
        var coalesced = await coordinator.AcquireSourceAsync(
            "temperature",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var produced = await first.ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, produced.Disposition);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Coalesced, coalesced.Disposition);
        Assert.AreEqual("trigger-coalesced", coalesced.Reason);
        Assert.AreEqual(1, publisher.PublishCount);
        Assert.IsTrue(publisher.Facts.All(fact => fact.ObservationId == produced.ObservationId));
    }

    [TestMethod]
    public async Task SourceTimeoutIsExplicitAndDoesNotPublish()
    {
        var publisher = new RecordingPublisher();
        using var coordinator = CreateCoordinator(publisher, delayMilliseconds: 1_000, timeoutMilliseconds: 20);

        var receipt = await coordinator.AcquireSourceAsync(
            "temperature",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.TimedOut, receipt.Disposition);
        Assert.AreEqual("source-timeout", receipt.Reason);
        Assert.AreEqual(0, publisher.PublishCount);
    }

    [TestMethod]
    public async Task EveryNthCaptureUsesDurableSequenceAndConfiguredTrigger()
    {
        var publisher = new RecordingPublisher();
        using var coordinator = CreateCoordinator(publisher, everyNthCapture: 3);

        var skipped = await coordinator.AcquireTriggerAsync(
            EnvironmentalAcquisitionTrigger.EveryNthCapture,
            Epoch,
            captureSequence: 2,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var selected = await coordinator.AcquireTriggerAsync(
            EnvironmentalAcquisitionTrigger.EveryNthCapture,
            Epoch.AddSeconds(1),
            captureSequence: 3,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.IsEmpty(skipped);
        Assert.HasCount(1, selected);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, selected[0].Disposition);
        Assert.AreEqual(1, publisher.PublishCount);
    }

    [TestMethod]
    public async Task OnDemandFailureReleasesDurableCommandLease()
    {
        using var coordinator = CreateCoordinator(new RecordingPublisher(), stateStore: new ThrowingStateStore());
        var commands = new RecordingCommandStore();
        var service = new EnvironmentalOnDemandAcquisitionService(
            coordinator,
            commands,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Path.GetTempPath(),
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    SourceTimeoutMilliseconds = 5_000
                }
            }),
            TimeProvider.System);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await service.AcquireAsync(
                "temperature", "command-1", "owner-1", null, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsTrue(commands.Released);
    }

    [TestMethod]
    public async Task TransientWriterContention_DurablyRecordsTheFailedAcquisitionAfterTheWriterClears()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-environmental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(busyTimeoutSeconds: 1);
            _ = await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            var stateStore = new SignalingStateStore(store);
            using var coordinator = CreateCoordinator(
                new StorePublisher(store, root),
                stateStore: stateStore,
                root: root);
            using var lockingConnection = new SqliteConnection(
                $"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")}");
            await lockingConnection.OpenAsync().ConfigureAwait(false);
            using (var lockCommand = lockingConnection.CreateCommand())
            {
                lockCommand.CommandText = "BEGIN IMMEDIATE;";
                await lockCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var acquisition = coordinator.AcquireSourceAsync(
                "temperature",
                EnvironmentalAcquisitionTrigger.OnDemand,
                Epoch,
                cancellationToken: CancellationToken.None).AsTask();
            await stateStore.FirstLockFailure.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            using (var rollback = lockingConnection.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var receipt = await acquisition.ConfigureAwait(false);
            var attempts = await store.ReadAttemptsAsync(
                root, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, attempts);
            var attempt = attempts[0];

            Assert.AreEqual(EnvironmentalAcquisitionDisposition.Failed, receipt.Disposition);
            Assert.AreEqual("journal-unavailable", receipt.Reason);
            Assert.AreEqual(receipt.SourceId, attempt.SourceId);
            Assert.AreEqual(receipt.Disposition, attempt.Disposition);
            Assert.AreEqual(receipt.Reason, attempt.Reason);
            Assert.AreEqual(2, stateStore.AttemptCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistentWriterContention_ExhaustsTheBoundedRetryAndRemainsVisible()
    {
        var stateStore = new BusyStateStore();
        using var coordinator = CreateCoordinator(new RecordingPublisher(), stateStore: stateStore);

        var exception = await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await coordinator.AcquireSourceAsync(
                "temperature",
                EnvironmentalAcquisitionTrigger.OnDemand,
                Epoch,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.AreEqual(SQLitePCL.raw.SQLITE_BUSY, exception.SqliteErrorCode);
        Assert.AreEqual(3, stateStore.AttemptCount);
    }

    private static EnvironmentalAcquisitionCoordinator CreateCoordinator(
        IEnvironmentalObservationPublisher publisher,
        int delayMilliseconds = 0,
        int timeoutMilliseconds = 5_000,
        int everyNthCapture = 3,
        IEnvironmentalAcquisitionStateStore? stateStore = null,
        string? root = null)
    {
        var sourceOptions = new VirtualEnvironmentalSourceOptions(
            209,
            Epoch,
            12.5,
            null,
            0.1,
            0.2,
            DelayMilliseconds: delayMilliseconds);
        var configuration = new EnvironmentalSourceConfiguration
        {
            Id = "temperature",
            Type = "VirtualEnvironment",
            Kind = EnvironmentalObservationKind.AirTemperature,
            Required = true,
            Triggers = [EnvironmentalAcquisitionTrigger.OnDemand, EnvironmentalAcquisitionTrigger.EveryNthCapture],
            ScheduleEpochUtc = Epoch,
            PeriodSeconds = 30,
            EveryNthCapture = everyNthCapture,
            ValidForSeconds = 120,
            StaleAfterSeconds = 45,
            Options = CaptureContractJson.SerializeToElement(sourceOptions)
        };
        var hostOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root ?? Path.GetTempPath(),
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                MaximumConcurrency = 4,
                SourceTimeoutMilliseconds = timeoutMilliseconds,
                Sources = [configuration]
            }
        });
        var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new EnvironmentalSourceFactory(
            provider,
            [new EnvironmentalSourceRegistration(
                "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
        return new EnvironmentalAcquisitionCoordinator(
            factory,
            publisher,
            new FixedDeploymentLocationStore(Location()),
            hostOptions,
            TimeProvider.System,
            stateStore);
    }

    private static DeploymentLocationSnapshot Location()
        => DeploymentLocationSnapshot.Create(
            "location",
            1,
            "test",
            null,
            Epoch.AddDays(-1),
            null,
            35.5599378,
            -113.9119818,
            520,
            "America/Phoenix");

    private sealed class RecordingPublisher : IEnvironmentalObservationPublisher
    {
        private readonly List<EnvironmentalObservationFactV1> _facts = [];
        public int PublishCount => _facts.Count;
        public IReadOnlyList<EnvironmentalObservationFactV1> Facts => _facts;

        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            _facts.Add(fact);
            return ValueTask.FromResult(new EnvironmentalObservationPublishResult(
                EnvironmentalObservationPublishDisposition.Enqueued,
                null));
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

    private sealed class FixedDeploymentLocationStore(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; } = active;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);

        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => Active!;
    }

    private class ThrowingStateStore : IEnvironmentalAcquisitionStateStore
    {
        public virtual ValueTask RecordAttemptAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            EnvironmentalAcquisitionReceipt receipt,
            long? captureSequence,
            Guid? captureId,
            CancellationToken cancellationToken)
            => ValueTask.FromException(new InvalidOperationException("state unavailable"));

        public ValueTask<bool> RecordCaptureRegimeAsync(
            string root,
            long captureSequence,
            Guid captureId,
            CaptureSolarRegime regime,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(false);

        public ValueTask UpdateSourceScheduleAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            DateTimeOffset nextPollUtc,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(
            string root,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<EnvironmentalSourceRuntimeState>>([]);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(
            string root,
            int maximumResults,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>>([]);
    }

    private sealed class BusyStateStore : ThrowingStateStore
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public override ValueTask RecordAttemptAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            EnvironmentalAcquisitionReceipt receipt,
            long? captureSequence,
            Guid? captureId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attemptCount);
            return ValueTask.FromException(
                new SqliteException("Injected busy writer.", SQLitePCL.raw.SQLITE_BUSY, SQLitePCL.raw.SQLITE_BUSY));
        }
    }

    private sealed class SignalingStateStore(IEnvironmentalAcquisitionStateStore inner)
        : IEnvironmentalAcquisitionStateStore
    {
        private readonly TaskCompletionSource _firstLockFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attemptCount;

        public Task FirstLockFailure => _firstLockFailure.Task;
        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public async ValueTask RecordAttemptAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            EnvironmentalAcquisitionReceipt receipt,
            long? captureSequence,
            Guid? captureId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attemptCount);
            try
            {
                await inner.RecordAttemptAsync(
                    root, source, receipt, captureSequence, captureId, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
            {
                _firstLockFailure.TrySetResult();
                throw;
            }
        }

        public ValueTask<bool> RecordCaptureRegimeAsync(
            string root,
            long captureSequence,
            Guid captureId,
            CaptureSolarRegime regime,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
            => inner.RecordCaptureRegimeAsync(root, captureSequence, captureId, regime, observedAtUtc, cancellationToken);

        public ValueTask UpdateSourceScheduleAsync(
            string root,
            EnvironmentalSourceDescriptor source,
            DateTimeOffset nextPollUtc,
            CancellationToken cancellationToken)
            => inner.UpdateSourceScheduleAsync(root, source, nextPollUtc, cancellationToken);

        public ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(
            string root,
            CancellationToken cancellationToken)
            => inner.ReadSourceStatesAsync(root, cancellationToken);

        public ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(
            string root,
            int maximumResults,
            CancellationToken cancellationToken)
            => inner.ReadAttemptsAsync(root, maximumResults, cancellationToken);
    }

    private sealed class RecordingCommandStore : IEnvironmentalOnDemandCommandStore
    {
        public bool Released { get; private set; }

        public ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
            string root,
            string idempotencyKey,
            string payloadSha256,
            string sourceId,
            string actorId,
            string? reason,
            DateTimeOffset observedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalOnDemandCommandClaim(
                EnvironmentalOnDemandClaimDisposition.Claimed,
                "lease-1",
                Epoch,
                null));

        public ValueTask CompleteOnDemandAsync(
            string root,
            string idempotencyKey,
            string leaseToken,
            EnvironmentalAcquisitionReceipt receipt,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseOnDemandAsync(
            string root,
            string idempotencyKey,
            string leaseToken,
            CancellationToken cancellationToken)
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
