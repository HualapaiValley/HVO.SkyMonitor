using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Scheduling;

public sealed record CaptureScheduleRevisionSnapshot(
    string RevisionId,
    long RevisionNumber,
    LocalCaptureProfileDefinition Profile,
    string ProfileSha256,
    string ScheduleSha256,
    string Source,
    string Actor,
    string? Reason,
    DateTimeOffset CreatedUtc)
{
    public CaptureScheduleDefinition Definition => Profile.Schedule;
}

public sealed record CaptureScheduleStoreSnapshot(
    CaptureScheduleRevisionSnapshot ActiveRevision,
    CaptureScheduleRevisionSnapshot? PendingRevision,
    long Version,
    DateTimeOffset? LastEvaluatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed class CaptureScheduleStoreConflictException : InvalidOperationException
{
    public CaptureScheduleStoreConflictException()
    {
    }

    public CaptureScheduleStoreConflictException(string message) : base(message)
    {
    }

    public CaptureScheduleStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SqliteCaptureScheduleStore(
    IRawCaptureIngress rawCaptureIngress,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(
            Path.GetFullPath(options.Value.RawIngressRoot), "journal", "raw-ingress.db"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false,
        DefaultTimeout = options.Value.RawIngressSqliteBusyTimeoutSeconds
    }.ToString();
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CaptureScheduleStoreSnapshot> InitializeAsync(
        CameraModuleConfig fileConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileConfiguration);
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                var initialSchedule = fileConfiguration.Schedule ?? CreateLegacySchedule(fileConfiguration.Rig);
                var initial = LocalCaptureProfileDefinition.Create(fileConfiguration, initialSchedule);
                var source = fileConfiguration.Schedule is null ? "legacy-bootstrap" : "file-bootstrap";
                var revision = await InsertRevisionAsync(
                    connection, transaction, initial, source, "system", "initial configuration", cancellationToken)
                    .ConfigureAwait(false);
                var now = Now();
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO capture_schedule_state(
                        state_key, active_revision_id, pending_revision_id, version,
                        last_evaluated_unix_ms, updated_unix_ms)
                    VALUES (1, $active, NULL, 1, NULL, $now);
                    INSERT INTO capture_schedule_activations(
                        idempotency_key, from_revision_id, to_revision_id, actor, reason,
                        state_version, activated_unix_ms)
                    VALUES ($operation, NULL, $active, 'system', 'initial configuration', 1, $now);
                    """, cancellationToken,
                    ("$active", revision.RevisionId),
                    ("$operation", $"bootstrap:{revision.ProfileSha256}"),
                    ("$now", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                snapshot = new CaptureScheduleStoreSnapshot(revision, null, 1, null, now);
            }
            else
            {
                var fileSchedule = fileConfiguration.Schedule ?? CreateLegacySchedule(fileConfiguration.Rig);
                var fileProfile = LocalCaptureProfileDefinition.Create(fileConfiguration, fileSchedule);
                var fileSha256 = LocalCaptureProfileContract.ComputeSha256(fileProfile);
                if (!string.Equals(fileSha256, snapshot.ActiveRevision.ProfileSha256, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fileSha256, snapshot.PendingRevision?.ProfileSha256, StringComparison.OrdinalIgnoreCase))
                {
                    var pending = await FindRevisionBySha256Async(
                        connection, transaction, fileSha256, cancellationToken).ConfigureAwait(false) ??
                        await InsertRevisionAsync(
                            connection, transaction, fileProfile, "file-draft", "system",
                            "configuration file differs from active revision", cancellationToken).ConfigureAwait(false);
                    var now = Now();
                    await ExecuteAsync(connection, transaction, """
                        UPDATE capture_schedule_state
                        SET pending_revision_id = $pending, version = version + 1, updated_unix_ms = $now
                        WHERE state_key = 1;
                        """, cancellationToken,
                        ("$pending", pending.RevisionId),
                        ("$now", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                    snapshot = snapshot with
                    {
                        PendingRevision = pending,
                        Version = snapshot.Version + 1,
                        UpdatedUtc = now
                    };
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CaptureScheduleStoreSnapshot> StageAsync(
        LocalCaptureProfileDefinition profile,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateCommand(idempotencyKey, expectedVersion, actor, reason);
        var validation = LocalCaptureProfileContract.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"The local capture profile is invalid ({validation.FieldPath}).", nameof(profile));
        }
        return await MutateAsync(idempotencyKey, "stage", profile, expectedVersion, actor, reason,
            async (connection, transaction, snapshot, now, token) =>
            {
                var sha256 = LocalCaptureProfileContract.ComputeSha256(profile);
                var pending = await FindRevisionBySha256Async(connection, transaction, sha256, token)
                    .ConfigureAwait(false) ?? await InsertRevisionAsync(
                        connection, transaction, profile, "operator-draft", actor, reason, token).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, """
                    UPDATE capture_schedule_state
                    SET pending_revision_id = $pending, version = version + 1, updated_unix_ms = $now
                    WHERE state_key = 1;
                    """, token,
                    ("$pending", pending.RevisionId),
                    ("$now", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                return snapshot with
                {
                    PendingRevision = pending,
                    Version = snapshot.Version + 1,
                    UpdatedUtc = now
                };
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CaptureScheduleStoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Capture schedule state has not been initialized.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<CaptureScheduleRevisionSnapshot> GetRevisionAsync(
        string revisionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 128)
        {
            throw new ArgumentException("A valid schedule revision identifier is required.", nameof(revisionId));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var revision = await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The capture schedule revision was not found.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public async Task<IReadOnlyList<CaptureScheduleRevisionSnapshot>> GetHistoryAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var revisionIds = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT revision_id FROM capture_schedule_revisions
                ORDER BY revision_number DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", maximumCount);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                revisionIds.Add(reader.GetString(0));
            }
        }
        var revisions = new List<CaptureScheduleRevisionSnapshot>(revisionIds.Count);
        foreach (var revisionId in revisionIds)
        {
            revisions.Add(await ReadRevisionAsync(
                connection, transaction, revisionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A capture schedule history revision is missing."));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revisions;
    }

    internal Task<CaptureScheduleStoreSnapshot> ActivateAsync(
        string revisionId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 128)
        {
            throw new ArgumentException("A valid schedule revision identifier is required.", nameof(revisionId));
        }
        ValidateCommand(idempotencyKey, expectedVersion, actor, reason);
        return MutateAsync(idempotencyKey, "activate", revisionId, expectedVersion, actor, reason,
            async (connection, transaction, snapshot, now, token) =>
            {
                var target = await ReadRevisionAsync(connection, transaction, revisionId, token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The capture schedule revision was not found.");
                var nextVersion = snapshot.Version + 1;
                await ExecuteAsync(connection, transaction, """
                    UPDATE capture_schedule_state
                    SET active_revision_id = $active,
                        pending_revision_id = CASE WHEN pending_revision_id = $active THEN NULL ELSE pending_revision_id END,
                        version = $version,
                        updated_unix_ms = $now
                    WHERE state_key = 1;
                    INSERT INTO capture_schedule_activations(
                        idempotency_key, from_revision_id, to_revision_id, actor, reason,
                        state_version, activated_unix_ms)
                    VALUES ($operation, $from, $active, $actor, $reason, $version, $now);
                    """, token,
                    ("$active", target.RevisionId),
                    ("$version", nextVersion),
                    ("$now", now.ToUnixTimeMilliseconds()),
                    ("$operation", idempotencyKey),
                    ("$from", snapshot.ActiveRevision.RevisionId),
                    ("$actor", actor),
                    ("$reason", (object?)reason ?? DBNull.Value)).ConfigureAwait(false);
                return snapshot with
                {
                    ActiveRevision = target,
                    PendingRevision = snapshot.PendingRevision?.RevisionId == target.RevisionId
                        ? null
                        : snapshot.PendingRevision,
                    Version = nextVersion,
                    UpdatedUtc = now
                };
            }, cancellationToken);
    }

    public async Task PersistPreviewAsync(
        CaptureScheduleRevisionSnapshot revision,
        CaptureLocationProvenance location,
        CaptureSchedulePreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(preview);
        if (!location.Validate().IsValid ||
            !string.Equals(revision.ScheduleSha256, preview.ScheduleRevisionSha256, StringComparison.OrdinalIgnoreCase) ||
            preview.PreviewStartUtc.Offset != TimeSpan.Zero || preview.PreviewEndUtc.Offset != TimeSpan.Zero ||
            preview.PreviewEndUtc <= preview.PreviewStartUtc)
        {
            throw new ArgumentException("The persisted schedule preview is invalid.", nameof(preview));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expansionKey = CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                preview.ExpansionSha256,
                revision.RevisionId,
                location.LocationId,
                location.Version
            });
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var exists = await ScalarLongAsync(connection, transaction, """
                SELECT COUNT(*) FROM capture_schedule_expansions WHERE expansion_key = $key;
                """, cancellationToken, ("$key", expansionKey)).ConfigureAwait(false) != 0;
            if (!exists)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO capture_schedule_expansions(
                        expansion_key, expansion_sha256, revision_id, deployment_location_id,
                        deployment_location_version, preview_start_unix_ms, preview_end_unix_ms,
                        expansion_algorithm_version, time_zone_rule_sha256,
                        solar_algorithm_version, created_unix_ms)
                    VALUES ($key, $sha, $revision, $location, $location_version, $start, $end,
                            $algorithm, $time_zone, $solar, $created);
                    """, cancellationToken,
                    ("$key", expansionKey),
                    ("$sha", preview.ExpansionSha256),
                    ("$revision", revision.RevisionId),
                    ("$location", location.LocationId),
                    ("$location_version", location.Version),
                    ("$start", preview.PreviewStartUtc.ToUnixTimeMilliseconds()),
                    ("$end", preview.PreviewEndUtc.ToUnixTimeMilliseconds()),
                    ("$algorithm", preview.ExpansionAlgorithmVersion),
                    ("$time_zone", preview.TimeZoneRuleSha256),
                    ("$solar", preview.SolarAlgorithmVersion),
                    ("$created", Now().ToUnixTimeMilliseconds())).ConfigureAwait(false);
                for (var index = 0; index < preview.Intervals.Count; index++)
                {
                    var interval = preview.Intervals[index];
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO capture_schedule_intervals(
                            expansion_key, ordinal, interval_id, source, disposition,
                            start_unix_ms, end_unix_ms, local_date, setpoint_profile_id,
                            solar_algorithm_version)
                        VALUES ($key, $ordinal, $id, $source, $disposition, $start, $end,
                                $date, $profile, $solar);
                        """, cancellationToken,
                        ("$key", expansionKey),
                        ("$ordinal", index),
                        ("$id", interval.Id),
                        ("$source", interval.Source.ToString()),
                        ("$disposition", interval.Disposition == ExpandedScheduleDisposition.Open ? "open" : "closed"),
                        ("$start", interval.StartUtc.ToUnixTimeMilliseconds()),
                        ("$end", interval.EndUtc.ToUnixTimeMilliseconds()),
                        ("$date", interval.LocalDate is { } localDate
                            ? localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                            : DBNull.Value),
                        ("$profile", (object?)interval.SetpointProfileId ?? DBNull.Value),
                        ("$solar", (object?)interval.SolarAlgorithmVersion ?? DBNull.Value)).ConfigureAwait(false);
                }
                for (var index = 0; index < preview.UnavailableWindows.Count; index++)
                {
                    var unavailable = preview.UnavailableWindows[index];
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO capture_schedule_unavailable(
                            expansion_key, ordinal, window_id, source, local_date,
                            start_unix_ms, end_unix_ms, reason_code, solar_algorithm_version)
                        VALUES ($key, $ordinal, $id, $source, $date, $start, $end, $reason, $solar);
                        """, cancellationToken,
                        ("$key", expansionKey),
                        ("$ordinal", index),
                        ("$id", unavailable.Id),
                        ("$source", unavailable.Source.ToString()),
                        ("$date", unavailable.LocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                        ("$start", unavailable.LocalDayStartUtc.ToUnixTimeMilliseconds()),
                        ("$end", unavailable.LocalDayEndUtc.ToUnixTimeMilliseconds()),
                        ("$reason", unavailable.ReasonCode),
                        ("$solar", unavailable.SolarAlgorithmVersion)).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CaptureSchedulePreview?> TryReadPreviewAsync(
        CaptureScheduleRevisionSnapshot revision,
        CaptureLocationProvenance location,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(location);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? expansionKey;
        string? expansionSha256;
        string? algorithmVersion;
        string? timeZoneRuleSha256;
        string? solarVersion;
        DateTimeOffset previewStart;
        DateTimeOffset previewEnd;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT expansion_key, expansion_sha256, preview_start_unix_ms, preview_end_unix_ms,
                       expansion_algorithm_version, time_zone_rule_sha256, solar_algorithm_version
                FROM capture_schedule_expansions
                WHERE revision_id = $revision
                  AND deployment_location_id = $location
                  AND deployment_location_version = $location_version
                  AND preview_start_unix_ms <= $utc AND preview_end_unix_ms > $utc
                ORDER BY preview_start_unix_ms DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$revision", revision.RevisionId);
            command.Parameters.AddWithValue("$location", location.LocationId);
            command.Parameters.AddWithValue("$location_version", location.Version);
            command.Parameters.AddWithValue("$utc", utc.ToUniversalTime().ToUnixTimeMilliseconds());
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            expansionKey = reader.GetString(0);
            expansionSha256 = reader.GetString(1);
            previewStart = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            previewEnd = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
            algorithmVersion = reader.GetString(4);
            timeZoneRuleSha256 = reader.GetString(5);
            solarVersion = reader.GetString(6);
        }
        var intervals = new List<ExpandedScheduleInterval>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT interval_id, source, disposition, start_unix_ms, end_unix_ms,
                       local_date, setpoint_profile_id, solar_algorithm_version
                FROM capture_schedule_intervals
                WHERE expansion_key = $key ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$key", expansionKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                intervals.Add(new ExpandedScheduleInterval(
                    reader.GetString(0),
                    Enum.Parse<CaptureScheduleIntervalSource>(reader.GetString(1)),
                    reader.GetString(2) == "open" ? ExpandedScheduleDisposition.Open : ExpandedScheduleDisposition.Closed,
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                        ? null
                        : DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                    await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7)));
            }
        }
        var unavailable = new List<UnavailableScheduleWindow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT window_id, source, local_date, start_unix_ms, end_unix_ms,
                       reason_code, solar_algorithm_version
                FROM capture_schedule_unavailable
                WHERE expansion_key = $key ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$key", expansionKey);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                unavailable.Add(new UnavailableScheduleWindow(
                    reader.GetString(0),
                    Enum.Parse<CaptureScheduleIntervalSource>(reader.GetString(1)),
                    DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }
        return new CaptureSchedulePreview(
            revision.ScheduleSha256,
            expansionSha256,
            algorithmVersion,
            timeZoneRuleSha256,
            solarVersion,
            previewStart,
            previewEnd,
            intervals,
            unavailable);
    }

    public async Task<bool> GrantAdmissionAsync(
        string admissionId,
        string revisionId,
        string? oneShotOverrideId,
        DateTimeOffset decisionUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(admissionId) || admissionId.Length > 128 ||
            string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 128 ||
            oneShotOverrideId?.Length > 128)
        {
            throw new ArgumentException("The schedule admission identity is invalid.", nameof(admissionId));
        }
        decisionUtc = decisionUtc.ToUniversalTime();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT revision_id, override_id, decision_unix_ms
                    FROM capture_schedule_admissions WHERE admission_id = $id;
                    """;
                existing.Parameters.AddWithValue("$id", admissionId);
                using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var same = string.Equals(reader.GetString(0), revisionId, StringComparison.Ordinal) &&
                        (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                            ? oneShotOverrideId is null
                            : string.Equals(reader.GetString(1), oneShotOverrideId, StringComparison.Ordinal)) &&
                        reader.GetInt64(2) == decisionUtc.ToUnixTimeMilliseconds();
                    if (!same)
                    {
                        throw new CaptureScheduleStoreConflictException(
                            "The schedule admission identifier has different durable content.");
                    }
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
            if (oneShotOverrideId is not null)
            {
                var consumed = await ExecuteAsync(connection, transaction, """
                    UPDATE capture_schedule_overrides
                    SET consumed_unix_ms = $utc
                    WHERE override_id = $override AND schedule_revision_id = $revision
                      AND one_shot = 1 AND consumed_unix_ms IS NULL AND cleared_unix_ms IS NULL;
                    """, cancellationToken,
                    ("$utc", decisionUtc.ToUnixTimeMilliseconds()),
                    ("$override", oneShotOverrideId),
                    ("$revision", revisionId)).ConfigureAwait(false);
                if (consumed != 1)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }
            var now = Now();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO capture_schedule_admissions(
                    admission_id, revision_id, override_id, decision_unix_ms, created_unix_ms)
                VALUES ($id, $revision, $override, $decision, $created);
                UPDATE capture_schedule_state
                SET last_evaluated_unix_ms = CASE
                        WHEN last_evaluated_unix_ms IS NULL OR last_evaluated_unix_ms < $decision
                            THEN $decision ELSE last_evaluated_unix_ms END,
                    version = version + $version_increment,
                    updated_unix_ms = $created
                WHERE state_key = 1;
                """, cancellationToken,
                ("$id", admissionId),
                ("$revision", revisionId),
                ("$override", (object?)oneShotOverrideId ?? DBNull.Value),
                ("$decision", decisionUtc.ToUnixTimeMilliseconds()),
                ("$version_increment", oneShotOverrideId is null ? 0 : 1),
                ("$created", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            if (oneShotOverrideId is not null)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO capture_schedule_override_events(
                        event_id, override_id, event_kind, actor, reason, occurred_unix_ms)
                    VALUES ($event, $override, 'consumed', 'system', 'capture admission', $occurred);
                    """, cancellationToken,
                    ("$event", $"admission:{admissionId}"),
                    ("$override", oneShotOverrideId),
                    ("$occurred", decisionUtc.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordDecisionAsync(
        string revisionId,
        CaptureScheduleDecision decision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revisionId) || revisionId.Length > 128)
        {
            throw new ArgumentException("A valid schedule revision identifier is required.", nameof(revisionId));
        }
        ArgumentNullException.ThrowIfNull(decision);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var changed = await ExecuteAsync(connection, transaction: null, """
                UPDATE capture_schedule_state
                SET last_evaluated_unix_ms = CASE
                        WHEN last_evaluated_unix_ms IS NULL OR last_evaluated_unix_ms < $decision
                            THEN $decision ELSE last_evaluated_unix_ms END,
                    last_decision_unix_ms = $decision,
                    last_decision_admitted = $admitted,
                    last_decision_reason = $reason,
                    last_decision_profile_id = $profile,
                    last_decision_interval_id = $interval,
                    next_transition_unix_ms = $next,
                    updated_unix_ms = $updated
                WHERE state_key = 1 AND active_revision_id = $revision;
                """, cancellationToken,
                ("$decision", decision.DecisionUtc.ToUnixTimeMilliseconds()),
                ("$admitted", decision.Admitted ? 1 : 0),
                ("$reason", decision.Reason.ToString()),
                ("$profile", (object?)decision.SetpointProfileId ?? DBNull.Value),
                ("$interval", (object?)decision.Interval?.Id ?? DBNull.Value),
                ("$next", decision.NextTransitionUtc is { } next
                    ? next.ToUnixTimeMilliseconds()
                    : DBNull.Value),
                ("$updated", Now().ToUnixTimeMilliseconds()),
                ("$revision", revisionId)).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new CaptureScheduleStoreConflictException(
                    "The active capture schedule revision changed before the decision was recorded.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CaptureScheduleOverride>> GetActiveOverridesAsync(
        string revisionId,
        DateTimeOffset utc,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.override_id, r.schedule_sha256, o.mode, o.start_unix_ms, o.end_unix_ms,
                   o.setpoint_profile_id, o.one_shot, o.consumed_unix_ms
            FROM capture_schedule_overrides o
            JOIN capture_schedule_revisions r ON r.revision_id = o.schedule_revision_id
            WHERE o.schedule_revision_id = $revision
              AND cleared_unix_ms IS NULL
              AND end_unix_ms > $utc
            ORDER BY start_unix_ms, override_id;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$utc", utc.ToUniversalTime().ToUnixTimeMilliseconds());
        var values = new List<CaptureScheduleOverride>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new CaptureScheduleOverride(
                reader.GetString(0),
                revisionId,
                reader.GetString(1),
                reader.GetString(2) == "force_closed"
                    ? CaptureScheduleOverrideMode.ForceClosed
                    : CaptureScheduleOverrideMode.ForceOpen,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                reader.GetInt64(6) != 0,
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))));
        }
        return values;
    }

    public Task<CaptureScheduleStoreSnapshot> AddOverrideAsync(
        CaptureScheduleOverride scheduleOverride,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scheduleOverride);
        ValidateCommand(idempotencyKey, expectedVersion, actor, reason);
        return MutateAsync(
            idempotencyKey,
            "override-add",
            scheduleOverride,
            expectedVersion,
            actor,
            reason,
            async (connection, transaction, snapshot, now, token) =>
            {
            var revision = await ReadRevisionAsync(
                connection, transaction, scheduleOverride.ScheduleRevisionId, token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The capture schedule revision was not found.");
            if (!string.Equals(
                    revision.ScheduleSha256,
                    scheduleOverride.ScheduleRevisionSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !ValidOverride(scheduleOverride, revision.Definition))
            {
                throw new ArgumentException("The capture schedule override is invalid.", nameof(scheduleOverride));
            }
            var existing = await ReadOverrideAsync(
                connection, transaction, scheduleOverride.Id, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing != scheduleOverride)
                {
                    throw new CaptureScheduleStoreConflictException("The override identifier has different durable content.");
                }
                return snapshot;
            }
            await ExecuteAsync(connection, transaction, """
                INSERT INTO capture_schedule_overrides(
                    override_id, schedule_revision_id, mode, start_unix_ms, end_unix_ms,
                    setpoint_profile_id, one_shot, consumed_unix_ms, cleared_unix_ms,
                    actor, reason, created_unix_ms)
                VALUES ($id, $revision, $mode, $start, $end, $profile, $one_shot, $consumed,
                        NULL, $actor, $reason, $created);
                """, token,
                ("$id", scheduleOverride.Id),
                ("$revision", scheduleOverride.ScheduleRevisionId),
                ("$mode", scheduleOverride.Mode == CaptureScheduleOverrideMode.ForceClosed ? "force_closed" : "force_open"),
                ("$start", scheduleOverride.StartUtc.ToUniversalTime().ToUnixTimeMilliseconds()),
                ("$end", scheduleOverride.EndUtc.ToUniversalTime().ToUnixTimeMilliseconds()),
                ("$profile", (object?)scheduleOverride.SetpointProfileId ?? DBNull.Value),
                ("$one_shot", scheduleOverride.OneShot ? 1 : 0),
                ("$consumed", scheduleOverride.ConsumedUtc is { } consumed
                    ? consumed.ToUniversalTime().ToUnixTimeMilliseconds()
                    : DBNull.Value),
                ("$actor", actor),
                ("$reason", (object?)reason ?? DBNull.Value),
                ("$created", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO capture_schedule_override_events(
                    event_id, override_id, event_kind, actor, reason, occurred_unix_ms)
                VALUES ($event, $override, 'created', $actor, $reason, $occurred);
                """, token,
                ("$event", idempotencyKey),
                ("$override", scheduleOverride.Id),
                ("$actor", actor),
                ("$reason", (object?)reason ?? DBNull.Value),
                ("$occurred", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE capture_schedule_state
                SET version = version + 1, updated_unix_ms = $updated
                WHERE state_key = 1;
                """, token, ("$updated", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            return snapshot with { Version = snapshot.Version + 1, UpdatedUtc = now };
            },
            cancellationToken);
    }

    public Task<CaptureScheduleStoreSnapshot> ClearOverrideAsync(
        string overrideId,
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(overrideId) || overrideId.Length > 128)
        {
            throw new ArgumentException("The override clear identity is invalid.", nameof(overrideId));
        }
        ValidateCommand(idempotencyKey, expectedVersion, actor, reason);
        return MutateAsync(
            idempotencyKey,
            "override-clear",
            overrideId,
            expectedVersion,
            actor,
            reason,
            async (connection, transaction, snapshot, now, token) =>
            {
            var occurred = now.ToUnixTimeMilliseconds();
            var changed = await ExecuteAsync(connection, transaction, """
                UPDATE capture_schedule_overrides
                SET cleared_unix_ms = $occurred
                WHERE override_id = $override AND cleared_unix_ms IS NULL;
                """, token,
                ("$occurred", occurred),
                ("$override", overrideId)).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new CaptureScheduleStoreConflictException("The capture schedule override is not active.");
            }
            await ExecuteAsync(connection, transaction, """
                INSERT INTO capture_schedule_override_events(
                    event_id, override_id, event_kind, actor, reason, occurred_unix_ms)
                VALUES ($event, $override, 'cleared', $actor, $reason, $occurred);
                """, token,
                ("$event", idempotencyKey),
                ("$override", overrideId),
                ("$actor", actor),
                ("$reason", (object?)reason ?? DBNull.Value),
                ("$occurred", occurred)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                UPDATE capture_schedule_state
                SET version = version + 1, updated_unix_ms = $updated
                WHERE state_key = 1;
                """, token, ("$updated", occurred)).ConfigureAwait(false);
            return snapshot with { Version = snapshot.Version + 1, UpdatedUtc = now };
            },
            cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<CaptureScheduleStoreSnapshot> MutateAsync<TPayload>(
        string idempotencyKey,
        string commandKind,
        TPayload payload,
        long? expectedVersion,
        string actor,
        string? reason,
        Func<SqliteConnection, SqliteTransaction, CaptureScheduleStoreSnapshot, DateTimeOffset,
            CancellationToken, Task<CaptureScheduleStoreSnapshot>> mutation,
        CancellationToken cancellationToken)
    {
        await _rawCaptureIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var payloadSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            CommandKind = commandKind,
            Payload = payload,
            expectedVersion,
            actor,
            reason
        });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var replay = await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                if (!string.Equals(replay.Value.PayloadSha256, payloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CaptureScheduleStoreConflictException("The idempotency key has different durable content.");
                }
                var active = await ReadRevisionAsync(
                    connection, transaction, replay.Value.ActiveRevisionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The command result active revision is missing.");
                var pending = replay.Value.PendingRevisionId is null
                    ? null
                    : await ReadRevisionAsync(
                        connection, transaction, replay.Value.PendingRevisionId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The command result pending revision is missing.");
                var replaySnapshot = new CaptureScheduleStoreSnapshot(
                    active,
                    pending,
                    replay.Value.StateVersion,
                    replay.Value.LastEvaluatedUtc,
                    replay.Value.CompletedUtc);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replaySnapshot;
            }

            var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Capture schedule state has not been initialized.");
            if (expectedVersion.HasValue && expectedVersion.Value != snapshot.Version)
            {
                throw new CaptureScheduleStoreConflictException("The capture schedule state version has changed.");
            }
            var now = Now();
            var result = await mutation(connection, transaction, snapshot, now, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO capture_schedule_commands(
                    idempotency_key, command_kind, payload_sha256,
                    result_active_revision_id, result_pending_revision_id,
                    result_state_version, result_last_evaluated_unix_ms,
                    created_unix_ms, completed_unix_ms)
                VALUES ($key, $kind, $payload, $active, $pending, $version,
                        $last_evaluated, $now, $now);
                """, cancellationToken,
                ("$key", idempotencyKey),
                ("$kind", commandKind),
                ("$payload", payloadSha256),
                ("$active", result.ActiveRevision.RevisionId),
                ("$pending", (object?)result.PendingRevision?.RevisionId ?? DBNull.Value),
                ("$version", result.Version),
                ("$last_evaluated", result.LastEvaluatedUtc is { } lastEvaluated
                    ? lastEvaluated.ToUnixTimeMilliseconds()
                    : DBNull.Value),
                ("$now", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<CaptureScheduleStoreSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT active_revision_id, pending_revision_id, version,
                   last_evaluated_unix_ms, updated_unix_ms
            FROM capture_schedule_state WHERE state_key = 1;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var activeId = reader.GetString(0);
        var pendingId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(1);
        var version = reader.GetInt64(2);
        DateTimeOffset? lastEvaluated = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
        await reader.DisposeAsync().ConfigureAwait(false);
        var active = await ReadRevisionAsync(connection, transaction, activeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The active capture schedule revision is missing.");
        var pending = pendingId is null
            ? null
            : await ReadRevisionAsync(connection, transaction, pendingId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending capture schedule revision is missing.");
        return new CaptureScheduleStoreSnapshot(active, pending, version, lastEvaluated, updated);
    }

    private async Task<CaptureScheduleRevisionSnapshot> InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalCaptureProfileDefinition profile,
        string source,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        var revisionNumber = await ScalarLongAsync(
            connection, transaction, "SELECT COALESCE(MAX(revision_number), 0) + 1 FROM capture_schedule_revisions;",
            cancellationToken).ConfigureAwait(false);
        var profileSha256 = LocalCaptureProfileContract.ComputeSha256(profile);
        var scheduleSha256 = CaptureScheduleContract.ComputeSha256(profile.Schedule);
        var revisionId = $"profile-{revisionNumber:D8}-{profileSha256[..12]}";
        var json = Encoding.UTF8.GetBytes(CaptureContractJson.SerializeToElement(profile).GetRawText());
        var now = Now();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO capture_schedule_revisions(
                revision_id, revision_number, profile_json, profile_sha256, schedule_sha256,
                source, actor, reason, created_unix_ms)
            VALUES ($id, $number, $json, $profile_sha, $schedule_sha, $source, $actor, $reason, $created);
            """, cancellationToken,
            ("$id", revisionId),
            ("$number", revisionNumber),
            ("$json", json),
            ("$profile_sha", profileSha256),
            ("$schedule_sha", scheduleSha256),
            ("$source", source),
            ("$actor", actor),
            ("$reason", (object?)reason ?? DBNull.Value),
            ("$created", now.ToUnixTimeMilliseconds())).ConfigureAwait(false);
        return new CaptureScheduleRevisionSnapshot(
            revisionId, revisionNumber, profile, profileSha256, scheduleSha256, source, actor, reason, now);
    }

    private static async Task<CaptureScheduleRevisionSnapshot?> FindRevisionBySha256Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sha256,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_id FROM capture_schedule_revisions
            WHERE profile_sha256 = $sha ORDER BY revision_number DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string revisionId
            ? await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task<CaptureScheduleRevisionSnapshot?> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string revisionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_number, profile_json, profile_sha256, schedule_sha256,
                   source, actor, reason, created_unix_ms
            FROM capture_schedule_revisions WHERE revision_id = $id;
            """;
        command.Parameters.AddWithValue("$id", revisionId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var profile = JsonSerializer.Deserialize<LocalCaptureProfileDefinition>((byte[])reader.GetValue(1), SerializerOptions)
            ?? throw new InvalidDataException("A durable local capture profile is invalid JSON.");
        var validation = LocalCaptureProfileContract.Validate(profile);
        var profileSha256 = reader.GetString(2);
        var scheduleSha256 = reader.GetString(3);
        if (!validation.IsValid || !string.Equals(
                profileSha256, LocalCaptureProfileContract.ComputeSha256(profile), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                scheduleSha256, CaptureScheduleContract.ComputeSha256(profile.Schedule), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A durable capture schedule revision failed validation.");
        }
        return new CaptureScheduleRevisionSnapshot(
            revisionId,
            reader.GetInt64(0),
            profile,
            profileSha256,
            scheduleSha256,
            reader.GetString(4),
            reader.GetString(5),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
    }

    private static async Task<CaptureScheduleOverride?> ReadOverrideAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string overrideId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.schedule_revision_id, r.schedule_sha256, o.mode, o.start_unix_ms, o.end_unix_ms,
                   o.setpoint_profile_id, o.one_shot, o.consumed_unix_ms
            FROM capture_schedule_overrides o
            JOIN capture_schedule_revisions r ON r.revision_id = o.schedule_revision_id
            WHERE o.override_id = $id;
            """;
        command.Parameters.AddWithValue("$id", overrideId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new CaptureScheduleOverride(
            overrideId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2) == "force_closed"
                ? CaptureScheduleOverrideMode.ForceClosed
                : CaptureScheduleOverrideMode.ForceOpen,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
            reader.GetInt64(6) != 0,
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
    }

    private static async Task<ScheduleCommandResult?> ReadCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_sha256, result_active_revision_id, result_pending_revision_id,
                   result_state_version, result_last_evaluated_unix_ms, completed_unix_ms
            FROM capture_schedule_commands WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ScheduleCommandResult(
                reader.GetString(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                reader.GetInt64(3),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)))
            : null;
    }

    private readonly record struct ScheduleCommandResult(
        string PayloadSha256,
        string ActiveRevisionId,
        string? PendingRevisionId,
        long StateVersion,
        DateTimeOffset? LastEvaluatedUtc,
        DateTimeOffset CompletedUtc);

    private static CaptureScheduleDefinition CreateLegacySchedule(CameraRigConfig rig)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                "legacy-pipeline",
                rig.Pipeline.NightExposure,
                rig.Pipeline.NightGain,
                rig.Pipeline.CaptureInterval,
                rig.Pipeline.CadenceMode)],
            [],
            LegacyAlwaysOpen: true,
            LegacySetpointProfileId: "legacy-pipeline");

    private static bool ValidOverride(
        CaptureScheduleOverride scheduleOverride,
        CaptureScheduleDefinition definition)
        => !string.IsNullOrWhiteSpace(scheduleOverride.Id) && scheduleOverride.Id.Length <= 128 &&
           !string.IsNullOrWhiteSpace(scheduleOverride.ScheduleRevisionId) &&
           scheduleOverride.ScheduleRevisionId.Length <= 128 &&
           scheduleOverride.ScheduleRevisionSha256.Length == 64 &&
           Enum.IsDefined(scheduleOverride.Mode) &&
           scheduleOverride.StartUtc.Offset == TimeSpan.Zero &&
           scheduleOverride.EndUtc.Offset == TimeSpan.Zero &&
           scheduleOverride.StartUtc.Ticks % TimeSpan.TicksPerMillisecond == 0 &&
           scheduleOverride.EndUtc.Ticks % TimeSpan.TicksPerMillisecond == 0 &&
           scheduleOverride.EndUtc > scheduleOverride.StartUtc &&
           (!scheduleOverride.ConsumedUtc.HasValue || scheduleOverride.ConsumedUtc.Value.Offset == TimeSpan.Zero) &&
           (!scheduleOverride.OneShot || scheduleOverride.Mode == CaptureScheduleOverrideMode.ForceOpen) &&
           (scheduleOverride.Mode == CaptureScheduleOverrideMode.ForceClosed
               ? scheduleOverride.SetpointProfileId is null
               : !string.IsNullOrWhiteSpace(scheduleOverride.SetpointProfileId) &&
                 definition.SetpointProfiles.Any(profile => string.Equals(
                     profile.Id, scheduleOverride.SetpointProfileId, StringComparison.Ordinal)));

    private static void ValidateCommand(
        string idempotencyKey,
        long? expectedVersion,
        string actor,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw new ArgumentException("A valid idempotency key is required.", nameof(idempotencyKey));
        }
        if (expectedVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }
        ValidateActor(actor, reason);
    }

    private static void ValidateActor(string actor, string? reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128)
        {
            throw new ArgumentException("A valid actor is required.", nameof(actor));
        }
        if (reason?.Length > 512)
        {
            throw new ArgumentException("The reason is too long.", nameof(reason));
        }
    }

    private DateTimeOffset Now()
        => DateTimeOffset.FromUnixTimeMilliseconds(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.")]
    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
        => connection.BeginTransaction(deferred: false);
}
