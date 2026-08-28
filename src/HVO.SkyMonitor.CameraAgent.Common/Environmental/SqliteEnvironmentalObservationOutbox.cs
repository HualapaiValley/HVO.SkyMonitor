using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

/// <summary>Bounded SQLite WAL delivery state for fully enriched environmental observations.</summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite field getters read buffered values after asynchronous row reads.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Dynamic SQL inputs are private settlement constants or validated numeric configuration; record values remain parameterized.")]
public sealed class SqliteEnvironmentalObservationOutbox(
    TimeProvider? timeProvider = null,
    int busyTimeoutSeconds = 5,
    int maximumRecords = 10_000,
    long maximumBytes = 64L * 1024 * 1024,
    IEnvironmentalObservationOutboxFaultInjector? faultInjector = null,
    int maximumLocalRecords = 100_000,
    long maximumLocalBytes = 256L * 1024 * 1024,
    int localRetentionDays = 31,
    int localRetentionBatchSize = 1_000,
    EnvironmentalAcquisitionTelemetry? acquisitionTelemetry = null) : IEnvironmentalObservationOutbox, ILocalEnvironmentalObservationStore,
    IEnvironmentalObservationProjectionStore, ILocalEnvironmentalAssociationStore, IEnvironmentalAcquisitionStateStore,
    IEnvironmentalOnDemandCommandStore, ILocalEnvironmentalRetentionStore, IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CanonicalSchemaDefinitions =
        new(CreateCanonicalSchemaDefinitions);
    private const int MaximumAuditRecords = 10_000;
    internal const int MaximumOperationReceipts = 10_000;
    internal const int OperationReceiptRetentionDays = 30;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IEnvironmentalObservationOutboxFaultInjector _faultInjector =
        faultInjector ?? NullEnvironmentalObservationOutboxFaultInjector.Instance;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initializationGates = new(PathComparer);
    private readonly HashSet<string> _initializedRoots = new(PathComparer);
    private readonly object _initializedLock = new();

    public ValueTask<LocalEnvironmentalObservationCommitResult> CommitLocalAsync(
        string root,
        EnvironmentalObservationFactV1 fact,
        CancellationToken cancellationToken)
        => CommitLocalAsync(root, fact, null, cancellationToken);

    public async ValueTask<LocalEnvironmentalObservationCommitResult> CommitLocalAsync(
        string root,
        EnvironmentalObservationFactV1 fact,
        EnvironmentalObservationResolvedTarget? deliveryTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var validation = EnvironmentalObservationFactJson.Validate(fact);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Environmental observation fact is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(fact));
        }
        var canonical = EnvironmentalObservationFactJson.Parse(EnvironmentalObservationFactJson.Serialize(fact)).Fact
            ?? throw new InvalidDataException("Canonical targetless environmental observation could not be parsed.");
        var payload = EnvironmentalObservationFactJson.Serialize(canonical);
        var sourceIdentity = EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(canonical);
        var sourceContentIdentity = EnvironmentalObservationFactJson.ComputeSourceContentSha256(canonical);
        var contentIdentity = EnvironmentalObservationFactJson.ComputeContentSha256(canonical);
        if (canonical.Lineage.Any(reference => reference.ObservationId == canonical.ObservationId &&
            string.Equals(reference.SourceIdentitySha256, sourceIdentity, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("An environmental observation cannot reference itself.", nameof(fact));
        }
        deliveryTarget = deliveryTarget is null
            ? null
            : deliveryTarget with { RigId = fact.RigId ?? deliveryTarget.RigId };
        if (deliveryTarget is not null &&
            (deliveryTarget.ObservatoryId == Guid.Empty || deliveryTarget.DevicePublicId == Guid.Empty ||
                deliveryTarget.RigId is { Length: > 128 } ||
                deliveryTarget.RigId is not null && deliveryTarget.RigId != deliveryTarget.RigId.Trim()))
        {
            throw new ArgumentException("The environmental delivery target is invalid.", nameof(deliveryTarget));
        }

        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        var rawIngressAttached = await AttachRawIngressAsync(connection, root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT record_id, content_sha256 FROM environmental_observation_journal WHERE source_identity_sha256 = $source AND observation_id = $observation;";
            existing.Parameters.AddWithValue("$source", sourceIdentity);
            existing.Parameters.AddWithValue("$observation", canonical.ObservationId.ToString("D"));
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var recordId = reader.GetInt64(0);
                var committedHash = reader.GetString(1);
                await reader.DisposeAsync().ConfigureAwait(false);
                if (!string.Equals(committedHash, contentIdentity, StringComparison.Ordinal))
                {
                    throw new EnvironmentalObservationIdentityConflictException(
                        "A different targetless environmental payload already uses this source and observation identity.");
                }
                var record = await ReadLocalRecordAsync(connection, transaction, recordId, cancellationToken)
                    .ConfigureAwait(false);
                await FreezeDeliveryTargetAsync(
                    connection, transaction, recordId, deliveryTarget, cancellationToken).ConfigureAwait(false);
                var projection = await EnsureProjectionAsync(
                    connection, transaction, record, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LocalEnvironmentalObservationCommitResult(
                    LocalEnvironmentalObservationCommitDisposition.Duplicate,
                    record,
                    projection.Disposition,
                    projection.Observation,
                    projection.EnqueueDisposition);
            }
        }
        using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "SELECT source_content_sha256 FROM environmental_observation_journal WHERE source_identity_sha256 = $source LIMIT 1;";
            source.Parameters.AddWithValue("$source", sourceIdentity);
            var existingSourceContent = await source.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (existingSourceContent is not null && !string.Equals(
                Convert.ToString(existingSourceContent, System.Globalization.CultureInfo.InvariantCulture),
                sourceContentIdentity,
                StringComparison.Ordinal))
            {
                throw new EnvironmentalObservationIdentityConflictException(
                    "The environmental source identity already has different immutable source content.");
            }
        }

        var now = _timeProvider.GetUtcNow();
        long storedCount;
        long storedBytes;
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = "SELECT stored_count, stored_bytes FROM environmental_observation_journal_metadata WHERE metadata_key = 1;";
            using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            storedCount = reader.GetInt64(0);
            storedBytes = reader.GetInt64(1);
        }
        if (storedCount >= maximumLocalRecords || storedBytes > maximumLocalBytes - payload.Length)
        {
            _ = await RetainLocalCoreAsync(
                connection,
                transaction,
                now,
                now.AddDays(-localRetentionDays),
                localRetentionBatchSize,
                forcePressure: true,
                rawIngressAttached,
                cancellationToken).ConfigureAwait(false);
            using (var capacity = connection.CreateCommand())
            {
                capacity.Transaction = transaction;
                capacity.CommandText = "SELECT stored_count, stored_bytes FROM environmental_observation_journal_metadata WHERE metadata_key = 1;";
                using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                storedCount = reader.GetInt64(0);
                storedBytes = reader.GetInt64(1);
            }
        }
        if (storedCount >= maximumLocalRecords || storedBytes > maximumLocalBytes - payload.Length)
        {
            using var overflow = connection.CreateCommand();
            overflow.Transaction = transaction;
            overflow.CommandText = "UPDATE environmental_observation_journal_metadata SET overflow_count = overflow_count + 1 WHERE metadata_key = 1;";
            await overflow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new LocalEnvironmentalObservationCapacityException(
                "The local environmental observation journal reached its configured count or byte capacity.");
        }

        var recordedUnixMs = now.ToUnixTimeMilliseconds();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO environmental_observation_journal(
                    source_identity_sha256, source_content_sha256, observation_id, content_sha256,
                    schema_version, observation_kind, rig_id, source_kind, quality, observed_at_unix_ms,
                    valid_from_unix_ms, valid_through_unix_ms, stale_after_unix_ms,
                    payload, payload_bytes, recorded_unix_ms,
                    central_target_site_id, central_target_agent_id, central_target_rig_id)
                VALUES($source, $sourceContent, $observation, $content, $schema, $kind, $rig, $sourceKind,
                    $quality, $observed, $validFrom, $validThrough, $staleAfter, $payload, $bytes, $recorded,
                    $targetSite, $targetAgent, $targetRig);
                """;
            AddLocalRecordParameters(
                insert, canonical, sourceIdentity, sourceContentIdentity, contentIdentity, payload, recordedUnixMs);
            AddDeliveryTargetParameters(insert, deliveryTarget);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_journal_metadata SET stored_count = stored_count + 1, stored_bytes = stored_bytes + $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payload.Length);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long localRecordId;
        using (var identity = connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = "SELECT last_insert_rowid();";
            localRecordId = Convert.ToInt64(
                await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        var committed = await ReadLocalRecordAsync(connection, transaction, localRecordId, cancellationToken)
            .ConfigureAwait(false);
        await LinkLineageAsync(connection, transaction, committed, cancellationToken).ConfigureAwait(false);
        var committedProjection = await EnsureProjectionAsync(
            connection, transaction, committed, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LocalEnvironmentalObservationCommitResult(
            LocalEnvironmentalObservationCommitDisposition.Committed,
            committed,
            committedProjection.Disposition,
            committedProjection.Observation,
            committedProjection.EnqueueDisposition);
    }

    public async ValueTask<LocalEnvironmentalObservationSnapshot> GetLocalSnapshotAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stored_count, stored_bytes, overflow_count,
                (SELECT MIN(recorded_unix_ms) FROM environmental_observation_journal)
            FROM environmental_observation_journal_metadata WHERE metadata_key = 1;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Local environmental observation metadata is missing.");
        }
        return new LocalEnvironmentalObservationSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            _timeProvider.GetUtcNow());
    }

    public async ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
        string root,
        DateTimeOffset recordedBeforeUtc,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (recordedBeforeUtc == default || recordedBeforeUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The environmental retention cutoff must be UTC.", nameof(recordedBeforeUtc));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 10_000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        var rawIngressAttached = await AttachRawIngressAsync(connection, root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = await RetainLocalCoreAsync(
            connection,
            transaction,
            _timeProvider.GetUtcNow(),
            recordedBeforeUtc,
            maximumResults,
            forcePressure: false,
            rawIngressAttached,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<LocalEnvironmentalObservationPage> ReadLocalPageAsync(
        string root,
        EnvironmentalObservationKind? kind,
        int pageSize,
        LocalEnvironmentalObservationCursor? cursor,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        using var activity = EnvironmentalAcquisitionTelemetry.ActivitySource.StartActivity("environment.history.query");
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        if (kind is { } requestedKind && !Enum.IsDefined(requestedKind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (cursor is not null && (cursor.RecordId < 1 || cursor.ObservedAtUtc.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("The local environmental history cursor is invalid.", nameof(cursor));
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT record_id FROM environmental_observation_journal
            WHERE ($kind IS NULL OR observation_kind = $kind)
                AND ($cursorObserved IS NULL OR observed_at_unix_ms < $cursorObserved
                    OR (observed_at_unix_ms = $cursorObserved AND record_id < $cursorRecord))
            ORDER BY observed_at_unix_ms DESC, record_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : kind.Value.ToString());
        command.Parameters.AddWithValue("$cursorObserved", cursor is null
            ? DBNull.Value
            : cursor.ObservedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$cursorRecord", cursor?.RecordId ?? long.MaxValue);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var recordIds = new List<long>(pageSize + 1);
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                recordIds.Add(reader.GetInt64(0));
            }
        }
        var hasMore = recordIds.Count > pageSize;
        if (hasMore)
        {
            recordIds.RemoveAt(recordIds.Count - 1);
        }
        var records = new List<LocalEnvironmentalObservationRecord>(recordIds.Count);
        foreach (var recordId in recordIds)
        {
            records.Add(await ReadLocalRecordAsync(connection, transaction, recordId, cancellationToken)
                .ConfigureAwait(false));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var next = hasMore && records.Count > 0
            ? new LocalEnvironmentalObservationCursor(records[^1].Fact.ObservedAtUtc, records[^1].RecordId)
            : null;
        acquisitionTelemetry?.RecordHistoryQuery(kind, _timeProvider.GetElapsedTime(started));
        return new LocalEnvironmentalObservationPage(records, next);
    }

    public async ValueTask<LocalEnvironmentalObservationRecord?> ReadLocalDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM environmental_observation_journal WHERE record_id = $id;";
        exists.Parameters.AddWithValue("$id", recordId);
        if (Convert.ToInt32(
            await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var record = await ReadLocalRecordAsync(connection, transaction, recordId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async ValueTask<IReadOnlyList<LocalEnvironmentalObservationRecord>> ReadLocalCandidatesAsync(
        string root,
        EnvironmentalObservationKind kind,
        string? rigId,
        DateTimeOffset intervalFromUtc,
        DateTimeOffset intervalThroughUtc,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind) || intervalFromUtc.Offset != TimeSpan.Zero || intervalThroughUtc.Offset != TimeSpan.Zero ||
            intervalFromUtc >= intervalThroughUtc || rigId is { Length: > 128 } ||
            rigId is not null && rigId != rigId.Trim())
        {
            throw new ArgumentException("The environmental association interval is invalid.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT record_id FROM environmental_observation_journal
            WHERE observation_kind = $kind AND valid_from_unix_ms < $through
                AND valid_through_unix_ms > $from
                AND (rig_id IS NULL OR rig_id = $rig)
            ORDER BY observed_at_unix_ms DESC, source_identity_sha256, observation_id, record_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$rig", (object?)rigId ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", intervalFromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$through", intervalThroughUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", maximumResults);
        var recordIds = new List<long>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                recordIds.Add(reader.GetInt64(0));
            }
        }
        var records = new List<LocalEnvironmentalObservationRecord>(recordIds.Count);
        foreach (var recordId in recordIds)
        {
            records.Add(await ReadLocalRecordAsync(connection, transaction, recordId, cancellationToken)
                .ConfigureAwait(false));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async ValueTask<int> ProjectWaitingAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1_000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var projected = 0;
        for (var index = 0; index < maximumResults; index++)
        {
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            long? localRecordId;
            using (var candidate = connection.CreateCommand())
            {
                candidate.Transaction = transaction;
                candidate.CommandText = """
                    SELECT local_record_id FROM environmental_observation_central_projection
                    WHERE status = 'waiting'
                    ORDER BY updated_unix_ms, local_record_id
                    LIMIT 1;
                    """;
                var value = await candidate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                localRecordId = value is null
                    ? null
                    : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (localRecordId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            var record = await ReadLocalRecordAsync(
                connection, transaction, localRecordId.Value, cancellationToken).ConfigureAwait(false);
            var result = await EnsureProjectionAsync(connection, transaction, record, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Disposition != EnvironmentalObservationProjectionDisposition.Staged)
            {
                break;
            }
            projected++;
        }
        return projected;
    }

    public async ValueTask<int> AssignUnprojectedAsync(
        string root,
        EnvironmentalObservationResolvedTarget target,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ObservatoryId == Guid.Empty || target.DevicePublicId == Guid.Empty ||
            target.RigId is { Length: > 128 } || target.RigId is not null && target.RigId != target.RigId.Trim())
        {
            throw new ArgumentException("The environmental delivery target is invalid.", nameof(target));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1_000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var recordIds = new List<long>(maximumResults);
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT record_id FROM environmental_observation_journal
                WHERE central_target_site_id IS NULL
                ORDER BY recorded_unix_ms, record_id
                LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$limit", maximumResults);
            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                recordIds.Add(reader.GetInt64(0));
            }
        }
        foreach (var recordId in recordIds)
        {
            var record = await ReadLocalRecordAsync(connection, transaction, recordId, cancellationToken)
                .ConfigureAwait(false);
            var effectiveTarget = target with { RigId = record.Fact.RigId ?? target.RigId };
            await FreezeDeliveryTargetAsync(
                connection, transaction, recordId, effectiveTarget, cancellationToken).ConfigureAwait(false);
            _ = await EnsureProjectionAsync(connection, transaction, record, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recordIds.Count;
    }

    public ValueTask SaveAssociationAsync(
        string root,
        LocalEnvironmentalCaptureAssociation association,
        CancellationToken cancellationToken)
        => SaveAssociationSetAsync(root, [association], cancellationToken);

    public async ValueTask SaveAssociationSetAsync(
        string root,
        IReadOnlyList<LocalEnvironmentalCaptureAssociation> associations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(associations);
        if (associations.Count == 0 || associations.Any(static association => association is null) ||
            associations.Select(static association => (association.CaptureId, association.Kind, association.PolicyIdentitySha256))
                .Distinct().Count() != associations.Count)
        {
            throw new ArgumentException("The local environmental capture association set is invalid.", nameof(associations));
        }
        foreach (var association in associations)
        {
            ValidateAssociation(association);
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var association in associations)
        {
            var referenced = association.SelectedRecordId is { } selected
                ? new[] { selected }.Concat(association.ConflictingRecordIds).ToArray()
                : association.ConflictingRecordIds.ToArray();
            var evidence = new List<(long RecordId, string SourceIdentity, string ObservationId, string ContentIdentity)>(
                referenced.Length);
            foreach (var recordId in referenced)
            {
                using var verify = connection.CreateCommand();
                verify.Transaction = transaction;
                verify.CommandText = "SELECT observation_kind, source_identity_sha256, observation_id, content_sha256 FROM environmental_observation_journal WHERE record_id = $id;";
                verify.Parameters.AddWithValue("$id", recordId);
                using var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    !string.Equals(reader.GetString(0), association.Kind.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Environmental association references missing or incompatible local history.");
                }
                evidence.Add((recordId, reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT association_identity_sha256 FROM environmental_capture_associations WHERE capture_id = $capture AND observation_kind = $kind AND policy_identity_sha256 = $policy;";
                existing.Parameters.AddWithValue("$capture", association.CaptureId.ToString("D"));
                existing.Parameters.AddWithValue("$kind", association.Kind.ToString());
                existing.Parameters.AddWithValue("$policy", association.PolicyIdentitySha256);
                var value = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is not null)
                {
                    if (!string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                        association.AssociationIdentitySha256, StringComparison.Ordinal))
                    {
                        throw new EnvironmentalObservationIdentityConflictException(
                            "A different environmental association already uses this capture, kind, and policy identity.");
                    }
                    continue;
                }
            }
            var conflicts = JsonSerializer.SerializeToUtf8Bytes(association.ConflictingRecordIds);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO environmental_capture_associations(
                    capture_id, capture_sequence, observation_kind, rig_id, exposure_from_unix_ms,
                    exposure_through_unix_ms, policy_identity_sha256, status, selected_record_id,
                    conflicting_record_ids_json, association_identity_sha256, created_unix_ms)
                VALUES($capture, $sequence, $kind, $rig, $from, $through, $policy, $status, $selected,
                    $conflicts, $identity, $created);
                """;
            insert.Parameters.AddWithValue("$capture", association.CaptureId.ToString("D"));
            insert.Parameters.AddWithValue("$sequence", association.CaptureSequence);
            insert.Parameters.AddWithValue("$kind", association.Kind.ToString());
            insert.Parameters.AddWithValue("$rig", (object?)association.RigId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$from", association.ExposureFromUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$through", association.ExposureThroughUtc.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$policy", association.PolicyIdentitySha256);
            insert.Parameters.AddWithValue("$status", association.Status.ToString());
            insert.Parameters.AddWithValue("$selected", DbValue(association.SelectedRecordId));
            insert.Parameters.AddWithValue("$conflicts", conflicts);
            insert.Parameters.AddWithValue("$identity", association.AssociationIdentitySha256);
            insert.Parameters.AddWithValue("$created", association.CreatedUtc.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            long associationId;
            using (var identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = "SELECT last_insert_rowid();";
                associationId = Convert.ToInt64(
                    await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            for (var ordinal = 0; ordinal < evidence.Count; ordinal++)
            {
                var item = evidence[ordinal];
                using var reference = connection.CreateCommand();
                reference.Transaction = transaction;
                reference.CommandText = """
                    INSERT INTO environmental_capture_association_evidence(
                        association_id, ordinal, role, local_record_id, source_identity_sha256,
                        observation_id, content_sha256)
                    VALUES($association, $ordinal, $role, $record, $source, $observation, $content);
                    """;
                reference.Parameters.AddWithValue("$association", associationId);
                reference.Parameters.AddWithValue("$ordinal", ordinal);
                reference.Parameters.AddWithValue("$role", association.SelectedRecordId.HasValue ? "selected" : "conflict");
                reference.Parameters.AddWithValue("$record", item.RecordId);
                reference.Parameters.AddWithValue("$source", item.SourceIdentity);
                reference.Parameters.AddWithValue("$observation", item.ObservationId);
                reference.Parameters.AddWithValue("$content", item.ContentIdentity);
                await reference.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<LocalEnvironmentalCaptureAssociation>> ReadAssociationsAsync(
        string root,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty)
        {
            throw new ArgumentException("The capture identifier is required.", nameof(captureId));
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capture_sequence, observation_kind, rig_id, exposure_from_unix_ms, exposure_through_unix_ms,
                policy_identity_sha256, status, selected_record_id, conflicting_record_ids_json,
                association_identity_sha256, created_unix_ms
            FROM environmental_capture_associations
            WHERE capture_id = $capture
            ORDER BY observation_kind, policy_identity_sha256;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("D"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LocalEnvironmentalCaptureAssociation>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var conflicts = JsonSerializer.Deserialize<long[]>((byte[])reader.GetValue(8))
                ?? throw new InvalidDataException("Environmental association conflict references are invalid.");
            results.Add(new LocalEnvironmentalCaptureAssociation(
                captureId,
                reader.GetInt64(0),
                Enum.Parse<EnvironmentalObservationKind>(reader.GetString(1), ignoreCase: false),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.GetString(5),
                Enum.Parse<LocalEnvironmentalAssociationStatus>(reader.GetString(6), ignoreCase: false),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                conflicts,
                reader.GetString(9),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10))));
        }
        return results;
    }

    public async ValueTask RecordAttemptAsync(
        string root,
        EnvironmentalSourceDescriptor source,
        EnvironmentalAcquisitionReceipt receipt,
        long? captureSequence,
        Guid? captureId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 128 ||
            !string.Equals(source.Id, receipt.SourceId, StringComparison.Ordinal) ||
            !Enum.IsDefined(source.Kind) || !Enum.IsDefined(receipt.Trigger) || !Enum.IsDefined(receipt.Disposition) ||
            string.IsNullOrWhiteSpace(receipt.Reason) || receipt.Reason.Length > 128 ||
            receipt.StartedUtc.Offset != TimeSpan.Zero || receipt.CompletedUtc.Offset != TimeSpan.Zero ||
            receipt.CompletedUtc < receipt.StartedUtc || captureSequence.HasValue != captureId.HasValue ||
            captureSequence < 1 || captureId == Guid.Empty ||
            receipt.ObservedAtUtc.HasValue != receipt.StaleAfterUtc.HasValue ||
            receipt.ObservedAtUtc is { } observedAtUtc && observedAtUtc.Offset != TimeSpan.Zero ||
            receipt.StaleAfterUtc is { } staleAfterUtc && staleAfterUtc.Offset != TimeSpan.Zero ||
            receipt.ObservationId.HasValue != receipt.ObservedAtUtc.HasValue)
        {
            throw new ArgumentException("The environmental acquisition attempt is invalid.", nameof(receipt));
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO environmental_acquisition_attempts(
                    source_id, observation_kind, required, trigger, outcome, reason, observation_id,
                    capture_sequence, capture_id, started_unix_ms, completed_unix_ms)
                VALUES($source, $kind, $required, $trigger, $outcome, $reason, $observation,
                    $sequence, $capture, $started, $completed);
                """;
            AddAttemptParameters(insert, source, receipt, captureSequence, captureId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var runtime = connection.CreateCommand())
        {
            runtime.Transaction = transaction;
            runtime.CommandText = """
                INSERT INTO environmental_source_runtime(
                    source_id, observation_kind, required, last_outcome, last_reason,
                    last_observation_id, last_observed_unix_ms, last_stale_after_unix_ms,
                    last_started_unix_ms, last_completed_unix_ms,
                    consecutive_failures, next_poll_unix_ms)
                VALUES($source, $kind, $required, $outcome, $reason, $observation, $observed, $staleAfter,
                    $started, $completed,
                    CASE WHEN $outcome IN ('Produced','Duplicate','Coalesced') THEN 0 ELSE 1 END, NULL)
                ON CONFLICT(source_id) DO UPDATE SET
                    observation_kind = excluded.observation_kind,
                    required = excluded.required,
                    last_outcome = excluded.last_outcome,
                    last_reason = excluded.last_reason,
                    last_observation_id = COALESCE(excluded.last_observation_id, environmental_source_runtime.last_observation_id),
                    last_observed_unix_ms = COALESCE(excluded.last_observed_unix_ms, environmental_source_runtime.last_observed_unix_ms),
                    last_stale_after_unix_ms = COALESCE(excluded.last_stale_after_unix_ms, environmental_source_runtime.last_stale_after_unix_ms),
                    last_started_unix_ms = excluded.last_started_unix_ms,
                    last_completed_unix_ms = excluded.last_completed_unix_ms,
                    consecutive_failures = CASE
                        WHEN excluded.last_outcome IN ('Produced','Duplicate') THEN 0
                        WHEN excluded.last_outcome = 'Coalesced' THEN environmental_source_runtime.consecutive_failures
                        ELSE environmental_source_runtime.consecutive_failures + 1 END;
                """;
            AddAttemptParameters(runtime, source, receipt, captureSequence, captureId);
            await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateSourceScheduleAsync(
        string root,
        EnvironmentalSourceDescriptor source,
        DateTimeOffset nextPollUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 128 || !Enum.IsDefined(source.Kind) ||
            nextPollUtc == default || nextPollUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The environmental source schedule is invalid.", nameof(source));
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO environmental_source_runtime(
                source_id, observation_kind, required, last_outcome, last_reason,
                last_observation_id, last_observed_unix_ms, last_stale_after_unix_ms,
                last_started_unix_ms, last_completed_unix_ms,
                consecutive_failures, next_poll_unix_ms)
            VALUES($source, $kind, $required, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, $next)
            ON CONFLICT(source_id) DO UPDATE SET
                observation_kind = excluded.observation_kind,
                required = excluded.required,
                next_poll_unix_ms = excluded.next_poll_unix_ms;
            """;
        command.Parameters.AddWithValue("$source", source.Id);
        command.Parameters.AddWithValue("$kind", source.Kind.ToString());
        command.Parameters.AddWithValue("$required", source.Required ? 1 : 0);
        command.Parameters.AddWithValue("$next", nextPollUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RecordCaptureRegimeAsync(
        string root,
        long captureSequence,
        Guid captureId,
        CaptureSolarRegime regime,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (captureSequence < 1 || captureId == Guid.Empty || !Enum.IsDefined(regime) ||
            observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The environmental capture regime state is invalid.");
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT capture_sequence, capture_id, solar_regime FROM environmental_capture_regime_state WHERE state_key = 1;";
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var previousSequence = reader.GetInt64(0);
                var previousCaptureId = Guid.Parse(reader.GetString(1));
                var previousRegime = Enum.Parse<CaptureSolarRegime>(reader.GetString(2), ignoreCase: false);
                if (captureSequence < previousSequence ||
                    captureSequence == previousSequence && captureId == previousCaptureId)
                {
                    if (captureSequence == previousSequence && captureId == previousCaptureId && previousRegime != regime)
                    {
                        throw new EnvironmentalObservationIdentityConflictException(
                            "The durable capture regime conflicts with the replayed capture.");
                    }
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }
                if (captureSequence == previousSequence)
                {
                    throw new EnvironmentalObservationIdentityConflictException(
                        "The durable capture sequence identifies a different capture.");
                }
                await reader.DisposeAsync().ConfigureAwait(false);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE environmental_capture_regime_state
                    SET capture_sequence = $sequence, capture_id = $capture, solar_regime = $regime,
                        observed_unix_ms = $observed
                    WHERE state_key = 1;
                    """;
                update.Parameters.AddWithValue("$sequence", captureSequence);
                update.Parameters.AddWithValue("$capture", captureId.ToString("D"));
                update.Parameters.AddWithValue("$regime", regime.ToString());
                update.Parameters.AddWithValue("$observed", observedAtUtc.ToUnixTimeMilliseconds());
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return previousRegime != regime;
            }
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO environmental_capture_regime_state(
                state_key, capture_sequence, capture_id, solar_regime, observed_unix_ms)
            VALUES(1, $sequence, $capture, $regime, $observed);
            """;
        insert.Parameters.AddWithValue("$sequence", captureSequence);
        insert.Parameters.AddWithValue("$capture", captureId.ToString("D"));
        insert.Parameters.AddWithValue("$regime", regime.ToString());
        insert.Parameters.AddWithValue("$observed", observedAtUtc.ToUnixTimeMilliseconds());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, observation_kind, required, last_outcome, last_reason,
                last_observation_id, last_observed_unix_ms, last_stale_after_unix_ms,
                last_started_unix_ms, last_completed_unix_ms,
                consecutive_failures, next_poll_unix_ms
            FROM environmental_source_runtime ORDER BY source_id;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<EnvironmentalSourceRuntimeState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new EnvironmentalSourceRuntimeState(
                reader.GetString(0),
                Enum.Parse<EnvironmentalObservationKind>(reader.GetString(1), ignoreCase: false),
                reader.GetInt64(2) == 1,
                reader.IsDBNull(3)
                    ? null
                    : Enum.Parse<EnvironmentalAcquisitionDisposition>(reader.GetString(3), ignoreCase: false),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11))));
        }
        return results;
    }

    public async ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1_000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_id, source_id, observation_kind, required, trigger, outcome, reason,
                observation_id, capture_sequence, capture_id, started_unix_ms, completed_unix_ms
            FROM environmental_acquisition_attempts ORDER BY attempt_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumResults);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<EnvironmentalAcquisitionAttemptRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new EnvironmentalAcquisitionAttemptRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<EnvironmentalObservationKind>(reader.GetString(2), ignoreCase: false),
                reader.GetInt64(3) == 1,
                Enum.Parse<EnvironmentalAcquisitionTrigger>(reader.GetString(4), ignoreCase: false),
                Enum.Parse<EnvironmentalAcquisitionDisposition>(reader.GetString(5), ignoreCase: false),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11))));
        }
        return results;
    }

    public async ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
        string root,
        string idempotencyKey,
        string payloadSha256,
        string sourceId,
        string actorId,
        string? reason,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 || !IsSha256(payloadSha256) ||
            string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128 ||
            string.IsNullOrWhiteSpace(actorId) || actorId.Length > 256 || reason is { Length: > 128 } ||
            observedAtUtc.Offset != TimeSpan.Zero || leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("The environmental on-demand command claim is invalid.");
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow();
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT payload_sha256, observed_unix_ms, status, lease_expires_unix_ms, receipt_json
                FROM environmental_on_demand_commands WHERE idempotency_key = $key;
                """;
            existing.Parameters.AddWithValue("$key", idempotencyKey);
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), payloadSha256, StringComparison.Ordinal))
                {
                    throw new EnvironmentalOnDemandCommandConflictException(
                        "The environmental idempotency key already identifies a different command.");
                }
                var persistedObservedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                var status = reader.GetString(2);
                if (status == "completed")
                {
                    var receipt = JsonSerializer.Deserialize<EnvironmentalAcquisitionReceipt>((byte[])reader.GetValue(4))
                        ?? throw new InvalidDataException("The environmental command receipt is invalid.");
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new(EnvironmentalOnDemandClaimDisposition.Completed, string.Empty, persistedObservedAtUtc, receipt);
                }
                if (!reader.IsDBNull(3) && reader.GetInt64(3) > now.ToUnixTimeMilliseconds())
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new(EnvironmentalOnDemandClaimDisposition.Busy, string.Empty, persistedObservedAtUtc, null);
                }
                await reader.DisposeAsync().ConfigureAwait(false);
                var reclaimedToken = Guid.NewGuid().ToString("N");
                using var reclaim = connection.CreateCommand();
                reclaim.Transaction = transaction;
                reclaim.CommandText = """
                    UPDATE environmental_on_demand_commands
                    SET lease_token = $token, lease_expires_unix_ms = $expires, updated_unix_ms = $updated
                    WHERE idempotency_key = $key AND status = 'running';
                    """;
                reclaim.Parameters.AddWithValue("$token", reclaimedToken);
                reclaim.Parameters.AddWithValue("$expires", (now + leaseDuration).ToUnixTimeMilliseconds());
                reclaim.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
                reclaim.Parameters.AddWithValue("$key", idempotencyKey);
                await reclaim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(EnvironmentalOnDemandClaimDisposition.Claimed, reclaimedToken, persistedObservedAtUtc, null);
            }
        }
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM environmental_on_demand_commands;";
            var commandCount = Convert.ToInt32(
                await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (commandCount >= MaximumOperationReceipts)
            {
                throw new EnvironmentalOnDemandCommandCapacityException(
                    "The environmental on-demand command journal reached its bounded capacity.");
            }
        }
        var leaseToken = Guid.NewGuid().ToString("N");
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO environmental_on_demand_commands(
                idempotency_key, payload_sha256, source_id, actor_id, reason, observed_unix_ms,
                status, lease_token, lease_expires_unix_ms, receipt_json, updated_unix_ms)
            VALUES($key, $payload, $source, $actor, $reason, $observed,
                'running', $token, $expires, NULL, $updated);
            """;
        insert.Parameters.AddWithValue("$key", idempotencyKey);
        insert.Parameters.AddWithValue("$payload", payloadSha256);
        insert.Parameters.AddWithValue("$source", sourceId);
        insert.Parameters.AddWithValue("$actor", actorId);
        insert.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        insert.Parameters.AddWithValue("$observed", observedAtUtc.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$token", leaseToken);
        insert.Parameters.AddWithValue("$expires", (now + leaseDuration).ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(EnvironmentalOnDemandClaimDisposition.Claimed, leaseToken, observedAtUtc, null);
    }

    public async ValueTask CompleteOnDemandAsync(
        string root,
        string idempotencyKey,
        string leaseToken,
        EnvironmentalAcquisitionReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(leaseToken))
        {
            throw new ArgumentException("The environmental on-demand settlement is invalid.");
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE environmental_on_demand_commands
            SET status = 'completed', lease_token = NULL, lease_expires_unix_ms = NULL,
                receipt_json = $receipt, updated_unix_ms = $updated
            WHERE idempotency_key = $key AND status = 'running' AND lease_token = $token
                AND lease_expires_unix_ms > $updated;
            """;
        command.Parameters.AddWithValue("$receipt", JsonSerializer.SerializeToUtf8Bytes(receipt));
        command.Parameters.AddWithValue("$updated", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$token", leaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new EnvironmentalObservationLeaseLostException(
                "The environmental on-demand command lease was lost before settlement.");
        }
    }

    public async ValueTask ReleaseOnDemandAsync(
        string root,
        string idempotencyKey,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(leaseToken))
        {
            throw new ArgumentException("The environmental on-demand release is invalid.");
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE environmental_on_demand_commands
            SET lease_expires_unix_ms = $expired, updated_unix_ms = $expired
            WHERE idempotency_key = $key AND status = 'running' AND lease_token = $token;
            """;
        command.Parameters.AddWithValue("$expired", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$token", leaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new EnvironmentalObservationLeaseLostException(
                "The environmental on-demand command lease was lost before release.");
        }
    }

    public async ValueTask<EnvironmentalObservationEnqueueDisposition> EnqueueAsync(
        string root,
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var validation = EnvironmentalObservationJson.Validate(observation);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Environmental observation is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                nameof(observation));
        }
        if (observation.Target.AgentId is null)
        {
            throw new ArgumentException("Environmental delivery requires a device-scoped target.", nameof(observation));
        }
        var canonical = EnvironmentalObservationJson.Parse(EnvironmentalObservationJson.Serialize(observation)).Observation
            ?? throw new InvalidDataException("Canonical environmental observation could not be parsed.");
        var targetAgentId = canonical.Target.AgentId
            ?? throw new InvalidDataException("Canonical environmental observation lost its device target.");
        var payload = EnvironmentalObservationJson.Serialize(canonical);
        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(canonical);
        var contentIdentity = EnvironmentalObservationJson.ComputeContentSha256(canonical);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT record_id, content_sha256 FROM environmental_observation_outbox WHERE source_identity_sha256 = $source AND observation_id = $observation;";
            existing.Parameters.AddWithValue("$source", sourceIdentity);
            existing.Parameters.AddWithValue("$observation", canonical.ObservationId.ToString("D"));
            long? existingRecordId = null;
            string? existingHash = null;
            using (var existingReader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await existingReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingRecordId = existingReader.GetInt64(0);
                    existingHash = existingReader.GetString(1);
                }
            }
            if (existingRecordId is { } recordId)
            {
                if (!string.Equals(Convert.ToString(existingHash, System.Globalization.CultureInfo.InvariantCulture), contentIdentity, StringComparison.Ordinal))
                {
                    throw new EnvironmentalObservationIdentityConflictException(
                        "A different environmental payload already uses this local source and observation identity.");
                }
                _ = await ReadRecordAsync(connection, transaction, recordId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return EnvironmentalObservationEnqueueDisposition.Duplicate;
            }
        }

        long storedCount;
        long storedBytes;
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = "SELECT stored_count, stored_bytes FROM environmental_observation_metadata WHERE metadata_key = 1;";
            using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            storedCount = reader.GetInt64(0);
            storedBytes = reader.GetInt64(1);
        }
        if (storedCount >= maximumRecords || storedBytes > maximumBytes - payload.Length)
        {
            using var overflow = connection.CreateCommand();
            overflow.Transaction = transaction;
            overflow.CommandText = "UPDATE environmental_observation_metadata SET overflow_count = overflow_count + 1 WHERE metadata_key = 1;";
            await overflow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new EnvironmentalObservationOutboxCapacityException(
                "The environmental observation outbox reached its configured count or byte capacity.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO environmental_observation_outbox(
                    source_identity_sha256, observation_id, content_sha256, target_site_id, target_agent_id,
                    payload, payload_bytes, status, attempt_count, next_attempt_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES($source, $observation, $content, $site, $agent, $payload, $bytes,
                    'pending', 0, $now, $now, $now);
                """;
            insert.Parameters.AddWithValue("$source", sourceIdentity);
            insert.Parameters.AddWithValue("$observation", canonical.ObservationId.ToString("D"));
            insert.Parameters.AddWithValue("$content", contentIdentity);
            insert.Parameters.AddWithValue("$site", canonical.Target.SiteId.ToString("D"));
            insert.Parameters.AddWithValue("$agent", targetAgentId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$bytes", payload.Length);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count + 1, stored_bytes = stored_bytes + $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payload.Length);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await _faultInjector.BeforeEnqueueCommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _faultInjector.AfterEnqueueCommitAsync(cancellationToken).ConfigureAwait(false);
        return EnvironmentalObservationEnqueueDisposition.Enqueued;
    }

    public async ValueTask<EnvironmentalObservationOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        for (var malformedRecords = 0; malformedRecords < 100; malformedRecords++)
        {
            var now = _timeProvider.GetUtcNow();
            var expires = now + leaseDuration;
            var token = Guid.NewGuid().ToString("N");
            using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            long? recordId;
            using (var candidate = connection.CreateCommand())
            {
                candidate.Transaction = transaction;
                candidate.CommandText = """
                    SELECT record_id FROM (
                        SELECT * FROM (
                            SELECT record_id, next_attempt_unix_ms, created_unix_ms
                            FROM environmental_observation_outbox
                            WHERE status = 'pending'
                            ORDER BY next_attempt_unix_ms, created_unix_ms, record_id
                            LIMIT 1)
                        UNION ALL
                        SELECT * FROM (
                            SELECT record_id, next_attempt_unix_ms, created_unix_ms
                            FROM environmental_observation_outbox
                            WHERE status = 'retry' AND next_attempt_unix_ms <= $now
                            ORDER BY next_attempt_unix_ms, created_unix_ms, record_id
                            LIMIT 1)
                        UNION ALL
                        SELECT * FROM (
                            SELECT record_id, lease_expires_unix_ms, created_unix_ms
                            FROM environmental_observation_outbox
                            WHERE status = 'leased' AND lease_expires_unix_ms <= $now
                            ORDER BY lease_expires_unix_ms, created_unix_ms, record_id
                            LIMIT 1))
                    ORDER BY next_attempt_unix_ms, created_unix_ms, record_id
                    LIMIT 1;
                    """;
                candidate.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                var value = await candidate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                recordId = value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (recordId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE environmental_observation_outbox
                    SET status = 'leased', attempt_count = attempt_count + 1, lease_owner = $owner,
                        lease_token = $token, lease_expires_unix_ms = $expires, updated_unix_ms = $now
                    WHERE record_id = $id AND (status = 'pending'
                        OR (status = 'retry' AND next_attempt_unix_ms <= $now)
                        OR (status = 'leased' AND lease_expires_unix_ms <= $now));
                    """;
                update.Parameters.AddWithValue("$owner", Bound(owner, 128));
                update.Parameters.AddWithValue("$token", token);
                update.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$id", recordId.Value);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new EnvironmentalObservationLeaseLostException("Environmental observation claim changed before commit.");
                }
            }
            try
            {
                var record = await ReadRecordAsync(connection, transaction, recordId.Value, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new EnvironmentalObservationOutboxLease(record, Bound(owner, 128), token, expires);
            }
            catch (InvalidDataException)
            {
                using var quarantine = connection.CreateCommand();
                quarantine.Transaction = transaction;
                quarantine.CommandText = """
                    UPDATE environmental_observation_outbox
                    SET status = 'quarantined', last_reason = 'malformed-committed-record',
                        lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now
                    WHERE record_id = $id;
                    """;
                quarantine.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                quarantine.Parameters.AddWithValue("$id", recordId.Value);
                await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }

    public async ValueTask AcknowledgeAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        EnvironmentalObservationAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!EnvironmentalObservationDeliveryJson.Matches(acknowledgement, lease.Record.Observation))
        {
            throw new InvalidDataException("Environmental acknowledgement does not match the leased observation.");
        }
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM environmental_observation_outbox WHERE record_id = $id AND status = 'leased' AND lease_owner = $owner AND lease_token = $token AND lease_expires_unix_ms > $now;";
            delete.Parameters.AddWithValue("$id", lease.Record.RecordId);
            delete.Parameters.AddWithValue("$owner", lease.Owner);
            delete.Parameters.AddWithValue("$token", lease.Token);
            delete.Parameters.AddWithValue("$now", now);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new EnvironmentalObservationLeaseLostException(
                    "Environmental observation lease was lost before acknowledgement.");
            }
        }
        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", lease.Record.PayloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (lease.Record.LocalRecordId is { } localRecordId)
        {
            using var projection = connection.CreateCommand();
            projection.Transaction = transaction;
            projection.CommandText = """
                UPDATE environmental_observation_central_projection
                SET status = 'acknowledged', outbox_record_id = NULL,
                    acknowledged_unix_ms = $acknowledged, updated_unix_ms = $updated
                WHERE local_record_id = $local AND status = 'staged' AND outbox_record_id = $outbox;
                """;
            projection.Parameters.AddWithValue("$acknowledged", acknowledgement.ReceivedAtUtc.ToUnixTimeMilliseconds());
            projection.Parameters.AddWithValue("$updated", now);
            projection.Parameters.AddWithValue("$local", localRecordId);
            projection.Parameters.AddWithValue("$outbox", lease.Record.RecordId);
            if (await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("Environmental delivery projection changed before acknowledgement.");
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RetryAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        DateTimeOffset retryAtUtc,
        string reason,
        CancellationToken cancellationToken)
        => SettleAsync(root, lease, """
            UPDATE environmental_observation_outbox
            SET status = 'retry', next_attempt_unix_ms = $next, last_reason = $reason,
                lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now
            WHERE record_id = $id AND status = 'leased' AND lease_owner = $owner AND lease_token = $token
                AND lease_expires_unix_ms > $now;
            """, retryAtUtc, reason, cancellationToken);

    public ValueTask QuarantineAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
        => TerminalSettleAsync(root, lease, "quarantined", reason, cancellationToken);

    public ValueTask TerminalAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
        => TerminalSettleAsync(root, lease, "terminal", reason, cancellationToken);

    public async ValueTask<EnvironmentalObservationOutboxSnapshot> GetSnapshotAsync(
        string root,
        CancellationToken cancellationToken)
    {
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT stored_count FROM environmental_observation_metadata WHERE metadata_key = 1),
                (SELECT stored_bytes FROM environmental_observation_metadata WHERE metadata_key = 1),
                COALESCE(SUM(CASE WHEN status IN ('pending','retry','leased') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status IN ('pending','retry','leased') THEN payload_bytes ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'leased' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'terminal' THEN 1 ELSE 0 END), 0),
                (SELECT overflow_count FROM environmental_observation_metadata WHERE metadata_key = 1),
                MIN(CASE WHEN status IN ('pending','retry','leased') THEN created_unix_ms END)
            FROM environmental_observation_outbox;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new EnvironmentalObservationOutboxSnapshot(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            _timeProvider.GetUtcNow());
    }

    public async ValueTask<IReadOnlyList<EnvironmentalObservationDeadLetter>> ReadDeadLettersAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResults, 1000);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT record_id, status, COALESCE(last_reason, 'unspecified'), payload_bytes, attempt_count,
                created_unix_ms, updated_unix_ms
            FROM environmental_observation_outbox
            WHERE status IN ('quarantined','terminal')
            ORDER BY updated_unix_ms, record_id
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$maximum", maximumResults);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<EnvironmentalObservationDeadLetter>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new(
                reader.GetInt64(0),
                reader.GetString(1) == "quarantined"
                    ? EnvironmentalObservationDeadLetterStatus.Quarantined
                    : EnvironmentalObservationDeadLetterStatus.Terminal,
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        }
        return records;
    }

    public ValueTask ReplayAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => ResolveDeadLetterAsync(root, recordId, actor, reason, replay: true, cancellationToken);

    public ValueTask AbandonAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => ResolveDeadLetterAsync(root, recordId, actor, reason, replay: false, cancellationToken);

    public async ValueTask<EnvironmentalOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        EnvironmentalOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = cursor is null
            ? string.Concat(OperationsSelectColumns, " WHERE status IN ('quarantined','terminal') ORDER BY record_id DESC LIMIT $limit;")
            : string.Concat(OperationsSelectColumns, " WHERE status IN ('quarantined','terminal') AND record_id < $record_id ORDER BY record_id DESC LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$record_id", cursor.RecordId);
        }
        var records = new List<EnvironmentalOutboxOperationsRecord>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadOperationsRecord(reader));
        }
        var hasMore = records.Count > pageSize;
        if (hasMore)
        {
            records.RemoveAt(records.Count - 1);
        }
        return new EnvironmentalOutboxOperationsPage(
            records,
            hasMore && records.Count > 0 ? records[^1].Cursor : null);
    }

    public async ValueTask<EnvironmentalOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat(
            OperationsSelectColumns,
            " WHERE record_id = $record_id AND status IN ('quarantined','terminal');");
        command.Parameters.AddWithValue("$record_id", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOperationsRecord(reader) : null;
    }

    public async ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
        string root,
        long recordId,
        int pageSize,
        OutboxOperationsAuditCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = cursor is null
            ? "SELECT audit_id, action, actor, reason, occurred_unix_ms FROM environmental_observation_outbox_audit WHERE record_id = $record_id ORDER BY audit_id DESC LIMIT $limit;"
            : "SELECT audit_id, action, actor, reason, occurred_unix_ms FROM environmental_observation_outbox_audit WHERE record_id = $record_id AND audit_id < $sequence ORDER BY audit_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$record_id", recordId);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$sequence", cursor.Sequence);
        }
        var entries = new List<OutboxOperationsAuditRecord>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new OutboxOperationsAuditRecord(
                reader.GetInt64(0),
                Bound(reader.GetString(1), 32),
                string.Equals(reader.GetString(2), "owner", StringComparison.Ordinal) ? "owner" : "system",
                OutboxOperationsReasonCodes.Sanitize(reader.GetString(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }
        var hasMore = entries.Count > pageSize;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }
        return new OutboxOperationsAuditPage(
            entries,
            hasMore && entries.Count > 0 ? new OutboxOperationsAuditCursor(entries[^1].Sequence) : null);
    }

    public async ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
        string root,
        long recordId,
        OutboxOperationAction action,
        string operationKey,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        ValidateOperation(operationKey, actorKind, reasonCode);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        var existing = await ReadOperationAsync(root, operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return MatchOperation(existing, recordId, action, actorKind, reasonCode);
        }

        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        existing = await ReadOperationAsync(connection, transaction, operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var disposition = MatchOperation(existing, recordId, action, actorKind, reasonCode);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return disposition;
        }
        string status;
        int payloadBytes;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status, payload_bytes FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
            read.Parameters.AddWithValue("$id", recordId);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
            }
            status = reader.GetString(0);
            payloadBytes = reader.GetInt32(1);
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = action == OutboxOperationAction.Replay
                ? "UPDATE environmental_observation_outbox SET status = 'pending', attempt_count = 0, next_attempt_unix_ms = $now, last_reason = NULL, lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now WHERE record_id = $id AND status IN ('quarantined','terminal');"
                : "DELETE FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
            update.Parameters.AddWithValue("$id", recordId);
            update.Parameters.AddWithValue("$now", now);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
            }
        }
        if (action == OutboxOperationAction.Abandon)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ResetProjectionAfterAbandonAsync(
                connection, transaction, recordId, now, cancellationToken).ConfigureAwait(false);
        }
        using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = "INSERT INTO environmental_observation_outbox_operations(operation_key, record_id, action, actor_kind, reason, occurred_unix_ms) VALUES($operation, $id, $action, $actor, $reason, $now);";
            operation.Parameters.AddWithValue("$operation", operationKey);
            operation.Parameters.AddWithValue("$id", recordId);
            operation.Parameters.AddWithValue("$action", FormatAction(action));
            operation.Parameters.AddWithValue("$actor", actorKind);
            operation.Parameters.AddWithValue("$reason", reasonCode);
            operation.Parameters.AddWithValue("$now", now);
            await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO environmental_observation_outbox_audit(record_id, previous_status, action, actor, reason, occurred_unix_ms, operation_key) VALUES($id, $status, $action, $actor, $reason, $now, $operation);";
            audit.Parameters.AddWithValue("$id", recordId);
            audit.Parameters.AddWithValue("$status", status);
            audit.Parameters.AddWithValue("$action", FormatAction(action));
            audit.Parameters.AddWithValue("$actor", actorKind);
            audit.Parameters.AddWithValue("$reason", reasonCode);
            audit.Parameters.AddWithValue("$now", now);
            audit.Parameters.AddWithValue("$operation", operationKey);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await PruneOperationReceiptsAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OutboxOperationDisposition.Applied;
    }

    internal async ValueTask InitializeAsync(string root, CancellationToken cancellationToken)
    {
        lock (_initializedLock)
        {
            if (_initializedRoots.Contains(root))
            {
                return;
            }
        }
        var gate = _initializationGates.GetOrAdd(root, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_initializedLock)
            {
                if (_initializedRoots.Contains(root))
                {
                    return;
                }
            }
            Directory.CreateDirectory(DatabaseDirectory(root));
            EnsureDatabaseFilesArePhysical(root);
            var inspection = File.Exists(DatabasePath(root))
                ? await InspectExistingDatabaseAsync(root, cancellationToken).ConfigureAwait(false)
                : new EnvironmentalSchemaInspection(0, 0);
            var initializeSchema = inspection.SchemaVersion == 0 && inspection.SchemaObjectCount == 0;
            if (!initializeSchema && inspection.SchemaVersion != CurrentSchemaVersion)
            {
                var relationship = inspection.SchemaVersion > CurrentSchemaVersion
                    ? "newer than supported"
                    : "unsupported";
                throw new InvalidOperationException(
                    $"Environmental observation schema {inspection.SchemaVersion} is {relationship}; archive the database and complete an explicit state-disposition procedure before starting this CameraAgent.");
            }
            using var connection = await OpenUnconfiguredAsync(root, cancellationToken).ConfigureAwait(false);
            if (initializeSchema)
            {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
                using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
                var writableVersion = await ExecuteScalarLongAsync(
                    connection, "PRAGMA user_version;", transaction, cancellationToken).ConfigureAwait(false);
                var writableObjectCount = await CountSchemaObjectsAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                if (writableVersion == 0 && writableObjectCount == 0)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = CanonicalSchemaSql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (writableVersion != CurrentSchemaVersion)
                {
                    throw new InvalidOperationException(
                        "Environmental observation schema changed during initialization; restart after completing an explicit state-disposition procedure.");
                }
                await ValidateSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ValidateSchemaAsync(connection, null, cancellationToken).ConfigureAwait(false);
            }
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            lock (_initializedLock)
            {
                _initializedRoots.Add(root);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask ResolveDeadLetterAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        bool replay,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        actor = actor.Trim();
        reason = reason.Trim();
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        string status;
        int payloadBytes;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status, payload_bytes FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
            read.Parameters.AddWithValue("$id", recordId);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
            }
            status = reader.GetString(0);
            payloadBytes = reader.GetInt32(1);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = replay
            ? """
              UPDATE environmental_observation_outbox
              SET status = 'pending', attempt_count = 0, next_attempt_unix_ms = $now, last_reason = NULL,
                  lease_owner = NULL, lease_token = NULL, lease_expires_unix_ms = NULL, updated_unix_ms = $now
              WHERE record_id = $id AND status IN ('quarantined','terminal');
              """
            : "DELETE FROM environmental_observation_outbox WHERE record_id = $id AND status IN ('quarantined','terminal');";
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Environmental observation dead-letter resolution requires one terminal or quarantined record.");
        }
        if (!replay)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ResetProjectionAfterAbandonAsync(
                connection,
                transaction,
                recordId,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
        }
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO environmental_observation_outbox_audit(
                    record_id, previous_status, action, actor, reason, occurred_unix_ms)
                VALUES($id, $status, $action, $actor, $reason, $now);
                """;
            audit.Parameters.AddWithValue("$id", recordId);
            audit.Parameters.AddWithValue("$status", status);
            audit.Parameters.AddWithValue("$action", replay ? "replay" : "abandon");
            audit.Parameters.AddWithValue("$actor", Bound(actor, 128));
            audit.Parameters.AddWithValue("$reason", Bound(reason, 512));
            audit.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var retention = connection.CreateCommand())
        {
            retention.Transaction = transaction;
            retention.CommandText = """
                DELETE FROM environmental_observation_outbox_audit
                WHERE audit_id <= COALESCE(
                    (SELECT audit_id FROM environmental_observation_outbox_audit
                     ORDER BY audit_id DESC LIMIT 1 OFFSET $maximum), 0);
                """;
            retention.Parameters.AddWithValue("$maximum", MaximumAuditRecords);
            await retention.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ResetProjectionAfterAbandonAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long outboxRecordId,
        long updatedUnixMs,
        CancellationToken cancellationToken)
    {
        using var projection = connection.CreateCommand();
        projection.Transaction = transaction;
        projection.CommandText = """
            UPDATE environmental_observation_central_projection
            SET status = 'waiting', outbox_record_id = NULL, updated_unix_ms = $updated
            WHERE outbox_record_id = $outbox AND status = 'staged';
            """;
        projection.Parameters.AddWithValue("$updated", updatedUnixMs);
        projection.Parameters.AddWithValue("$outbox", outboxRecordId);
        await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PruneOperationReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long nowUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM environmental_observation_outbox_audit
            WHERE operation_key IN (
                SELECT operation_key FROM environmental_observation_outbox_operations
                WHERE occurred_unix_ms < $cutoff
                   OR operation_key IN (
                       SELECT operation_key FROM environmental_observation_outbox_operations
                       ORDER BY occurred_unix_ms DESC, operation_key DESC
                       LIMIT -1 OFFSET $maximum));
            DELETE FROM environmental_observation_outbox_operations
            WHERE occurred_unix_ms < $cutoff
               OR operation_key IN (
                   SELECT operation_key FROM environmental_observation_outbox_operations
                   ORDER BY occurred_unix_ms DESC, operation_key DESC
                   LIMIT -1 OFFSET $maximum);
            DELETE FROM environmental_observation_outbox_audit
            WHERE audit_id IN (
                SELECT audit_id FROM environmental_observation_outbox_audit
                ORDER BY audit_id DESC LIMIT -1 OFFSET $maximum);
            """;
        command.Parameters.AddWithValue(
            "$cutoff",
            DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMilliseconds)
                .AddDays(-OperationReceiptRetentionDays)
                .ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$maximum", MaximumOperationReceipts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommittedOperation?> ReadOperationAsync(
        string root,
        string operationKey,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        return await ReadOperationAsync(connection, transaction: null, operationKey, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CommittedOperation?> ReadOperationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_id, action, actor_kind, reason FROM environmental_observation_outbox_operations WHERE operation_key = $operation;";
        command.Parameters.AddWithValue("$operation", operationKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CommittedOperation(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static OutboxOperationDisposition MatchOperation(
        CommittedOperation existing,
        long recordId,
        OutboxOperationAction action,
        string actorKind,
        string reasonCode)
    {
        if (existing.RecordId != recordId ||
            !string.Equals(existing.Action, FormatAction(action), StringComparison.Ordinal) ||
            !string.Equals(existing.ActorKind, actorKind, StringComparison.Ordinal) ||
            !string.Equals(existing.ReasonCode, reasonCode, StringComparison.Ordinal))
        {
            throw new OutboxOperationCollisionException("The operation key is already bound to a different request.");
        }
        return OutboxOperationDisposition.Duplicate;
    }

    private static void ValidateOperation(string operationKey, string actorKind, string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (operationKey.Length > 128 || operationKey.Any(static character => char.IsControl(character)))
        {
            throw new ArgumentException("Operation key is invalid.", nameof(operationKey));
        }
        if (actorKind is not ("owner" or "system"))
        {
            throw new ArgumentException("Actor kind is invalid.", nameof(actorKind));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (reasonCode.Length > 64)
        {
            throw new ArgumentException("Reason code is invalid.", nameof(reasonCode));
        }
    }

    private static EnvironmentalOutboxOperationsRecord ReadOperationsRecord(SqliteDataReader reader)
    {
        var updatedUnixMilliseconds = reader.GetInt64(6);
        return new EnvironmentalOutboxOperationsRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            DateTimeOffset.FromUnixTimeMilliseconds(updatedUnixMilliseconds),
            reader.IsDBNull(4) ? null : OutboxOperationsReasonCodes.Sanitize(reader.GetString(4)),
            CanReplay: true,
            CanAbandon: true,
            new EnvironmentalOutboxOperationsCursor(reader.GetInt64(0)));
    }

    private static string FormatAction(OutboxOperationAction action) => action switch
    {
        OutboxOperationAction.Replay => "replay",
        OutboxOperationAction.Abandon => "abandon",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private ValueTask TerminalSettleAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string status,
        string reason,
        CancellationToken cancellationToken)
        => SettleAsync(root, lease, $"""
            UPDATE environmental_observation_outbox
            SET status = '{status}', last_reason = $reason, lease_owner = NULL, lease_token = NULL,
                lease_expires_unix_ms = NULL, updated_unix_ms = $now
            WHERE record_id = $id AND status = 'leased' AND lease_owner = $owner AND lease_token = $token
                AND lease_expires_unix_ms > $now;
            """, null, reason, cancellationToken);

    private async ValueTask SettleAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string sql,
        DateTimeOffset? retryAtUtc,
        string? reason,
        CancellationToken cancellationToken,
        bool removesRecord = false)
    {
        ArgumentNullException.ThrowIfNull(lease);
        root = NormalizeRoot(root);
        await InitializeAsync(root, cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(root, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", lease.Record.RecordId);
        command.Parameters.AddWithValue("$owner", lease.Owner);
        command.Parameters.AddWithValue("$token", lease.Token);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (retryAtUtc is { } next)
        {
            command.Parameters.AddWithValue("$next", next.ToUnixTimeMilliseconds());
        }
        if (reason is not null)
        {
            command.Parameters.AddWithValue("$reason", Bound(reason, 128));
        }
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new EnvironmentalObservationLeaseLostException(
                "Environmental observation lease was lost before settlement.");
        }
        if (removesRecord)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count - 1, stored_bytes = stored_bytes - $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", lease.Record.PayloadBytes);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddLocalRecordParameters(
        SqliteCommand command,
        EnvironmentalObservationFactV1 fact,
        string sourceIdentity,
        string sourceContentIdentity,
        string contentIdentity,
        byte[] payload,
        long recordedUnixMs)
    {
        command.Parameters.AddWithValue("$source", sourceIdentity);
        command.Parameters.AddWithValue("$sourceContent", sourceContentIdentity);
        command.Parameters.AddWithValue("$observation", fact.ObservationId.ToString("D"));
        command.Parameters.AddWithValue("$content", contentIdentity);
        command.Parameters.AddWithValue("$schema", fact.SchemaVersion);
        command.Parameters.AddWithValue("$kind", fact.Value.Kind.ToString());
        command.Parameters.AddWithValue("$rig", (object?)fact.RigId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceKind", fact.Source.Kind.ToString());
        command.Parameters.AddWithValue("$quality", fact.Value.Quality.ToString());
        command.Parameters.AddWithValue("$observed", fact.ObservedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$validFrom", fact.ValidFromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$validThrough", fact.ValidThroughUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$staleAfter", fact.StaleAfterUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$bytes", payload.Length);
        command.Parameters.AddWithValue("$recorded", recordedUnixMs);
    }

    private static void AddAttemptParameters(
        SqliteCommand command,
        EnvironmentalSourceDescriptor source,
        EnvironmentalAcquisitionReceipt receipt,
        long? captureSequence,
        Guid? captureId)
    {
        command.Parameters.AddWithValue("$source", source.Id);
        command.Parameters.AddWithValue("$kind", source.Kind.ToString());
        command.Parameters.AddWithValue("$required", source.Required ? 1 : 0);
        command.Parameters.AddWithValue("$trigger", receipt.Trigger.ToString());
        command.Parameters.AddWithValue("$outcome", receipt.Disposition.ToString());
        command.Parameters.AddWithValue("$reason", receipt.Reason);
        command.Parameters.AddWithValue("$observation", receipt.ObservationId is { } observationId
            ? observationId.ToString("D")
            : DBNull.Value);
        command.Parameters.AddWithValue("$observed", receipt.ObservedAtUtc is { } observedAtUtc
            ? observedAtUtc.ToUnixTimeMilliseconds()
            : DBNull.Value);
        command.Parameters.AddWithValue("$staleAfter", receipt.StaleAfterUtc is { } staleAfterUtc
            ? staleAfterUtc.ToUnixTimeMilliseconds()
            : DBNull.Value);
        command.Parameters.AddWithValue("$sequence", captureSequence is { } sequence ? sequence : DBNull.Value);
        command.Parameters.AddWithValue("$capture", captureId is { } captured ? captured.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$started", receipt.StartedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$completed", receipt.CompletedUtc.ToUnixTimeMilliseconds());
    }

    private static async ValueTask LinkLineageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalEnvironmentalObservationRecord record,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < record.Fact.Lineage.Count; ordinal++)
        {
            var lineage = record.Fact.Lineage[ordinal];
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO environmental_observation_local_lineage(
                    local_record_id, ordinal, source_identity_sha256, observation_id, referenced_record_id)
                SELECT $local, $ordinal, $source, $observation,
                    (SELECT record_id FROM environmental_observation_journal
                     WHERE source_identity_sha256 = $source AND observation_id = $observation)
                WHERE EXISTS(SELECT 1 FROM environmental_observation_journal WHERE record_id = $local);
                """;
            insert.Parameters.AddWithValue("$local", record.RecordId);
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$source", lineage.SourceIdentitySha256);
            insert.Parameters.AddWithValue("$observation", lineage.ObservationId.ToString("D"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using var resolve = connection.CreateCommand();
        resolve.Transaction = transaction;
        resolve.CommandText = """
            UPDATE environmental_observation_local_lineage
            SET referenced_record_id = $record
            WHERE referenced_record_id IS NULL AND source_identity_sha256 = $source
                AND observation_id = $observation;
            """;
        resolve.Parameters.AddWithValue("$record", record.RecordId);
        resolve.Parameters.AddWithValue("$source", record.SourceIdentitySha256);
        resolve.Parameters.AddWithValue("$observation", record.Fact.ObservationId.ToString("D"));
        await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LocalEnvironmentalRetentionResult> RetainLocalCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset nowUtc,
        DateTimeOffset recordedBeforeUtc,
        int maximumResults,
        bool forcePressure,
        bool rawIngressAttached,
        CancellationToken cancellationToken)
    {
        var cutoffUnixMs = recordedBeforeUtc.ToUnixTimeMilliseconds();
        using (var associations = connection.CreateCommand())
        {
            associations.Transaction = transaction;
            associations.CommandText = rawIngressAttached
                ? """
                    DELETE FROM environmental_capture_associations
                    WHERE association_id IN (
                        SELECT association.association_id
                        FROM environmental_capture_associations AS association
                        WHERE association.created_unix_ms < $cutoff
                            AND NOT EXISTS(
                                SELECT 1 FROM raw_ingress.raw_captures AS capture
                                WHERE capture.capture_id = replace(association.capture_id, '-', ''))
                        ORDER BY association.created_unix_ms, association.association_id
                        LIMIT $limit);
                    """
                : """
                    DELETE FROM environmental_capture_associations
                    WHERE association_id IN (
                        SELECT association_id FROM environmental_capture_associations
                        WHERE created_unix_ms < $cutoff
                        ORDER BY created_unix_ms, association_id
                        LIMIT $limit);
                    """;
            associations.Parameters.AddWithValue("$cutoff", cutoffUnixMs);
            associations.Parameters.AddWithValue("$limit", maximumResults);
            await associations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var attempts = connection.CreateCommand())
        {
            attempts.Transaction = transaction;
            attempts.CommandText = """
                DELETE FROM environmental_acquisition_attempts
                WHERE attempt_id IN (
                    SELECT attempt_id FROM environmental_acquisition_attempts
                    WHERE completed_unix_ms < $cutoff
                    ORDER BY completed_unix_ms, attempt_id
                    LIMIT $limit);
                """;
            attempts.Parameters.AddWithValue("$cutoff", cutoffUnixMs);
            attempts.Parameters.AddWithValue("$limit", maximumResults);
            await attempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var commands = connection.CreateCommand())
        {
            commands.Transaction = transaction;
            commands.CommandText = """
                DELETE FROM environmental_on_demand_commands
                WHERE idempotency_key IN (
                    SELECT idempotency_key FROM environmental_on_demand_commands
                    WHERE (status = 'completed' OR (status = 'running' AND lease_expires_unix_ms < $now))
                        AND updated_unix_ms < $cutoff
                    ORDER BY updated_unix_ms, idempotency_key
                    LIMIT $limit);
                """;
            commands.Parameters.AddWithValue("$cutoff", cutoffUnixMs);
            commands.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
            commands.Parameters.AddWithValue("$limit", maximumResults);
            await commands.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long storedCount;
        long storedBytes;
        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "SELECT stored_count, stored_bytes FROM environmental_observation_journal_metadata WHERE metadata_key = 1;";
            using var reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Local environmental observation metadata is missing.");
            }
            storedCount = reader.GetInt64(0);
            storedBytes = reader.GetInt64(1);
        }
        var underPressure = forcePressure || storedCount >= maximumLocalRecords || storedBytes >= maximumLocalBytes;
        var candidates = new List<(long RecordId, int PayloadBytes)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT journal.record_id, journal.payload_bytes
                FROM environmental_observation_journal AS journal
                WHERE journal.valid_through_unix_ms <= $now
                    AND (journal.recorded_unix_ms < $cutoff OR $pressure = 1)
                    AND NOT EXISTS(
                        SELECT 1 FROM environmental_capture_association_evidence AS evidence
                        WHERE evidence.local_record_id = journal.record_id)
                    AND NOT EXISTS(
                        SELECT 1 FROM environmental_observation_local_lineage AS lineage
                        WHERE lineage.referenced_record_id = journal.record_id)
                    AND NOT EXISTS(
                        SELECT 1 FROM environmental_observation_central_projection AS projection
                        WHERE projection.local_record_id = journal.record_id
                            AND projection.status IN ('waiting','staged'))
                    AND NOT EXISTS(
                        SELECT 1 FROM environmental_observation_outbox AS delivery
                        WHERE delivery.local_record_id = journal.record_id)
                ORDER BY journal.recorded_unix_ms, journal.valid_through_unix_ms, journal.record_id
                LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$now", nowUtc.ToUnixTimeMilliseconds());
            select.Parameters.AddWithValue("$cutoff", recordedBeforeUtc.ToUnixTimeMilliseconds());
            select.Parameters.AddWithValue("$pressure", underPressure ? 1 : 0);
            select.Parameters.AddWithValue("$limit", maximumResults);
            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add((reader.GetInt64(0), reader.GetInt32(1)));
            }
        }
        foreach (var candidate in candidates)
        {
            using (var projection = connection.CreateCommand())
            {
                projection.Transaction = transaction;
                projection.CommandText = "DELETE FROM environmental_observation_central_projection WHERE local_record_id = $id AND status = 'acknowledged';";
                projection.Parameters.AddWithValue("$id", candidate.RecordId);
                await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM environmental_observation_journal WHERE record_id = $id;";
            delete.Parameters.AddWithValue("$id", candidate.RecordId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("Local environmental retention candidate changed before deletion.");
            }
        }
        var removedBytes = candidates.Sum(static candidate => (long)candidate.PayloadBytes);
        if (candidates.Count > 0)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = """
                UPDATE environmental_observation_journal_metadata
                SET stored_count = stored_count - $count,
                    stored_bytes = stored_bytes - $bytes
                WHERE metadata_key = 1 AND stored_count >= $count AND stored_bytes >= $bytes;
                """;
            metadata.Parameters.AddWithValue("$count", candidates.Count);
            metadata.Parameters.AddWithValue("$bytes", removedBytes);
            if (await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("Local environmental retention metadata could not be updated.");
            }
        }
        return new LocalEnvironmentalRetentionResult(
            candidates.Count,
            removedBytes,
            storedCount - candidates.Count,
            storedBytes - removedBytes);
    }

    private static async ValueTask<bool> AttachRawIngressAsync(
        SqliteConnection connection,
        string root,
        CancellationToken cancellationToken)
    {
        using (var databases = connection.CreateCommand())
        {
            databases.CommandText = "SELECT COUNT(*) FROM pragma_database_list WHERE name = 'raw_ingress';";
            if (Convert.ToInt32(
                await databases.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                return true;
            }
        }
        var rawIngressPath = Path.Combine(root, "journal", "raw-ingress.db");
        if (!File.Exists(rawIngressPath))
        {
            return false;
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(root, rawIngressPath);
        using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $path AS raw_ingress;";
        attach.Parameters.AddWithValue("$path", rawIngressPath);
        await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void AddDeliveryTargetParameters(
        SqliteCommand command,
        EnvironmentalObservationResolvedTarget? target)
    {
        command.Parameters.AddWithValue("$targetSite", target is null
            ? DBNull.Value
            : target.ObservatoryId.ToString("D"));
        command.Parameters.AddWithValue("$targetAgent", target is null
            ? DBNull.Value
            : target.DevicePublicId.ToString("D"));
        command.Parameters.AddWithValue("$targetRig", (object?)target?.RigId ?? DBNull.Value);
    }

    private static async ValueTask FreezeDeliveryTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        EnvironmentalObservationResolvedTarget? target,
        CancellationToken cancellationToken)
    {
        if (target is null)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE environmental_observation_journal
            SET central_target_site_id = $targetSite,
                central_target_agent_id = $targetAgent,
                central_target_rig_id = $targetRig
            WHERE record_id = $id AND central_target_site_id IS NULL;
            """;
        command.Parameters.AddWithValue("$id", recordId);
        AddDeliveryTargetParameters(command, target);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProjectionResult> EnsureProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalEnvironmentalObservationRecord record,
        CancellationToken cancellationToken)
    {
        using var activity = EnvironmentalAcquisitionTelemetry.ActivitySource.StartActivity("environment.delivery.project");
        string? siteText;
        string? agentText;
        string? rigId;
        using (var target = connection.CreateCommand())
        {
            target.Transaction = transaction;
            target.CommandText = """
                SELECT central_target_site_id, central_target_agent_id, central_target_rig_id
                FROM environmental_observation_journal WHERE record_id = $id;
                """;
            target.Parameters.AddWithValue("$id", record.RecordId);
            using var reader = await target.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Local environmental observation disappeared before delivery projection.");
            }
            siteText = reader.IsDBNull(0) ? null : reader.GetString(0);
            agentText = reader.IsDBNull(1) ? null : reader.GetString(1);
            rigId = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        if (siteText is null || agentText is null)
        {
            return new ProjectionResult(EnvironmentalObservationProjectionDisposition.NotRequested, null, null);
        }
        if (!Guid.TryParse(siteText, out var siteId) || !Guid.TryParse(agentText, out var agentId))
        {
            throw new InvalidDataException("Local environmental delivery target is invalid.");
        }
        var observation = record.Fact.Enrich(new EnvironmentalObservationTarget(siteId, agentId, rigId));
        if (!EnvironmentalObservationJson.Validate(observation).IsValid)
        {
            throw new InvalidDataException("Local environmental observation cannot be projected for delivery.");
        }

        string? status;
        using (var readProjection = connection.CreateCommand())
        {
            readProjection.Transaction = transaction;
            readProjection.CommandText = "SELECT status FROM environmental_observation_central_projection WHERE local_record_id = $local;";
            readProjection.Parameters.AddWithValue("$local", record.RecordId);
            var value = await readProjection.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            status = value is null
                ? null
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (status == "acknowledged")
        {
            return new ProjectionResult(EnvironmentalObservationProjectionDisposition.Acknowledged, observation, null);
        }
        if (status == "staged")
        {
            return new ProjectionResult(
                EnvironmentalObservationProjectionDisposition.Staged,
                observation,
                EnvironmentalObservationEnqueueDisposition.Duplicate);
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (status is null)
        {
            using var createProjection = connection.CreateCommand();
            createProjection.Transaction = transaction;
            createProjection.CommandText = """
                INSERT INTO environmental_observation_central_projection(
                    local_record_id, status, target_site_id, target_agent_id, target_rig_id, updated_unix_ms)
                VALUES($local, 'waiting', $site, $agent, $rig, $updated);
                """;
            createProjection.Parameters.AddWithValue("$local", record.RecordId);
            createProjection.Parameters.AddWithValue("$site", siteText);
            createProjection.Parameters.AddWithValue("$agent", agentText);
            createProjection.Parameters.AddWithValue("$rig", (object?)rigId ?? DBNull.Value);
            createProjection.Parameters.AddWithValue("$updated", now);
            await createProjection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var sourceIdentity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
        var contentIdentity = EnvironmentalObservationJson.ComputeContentSha256(observation);
        var payload = EnvironmentalObservationJson.Serialize(observation);
        long? existingOutboxId;
        string? existingContent;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT record_id, content_sha256 FROM environmental_observation_outbox WHERE source_identity_sha256 = $source AND observation_id = $observation;";
            existing.Parameters.AddWithValue("$source", sourceIdentity);
            existing.Parameters.AddWithValue("$observation", observation.ObservationId.ToString("D"));
            using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingOutboxId = reader.GetInt64(0);
                existingContent = reader.GetString(1);
            }
            else
            {
                existingOutboxId = null;
                existingContent = null;
            }
        }
        if (existingOutboxId is { } outboxId)
        {
            if (!string.Equals(existingContent, contentIdentity, StringComparison.Ordinal))
            {
                throw new EnvironmentalObservationIdentityConflictException(
                    "Environmental delivery projection conflicts with committed outbox content.");
            }
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "UPDATE environmental_observation_outbox SET local_record_id = $local WHERE record_id = $outbox AND (local_record_id IS NULL OR local_record_id = $local);";
            link.Parameters.AddWithValue("$local", record.RecordId);
            link.Parameters.AddWithValue("$outbox", outboxId);
            if (await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new EnvironmentalObservationIdentityConflictException(
                    "Environmental delivery projection is linked to different local history.");
            }
            await MarkProjectionStagedAsync(
                connection, transaction, record.RecordId, outboxId, now, cancellationToken).ConfigureAwait(false);
            return new ProjectionResult(
                EnvironmentalObservationProjectionDisposition.Staged,
                observation,
                EnvironmentalObservationEnqueueDisposition.Duplicate);
        }

        long storedCount;
        long storedBytes;
        using (var capacity = connection.CreateCommand())
        {
            capacity.Transaction = transaction;
            capacity.CommandText = "SELECT stored_count, stored_bytes FROM environmental_observation_metadata WHERE metadata_key = 1;";
            using var reader = await capacity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            storedCount = reader.GetInt64(0);
            storedBytes = reader.GetInt64(1);
        }
        if (storedCount >= maximumRecords || storedBytes > maximumBytes - payload.Length)
        {
            using var overflow = connection.CreateCommand();
            overflow.Transaction = transaction;
            overflow.CommandText = "UPDATE environmental_observation_metadata SET overflow_count = overflow_count + 1 WHERE metadata_key = 1;";
            await overflow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectionResult(EnvironmentalObservationProjectionDisposition.Waiting, null, null);
        }

        long newOutboxId;
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO environmental_observation_outbox(
                    source_identity_sha256, observation_id, content_sha256, target_site_id, target_agent_id,
                    payload, payload_bytes, status, attempt_count, next_attempt_unix_ms,
                    created_unix_ms, updated_unix_ms, local_record_id)
                VALUES($source, $observation, $content, $site, $agent, $payload, $bytes,
                    'pending', 0, $now, $now, $now, $local);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$source", sourceIdentity);
            insert.Parameters.AddWithValue("$observation", observation.ObservationId.ToString("D"));
            insert.Parameters.AddWithValue("$content", contentIdentity);
            insert.Parameters.AddWithValue("$site", siteText);
            insert.Parameters.AddWithValue("$agent", agentText);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$bytes", payload.Length);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$local", record.RecordId);
            newOutboxId = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE environmental_observation_metadata SET stored_count = stored_count + 1, stored_bytes = stored_bytes + $bytes WHERE metadata_key = 1;";
            metadata.Parameters.AddWithValue("$bytes", payload.Length);
            await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await MarkProjectionStagedAsync(
            connection, transaction, record.RecordId, newOutboxId, now, cancellationToken).ConfigureAwait(false);
        return new ProjectionResult(
            EnvironmentalObservationProjectionDisposition.Staged,
            observation,
            EnvironmentalObservationEnqueueDisposition.Enqueued);
    }

    private static async ValueTask MarkProjectionStagedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long localRecordId,
        long outboxRecordId,
        long updatedUnixMs,
        CancellationToken cancellationToken)
    {
        using var projection = connection.CreateCommand();
        projection.Transaction = transaction;
        projection.CommandText = """
            UPDATE environmental_observation_central_projection
            SET status = 'staged', outbox_record_id = $outbox, updated_unix_ms = $updated
            WHERE local_record_id = $local AND status IN ('waiting','staged');
            """;
        projection.Parameters.AddWithValue("$outbox", outboxRecordId);
        projection.Parameters.AddWithValue("$updated", updatedUnixMs);
        projection.Parameters.AddWithValue("$local", localRecordId);
        if (await projection.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Environmental delivery projection state changed before staging.");
        }
    }

    private static async ValueTask<LocalEnvironmentalObservationRecord> ReadLocalRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload, payload_bytes, source_identity_sha256, source_content_sha256,
                content_sha256, observation_id, schema_version, observation_kind, source_kind,
                quality, observed_at_unix_ms, valid_from_unix_ms, valid_through_unix_ms,
                stale_after_unix_ms, recorded_unix_ms
            FROM environmental_observation_journal WHERE record_id = $id;
            """;
        command.Parameters.AddWithValue("$id", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Local environmental observation disappeared during validation.");
        }
        var payload = (byte[])reader.GetValue(0);
        var parsed = EnvironmentalObservationFactJson.Parse(payload);
        var fact = parsed.Fact;
        if (fact is null || payload.Length != reader.GetInt32(1) ||
            !Guid.TryParse(reader.GetString(5), out var storedObservationId) || fact.ObservationId != storedObservationId ||
            !string.Equals(fact.SchemaVersion, reader.GetString(6), StringComparison.Ordinal) ||
            !string.Equals(fact.Value.Kind.ToString(), reader.GetString(7), StringComparison.Ordinal) ||
            !string.Equals(fact.Source.Kind.ToString(), reader.GetString(8), StringComparison.Ordinal) ||
            !string.Equals(fact.Value.Quality.ToString(), reader.GetString(9), StringComparison.Ordinal) ||
            fact.ObservedAtUtc.ToUnixTimeMilliseconds() != reader.GetInt64(10) ||
            fact.ValidFromUtc.ToUnixTimeMilliseconds() != reader.GetInt64(11) ||
            fact.ValidThroughUtc.ToUnixTimeMilliseconds() != reader.GetInt64(12) ||
            fact.StaleAfterUtc.ToUnixTimeMilliseconds() != reader.GetInt64(13) ||
            !FixedHash(EnvironmentalObservationFactJson.ComputeSourceIdentitySha256(fact), reader.GetString(2)) ||
            !FixedHash(EnvironmentalObservationFactJson.ComputeSourceContentSha256(fact), reader.GetString(3)) ||
            !FixedHash(EnvironmentalObservationFactJson.ComputeContentSha256(fact), reader.GetString(4)))
        {
            throw new InvalidDataException("Local environmental observation committed payload failed validation.");
        }
        return new LocalEnvironmentalObservationRecord(
            recordId,
            fact,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            payload.Length,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)));
    }

    private static async ValueTask<EnvironmentalObservationOutboxRecord> ReadRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recordId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload, payload_bytes, source_identity_sha256, content_sha256, observation_id, attempt_count, local_record_id FROM environmental_observation_outbox WHERE record_id = $id;";
        command.Parameters.AddWithValue("$id", recordId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Environmental observation disappeared during claim.");
        }
        var payload = (byte[])reader.GetValue(0);
        var parsed = EnvironmentalObservationJson.Parse(payload);
        var observation = parsed.Observation;
        if (observation is null || payload.Length != reader.GetInt32(1) ||
            !Guid.TryParse(reader.GetString(4), out var storedObservationId) ||
            observation.ObservationId != storedObservationId ||
            !FixedHash(EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation), reader.GetString(2)) ||
            !FixedHash(EnvironmentalObservationJson.ComputeContentSha256(observation), reader.GetString(3)))
        {
            throw new InvalidDataException("Environmental observation committed payload failed validation.");
        }
        return new EnvironmentalObservationOutboxRecord(
            recordId, observation, reader.GetString(2), reader.GetString(3), payload.Length, reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    private async ValueTask<SqliteConnection> OpenAsync(string root, CancellationToken cancellationToken)
    {
        var connection = await OpenUnconfiguredAsync(root, cancellationToken, pooled: true).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async ValueTask<SqliteConnection> OpenUnconfiguredAsync(
        string root,
        CancellationToken cancellationToken,
        bool pooled = false)
    {
        EnsureDatabaseFilesArePhysical(root);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(root),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = pooled ? SqliteCacheMode.Shared : SqliteCacheMode.Private,
            Pooling = pooled,
            DefaultTimeout = busyTimeoutSeconds
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical(root);
        return connection;
    }

    private async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout={busyTimeoutSeconds * 1000};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<EnvironmentalSchemaInspection> InspectExistingDatabaseAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var databasePath = DatabasePath(root);
        var hasRecoveryState = File.Exists(string.Concat(databasePath, "-wal")) ||
            File.Exists(string.Concat(databasePath, "-shm")) ||
            File.Exists(string.Concat(databasePath, "-journal"));
        DirectoryInfo? snapshotRoot = null;
        try
        {
            var inspectionPath = databasePath;
            var immutable = !hasRecoveryState;
            if (!immutable)
            {
                (snapshotRoot, inspectionPath) = CopyStableDatabaseSnapshot(
                    databasePath, "hvo-environment-inspection-");
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = immutable
                    ? string.Concat(new Uri(inspectionPath).AbsoluteUri, "?immutable=1")
                    : inspectionPath,
                Mode = immutable ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = busyTimeoutSeconds
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await ExecuteScalarLongAsync(
                connection, "PRAGMA user_version;", null, cancellationToken).ConfigureAwait(false);
            var objectCount = await CountSchemaObjectsAsync(connection, null, cancellationToken).ConfigureAwait(false);
            if (version == CurrentSchemaVersion)
            {
                await ValidateSchemaAsync(connection, null, cancellationToken).ConfigureAwait(false);
            }
            return new(checked((int)version), objectCount);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Environmental observation SQLite schema inspection failed for '{databasePath}'; archive the database and complete an explicit state-disposition procedure before starting this CameraAgent.",
                exception);
        }
        finally
        {
            snapshotRoot?.Delete(recursive: true);
        }
    }

    private static (DirectoryInfo Root, string DatabasePath) CopyStableDatabaseSnapshot(
        string databasePath,
        string temporaryPrefix)
    {
        var sourcePaths = new[]
        {
            databasePath,
            string.Concat(databasePath, "-wal"),
            string.Concat(databasePath, "-journal")
        };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            DirectoryInfo? snapshotRoot = null;
            try
            {
                var before = sourcePaths.Select(ReadDatabaseFileState).ToArray();
                snapshotRoot = Directory.CreateTempSubdirectory(temporaryPrefix);
                var inspectionPath = Path.Combine(snapshotRoot.FullName, Path.GetFileName(databasePath));
                foreach (var source in before.Where(static state => state.Exists))
                {
                    File.Copy(
                        source.Path,
                        string.Concat(inspectionPath, source.Path.AsSpan(databasePath.Length)));
                }
                var after = sourcePaths.Select(ReadDatabaseFileState).ToArray();
                if (before.SequenceEqual(after))
                {
                    return (snapshotRoot, inspectionPath);
                }
            }
            catch (IOException)
            {
                snapshotRoot?.Delete(recursive: true);
                if (attempt == 2)
                {
                    throw;
                }
                continue;
            }
            snapshotRoot?.Delete(recursive: true);
        }
        throw new IOException("Environmental observation SQLite files changed during schema inspection.");
    }

    private static DatabaseFileState ReadDatabaseFileState(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return new(path, false, 0, 0, string.Empty);
        }
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return new(
            path,
            true,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static async ValueTask ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (await ExecuteScalarLongAsync(connection, "PRAGMA user_version;", transaction, cancellationToken)
                .ConfigureAwait(false) != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Environmental observation schema version is not canonical schema 3.");
        }
        var actual = await ReadSchemaDefinitionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (CanonicalSchemaDefinitions.Value.Any(expected =>
                !actual.TryGetValue(expected.Key, out var definition) ||
                !string.Equals(definition, expected.Value, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !CanonicalSchemaDefinitions.Value.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "Environmental observation schema is unsupported; archive the database and complete an explicit state-disposition procedure before starting this CameraAgent.");
        }
        var integrity = await ExecuteScalarStringAsync(
            connection, "PRAGMA integrity_check;", transaction, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Environmental observation SQLite integrity check failed.");
        }
        if (await ExecuteScalarLongAsync(
                connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;", transaction, cancellationToken)
                .ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Environmental observation SQLite foreign-key validation failed.");
        }
        if (await ExecuteScalarLongAsync(connection, """
                SELECT stored_count = (SELECT COUNT(*) FROM environmental_observation_outbox)
                    AND stored_bytes = (SELECT COALESCE(SUM(payload_bytes), 0) FROM environmental_observation_outbox)
                FROM environmental_observation_metadata WHERE metadata_key = 1;
                """, transaction, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Environmental observation outbox metadata counters are inconsistent.");
        }
        if (await ExecuteScalarLongAsync(connection, """
                SELECT stored_count = (SELECT COUNT(*) FROM environmental_observation_journal)
                    AND stored_bytes = (SELECT COALESCE(SUM(payload_bytes), 0) FROM environmental_observation_journal)
                FROM environmental_observation_journal_metadata WHERE metadata_key = 1;
                """, transaction, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("Local environmental observation metadata counters are inconsistent.");
        }
    }

    private static async ValueTask<long> CountSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
        => await ExecuteScalarLongAsync(connection, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';
            """, transaction, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
        => Convert.ToInt64(
            await ExecuteScalarAsync(connection, sql, transaction, cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

    private static async ValueTask<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
        => Convert.ToString(
            await ExecuteScalarAsync(connection, sql, transaction, cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static async ValueTask<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> CreateCanonicalSchemaDefinitions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = CanonicalSchemaSql;
        command.ExecuteNonQuery();
        return ReadSchemaDefinitions(connection);
    }

    private static async ValueTask<Dictionary<string, string>> ReadSchemaDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateSchemaDefinitionCommand(connection, transaction);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinition(reader));
        }
        return definitions;
    }

    private static Dictionary<string, string> ReadSchemaDefinitions(SqliteConnection connection)
    {
        using var command = CreateSchemaDefinitionCommand(connection, null);
        using var reader = command.ExecuteReader();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinition(reader));
        }
        return definitions;
    }

    private static SqliteCommand CreateSchemaDefinitionCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        return command;
    }

    private static string ReadSchemaDefinition(SqliteDataReader reader)
    {
        var definition = new System.Text.StringBuilder();
        foreach (var ordinal in new[] { 0, 2 })
        {
            var value = reader.GetString(ordinal);
            definition.Append(value.Length).Append(':').Append(value);
        }
        var sql = SqliteRawCaptureJournal.NormalizeSchemaSql(reader.GetString(3));
        definition.Append(sql.Length).Append(':').Append(sql);
        return definition.ToString();
    }

    private static bool FixedHash(string expected, string actual)
        => actual.Length == expected.Length && actual.All(Uri.IsHexDigit) && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected), Convert.FromHexString(actual));

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateAssociation(LocalEnvironmentalCaptureAssociation association)
    {
        if (association.CaptureId == Guid.Empty || association.CaptureSequence < 1 ||
            !Enum.IsDefined(association.Kind) || !Enum.IsDefined(association.Status) ||
            association.RigId is { Length: > 128 } || association.RigId is not null && association.RigId != association.RigId.Trim() ||
            association.ExposureFromUtc.Offset != TimeSpan.Zero || association.ExposureThroughUtc.Offset != TimeSpan.Zero ||
            association.ExposureFromUtc >= association.ExposureThroughUtc ||
            !IsSha256(association.PolicyIdentitySha256) || !IsSha256(association.AssociationIdentitySha256) ||
            association.CreatedUtc.Offset != TimeSpan.Zero || association.ConflictingRecordIds.Count > 1_000 ||
            association.ConflictingRecordIds.Any(static recordId => recordId < 1) ||
            association.ConflictingRecordIds.Distinct().Count() != association.ConflictingRecordIds.Count ||
            association.Status == LocalEnvironmentalAssociationStatus.Contradictory !=
                (association.SelectedRecordId is null && association.ConflictingRecordIds.Count > 1) ||
            (association.Status is LocalEnvironmentalAssociationStatus.Fresh or LocalEnvironmentalAssociationStatus.Stale) !=
                (association.SelectedRecordId is not null && association.ConflictingRecordIds.Count == 0) ||
            association.Status == LocalEnvironmentalAssociationStatus.Missing !=
                (association.SelectedRecordId is null && association.ConflictingRecordIds.Count == 0))
        {
            throw new ArgumentException("The local environmental capture association is invalid.", nameof(association));
        }
    }

    private static object DbValue(long? value)
        => value.HasValue ? value.GetValueOrDefault() : DBNull.Value;

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.GetFullPath(root);
    }

    private static string DatabaseDirectory(string root) => Path.Combine(root, ".environment");
    private static string DatabasePath(string root) => Path.Combine(DatabaseDirectory(root), "environmental-observation-outbox.db");
    private static void EnsureDatabaseFilesArePhysical(string root)
    {
        var databasePath = DatabasePath(root);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-shm"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-journal"));
    }
    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Dispose()
    {
        foreach (var gate in _initializationGates.Values)
        {
            gate.Dispose();
        }
    }

    private sealed record CommittedOperation(
        long RecordId,
        string Action,
        string ActorKind,
        string ReasonCode);

    private sealed record ProjectionResult(
        EnvironmentalObservationProjectionDisposition Disposition,
        EnvironmentalObservationV1? Observation,
        EnvironmentalObservationEnqueueDisposition? EnqueueDisposition);

    private sealed record EnvironmentalSchemaInspection(int SchemaVersion, long SchemaObjectCount);

    private sealed record DatabaseFileState(
        string Path,
        bool Exists,
        long Length,
        long LastWriteUtcTicks,
        string Sha256);

    private const string OperationsSelectColumns = """
        SELECT record_id, status, attempt_count, payload_bytes, last_reason, created_unix_ms, updated_unix_ms
        FROM environmental_observation_outbox
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS environmental_observation_metadata(
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
            stored_count INTEGER NOT NULL DEFAULT 0 CHECK(stored_count >= 0),
            stored_bytes INTEGER NOT NULL DEFAULT 0 CHECK(stored_bytes >= 0),
            overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0)) STRICT;
        INSERT OR IGNORE INTO environmental_observation_metadata(metadata_key, stored_count, stored_bytes, overflow_count)
            VALUES(1, 0, 0, 0);
        CREATE TABLE IF NOT EXISTS environmental_observation_outbox_audit(
            audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            record_id INTEGER NOT NULL,
            previous_status TEXT NOT NULL CHECK(previous_status IN ('quarantined','terminal')),
            action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
            actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 512),
            occurred_unix_ms INTEGER NOT NULL,
            operation_key TEXT NULL) STRICT;
        CREATE TABLE IF NOT EXISTS environmental_observation_outbox(
            record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
            observation_id TEXT NOT NULL,
            content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
            target_site_id TEXT NOT NULL,
            target_agent_id TEXT NOT NULL,
            payload BLOB NOT NULL,
            payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
            status TEXT NOT NULL CHECK(status IN ('pending','leased','retry','quarantined','terminal')),
            attempt_count INTEGER NOT NULL CHECK(attempt_count >= 0),
            next_attempt_unix_ms INTEGER NOT NULL,
            lease_owner TEXT NULL,
            lease_token TEXT NULL,
            lease_expires_unix_ms INTEGER NULL,
            last_reason TEXT NULL,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            local_record_id INTEGER NULL
                REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
            CHECK(payload_bytes = length(payload)),
            CHECK((status = 'leased' AND lease_owner IS NOT NULL AND lease_token IS NOT NULL AND lease_expires_unix_ms IS NOT NULL)
                OR (status != 'leased' AND lease_owner IS NULL AND lease_token IS NULL AND lease_expires_unix_ms IS NULL))) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_environment_identity
            ON environmental_observation_outbox(source_identity_sha256, observation_id);
        CREATE INDEX IF NOT EXISTS ix_environment_operations
            ON environmental_observation_outbox(status, record_id DESC);
        CREATE INDEX IF NOT EXISTS ix_environment_claim
            ON environmental_observation_outbox(status, next_attempt_unix_ms, created_unix_ms, record_id);
        CREATE INDEX IF NOT EXISTS ix_environment_lease
            ON environmental_observation_outbox(status, lease_expires_unix_ms, created_unix_ms, record_id);
        """;

    private const string OperationsSchemaSql = """
        CREATE UNIQUE INDEX ux_environment_audit_operation
            ON environmental_observation_outbox_audit(operation_key) WHERE operation_key IS NOT NULL;
        CREATE TABLE environmental_observation_outbox_operations(
            operation_key TEXT NOT NULL PRIMARY KEY CHECK(length(operation_key) BETWEEN 1 AND 128),
            record_id INTEGER NOT NULL,
            action TEXT NOT NULL CHECK(action IN ('replay','abandon')),
            actor_kind TEXT NOT NULL CHECK(actor_kind IN ('owner','system')),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 64),
            occurred_unix_ms INTEGER NOT NULL
        ) STRICT;
        """;

    private const string LocalSchemaSql = """
        CREATE TABLE environmental_observation_journal_metadata(
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1),
            stored_count INTEGER NOT NULL DEFAULT 0 CHECK(stored_count >= 0),
            stored_bytes INTEGER NOT NULL DEFAULT 0 CHECK(stored_bytes >= 0),
            overflow_count INTEGER NOT NULL DEFAULT 0 CHECK(overflow_count >= 0)) STRICT;
        INSERT INTO environmental_observation_journal_metadata(metadata_key, stored_count, stored_bytes, overflow_count)
            VALUES(1, 0, 0, 0);
        CREATE TABLE environmental_observation_journal(
            record_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
            source_content_sha256 TEXT NOT NULL CHECK(length(source_content_sha256) = 64),
            observation_id TEXT NOT NULL,
            content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
            schema_version TEXT NOT NULL CHECK(length(schema_version) BETWEEN 1 AND 64),
            observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
            rig_id TEXT NULL CHECK(rig_id IS NULL OR length(rig_id) BETWEEN 1 AND 128),
            source_kind TEXT NOT NULL CHECK(source_kind IN ('Measured','Simulated','Imported','Manual','Derived')),
            quality TEXT NOT NULL CHECK(quality IN ('Unknown','Good','Suspect')),
            observed_at_unix_ms INTEGER NOT NULL,
            valid_from_unix_ms INTEGER NOT NULL,
            valid_through_unix_ms INTEGER NOT NULL,
            stale_after_unix_ms INTEGER NOT NULL,
            payload BLOB NOT NULL,
            payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0),
            recorded_unix_ms INTEGER NOT NULL,
            central_target_site_id TEXT NULL,
            central_target_agent_id TEXT NULL,
            central_target_rig_id TEXT NULL,
            CHECK(valid_from_unix_ms < valid_through_unix_ms),
            CHECK(stale_after_unix_ms BETWEEN valid_from_unix_ms AND valid_through_unix_ms),
            CHECK((central_target_site_id IS NULL AND central_target_agent_id IS NULL AND central_target_rig_id IS NULL)
                OR (central_target_site_id IS NOT NULL AND central_target_agent_id IS NOT NULL)),
            CHECK(payload_bytes = length(payload))) STRICT;
        CREATE UNIQUE INDEX ux_environment_local_identity
            ON environmental_observation_journal(source_identity_sha256, observation_id);
        CREATE INDEX ix_environment_local_history
            ON environmental_observation_journal(
                observation_kind, observed_at_unix_ms DESC, source_identity_sha256, observation_id, record_id DESC);
        CREATE INDEX ix_environment_local_validity
            ON environmental_observation_journal(
                observation_kind, rig_id, valid_through_unix_ms, valid_from_unix_ms, stale_after_unix_ms,
                observed_at_unix_ms DESC, record_id);
        CREATE INDEX ix_environment_local_retention
            ON environmental_observation_journal(recorded_unix_ms, valid_through_unix_ms, record_id);
        CREATE INDEX ix_environment_local_page
            ON environmental_observation_journal(observation_kind, observed_at_unix_ms DESC, record_id DESC);
        CREATE TABLE environmental_observation_local_lineage(
            local_record_id INTEGER NOT NULL
                REFERENCES environmental_observation_journal(record_id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
            observation_id TEXT NOT NULL,
            referenced_record_id INTEGER NULL
                REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
            PRIMARY KEY(local_record_id, ordinal),
            UNIQUE(local_record_id, source_identity_sha256, observation_id)) STRICT;
        CREATE INDEX ix_environment_local_lineage_reference
            ON environmental_observation_local_lineage(referenced_record_id)
            WHERE referenced_record_id IS NOT NULL;
        CREATE INDEX ix_environment_local_lineage_identity
            ON environmental_observation_local_lineage(source_identity_sha256, observation_id)
            WHERE referenced_record_id IS NULL;
        CREATE TABLE environmental_source_runtime(
            source_id TEXT NOT NULL PRIMARY KEY CHECK(length(source_id) BETWEEN 1 AND 128),
            observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
            required INTEGER NOT NULL CHECK(required IN (0,1)),
            last_outcome TEXT NULL CHECK(last_outcome IS NULL OR last_outcome IN ('Produced','Duplicate','Missing','Failed','TimedOut','Coalesced')),
            last_reason TEXT NULL CHECK(last_reason IS NULL OR length(last_reason) BETWEEN 1 AND 128),
            last_observation_id TEXT NULL,
            last_observed_unix_ms INTEGER NULL,
            last_stale_after_unix_ms INTEGER NULL,
            last_started_unix_ms INTEGER NULL,
            last_completed_unix_ms INTEGER NULL,
            consecutive_failures INTEGER NOT NULL CHECK(consecutive_failures >= 0),
            next_poll_unix_ms INTEGER NULL,
            CHECK((last_observation_id IS NULL AND last_observed_unix_ms IS NULL AND last_stale_after_unix_ms IS NULL)
                OR (last_observation_id IS NOT NULL AND last_observed_unix_ms IS NOT NULL
                    AND last_stale_after_unix_ms IS NOT NULL AND last_observed_unix_ms <= last_stale_after_unix_ms)),
            CHECK((last_outcome IS NULL AND last_reason IS NULL AND last_started_unix_ms IS NULL AND last_completed_unix_ms IS NULL)
                OR (last_outcome IS NOT NULL AND last_reason IS NOT NULL AND last_started_unix_ms IS NOT NULL
                    AND last_completed_unix_ms IS NOT NULL AND last_started_unix_ms <= last_completed_unix_ms))) STRICT;
        CREATE TABLE environmental_capture_regime_state(
            state_key INTEGER NOT NULL PRIMARY KEY CHECK(state_key = 1),
            capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
            capture_id TEXT NOT NULL,
            solar_regime TEXT NOT NULL CHECK(solar_regime IN ('Day','Twilight','Night')),
            observed_unix_ms INTEGER NOT NULL) STRICT;
        CREATE TABLE environmental_acquisition_attempts(
            attempt_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            source_id TEXT NOT NULL CHECK(length(source_id) BETWEEN 1 AND 128),
            observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
            required INTEGER NOT NULL CHECK(required IN (0,1)),
            trigger TEXT NOT NULL CHECK(trigger IN ('Periodic','BeforeCapture','AfterCapture','EveryNthCapture','RegimeChange','OnDemand')),
            outcome TEXT NOT NULL CHECK(outcome IN ('Produced','Duplicate','Missing','Failed','TimedOut','Coalesced')),
            reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 128),
            observation_id TEXT NULL,
            capture_sequence INTEGER NULL CHECK(capture_sequence IS NULL OR capture_sequence > 0),
            capture_id TEXT NULL,
            started_unix_ms INTEGER NOT NULL,
            completed_unix_ms INTEGER NOT NULL,
            CHECK((capture_sequence IS NULL) = (capture_id IS NULL)),
            CHECK(started_unix_ms <= completed_unix_ms)) STRICT;
        CREATE INDEX ix_environment_attempt_source
            ON environmental_acquisition_attempts(source_id, completed_unix_ms DESC, attempt_id DESC);
        CREATE INDEX ix_environment_attempt_capture
            ON environmental_acquisition_attempts(capture_id, source_id, attempt_id) WHERE capture_id IS NOT NULL;
        CREATE TABLE environmental_on_demand_commands(
            idempotency_key TEXT NOT NULL PRIMARY KEY CHECK(length(idempotency_key) BETWEEN 1 AND 128),
            payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64),
            source_id TEXT NOT NULL CHECK(length(source_id) BETWEEN 1 AND 128),
            actor_id TEXT NOT NULL CHECK(length(actor_id) BETWEEN 1 AND 256),
            reason TEXT NULL CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 128),
            observed_unix_ms INTEGER NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('running','completed')),
            lease_token TEXT NULL,
            lease_expires_unix_ms INTEGER NULL,
            receipt_json BLOB NULL,
            updated_unix_ms INTEGER NOT NULL,
            CHECK((status = 'running' AND lease_token IS NOT NULL AND lease_expires_unix_ms IS NOT NULL AND receipt_json IS NULL)
                OR (status = 'completed' AND lease_token IS NULL AND lease_expires_unix_ms IS NULL AND receipt_json IS NOT NULL))) STRICT;
        CREATE INDEX ix_environment_on_demand_lease
            ON environmental_on_demand_commands(status, lease_expires_unix_ms, idempotency_key);
        CREATE TABLE environmental_capture_associations(
            association_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            capture_id TEXT NOT NULL,
            capture_sequence INTEGER NOT NULL CHECK(capture_sequence > 0),
            observation_kind TEXT NOT NULL CHECK(length(observation_kind) BETWEEN 1 AND 64),
            rig_id TEXT NULL CHECK(rig_id IS NULL OR length(rig_id) BETWEEN 1 AND 128),
            exposure_from_unix_ms INTEGER NOT NULL,
            exposure_through_unix_ms INTEGER NOT NULL,
            policy_identity_sha256 TEXT NOT NULL CHECK(length(policy_identity_sha256) = 64),
            status TEXT NOT NULL CHECK(status IN ('Fresh','Stale','Missing','Contradictory')),
            selected_record_id INTEGER NULL
                REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
            conflicting_record_ids_json BLOB NOT NULL,
            association_identity_sha256 TEXT NOT NULL CHECK(length(association_identity_sha256) = 64),
            created_unix_ms INTEGER NOT NULL,
            CHECK(exposure_from_unix_ms < exposure_through_unix_ms),
            UNIQUE(capture_id, observation_kind, policy_identity_sha256)) STRICT;
        CREATE INDEX ix_environment_association_capture
            ON environmental_capture_associations(capture_id, observation_kind, rig_id, policy_identity_sha256);
        CREATE TABLE environmental_capture_association_evidence(
            association_id INTEGER NOT NULL
                REFERENCES environmental_capture_associations(association_id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
            role TEXT NOT NULL CHECK(role IN ('selected','conflict')),
            local_record_id INTEGER NOT NULL
                REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
            source_identity_sha256 TEXT NOT NULL CHECK(length(source_identity_sha256) = 64),
            observation_id TEXT NOT NULL,
            content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
            PRIMARY KEY(association_id, ordinal),
            UNIQUE(association_id, local_record_id)) STRICT;
        CREATE INDEX ix_environment_association_evidence_record
            ON environmental_capture_association_evidence(local_record_id, association_id);
        CREATE INDEX ix_environment_delivery_local_record
            ON environmental_observation_outbox(local_record_id) WHERE local_record_id IS NOT NULL;
        CREATE TABLE environmental_observation_central_projection(
            projection_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            local_record_id INTEGER NOT NULL
                REFERENCES environmental_observation_journal(record_id) ON DELETE RESTRICT,
            status TEXT NOT NULL CHECK(status IN ('waiting','staged','acknowledged')),
            outbox_record_id INTEGER NULL,
            target_site_id TEXT NOT NULL,
            target_agent_id TEXT NOT NULL,
            target_rig_id TEXT NULL,
            acknowledged_unix_ms INTEGER NULL,
            updated_unix_ms INTEGER NOT NULL,
            CHECK((status = 'staged' AND outbox_record_id IS NOT NULL AND acknowledged_unix_ms IS NULL)
                OR (status = 'waiting' AND outbox_record_id IS NULL AND acknowledged_unix_ms IS NULL)
                OR (status = 'acknowledged' AND outbox_record_id IS NULL AND acknowledged_unix_ms IS NOT NULL)),
            UNIQUE(local_record_id, target_site_id, target_agent_id, target_rig_id)) STRICT;
        CREATE INDEX ix_environment_projection_waiting
            ON environmental_observation_central_projection(status, updated_unix_ms, local_record_id);
        PRAGMA user_version=3;
        """;

    private const string CanonicalSchemaSql = SchemaSql + OperationsSchemaSql + LocalSchemaSql;
}
