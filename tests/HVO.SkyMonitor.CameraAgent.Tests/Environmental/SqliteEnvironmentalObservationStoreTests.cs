using System.Text.Json;
using System.Diagnostics.Metrics;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
[TestProperty("Area", "Environmental")]
// Six of this class's seven pool clears are load-bearing, and they are why the class opts out of
// parallelisation rather than losing them: one makes a simulated restart genuine, since a pooled
// connection would otherwise survive it, and five bracket the raw DDL the schema-rejection tests apply,
// so that no connection serves a cached schema across the mutation. Removing those would leave the tests
// passing for a different reason than the one they assert, which the assertions cannot detect. The
// seventh, in Cleanup, is an ordinary teardown clear; the rule elsewhere in this assembly is to delete
// teardown clears in parallelisable classes, and this class is not one, so it is simply left alone. A
// global clear is safe only when nothing else runs beside it, which the class-level opt-out provides;
// that exclusion was verified by measurement under MSTest 4.3.3. See issue #754.
[DoNotParallelize]
public sealed class SqliteEnvironmentalObservationStoreTests
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
    public async Task LocalJournalRejectsConflictsAndCorruptCommittedDuplicates()
    {
        using var store = new SqliteEnvironmentalObservationStore();
        var fact = CreateFact(Guid.NewGuid());

        Assert.AreEqual(
            LocalEnvironmentalObservationCommitDisposition.Committed,
            (await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false)).Disposition);
        Assert.AreEqual(
            LocalEnvironmentalObservationCommitDisposition.Duplicate,
            (await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false)).Disposition);
        await Assert.ThrowsAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await store.CommitLocalAsync(
                _root!,
                fact with { Value = fact.Value with { NumericValue = fact.Value.NumericValue + 1 } },
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        var databasePath = Path.Combine(_root!, ".environment", "environmental-observations.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE environmental_observation_journal SET payload = zeroblob(payload_bytes);";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalJournalRejectsChangedContentForAnExistingSourceIdentity()
    {
        using var store = new SqliteEnvironmentalObservationStore();
        var first = CreateFact(Guid.NewGuid());
        await store.CommitLocalAsync(_root!, first, CancellationToken.None).ConfigureAwait(false);
        using var document = JsonDocument.Parse("{\"normalizer\":\"changed-v2\"}");
        var parameters = document.RootElement.Clone();
        var changed = first with
        {
            ObservationId = Guid.NewGuid(),
            Source = first.Source with
            {
                Provenance = first.Source.Provenance with
                {
                    Parameters = parameters,
                    ParametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters)
                }
            }
        };

        await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await store.CommitLocalAsync(_root!, changed, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LocalJournalCapacityIsIndependentAndDurablyCounted()
    {
        using var store = new SqliteEnvironmentalObservationStore(
            new MutableTimeProvider(Epoch), maximumLocalRecords: 1);
        await store.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsAsync<LocalEnvironmentalObservationCapacityException>(async () =>
            await store.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);

        var snapshot = await store.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, snapshot.StoredCount);
        Assert.AreEqual(1, snapshot.OverflowCount);
    }

    [TestMethod]
    public async Task LocalPressureRetentionRemovesExpiredUnpinnedFactsBeforeRejectingNewHistory()
    {
        var time = new MutableTimeProvider(Epoch);
        using var store = new SqliteEnvironmentalObservationStore(
            time,
            maximumLocalRecords: 1,
            localRetentionDays: 31,
            localRetentionBatchSize: 1);
        var first = await store.CommitLocalAsync(
            _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        time.Advance(TimeSpan.FromDays(1));

        var second = await store.CommitLocalAsync(
            _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, (await store.GetLocalSnapshotAsync(_root!, CancellationToken.None)
            .ConfigureAwait(false)).StoredCount);
        Assert.IsNull(await store.ReadLocalDetailAsync(
            _root!, first.Record.RecordId, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNotNull(await store.ReadLocalDetailAsync(
            _root!, second.Record.RecordId, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task LocalHistoryUsesBoundedKeysetPagingAndTemporalCandidates()
    {
        using var store = new SqliteEnvironmentalObservationStore();
        var facts = Enumerable.Range(1, 3)
            .Select(index => CreateFact(Guid.Parse($"20000000-0000-0000-0000-{index:D12}")) with
            {
                ObservedAtUtc = Epoch.AddSeconds(index)
            })
            .ToArray();
        foreach (var fact in facts)
        {
            await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);
        }

        var first = await store.ReadLocalPageAsync(
            _root!, EnvironmentalObservationKind.RelativeHumidity, 2, null, CancellationToken.None)
            .ConfigureAwait(false);
        var second = await store.ReadLocalPageAsync(
            _root!, EnvironmentalObservationKind.RelativeHumidity, 2, first.NextCursor, CancellationToken.None)
            .ConfigureAwait(false);
        var candidates = await store.ReadLocalCandidatesAsync(
            _root!, EnvironmentalObservationKind.RelativeHumidity, null, Epoch, Epoch.AddMinutes(1), 10,
            CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, first.Items);
        Assert.AreEqual(facts[2].ObservationId, first.Items[0].Fact.ObservationId);
        Assert.IsNotNull(first.NextCursor);
        Assert.HasCount(1, second.Items);
        Assert.AreEqual(facts[0].ObservationId, second.Items[0].Fact.ObservationId);
        Assert.IsNull(second.NextCursor);
        Assert.HasCount(3, candidates);
        var detail = await store.ReadLocalDetailAsync(
            _root!, first.Items[0].RecordId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(detail);
        Assert.AreEqual(first.Items[0].ContentSha256, detail!.ContentSha256);
        Assert.IsEmpty((await store.ReadLocalPageAsync(
            _root!, EnvironmentalObservationKind.AirTemperature, 10, null, CancellationToken.None)
            .ConfigureAwait(false)).Items);
    }

    [TestMethod]
    public async Task AcquisitionAttemptsAndSourceRuntimeSurviveRestartWithoutManufacturingObservations()
    {
        using var document = JsonDocument.Parse("{}");
        var source = new EnvironmentalSourceDescriptor(
            "temperature",
            "VirtualEnvironment",
            EnvironmentalObservationKind.AirTemperature,
            true,
            [EnvironmentalAcquisitionTrigger.Periodic],
            Epoch,
            30,
            3,
            120,
            45,
            null,
            document.RootElement.Clone());
        var observationId = Guid.NewGuid();
        using (var store = new SqliteEnvironmentalObservationStore())
        {
            await store.RecordAttemptAsync(
                _root!,
                source,
                new EnvironmentalAcquisitionReceipt(
                    source.Id,
                    EnvironmentalAcquisitionTrigger.Periodic,
                    EnvironmentalAcquisitionDisposition.Produced,
                    "produced",
                    observationId,
                    Epoch,
                    Epoch.AddMilliseconds(10),
                    Epoch,
                    Epoch.AddSeconds(45)),
                null,
                null,
                CancellationToken.None).ConfigureAwait(false);
            await store.UpdateSourceScheduleAsync(
                _root!, source, Epoch.AddSeconds(60), CancellationToken.None).ConfigureAwait(false);
            await store.RecordAttemptAsync(
                _root!,
                source,
                new EnvironmentalAcquisitionReceipt(
                    source.Id,
                    EnvironmentalAcquisitionTrigger.Periodic,
                    EnvironmentalAcquisitionDisposition.Missing,
                    "source-missing",
                    null,
                    Epoch.AddSeconds(30),
                    Epoch.AddSeconds(30).AddMilliseconds(10)),
                null,
                null,
                CancellationToken.None).ConfigureAwait(false);
        }

        using var restarted = new SqliteEnvironmentalObservationStore();
        var state = (await restarted.ReadSourceStatesAsync(_root!, CancellationToken.None).ConfigureAwait(false)).Single();
        var attempts = await restarted.ReadAttemptsAsync(_root!, 10, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, state.LastDisposition);
        Assert.AreEqual(observationId, state.LastObservationId);
        Assert.AreEqual(Epoch, state.LastObservedUtc);
        Assert.AreEqual(Epoch.AddSeconds(45), state.LastStaleAfterUtc);
        Assert.AreEqual(1, state.ConsecutiveFailures);
        Assert.AreEqual(Epoch.AddSeconds(60), state.NextPollUtc);
        Assert.HasCount(2, attempts);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Missing, attempts[0].Disposition);
        Assert.IsNull(attempts[0].ObservationId);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, attempts[1].Disposition);
    }

    [TestMethod]
    public async Task CaptureReceiptRetentionAndOperatorCommandCapacityAreIndependent()
    {
        using var store = new SqliteEnvironmentalObservationStore(new MutableTimeProvider(Epoch));
        var payload = new string('A', 64);
        _ = await store.ClaimOnDemandAsync(_root!, "operator-seed", payload, "temperature", "owner", null,
            Epoch, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_root!, ".environment", "environmental-observations.db") }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < $maximum)
            INSERT INTO environmental_on_demand_commands(
                idempotency_key, payload_sha256, source_id, actor_id, reason, observed_unix_ms,
                status, lease_token, lease_expires_unix_ms, receipt_json, updated_unix_ms)
            SELECT $prefix || numbers.value, payload_sha256, source_id, actor_id, reason, observed_unix_ms,
                status, lease_token, lease_expires_unix_ms, receipt_json, updated_unix_ms
            FROM environmental_on_demand_commands AS template, numbers WHERE template.idempotency_key = 'operator-seed';
            """;
        seed.Parameters.AddWithValue("$prefix", "__capture-before:retained-");
        seed.Parameters.AddWithValue("$maximum", SqliteEnvironmentalObservationStore.MaximumOperationReceipts);
        await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Claimed, (await store.ClaimOnDemandAsync(
            _root!, "operator-next", payload, "temperature", "owner", null, Epoch, TimeSpan.FromMinutes(1), CancellationToken.None)
            .ConfigureAwait(false)).Disposition);

        seed.Parameters["$prefix"].Value = "operator-history-";
        seed.Parameters["$maximum"].Value = SqliteEnvironmentalObservationStore.MaximumOperationReceipts - 2;
        await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<EnvironmentalOnDemandCommandCapacityException>(async () =>
            await store.ClaimOnDemandAsync(_root!, "operator-over-capacity", payload, "temperature", "owner", null,
                Epoch, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Claimed, (await store.ClaimOnDemandAsync(
            _root!, EnvironmentalCaptureAcquisition.Prefix(Guid.NewGuid()) + "source", payload, "temperature", "capture-before", null,
            Epoch, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)).Disposition);
    }

    [TestMethod]
    public async Task OnDemandCommandIsLeasedConflictCheckedAndDurablyReplayable()
    {
        const string key = "environment-command-1";
        var payload = new string('A', 64);
        var receipt = new EnvironmentalAcquisitionReceipt(
            "temperature",
            EnvironmentalAcquisitionTrigger.OnDemand,
            EnvironmentalAcquisitionDisposition.Missing,
            "source-missing",
            null,
            Epoch,
            Epoch.AddMilliseconds(10));
        using (var store = new SqliteEnvironmentalObservationStore(new MutableTimeProvider(Epoch)))
        {
            var claim = await store.ClaimOnDemandAsync(
                _root!, key, payload, "temperature", "owner-1", "manual-check", Epoch,
                TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Claimed, claim.Disposition);
            Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Busy, (await store.ClaimOnDemandAsync(
                _root!, key, payload, "temperature", "owner-1", "manual-check", Epoch.AddSeconds(1),
                TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)).Disposition);
            await Assert.ThrowsExactlyAsync<EnvironmentalOnDemandCommandConflictException>(async () =>
                await store.ClaimOnDemandAsync(
                    _root!, key, new string('B', 64), "temperature", "owner-1", "manual-check", Epoch,
                    TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await store.CompleteOnDemandAsync(
                _root!, key, claim.LeaseToken, receipt, CancellationToken.None).ConfigureAwait(false);
        }

        using var restarted = new SqliteEnvironmentalObservationStore(new MutableTimeProvider(Epoch.AddMinutes(2)));
        var replay = await restarted.ClaimOnDemandAsync(
            _root!, key, payload, "temperature", "owner-1", "manual-check", Epoch.AddMinutes(2),
            TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalOnDemandClaimDisposition.Completed, replay.Disposition);
        Assert.AreEqual(receipt, replay.Receipt);
        Assert.AreEqual(Epoch, replay.ObservedAtUtc);
    }

    [TestMethod]
    public async Task CaptureRegimeChangeSurvivesRestartAndCaptureReplay()
    {
        var nightCapture = Guid.Parse("91000000-0000-0000-0000-000000000001");
        var twilightCapture = Guid.Parse("91000000-0000-0000-0000-000000000002");
        using (var store = new SqliteEnvironmentalObservationStore())
        {
            Assert.IsFalse(await store.RecordCaptureRegimeAsync(
                _root!, 1, nightCapture, CaptureSolarRegime.Night, Epoch, CancellationToken.None).ConfigureAwait(false));
        }

        using var restarted = new SqliteEnvironmentalObservationStore();
        Assert.IsTrue(await restarted.RecordCaptureRegimeAsync(
            _root!, 2, twilightCapture, CaptureSolarRegime.Twilight, Epoch.AddMinutes(1), CancellationToken.None)
            .ConfigureAwait(false));
        Assert.IsFalse(await restarted.RecordCaptureRegimeAsync(
            _root!, 2, twilightCapture, CaptureSolarRegime.Twilight, Epoch.AddMinutes(1), CancellationToken.None)
            .ConfigureAwait(false));
        Assert.IsFalse(await restarted.RecordCaptureRegimeAsync(
            _root!, 1, nightCapture, CaptureSolarRegime.Night, Epoch, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
            await restarted.RecordCaptureRegimeAsync(
                _root!, 2, twilightCapture, CaptureSolarRegime.Day, Epoch.AddMinutes(1), CancellationToken.None)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task HistoricalSchemaMigratesWithoutChangingPersistedPayloads(int version)
    {
        var fact = CreateFact(Guid.NewGuid());
        var scope = Guid.NewGuid();
        var key = EnvironmentalCaptureAcquisition.Prefix(scope) + "sqm";
        var now = DateTimeOffset.UtcNow;
        var receipt = new EnvironmentalAcquisitionReceipt("sqm", EnvironmentalAcquisitionTrigger.BeforeCapture,
            EnvironmentalAcquisitionDisposition.Failed, "source-failure", null, now, now);
        using (var initial = new SqliteEnvironmentalObservationStore())
        {
            await initial.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);
            var claim = await initial.ClaimOnDemandAsync(_root!, key, new string('A', 64), "sqm", "capture-before", null,
                now, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            await initial.CompleteOnDemandAsync(_root!, key, claim.LeaseToken, receipt, CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        using (var setup = new SqliteConnection($"Data Source={Path.Combine(_root!, ".environment", "environmental-observations.db")};Pooling=False"))
        {
            await setup.OpenAsync().ConfigureAwait(false);
            using var downgrade = setup.CreateCommand();
            if (version == 3)
                downgrade.CommandText = "DROP TABLE environmental_location_held_polls; DROP INDEX ix_environment_on_demand_retention; DROP INDEX ix_environment_attempt_retention; PRAGMA user_version=3;";
            else
                downgrade.CommandText = "DROP TABLE environmental_location_held_polls; PRAGMA user_version=4;";
            await downgrade.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        using var migrated = new SqliteEnvironmentalObservationStore();
        var retained = await migrated.ReadLocalLatestPrimaryAsync(
            _root!, fact.Value.Kind, [fact.Source.SourceId], [], CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(retained);
        CollectionAssert.AreEqual(EnvironmentalObservationFactJson.Serialize(fact), EnvironmentalObservationFactJson.Serialize(retained.Fact));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root!, ".environment", "environmental-observations.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT receipt_json FROM environmental_on_demand_commands WHERE idempotency_key = $key;";
        inspect.Parameters.AddWithValue("$key", key);
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(receipt), (byte[])(await inspect.ExecuteScalarAsync().ConfigureAwait(false))!);
        var availability = await migrated.ReadCaptureAvailabilityAsync(_root!, Guid.NewGuid(), scope, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.Contains(availability.UnavailableSources.ToArray(), "sqm");
        inspect.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(5L, await inspect.ExecuteScalarAsync().ConfigureAwait(false));
        inspect.CommandText = "SELECT COUNT(*) FROM environmental_location_held_polls;";
        Assert.AreEqual(0L, await inspect.ExecuteScalarAsync().ConfigureAwait(false));
        inspect.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('ix_environment_on_demand_retention','ix_environment_attempt_retention');";
        Assert.AreEqual(2L, await inspect.ExecuteScalarAsync().ConfigureAwait(false));
        inspect.CommandText = "EXPLAIN QUERY PLAN SELECT idempotency_key FROM environmental_on_demand_commands WHERE updated_unix_ms < 100 ORDER BY updated_unix_ms, idempotency_key LIMIT 1000;";
        using var plan = await inspect.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await plan.ReadAsync().ConfigureAwait(false));
        StringAssert.Contains(plan.GetString(3), "ix_environment_on_demand_retention", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UnpublishedSchemaFiveIsRefusedWithoutChangingItsPendingSlot()
    {
        using (var current = new SqliteEnvironmentalObservationStore())
            await current.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observations.db");
        var due = Epoch.AddTicks(5_000);
        using (var setup = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await setup.OpenAsync().ConfigureAwait(false);
            using var command = setup.CreateCommand();
            command.CommandText = """
                DROP TABLE environmental_location_held_polls;
                CREATE TABLE environmental_location_held_polls(
                    source_id TEXT NOT NULL PRIMARY KEY REFERENCES environmental_source_runtime(source_id) ON DELETE CASCADE,
                    source_type TEXT NOT NULL CHECK(length(source_type) BETWEEN 1 AND 128),
                    schedule_epoch_utc_ticks INTEGER NOT NULL CHECK(schedule_epoch_utc_ticks BETWEEN 0 AND 3155378975999999999),
                    period_seconds INTEGER NOT NULL CHECK(period_seconds > 0),
                    due_utc_ticks INTEGER NOT NULL CHECK(due_utc_ticks BETWEEN schedule_epoch_utc_ticks AND 3155378975999999999)) STRICT;
                INSERT INTO environmental_source_runtime(source_id, observation_kind, required, consecutive_failures)
                    VALUES('sqm', 'SkyQuality', 1, 0);
                INSERT INTO environmental_location_held_polls VALUES('sqm', 'SkyQualityMeter', $epoch, 1200, $due);
                """;
            command.Parameters.AddWithValue("$epoch", Epoch.UtcTicks);
            command.Parameters.AddWithValue("$due", due.UtcTicks);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var before = ReadDatabaseFiles(databasePath);
        using var rejected = new SqliteEnvironmentalObservationStore();
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await rejected.InitializeAsync(_root!, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        StringAssert.Contains(error.Message, "state-disposition", StringComparison.Ordinal);
        AssertDatabaseFilesUnchanged(before, ReadDatabaseFiles(databasePath));
    }

    [TestMethod]
    public async Task MalformedVersionThreeIsRejectedBeforeIndexMigrationWithoutMutation()
    {
        using (var current = new SqliteEnvironmentalObservationStore())
            await current.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        var path = Path.Combine(_root!, ".environment", "environmental-observations.db");
        using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await setup.OpenAsync().ConfigureAwait(false);
            using var change = setup.CreateCommand();
            change.CommandText = "DROP TABLE environmental_location_held_polls; DROP INDEX ix_environment_on_demand_retention; DROP INDEX ix_environment_attempt_retention; DROP INDEX ix_environment_attempt_source; PRAGMA user_version=3;";
            await change.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var before = ReadDatabaseFiles(path);
        using var rejected = new SqliteEnvironmentalObservationStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await rejected.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        AssertDatabaseFilesUnchanged(before, ReadDatabaseFiles(path));
    }

    [TestMethod]
    public async Task SchemaUsesWalStrictVersionedFullDurabilityWithoutCredentialColumns()
    {
        using var outbox = new SqliteEnvironmentalObservationStore();
        await outbox.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observations.db");
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(5L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA journal_mode;";
        Assert.AreEqual("wal", (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA synchronous;";
        Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'environmental_observation_journal';";
        var journalSchema = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        StringAssert.Contains(journalSchema, "STRICT", StringComparison.Ordinal);
        StringAssert.Contains(journalSchema, "payload_bytes = length(payload)", StringComparison.Ordinal);
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('environmental_observation_journal');";
        var journalColumns = (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        Assert.IsFalse(journalColumns.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(6)]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The switch selects one of two fixed test statements.")]
    public async Task UnsupportedVersionMarkerIsRejectedWithoutMutation(int version)
    {
        using (var current = new SqliteEnvironmentalObservationStore())
        {
            await current.CommitLocalAsync(
                _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observations.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = version switch
            {
                1 => "PRAGMA user_version=1;",
                6 => "PRAGMA user_version=6;",
                _ => throw new InvalidOperationException("Unsupported test schema version.")
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var filesBefore = ReadDatabaseFiles(databasePath);

        using var rejected = new SqliteEnvironmentalObservationStore();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await rejected.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        AssertDatabaseFilesUnchanged(filesBefore, ReadDatabaseFiles(databasePath));
    }

    [TestMethod]
    public async Task UnsupportedVersionInWalIsRejectedWithoutChangingRecoveryFiles()
    {
        using (var current = new SqliteEnvironmentalObservationStore())
        {
            await current.CommitLocalAsync(
                _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observations.db");
        using var setup = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await setup.OpenAsync().ConfigureAwait(false);
        const int sqliteDbConfigNoCheckpointOnClose = 1006;
        var result = SQLitePCL.raw.sqlite3_db_config(
            setup.Handle, sqliteDbConfigNoCheckpointOnClose, 1, out var enabled);
        Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, result);
        Assert.AreEqual(1, enabled);
        using (var command = setup.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0; PRAGMA user_version=6;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var filesBefore = ReadDatabaseFiles(databasePath);
        Assert.IsTrue(filesBefore.ContainsKey(Path.GetFileName($"{databasePath}-wal")));
        Assert.IsTrue(filesBefore.ContainsKey(Path.GetFileName($"{databasePath}-shm")));

        using var rejected = new SqliteEnvironmentalObservationStore();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await rejected.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        AssertDatabaseFilesUnchanged(filesBefore, ReadDatabaseFiles(databasePath));
    }

    [TestMethod]
    public async Task MalformedCurrentSchemaIsRejectedWithoutRepair()
    {
        using (var current = new SqliteEnvironmentalObservationStore())
        {
            await current.CommitLocalAsync(
                _root!, CreateFact(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        }
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_root!, ".environment", "environmental-observations.db");
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        const int sqliteDbConfigNoCheckpointOnClose = 1006;
        var result = SQLitePCL.raw.sqlite3_db_config(
            connection.Handle, sqliteDbConfigNoCheckpointOnClose, 1, out var enabled);
        Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, result);
        Assert.AreEqual(1, enabled);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA wal_autocheckpoint=0;
                CREATE TRIGGER unexpected_environment_trigger
                AFTER INSERT ON environmental_observation_journal
                BEGIN
                    SELECT 1;
                END;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var filesBefore = ReadDatabaseFiles(databasePath);

        using var rejected = new SqliteEnvironmentalObservationStore();
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await rejected.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
        StringAssert.Contains(exception.Message, "archive the database", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "state-disposition", StringComparison.Ordinal);
        AssertDatabaseFilesUnchanged(filesBefore, ReadDatabaseFiles(databasePath));

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'unexpected_environment_trigger';";
        Assert.AreEqual(1L, await read.ExecuteScalarAsync().ConfigureAwait(false));
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
            using var outbox = new SqliteEnvironmentalObservationStore();
            await Assert.ThrowsAsync<IOException>(async () =>
                await outbox.CommitLocalAsync(_root!, CreateFact(Guid.NewGuid()), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    private static EnvironmentalObservationFactV1 CreateFact(Guid observationId)
    {
        var observation = CreateLegacyFixtureObservation(observationId);
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
            observation.Lineage,
            observation.Target.RigId);
    }

    private static EnvironmentalObservationV1 CreateLegacyFixtureObservation(Guid observationId)
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

    private static Dictionary<string, byte[]> ReadDatabaseFiles(string databasePath)
        => new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm", $"{databasePath}-journal" }
            .Where(File.Exists)
            .ToDictionary(static path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertDatabaseFilesUnchanged(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var file in expected)
        {
            CollectionAssert.AreEqual(file.Value, actual[file.Key], file.Key);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

}
