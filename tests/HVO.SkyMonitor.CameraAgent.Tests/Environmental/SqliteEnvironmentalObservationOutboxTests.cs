using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class SqliteEnvironmentalObservationOutboxTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 17, 6, 0, 0, TimeSpan.Zero);
    private string? _root;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"environment-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublisherFreezesResolvedTargetBeforeDurableAcknowledgementAndRestart()
    {
        var firstTarget = new EnvironmentalObservationResolvedTarget(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var resolver = new MutableTargetResolver(firstTarget);
        using (var firstProcess = new SqliteEnvironmentalObservationOutbox())
        using (var telemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(),
            TimeProvider.System))
        {
            var publisher = new EnvironmentalObservationPublisher(
                resolver,
                firstProcess,
                new EnvironmentalObservationDeliveryWakeup(),
                new EnvironmentalObservationDeliveryState(),
                telemetry,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
                TimeProvider.System);
            var published = await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalObservationPublishDisposition.Enqueued, published.Disposition);
            resolver.Target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox();
        var lease = await restarted.ClaimAsync(_root!, "restart", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(lease);
        Assert.AreEqual(firstTarget.ObservatoryId, lease.Record.Observation.Target.SiteId);
        Assert.AreEqual(firstTarget.DevicePublicId, lease.Record.Observation.Target.AgentId);
        Assert.IsNull(lease.Record.Observation.Target.RigId);
        Assert.AreEqual(
            EnvironmentalObservationJson.ComputeContentSha256(lease.Record.Observation),
            lease.Record.ContentSha256);
    }

    [TestMethod]
    public async Task EnqueueExactDuplicateConvergesAndConflictingIdentityIsRejected()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var observation = CreateObservation(Guid.NewGuid());

        var first = await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
        var duplicate = await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
        var conflict = observation with { Value = observation.Value with { NumericValue = 70 } };

        Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Enqueued, first);
        Assert.AreEqual(EnvironmentalObservationEnqueueDisposition.Duplicate, duplicate);
        await Assert.ThrowsAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await outbox.EnqueueAsync(_root!, conflict, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var snapshot = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.StoredCount);
        Assert.AreEqual(EnvironmentalObservationJson.Serialize(observation).Length, snapshot.StoredBytes);
    }

    [TestMethod]
    public async Task CorruptedCommittedDuplicateIsNotAcknowledgedAsDurable()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        var observation = CreateObservation(Guid.NewGuid());
        await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE environmental_observation_outbox SET payload = zeroblob(payload_bytes);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await outbox.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CountAndByteCapacityAreDurablyBounded()
    {
        var first = CreateObservation(Guid.NewGuid());
        using (var countBound = new SqliteEnvironmentalObservationOutbox(maximumRecords: 1))
        {
            await countBound.EnqueueAsync(_root!, first, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsAsync<EnvironmentalObservationOutboxCapacityException>(async () =>
                await countBound.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            var snapshot = await countBound.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, snapshot.StoredCount);
            Assert.AreEqual(1, snapshot.OverflowCount);
        }

        var byteRoot = Path.Combine(_root!, "bytes");
        Directory.CreateDirectory(byteRoot);
        var payloadBytes = EnvironmentalObservationJson.Serialize(first).Length;
        using var byteBound = new SqliteEnvironmentalObservationOutbox(maximumRecords: 10, maximumBytes: payloadBytes);
        await byteBound.EnqueueAsync(byteRoot, first, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<EnvironmentalObservationOutboxCapacityException>(async () =>
            await byteBound.EnqueueAsync(byteRoot, CreateObservation(Guid.NewGuid()), CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(payloadBytes, (await byteBound.GetSnapshotAsync(byteRoot, CancellationToken.None)
            .ConfigureAwait(false)).StoredBytes);
    }

    [TestMethod]
    public async Task PublisherMarksDeliveryUnhealthyWhenCapacityRejectsProducer()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox(maximumRecords: 1);
        var state = new EnvironmentalObservationDeliveryState();
        using var telemetry = new EnvironmentalObservationDeliveryTelemetry(state, TimeProvider.System);
        var publisher = new EnvironmentalObservationPublisher(
            new MutableTargetResolver(new EnvironmentalObservationResolvedTarget(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))),
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            state,
            telemetry,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
            TimeProvider.System);
        await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false);

        await Assert.ThrowsAsync<EnvironmentalObservationOutboxCapacityException>(async () =>
            await publisher.PublishAsync(CreateFact(Guid.NewGuid())).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalObservationDeliveryAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.AreEqual("capacity-exhausted", state.Snapshot.Reason);
    }

    [TestMethod]
    public async Task SchemaUsesWalStrictVersionedFullDurabilityWithoutCredentialColumns()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox();
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observation-outbox.db");
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA journal_mode;";
        Assert.AreEqual("wal", (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA synchronous;";
        Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_outbox';";
        StringAssert.Contains((string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!, "STRICT", StringComparison.Ordinal);
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('environmental_observation_outbox');";
        var columns = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        Assert.IsFalse(columns.Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(columns.Contains("credential", StringComparison.OrdinalIgnoreCase));
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_outbox';";
        var schema = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        StringAssert.Contains(schema, "payload_bytes = length(payload)", StringComparison.Ordinal);
        StringAssert.Contains(schema, "status = 'leased' AND lease_owner IS NOT NULL", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SymbolicLinkDatabaseDirectoryIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Symbolic-link creation is not generally available to unprivileged Windows test processes.");
        }
        var external = Path.Combine(Path.GetTempPath(), $"environment-outbox-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(Path.Combine(_root!, ".environment"), external);
        try
        {
            using var outbox = new SqliteEnvironmentalObservationOutbox();
            await Assert.ThrowsAsync<IOException>(async () =>
                await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeferredRetryDoesNotBlockNewerPendingWorkAndSettlementsAreFenced()
    {
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        var first = CreateObservation(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var second = CreateObservation(Guid.Parse("10000000-0000-0000-0000-000000000002"));
        await outbox.EnqueueAsync(_root!, first, CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, second, CancellationToken.None).ConfigureAwait(false);
        var firstLease = (await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false))!;
        await outbox.RetryAsync(
            _root!, firstLease, time.GetUtcNow().AddMinutes(5), "offline", CancellationToken.None).ConfigureAwait(false);

        var secondLease = await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(secondLease);
        Assert.AreEqual(second.ObservationId, secondLease.Record.Observation.ObservationId);
        await outbox.QuarantineAsync(_root!, secondLease, "invalid", CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<EnvironmentalObservationLeaseLostException>(async () =>
            await outbox.TerminalAsync(_root!, secondLease, "stale-settlement", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsNull(await outbox.ClaimAsync(_root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(first.ObservationId, (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!.Record.Observation.ObservationId);
    }

    [TestMethod]
    public async Task ExpiredLeaseAfterRestartRedeliversAndAcknowledgementLossConverges()
    {
        var time = new MutableTimeProvider(Epoch);
        var observation = CreateObservation(Guid.NewGuid());
        var central = new FaithfulCentralReceiver(time);
        EnvironmentalObservationOutboxLease abandoned;
        EnvironmentalObservationAcknowledgement accepted;
        using (var firstProcess = new SqliteEnvironmentalObservationOutbox(time))
        {
            await firstProcess.EnqueueAsync(_root!, observation, CancellationToken.None).ConfigureAwait(false);
            abandoned = (await firstProcess.ClaimAsync(
                _root!, "worker-1", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
            accepted = central.Ingest(abandoned.Record.Observation);
        }
        time.Advance(TimeSpan.FromMinutes(1));
        using var restarted = new SqliteEnvironmentalObservationOutbox(time);

        var recovered = (await restarted.ClaimAsync(
            _root!, "worker-2", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;

        Assert.AreEqual(abandoned.Record.RecordId, recovered.Record.RecordId);
        Assert.AreEqual(2, recovered.Record.AttemptCount);
        var duplicate = central.Ingest(recovered.Record.Observation);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Accepted, accepted.Disposition);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Duplicate, duplicate.Disposition);
        Assert.AreEqual(accepted.ReceivedAtUtc, duplicate.ReceivedAtUtc);
        Assert.AreEqual(1, central.Count);
        await Assert.ThrowsAsync<EnvironmentalObservationLeaseLostException>(async () =>
            await restarted.RetryAsync(
                _root!, abandoned, time.GetUtcNow().AddMinutes(1), "stale-worker", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
        await restarted.AcknowledgeAsync(_root!, recovered, duplicate, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(0, (await restarted.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).StoredCount);
    }

    [TestMethod]
    public async Task ExpiredLeaseCannotSettleWithoutAReclaim()
    {
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var lease = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<EnvironmentalObservationLeaseLostException>(async () =>
            await outbox.RetryAsync(
                _root!, lease, time.GetUtcNow().AddMinutes(1), "late", CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AuthenticationFailureStopsBatchAndRetryLimitTerminalizes()
    {
        const string sensitive = "secret-device-site-payload-url";
        var time = new MutableTimeProvider(Epoch);
        using var outbox = new SqliteEnvironmentalObservationOutbox(time);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var authentication = new StubTransport(new(
            EnvironmentalObservationTransportDisposition.AuthenticationBlocked,
            sensitive,
            RetryAfter: TimeSpan.Zero));
        var logs = new CapturingLogger<EnvironmentalObservationDeliveryService>();
        var retryOptions = new EnvironmentalObservationDeliveryOptions
        {
            BatchSize = 100,
            RetryInitialDelaySeconds = 5,
            RetryMaximumDelaySeconds = 60,
            MaximumAttempts = 10
        };
        using (var telemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(),
            time))
        {
            var service = CreateDeliveryService(authentication, outbox, telemetry, time, retryOptions, logs);
            await service.DrainBatchAsync(_root!, retryOptions, CancellationToken.None).ConfigureAwait(false);
        }

        Assert.AreEqual(1, authentication.SendCount);
        var afterAuthentication = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, afterAuthentication.RetryCount);
        Assert.AreEqual(2, afterAuthentication.PendingCount);
        Assert.IsFalse(logs.Messages.Any(message => message.Contains(sensitive, StringComparison.Ordinal)));

        var pendingLease = await outbox.ClaimAsync(
            _root!, "test", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pendingLease);
        await outbox.TerminalAsync(_root!, pendingLease, "test-cleanup", CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromSeconds(5));
        var retry = new StubTransport(new(EnvironmentalObservationTransportDisposition.Retry, "offline"));
        var terminalOptions = new EnvironmentalObservationDeliveryOptions
        {
            BatchSize = 1,
            MaximumAttempts = 1
        };
        using (var telemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(),
            time))
        {
            var service = CreateDeliveryService(retry, outbox, telemetry, time, terminalOptions);
            await service.DrainBatchAsync(_root!, terminalOptions, CancellationToken.None).ConfigureAwait(false);
        }
        var terminal = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, terminal.TerminalCount);
        Assert.AreEqual(0, terminal.RetryCount);
    }

    [TestMethod]
    public void HistoricalOverflowDoesNotPoisonRecoveredHealth()
    {
        var snapshot = new EnvironmentalObservationOutboxSnapshot(
            0, 0, 0, 0, 0, 0, 0, 0, 3, null, Epoch);

        Assert.AreEqual(
            EnvironmentalObservationDeliveryAvailability.Healthy,
            EnvironmentalObservationDeliveryService.DetermineAvailability(snapshot));
    }

    [TestMethod]
    public async Task DeadLettersCanBeInspectedReplayedAndAbandonedToRecoverCapacity()
    {
        using var outbox = new SqliteEnvironmentalObservationOutbox(maximumRecords: 2);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var quarantine = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.QuarantineAsync(_root!, quarantine, "invalid-envelope", CancellationToken.None).ConfigureAwait(false);
        var terminal = (await outbox.ClaimAsync(
            _root!, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false))!;
        await outbox.TerminalAsync(_root!, terminal, "http-client", CancellationToken.None).ConfigureAwait(false);

        var deadLetters = await outbox.ReadDeadLettersAsync(_root!, 10, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, deadLetters.Count);
        Assert.AreEqual(EnvironmentalObservationDeadLetterStatus.Quarantined, deadLetters[0].Status);
        Assert.AreEqual(EnvironmentalObservationDeadLetterStatus.Terminal, deadLetters[1].Status);
        await outbox.ReplayAsync(
            _root!, deadLetters[0].RecordId, "test-operator", "corrected", CancellationToken.None).ConfigureAwait(false);
        await outbox.AbandonAsync(
            _root!, deadLetters[1].RecordId, "test-operator", "accepted-loss", CancellationToken.None).ConfigureAwait(false);
        var snapshot = await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.StoredCount);
        Assert.AreEqual(1, snapshot.PendingCount);
        await outbox.EnqueueAsync(_root!, CreateObservation(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, (await outbox.GetSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root!, ".environment", "environmental-observation-outbox.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT actor), COUNT(DISTINCT reason) FROM environmental_observation_outbox_audit;";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(2, reader.GetInt64(0));
        Assert.AreEqual(1, reader.GetInt64(1));
        Assert.AreEqual(2, reader.GetInt64(2));
    }

    private static EnvironmentalObservationFactV1 CreateFact(Guid observationId)
    {
        var observation = CreateObservation(observationId);
        return new EnvironmentalObservationFactV1(
            observation.SchemaVersion,
            observation.ObservationId,
            observation.Source,
            observation.ObservedAtUtc,
            observation.ObservedFromUtc,
            observation.ObservedThroughUtc,
            observation.ValidFromUtc,
            observation.ValidThroughUtc,
            observation.StaleAfterUtc,
            observation.Value,
            observation.Lineage);
    }

    private EnvironmentalObservationDeliveryService CreateDeliveryService(
        IEnvironmentalObservationTransport transport,
        IEnvironmentalObservationOutbox outbox,
        EnvironmentalObservationDeliveryTelemetry telemetry,
        TimeProvider timeProvider,
        EnvironmentalObservationDeliveryOptions deliveryOptions,
        ILogger<EnvironmentalObservationDeliveryService>? logger = null)
        => new(
            transport,
            outbox,
            new EnvironmentalObservationDeliveryWakeup(),
            new EnvironmentalObservationDeliveryState(),
            telemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = _root!,
                EnvironmentalDelivery = deliveryOptions
            }),
            timeProvider,
            logger ?? NullLogger<EnvironmentalObservationDeliveryService>.Instance);

    private static EnvironmentalObservationV1 CreateObservation(Guid observationId)
    {
        using var document = JsonDocument.Parse("{\"normalizer\":\"test-v1\"}");
        var parameters = document.RootElement.Clone();
        return new EnvironmentalObservationV1(
            EnvironmentalObservationV1.CurrentSchemaVersion,
            observationId,
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
                EnvironmentalObservationQuality.Good,
                0.5),
            []);
    }

    private sealed class MutableTargetResolver(EnvironmentalObservationResolvedTarget target)
        : IEnvironmentalObservationTargetResolver
    {
        public EnvironmentalObservationResolvedTarget Target { get; set; } = target;

        public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<EnvironmentalObservationResolvedTarget?>(Target);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class StubTransport(EnvironmentalObservationTransportResult result)
        : IEnvironmentalObservationTransport
    {
        public int SendCount { get; private set; }

        public ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class FaithfulCentralReceiver(TimeProvider timeProvider)
    {
        private readonly Dictionary<(string Source, Guid Observation), EnvironmentalObservationAcknowledgement> _observations = [];

        public int Count => _observations.Count;

        public EnvironmentalObservationAcknowledgement Ingest(EnvironmentalObservationV1 observation)
        {
            var source = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
            var key = (source, observation.ObservationId);
            if (_observations.TryGetValue(key, out var existing))
            {
                return existing with { Disposition = EnvironmentalObservationDeliveryDisposition.Duplicate };
            }
            var accepted = new EnvironmentalObservationAcknowledgement(
                EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
                observation.ObservationId,
                source,
                EnvironmentalObservationJson.ComputeContentSha256(observation),
                timeProvider.GetUtcNow(),
                EnvironmentalObservationDeliveryDisposition.Accepted);
            _observations.Add(key, accepted);
            return accepted;
        }
    }
}
