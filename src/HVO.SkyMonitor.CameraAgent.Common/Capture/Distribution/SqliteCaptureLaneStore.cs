using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal sealed class SqliteCaptureLaneStore(
    string root,
    int busyTimeoutSeconds,
    CaptureDistributionOptions options,
    CaptureLanePolicy policy,
    TimeProvider timeProvider,
    ICaptureLaneFaultInjector faultInjector,
    Action<TimeSpan>? lockWaitRecorder = null) : ICaptureLaneStore
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly string _databasePath = Path.Combine(Path.GetFullPath(root), "journal", "raw-ingress.db");
    private readonly int _busyTimeoutSeconds = busyTimeoutSeconds;
    private readonly CaptureDistributionOptions _options = options;
    private readonly CaptureLanePolicy _policy = policy;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ICaptureLaneFaultInjector _faultInjector = faultInjector;
    private readonly Action<TimeSpan>? _lockWaitRecorder = lockWaitRecorder;

    public async ValueTask InitializeLanesAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_lane_definitions;";
        if (Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) < 1)
        {
            throw new InvalidDataException("Capture lane definitions were not initialized.");
        }
    }

    public async ValueTask EnsureCanAcceptAsync(long payloadLength, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        var now = _timeProvider.GetUtcNow();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        string? blockedLane = null;
        foreach (var lane in _policy.Definitions.Where(static lane => lane.Enabled && lane.Required))
        {
            var backlog = await ReadBacklogAsync(connection, lane, transaction, cancellationToken).ConfigureAwait(false);
            var age = backlog.OldestPendingUtc is { } oldest ? now - oldest : TimeSpan.Zero;
            var hard = backlog.QuarantineCount > 0 ||
                       backlog.PendingCount >= _options.RequiredMaximumPendingCount ||
                       CaptureLanePressureMath.ExceedsAfterAdding(
                           backlog.PendingBytes, payloadLength, _options.RequiredMaximumPendingBytes) ||
                       age >= TimeSpan.FromMinutes(_options.RequiredMaximumOldestAgeMinutes);
            var recovered = backlog.QuarantineCount == 0 &&
                            backlog.PendingCount * 100 < _options.RequiredMaximumPendingCount * _options.PressureRecoveryPercent &&
                            !CaptureLanePressureMath.IsAtOrAbovePercentage(
                                backlog.PendingBytes,
                                _options.RequiredMaximumPendingBytes,
                                _options.PressureRecoveryPercent) &&
                            age.TotalMinutes * 100 < _options.RequiredMaximumOldestAgeMinutes * _options.PressureRecoveryPercent;
            var warning = backlog.PendingCount * 100 >= _options.RequiredMaximumPendingCount * _options.PressureRecoveryPercent ||
                          CaptureLanePressureMath.IsAtOrAbovePercentage(
                              backlog.PendingBytes,
                              _options.RequiredMaximumPendingBytes,
                              _options.PressureRecoveryPercent);
            var next = hard || backlog.PressureLevel == 2 && !recovered ? 2 : warning ? 1 : 0;
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE capture_lane_definitions SET pressure_state = $pressure WHERE lane_name = $lane;";
            update.Parameters.AddWithValue("$pressure", next);
            update.Parameters.AddWithValue("$lane", lane.Name);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (next == 2)
            {
                blockedLane ??= lane.Name;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (blockedLane is not null)
        {
            throw new CaptureLaneBackpressureException($"Required capture lane '{blockedLane}' cannot accept another capture.");
        }
    }

    public async ValueTask<CaptureLaneLease?> ClaimAsync(
        CaptureLaneDefinition lane,
        string owner,
        CameraModuleConfig fallbackConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(fallbackConfiguration);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var now = _timeProvider.GetUtcNow();
            var candidate = await ReadCandidateAsync(
                connection, transaction, lane, now, cancellationToken).ConfigureAwait(false);
            if (candidate is null || !IsEligible(candidate, now))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var token = Guid.NewGuid().ToString("N");
            CaptureLaneHandlerContext context;
            try
            {
                var parsed = CaptureContractJson.ParseManifest(candidate.ManifestJson);
                if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest)
                {
                    throw new InvalidDataException("Capture lane work references an invalid committed manifest.");
                }
                if (!string.Equals(
                        CaptureContractJson.ComputeManifestSha256(candidate.ManifestJson),
                        candidate.ManifestSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Capture lane work references altered committed manifest bytes.");
                }
                var envelope = candidate.ContextJson is not null && candidate.ContextSha256 is not null
                    ? CaptureLaneEnvelopeSerializer.Deserialize(candidate.ContextJson, candidate.ContextSha256)
                    : CreateFallbackEnvelope(fallbackConfiguration, manifest.Descriptor);
                var absolutePath = Resolve(candidate.PayloadRelativePath);
                if (!File.Exists(absolutePath) || !File.Exists(Path.ChangeExtension(absolutePath, ".json")))
                {
                    throw new FileNotFoundException("Capture lane evidence is no longer retained.");
                }
                var stored = new StoredFrameReference(
                    candidate.PayloadRelativePath,
                    absolutePath,
                    manifest.Descriptor.Timing.ExposureStartedUtc,
                    FrameArtifactRole.Raw);
                var receipt = new RawCaptureReceipt(
                    RawIngressOutcome.Existing,
                    manifest,
                    stored,
                    candidate.ManifestSha256);
                context = new CaptureLaneHandlerContext(
                    lane.Name,
                    candidate.AttemptCount + 1,
                    envelope.Configuration,
                    envelope.Submission,
                    receipt,
                    candidate.WorkId,
                    token);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await MarkUnprocessableAsync(
                    connection,
                    transaction,
                    candidate,
                    candidate.Required ? "quarantined" : "abandoned",
                    candidate.Required ? "evidence-invalid" : "evidence-expired",
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var expires = now.AddSeconds(_options.LeaseSeconds);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE capture_lane_work
                    SET state = 'leased', attempt_count = attempt_count + 1,
                        lease_token = $token, lease_owner = $owner, lease_expires_unix_ms = $expires,
                        updated_unix_ms = $now
                    WHERE work_id = $work;
                    """;
                update.Parameters.AddWithValue("$token", token);
                update.Parameters.AddWithValue("$owner", owner);
                update.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$work", candidate.WorkId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("Capture lane claim did not update exactly one row.");
                }
            }
            await SetRawRetentionHoldAsync(connection, transaction, candidate.RawRowId, hold: true, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CaptureLaneFaultPoint.AfterClaimCommitted);
            return new CaptureLaneLease(
                candidate.WorkId,
                lane.Name,
                candidate.Required,
                candidate.Ordered,
                candidate.AttemptCount + 1,
                token,
                owner,
                expires,
                context);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private static async Task MarkUnprocessableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Candidate candidate,
        string state,
        string reason,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE capture_lane_work
                SET state = $state, failure_reason = $reason,
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = unixepoch('subsec') * 1000
                WHERE work_id = $work;
                """;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$work", candidate.WorkId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await RecomputeRetentionHoldAsync(
            connection, transaction, candidate.RawRowId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RenewAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var expires = now.AddSeconds(_options.LeaseSeconds);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capture_lane_work
            SET lease_expires_unix_ms = $expires, updated_unix_ms = $now
            WHERE work_id = $work AND state = 'leased'
              AND lease_token = $token AND lease_owner = $owner
              AND lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        AddLeaseParameters(command, lease);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask CompleteAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
    {
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadOwnershipAsync(connection, transaction, lease.WorkId, cancellationToken).ConfigureAwait(false);
            if (current.State == "completed" && string.Equals(current.CompletionToken, lease.LeaseToken, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            EnsureOwned(lease, current, _timeProvider.GetUtcNow());
            _faultInjector.Inject(CaptureLaneFaultPoint.BeforeCompletionCommit);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE capture_lane_work
                    SET state = 'completed', completion_token = $token, completed_unix_ms = $now,
                        lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                        failure_reason = NULL, updated_unix_ms = $now
                    WHERE work_id = $work AND state = 'leased'
                      AND lease_token = $token AND lease_owner = $owner;
                    """;
                command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                AddLeaseParameters(command, lease);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CaptureLaneLeaseLostException($"Capture lane '{lease.Lane}' lease ownership was lost.");
                }
            }
            await RecomputeRetentionHoldAsync(connection, transaction, current.RawRowId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(CaptureLaneFaultPoint.AfterCompletionCommit);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask<CaptureLaneHandlerOutcome> FailAsync(
        CaptureLaneLease lease,
        CaptureLaneHandlerResult result,
        CancellationToken cancellationToken)
    {
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadOwnershipAsync(connection, transaction, lease.WorkId, cancellationToken).ConfigureAwait(false);
            EnsureOwned(lease, current, _timeProvider.GetUtcNow());
            var retry = result.Outcome == CaptureLaneHandlerOutcome.RetryableFailure && lease.Attempt < _options.MaximumAttempts;
            var now = _timeProvider.GetUtcNow();
            var state = retry ? "retry_wait" : "quarantined";
            var available = retry ? now + RetryDelay(lease.Attempt) : now;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE capture_lane_work
                    SET state = $state, available_unix_ms = $available,
                        lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                        failure_reason = $reason, updated_unix_ms = $now
                    WHERE work_id = $work AND state = 'leased'
                      AND lease_token = $token AND lease_owner = $owner;
                    """;
                command.Parameters.AddWithValue("$state", state);
                command.Parameters.AddWithValue("$available", available.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$reason", NormalizeReason(result.Reason));
                command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                AddLeaseParameters(command, lease);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CaptureLaneLeaseLostException($"Capture lane '{lease.Lane}' lease ownership was lost.");
                }
            }
            await RecomputeRetentionHoldAsync(connection, transaction, current.RawRowId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(retry ? CaptureLaneFaultPoint.AfterRetryCommit : CaptureLaneFaultPoint.AfterQuarantineCommit);
            return retry ? CaptureLaneHandlerOutcome.RetryableFailure : CaptureLaneHandlerOutcome.TerminalFailure;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask ReleaseAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
    {
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadOwnershipAsync(connection, transaction, lease.WorkId, cancellationToken).ConfigureAwait(false);
            if (current.State != "leased")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            EnsureOwned(lease, current, _timeProvider.GetUtcNow());
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE capture_lane_work
                    SET state = 'pending', available_unix_ms = $now,
                        lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                        updated_unix_ms = $now
                    WHERE work_id = $work AND state = 'leased'
                      AND lease_token = $token AND lease_owner = $owner;
                    """;
                command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                AddLeaseParameters(command, lease);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await RecomputeRetentionHoldAsync(connection, transaction, current.RawRowId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<CaptureLaneBacklog>> ReadBacklogsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var backlogs = new List<CaptureLaneBacklog>(_policy.Definitions.Count);
        foreach (var lane in _policy.Definitions)
        {
            backlogs.Add(await ReadBacklogAsync(connection, lane, null, cancellationToken).ConfigureAwait(false));
        }
        return backlogs;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query is selected from two internal constant statements; lane input remains parameterized.")]
    private static async Task<Candidate?> ReadCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureLaneDefinition lane,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = lane.Ordered
            ? """
              SELECT w.work_id, w.required, w.ordered, w.state, w.attempt_count,
                     w.available_unix_ms, w.lease_expires_unix_ms, r.raw_capture_row_id,
                     r.manifest_json, r.manifest_sha256, r.payload_relative_path, c.context_json, c.context_sha256
              FROM capture_lane_work w
              JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
               LEFT JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id
               WHERE w.lane_name = $lane AND w.state NOT IN ('completed', 'abandoned')
               ORDER BY w.agent_id, w.capture_sequence
               LIMIT 1;
              """
            : """
              SELECT w.work_id, w.required, w.ordered, w.state, w.attempt_count,
                     w.available_unix_ms, w.lease_expires_unix_ms, r.raw_capture_row_id,
                     r.manifest_json, r.manifest_sha256, r.payload_relative_path, c.context_json, c.context_sha256
              FROM capture_lane_work w
              JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
              LEFT JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id
              WHERE w.lane_name = $lane AND (
                    w.state = 'pending'
                    OR (w.state = 'retry_wait' AND w.available_unix_ms <= $now)
                    OR (w.state = 'leased' AND w.lease_expires_unix_ms <= $now))
              ORDER BY w.available_unix_ms, r.agent_id, r.capture_sequence
              LIMIT 1;
              """;
        command.Parameters.AddWithValue("$lane", lane.Name);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new Candidate(
            reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3), reader.GetInt32(4),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            reader.GetInt64(7), (byte[])reader[8], reader.GetString(9), reader.GetString(10),
            await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : (byte[])reader[11],
            await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(12));
    }

    private static bool IsEligible(Candidate candidate, DateTimeOffset now) => candidate.State switch
    {
        "pending" => true,
        "retry_wait" => candidate.AvailableUtc <= now,
        "leased" => candidate.LeaseExpiresUtc <= now,
        _ => false
    };

    private static async Task<CaptureLaneBacklog> ReadBacklogAsync(
        SqliteConnection connection,
        CaptureLaneDefinition lane,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (string.Equals(lane.Name, "transient", StringComparison.Ordinal))
        {
            return await ReadTransientBacklogAsync(
                connection, lane, transaction, cancellationToken).ConfigureAwait(false);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(r.payload_length), 0), MIN(r.durable_ingress_unix_ms),
                   COALESCE(SUM(CASE WHEN w.state = 'leased' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN w.state = 'quarantined' THEN 1 ELSE 0 END), 0)
            FROM capture_lane_work w
            JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
            WHERE w.lane_name = $lane AND w.state IN ('pending', 'leased', 'retry_wait', 'quarantined');
            """;
        command.Parameters.AddWithValue("$lane", lane.Name);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        DateTimeOffset? oldest = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var leased = reader.GetInt64(3);
        var quarantined = reader.GetInt64(4);
        await reader.DisposeAsync().ConfigureAwait(false);
        var required = count == 0
            ? lane.Required
            : await ReadHasRequiredAsync(connection, transaction, lane.Name, cancellationToken).ConfigureAwait(false);
        var backlog = new CaptureLaneBacklog(
            lane.Name,
            required,
            count,
            bytes,
            oldest,
            leased,
            quarantined);
        using var pressure = connection.CreateCommand();
        pressure.Transaction = transaction;
        pressure.CommandText = "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = $lane;";
        pressure.Parameters.AddWithValue("$lane", lane.Name);
        return backlog with
        {
            PressureLevel = Convert.ToInt32(
                await pressure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static async Task<CaptureLaneBacklog> ReadTransientBacklogAsync(
        SqliteConnection connection,
        CaptureLaneDefinition lane,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM capture_lane_work
                 WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')) +
                    (SELECT COUNT(*) FROM transient_capture_work WHERE state = 'pending') +
                    (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0),
                (SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (
                    SELECT raw_capture_row_id FROM capture_lane_work
                    WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                    UNION
                    SELECT raw_capture_row_id FROM transient_capture_work WHERE state = 'pending'
                    UNION
                    SELECT s.raw_capture_row_id
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE c.source_hold_released = 0)),
                (SELECT MIN(created_unix_ms) FROM (
                    SELECT created_unix_ms FROM capture_lane_work
                    WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                    UNION ALL
                    SELECT created_unix_ms FROM transient_capture_work WHERE state = 'pending'
                    UNION ALL
                    SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0)),
                (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'leased'),
                (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidate_conflicts),
                (SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new CaptureLaneBacklog(
            lane.Name,
            lane.Required,
            reader.GetInt64(0),
            reader.GetInt64(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5));
    }

    private static async Task<bool> ReadHasRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string lane,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM capture_lane_work
                WHERE lane_name = $lane AND required = 1
                  AND state IN ('pending', 'leased', 'retry_wait', 'quarantined'));
            """;
        command.Parameters.AddWithValue("$lane", lane);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<Ownership> ReadOwnershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long workId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_capture_row_id, state, lease_token, lease_owner, completion_token, lease_expires_unix_ms
            FROM capture_lane_work WHERE work_id = $work;
            """;
        command.Parameters.AddWithValue("$work", workId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Capture lane work no longer exists.");
        }
        return new Ownership(
            reader.GetInt64(0), reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)));
    }

    private static void EnsureOwned(CaptureLaneLease lease, Ownership ownership, DateTimeOffset now)
    {
        if (ownership.State != "leased" ||
            !string.Equals(ownership.LeaseToken, lease.LeaseToken, StringComparison.Ordinal) ||
            !string.Equals(ownership.LeaseOwner, lease.LeaseOwner, StringComparison.Ordinal) ||
            ownership.LeaseExpiresUtc is null || ownership.LeaseExpiresUtc <= now)
        {
            throw new CaptureLaneLeaseLostException($"Capture lane '{lease.Lane}' lease ownership was lost.");
        }
    }

    private static void AddLeaseParameters(SqliteCommand command, CaptureLaneLease lease)
    {
        command.Parameters.AddWithValue("$work", lease.WorkId);
        command.Parameters.AddWithValue("$token", lease.LeaseToken);
        command.Parameters.AddWithValue("$owner", lease.LeaseOwner);
    }

    private static async Task SetRawRetentionHoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        bool hold,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE raw_captures SET retention_hold = $hold WHERE raw_capture_row_id = $raw;";
        command.Parameters.AddWithValue("$hold", hold ? 1 : 0);
        command.Parameters.AddWithValue("$raw", rawRowId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecomputeRetentionHoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE raw_captures
            SET retention_hold = CASE WHEN
                EXISTS (
                    SELECT 1 FROM capture_lane_work
                    WHERE raw_capture_row_id = $raw
                      AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                OR EXISTS (
                    SELECT 1
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE s.raw_capture_row_id = $raw AND c.source_hold_released = 0)
                OR EXISTS (
                    SELECT 1 FROM transient_capture_work
                    WHERE raw_capture_row_id = $raw AND state = 'pending')
                THEN 1 ELSE 0 END
            WHERE raw_capture_row_id = $raw;
            """;
        command.Parameters.AddWithValue("$raw", rawRowId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CaptureLaneEnvelope CreateFallbackEnvelope(
        CameraModuleConfig configuration,
        ReconstructionDescriptor descriptor)
    {
        var requested = new CaptureSetpoint(
            descriptor.Controls.RequestedExposure,
            descriptor.Controls.RequestedGain,
            null,
            null);
        var effective = new CaptureSetpoint(
            descriptor.Controls.EffectiveExposure,
            descriptor.Controls.EffectiveGain,
            null,
            null);
        var request = new CaptureRequest(
            descriptor.Timing.RequestedStartUtc,
            configuration.Rig.Pipeline.CaptureInterval,
            CaptureMode.Still,
            requested);
        var result = new CaptureResult(null, effective, TimeSpan.Zero, CaptureMode.Still, false)
        {
            AcquisitionTiming = new CaptureAcquisitionTiming(
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc,
                descriptor.Timing.ReadoutCompletedUtc)
            {
                SetpointAppliedUtc = descriptor.Timing.SetpointAppliedUtc
            }
        };
        var submission = new CaptureLoopSubmission(
            request,
            result,
            descriptor.CycleEvidence?.ModuleCallStartedUtc ?? descriptor.Timing.RequestedStartUtc,
            configuration.Rig.Pipeline.CaptureInterval,
            TimeSpan.Zero)
        {
            CycleEvidence = descriptor.CycleEvidence
        };
        return new CaptureLaneEnvelope(
            configuration with { AgentId = descriptor.Capture.AgentId },
            submission);
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var seconds = Math.Min(
            _options.RetryMaximumDelaySeconds,
            _options.RetryInitialDelaySeconds * multiplier);
        return TimeSpan.FromSeconds(seconds);
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 64 ||
            reason.Any(static character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            return "handler-failure";
        }
        return reason;
    }

    private string Resolve(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capture lane payload path escapes the configured raw ingress root.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        return path;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000}; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return connection;
    }

    private SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
            return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        }
        finally
        {
            _lockWaitRecorder?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        }
    }

    private sealed record Candidate(
        long WorkId,
        bool Required,
        bool Ordered,
        string State,
        int AttemptCount,
        DateTimeOffset AvailableUtc,
        DateTimeOffset? LeaseExpiresUtc,
        long RawRowId,
        byte[] ManifestJson,
        string ManifestSha256,
        string PayloadRelativePath,
        byte[]? ContextJson,
        string? ContextSha256);

    private sealed record Ownership(
        long RawRowId,
        string State,
        string? LeaseToken,
        string? LeaseOwner,
        string? CompletionToken,
        DateTimeOffset? LeaseExpiresUtc);
}

[Serializable]
internal sealed class CaptureLaneBackpressureException : IOException
{
    public CaptureLaneBackpressureException()
    {
    }

    public CaptureLaneBackpressureException(string message)
        : base(message)
    {
    }

    public CaptureLaneBackpressureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[Serializable]
internal sealed class CaptureLaneLeaseLostException : InvalidOperationException
{
    public CaptureLaneLeaseLostException()
    {
    }

    public CaptureLaneLeaseLostException(string message)
        : base(message)
    {
    }

    public CaptureLaneLeaseLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
