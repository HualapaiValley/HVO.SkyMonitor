using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

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
    private bool _hasExecutionSchema;

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
        _hasExecutionSchema = await HasExecutionSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnsureCanAcceptAsync(long payloadLength, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        var now = _timeProvider.GetUtcNow();
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ReassertUnauditedTransientHoldsAsync(connection, cancellationToken).ConfigureAwait(false);
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
            if (!_hasExecutionSchema)
            {
                _hasExecutionSchema = await HasExecutionSchemaAsync(connection, cancellationToken, transaction)
                    .ConfigureAwait(false);
            }
            var now = _timeProvider.GetUtcNow();
            if (_hasExecutionSchema && string.Equals(lane.Name, "standard", StringComparison.Ordinal))
            {
                await ExpireLiveExecutionsAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
            }
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
                    token,
                    string.Equals(lane.Name, "standard", StringComparison.Ordinal) && candidate.ExecutionId is { } executionId
                        ? new ProcessingExecutionContext(
                            executionId,
                            ProcessingGraphExecutionClass.Live,
                            candidate.GraphRevisionId!,
                            candidate.LocalPlanIdentitySha256!,
                             candidate.AllowAutomaticPublication,
                             candidate.WorkId,
                             token,
                             owner,
                             ResolveExecutionDeadline(candidate, now))
                        : null);
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
            if (candidate.ExecutionId is { } claimedExecutionId)
            {
                using var startExecution = connection.CreateCommand();
                startExecution.Transaction = transaction;
                startExecution.CommandText = """
                    UPDATE processing_executions
                    SET status = 'Running',
                        deadline_unix_ms = CASE WHEN started_unix_ms IS NULL
                            THEN $now + (deadline_unix_ms - accepted_unix_ms)
                            ELSE deadline_unix_ms END,
                        started_unix_ms = COALESCE(started_unix_ms, $now),
                        completed_unix_ms = NULL,
                        failure_reason = NULL,
                        attempt_count = attempt_count + 1
                    WHERE execution_id = $execution AND execution_class = 'Live';
                    """;
                startExecution.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                startExecution.Parameters.AddWithValue("$execution", claimedExecutionId.ToString("N"));
                if (await startExecution.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("Live processing execution claim did not update exactly one row.");
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

    private async Task MarkUnprocessableAsync(
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
        if (candidate.ExecutionId is { } executionId)
        {
            _ = await UpdateLiveExecutionAsync(
                connection, transaction, executionId, ProcessingGraphExecutionStatus.Failed,
                $"processing.{reason}", terminal: true, observedUtc: null, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The optional execution deadline clause is fixed internal SQL and all values remain parameterized.")]
    public async ValueTask<bool> RenewAsync(CaptureLaneLease lease, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var expires = now.AddSeconds(_options.LeaseSeconds);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        var executionClause = lease.Context.Execution is null
            ? string.Empty
            : """
               AND EXISTS (
                   SELECT 1 FROM processing_executions execution
                   WHERE execution.execution_id = $execution
                     AND execution.started_unix_ms IS NOT NULL
                     AND execution.deadline_unix_ms > $now)
              """;
        command.CommandText = $"""
            UPDATE capture_lane_work
            SET lease_expires_unix_ms = $expires, updated_unix_ms = $now
            WHERE work_id = $work AND state = 'leased'
              AND lease_token = $token AND lease_owner = $owner
              AND lease_expires_unix_ms > $now
              {executionClause};
            """;
        command.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$execution", lease.Context.Execution?.ExecutionId.ToString("N") ?? string.Empty);
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
            if (lease.Context.Execution is not null)
            {
                await ExpireLiveExecutionsAsync(
                    connection, transaction, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            var current = await ReadOwnershipAsync(connection, transaction, lease.WorkId, cancellationToken).ConfigureAwait(false);
            if (current.State == "completed" && string.Equals(current.CompletionToken, lease.LeaseToken, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (current.State != "leased")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new CaptureLaneLeaseLostException($"Capture lane '{lease.Lane}' lease ownership was lost.");
            }
            _faultInjector.Inject(CaptureLaneFaultPoint.BeforeCompletionCommit);
            var completedUtc = _timeProvider.GetUtcNow();
            EnsureOwned(lease, current, completedUtc);
            if (lease.Context.Execution is { } execution &&
                !await UpdateLiveExecutionAsync(
                    connection, transaction, execution.ExecutionId, ProcessingGraphExecutionStatus.Completed,
                    null, terminal: true, completedUtc, cancellationToken).ConfigureAwait(false))
            {
                await ExpireLiveExecutionsAsync(
                    connection, transaction, completedUtc, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new CaptureLaneLeaseLostException(
                    $"Capture lane '{lease.Lane}' crossed its processing deadline before completion.");
            }
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
                command.Parameters.AddWithValue("$now", completedUtc.ToUnixTimeMilliseconds());
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
            if (lease.Context.Execution is not null)
            {
                await ExpireLiveExecutionsAsync(
                    connection, transaction, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            var current = await ReadOwnershipAsync(connection, transaction, lease.WorkId, cancellationToken).ConfigureAwait(false);
            if (current.State != "leased")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new CaptureLaneLeaseLostException($"Capture lane '{lease.Lane}' lease ownership was lost.");
            }
            EnsureOwned(lease, current, _timeProvider.GetUtcNow());
            var deferred = result.Outcome == CaptureLaneHandlerOutcome.Deferred;
            var retry = deferred || result.Outcome == CaptureLaneHandlerOutcome.RetryableFailure && lease.Attempt < _options.MaximumAttempts;
            var now = _timeProvider.GetUtcNow();
            var state = retry ? "retry_wait" : "quarantined";
            var available = retry ? now + RetryDelay(lease.Attempt) : now;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE capture_lane_work
                    SET state = $state, available_unix_ms = $available,
                        attempt_count = CASE WHEN $deferred = 1 THEN attempt_count - 1 ELSE attempt_count END,
                        lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                        failure_reason = $reason, updated_unix_ms = $now
                    WHERE work_id = $work AND state = 'leased'
                      AND lease_token = $token AND lease_owner = $owner;
                    """;
                command.Parameters.AddWithValue("$state", state);
                command.Parameters.AddWithValue("$available", available.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$reason", NormalizeReason(result.Reason));
                command.Parameters.AddWithValue("$deferred", deferred ? 1 : 0);
                command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                AddLeaseParameters(command, lease);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new CaptureLaneLeaseLostException($"Capture lane '{lease.Lane}' lease ownership was lost.");
                }
            }
            await RecomputeRetentionHoldAsync(connection, transaction, current.RawRowId, cancellationToken).ConfigureAwait(false);
            if (lease.Context.Execution is { } execution)
            {
                _ = await UpdateLiveExecutionAsync(
                    connection,
                    transaction,
                    execution.ExecutionId,
                    retry ? ProcessingGraphExecutionStatus.Pending : ProcessingGraphExecutionStatus.Failed,
                    result.Reason,
                    terminal: !retry,
                    observedUtc: null,
                    cancellationToken).ConfigureAwait(false);
                await RecomputeRetentionHoldAsync(connection, transaction, current.RawRowId, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(retry ? CaptureLaneFaultPoint.AfterRetryCommit : CaptureLaneFaultPoint.AfterQuarantineCommit);
            return deferred
                ? CaptureLaneHandlerOutcome.Deferred
                : retry ? CaptureLaneHandlerOutcome.RetryableFailure : CaptureLaneHandlerOutcome.TerminalFailure;
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
            if (lease.Context.Execution is not null)
            {
                await ExpireLiveExecutionsAsync(
                    connection, transaction, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
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
            if (lease.Context.Execution is { } execution)
            {
                _ = await UpdateLiveExecutionAsync(
                    connection, transaction, execution.ExecutionId, ProcessingGraphExecutionStatus.Pending,
                    null, terminal: false, observedUtc: null, cancellationToken).ConfigureAwait(false);
            }
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
        await ReassertUnauditedTransientHoldsAsync(connection, cancellationToken).ConfigureAwait(false);
        var backlogs = new List<CaptureLaneBacklog>(_policy.Definitions.Count);
        foreach (var lane in _policy.Definitions)
        {
            backlogs.Add(await ReadBacklogAsync(connection, lane, null, cancellationToken).ConfigureAwait(false));
        }
        return backlogs;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query is selected from two internal constant statements; lane input remains parameterized.")]
    private async Task<Candidate?> ReadCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CaptureLaneDefinition lane,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var executionColumns = _hasExecutionSchema
            ? ", e.execution_id, e.graph_revision_id, e.local_plan_identity_sha256, e.allow_automatic_publication, e.accepted_unix_ms, e.deadline_unix_ms, e.started_unix_ms"
            : ", NULL, NULL, NULL, NULL, NULL, NULL, NULL";
        var executionJoin = _hasExecutionSchema
            ? "LEFT JOIN processing_executions e ON e.capture_id = r.capture_id AND e.execution_class = 'Live'"
            : string.Empty;
        var orderedSql = $"""
SELECT w.work_id, w.required, w.ordered, w.state, w.attempt_count,
       w.available_unix_ms, w.lease_expires_unix_ms, r.raw_capture_row_id,
       r.manifest_json, r.manifest_sha256, r.payload_relative_path, c.context_json, c.context_sha256
       {executionColumns}
FROM capture_lane_work w
JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
LEFT JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id
{executionJoin}
WHERE w.lane_name = $lane AND w.state NOT IN ('completed', 'abandoned')
ORDER BY w.agent_id, w.capture_sequence
LIMIT 1;
""";
        var unorderedSql = $"""
SELECT w.work_id, w.required, w.ordered, w.state, w.attempt_count,
       w.available_unix_ms, w.lease_expires_unix_ms, r.raw_capture_row_id,
       r.manifest_json, r.manifest_sha256, r.payload_relative_path, c.context_json, c.context_sha256
       {executionColumns}
FROM capture_lane_work w
JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id
LEFT JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id
{executionJoin}
WHERE w.lane_name = $lane AND (
     w.state = 'pending'
     OR (w.state = 'retry_wait' AND w.available_unix_ms <= $now)
     OR (w.state = 'leased' AND w.lease_expires_unix_ms <= $now))
ORDER BY w.available_unix_ms, r.agent_id, r.capture_sequence
LIMIT 1;
""";
        command.CommandText = lane.Ordered ? orderedSql : unorderedSql;
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
            await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(12),
            await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false)
                ? null
                : Guid.ParseExact(reader.GetString(13), "N"),
            await reader.IsDBNullAsync(14, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(14),
            await reader.IsDBNullAsync(15, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(15),
            !await reader.IsDBNullAsync(16, cancellationToken).ConfigureAwait(false) && reader.GetBoolean(16),
            await reader.IsDBNullAsync(17, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(17)),
            await reader.IsDBNullAsync(18, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(18)),
            await reader.IsDBNullAsync(19, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(19)));
    }

    private static async ValueTask<bool> HasExecutionSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'processing_executions';";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static bool IsEligible(Candidate candidate, DateTimeOffset now) => candidate.State switch
    {
        "pending" => true,
        "retry_wait" => candidate.AvailableUtc <= now,
        "leased" => candidate.LeaseExpiresUtc <= now,
        _ => false
    };

    private static DateTimeOffset? ResolveExecutionDeadline(Candidate candidate, DateTimeOffset claimedUtc)
        => candidate.ExecutionAcceptedUtc is { } acceptedUtc && candidate.ExecutionDeadlineUtc is { } deadlineUtc
            ? candidate.ExecutionStartedUtc is null
                ? claimedUtc + (deadlineUtc - acceptedUtc)
                : deadlineUtc
            : null;

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
                   COALESCE(SUM(CASE WHEN w.state = 'retry_wait' THEN 1 ELSE 0 END), 0),
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
        var retrying = reader.GetInt64(4);
        var quarantined = reader.GetInt64(5);
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
            retrying,
            quarantined,
            PendingCaptures: []);
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
        command.CommandText = ValidTransientAbandonmentsCteSql + "\n" + """
            SELECT
                (SELECT COUNT(*) FROM capture_lane_work
                 WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')) +
                    (SELECT COUNT(*) FROM transient_capture_work work WHERE state IN ('pending', 'candidate_persisted', 'quarantined') OR
                        (state = 'abandoned' AND NOT EXISTS (SELECT 1 FROM valid_abandonments operation
                            WHERE operation.raw_capture_row_id = work.raw_capture_row_id
                              AND operation.lane_work_id = work.lane_work_id
                              AND operation.completed_unix_ms = work.updated_unix_ms))) +
                    (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0),
                (SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (
                    SELECT raw_capture_row_id FROM capture_lane_work
                    WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                    UNION
                    SELECT raw_capture_row_id FROM transient_capture_work work
                    WHERE state IN ('pending', 'candidate_persisted', 'quarantined') OR
                        (state = 'abandoned' AND NOT EXISTS (SELECT 1 FROM valid_abandonments operation
                            WHERE operation.raw_capture_row_id = work.raw_capture_row_id
                              AND operation.lane_work_id = work.lane_work_id
                              AND operation.completed_unix_ms = work.updated_unix_ms))
                    UNION
                    SELECT s.raw_capture_row_id
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE c.source_hold_released = 0)),
                (SELECT MIN(created_unix_ms) FROM (
                    SELECT created_unix_ms FROM capture_lane_work
                    WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                    UNION ALL
                    SELECT created_unix_ms FROM transient_capture_work work
                    WHERE state IN ('pending', 'candidate_persisted', 'quarantined') OR
                        (state = 'abandoned' AND NOT EXISTS (SELECT 1 FROM valid_abandonments operation
                            WHERE operation.raw_capture_row_id = work.raw_capture_row_id
                              AND operation.lane_work_id = work.lane_work_id
                              AND operation.completed_unix_ms = work.updated_unix_ms))
                    UNION ALL
                    SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0)),
                (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'leased'),
                (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'retry_wait'),
                (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'transient' AND state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_capture_work work WHERE state = 'quarantined' OR
                        (state = 'abandoned' AND NOT EXISTS (SELECT 1 FROM valid_abandonments operation
                            WHERE operation.raw_capture_row_id = work.raw_capture_row_id
                              AND operation.lane_work_id = work.lane_work_id
                              AND operation.completed_unix_ms = work.updated_unix_ms))) +
                    (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidate_conflicts),
                (SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        var oldest = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? null
            : (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var leased = reader.GetInt64(3);
        var retrying = reader.GetInt64(4);
        var quarantined = reader.GetInt64(5);
        var pressure = reader.GetInt32(6);
        await reader.DisposeAsync().ConfigureAwait(false);
        using var sequencesCommand = connection.CreateCommand();
        sequencesCommand.Transaction = transaction;
        sequencesCommand.CommandText = ValidTransientAbandonmentsCteSql + ",\n" + """
            active_centers(raw_capture_row_id) AS (
                SELECT raw_capture_row_id
                FROM capture_lane_work
                WHERE lane_name = 'transient' AND state IN ('pending', 'leased', 'retry_wait', 'quarantined')
                UNION
                SELECT raw_capture_row_id
                FROM transient_capture_work work
                WHERE state IN ('pending', 'candidate_persisted', 'quarantined') OR
                    (state = 'abandoned' AND NOT EXISTS (SELECT 1 FROM valid_abandonments operation
                        WHERE operation.raw_capture_row_id = work.raw_capture_row_id
                          AND operation.lane_work_id = work.lane_work_id
                          AND operation.completed_unix_ms = work.updated_unix_ms))
                UNION
                SELECT source.raw_capture_row_id
                FROM transient_candidates candidate
                JOIN transient_candidate_sources source ON source.candidate_id = candidate.candidate_id
                WHERE candidate.source_hold_released = 0
                  AND source.source_ordinal = (
                      SELECT MAX(center_source.source_ordinal)
                      FROM transient_candidate_sources center_source
                      WHERE center_source.candidate_id = candidate.candidate_id)
            )
            SELECT agent_id, capture_sequence FROM (
                SELECT r.agent_id, r.capture_sequence, r.durable_ingress_unix_ms, r.raw_capture_row_id
                FROM active_centers active
                JOIN raw_captures r ON r.raw_capture_row_id = active.raw_capture_row_id
                ORDER BY r.durable_ingress_unix_ms DESC, r.raw_capture_row_id DESC
                LIMIT 3)
            ORDER BY agent_id, capture_sequence;
            """;
        var pendingCaptures = new List<CaptureLanePendingCapture>();
        using var sequencesReader = await sequencesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await sequencesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pendingCaptures.Add(new CaptureLanePendingCapture(
                sequencesReader.GetString(0),
                sequencesReader.GetInt64(1)));
        }
        return new CaptureLaneBacklog(
            lane.Name,
            lane.Required,
            count,
            bytes,
            oldest,
            leased,
            retrying,
            quarantined,
            pressure,
            pendingCaptures);
    }

    private static async Task ReassertUnauditedTransientHoldsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ValidTransientAbandonmentsCteSql + "\n" + """
            UPDATE raw_captures
            SET retention_hold = 1
            WHERE retention_hold = 0 AND raw_capture_row_id IN (
                SELECT work.raw_capture_row_id
                FROM transient_capture_work work
                WHERE work.state = 'abandoned'
                  AND NOT EXISTS (
                      SELECT 1 FROM valid_abandonments operation
                      WHERE operation.raw_capture_row_id = work.raw_capture_row_id
                        AND operation.lane_work_id = work.lane_work_id
                        AND operation.completed_unix_ms = work.updated_unix_ms));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task ExpireLiveExecutionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rawRows = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT DISTINCT COALESCE(pin.raw_capture_row_id, raw.raw_capture_row_id)
                FROM processing_executions execution
                JOIN raw_captures raw ON raw.capture_id = execution.capture_id
                                     AND raw.raw_artifact_id = execution.primary_artifact_id
                LEFT JOIN processing_execution_input_pins pin ON pin.execution_id = execution.execution_id
                WHERE execution.execution_class = 'Live'
                  AND execution.status IN ('Pending', 'Running')
                  AND ((execution.started_unix_ms IS NULL AND execution.maximum_age_unix_ms <= $now)
                       OR (execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms <= $now));
                """;
            read.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rawRows.Add(reader.GetInt64(0));
        }
        if (rawRows.Count == 0) return;
        using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = """
                UPDATE processing_executions
                SET status = 'Expired', completed_unix_ms = $now,
                    failure_reason = CASE WHEN started_unix_ms IS NULL
                        THEN 'processing.live-maximum-age' ELSE 'processing.live-deadline' END
                WHERE execution_class = 'Live' AND status IN ('Pending', 'Running')
                  AND ((started_unix_ms IS NULL AND maximum_age_unix_ms <= $now)
                       OR (started_unix_ms IS NOT NULL AND deadline_unix_ms <= $now));
                UPDATE capture_lane_work
                SET state = 'quarantined', failure_reason = 'processing-expired',
                    lease_token = NULL, lease_owner = NULL, lease_expires_unix_ms = NULL,
                    updated_unix_ms = $now
                WHERE lane_name = 'standard' AND state IN ('pending', 'retry_wait', 'leased')
                  AND EXISTS (
                    SELECT 1 FROM raw_captures raw
                    JOIN processing_executions execution ON execution.capture_id = raw.capture_id
                    WHERE raw.raw_capture_row_id = capture_lane_work.raw_capture_row_id
                      AND execution.execution_class = 'Live' AND execution.status = 'Expired');
                UPDATE processing_execution_input_pins
                SET released_flag = 1, released_unix_ms = $now
                WHERE released_flag = 0 AND execution_id IN (
                    SELECT execution_id FROM processing_executions
                    WHERE execution_class = 'Live' AND status = 'Expired');
                UPDATE processing_execution_output_input_pins
                SET released_flag = 1, released_unix_ms = $now
                WHERE released_flag = 0 AND execution_id IN (
                    SELECT execution_id FROM processing_executions
                    WHERE execution_class = 'Live' AND status = 'Expired');
                UPDATE processing_node_attempts
                SET status = 'Interrupted', completed_unix_ms = $now,
                    reason = 'processing.live-expired'
                WHERE status = 'Running' AND execution_id IN (
                    SELECT execution_id FROM processing_executions
                    WHERE execution_class = 'Live' AND status = 'Expired');
                UPDATE processing_execution_nodes
                SET status = 'TerminalFailure', completed_unix_ms = $now,
                    reason = 'processing.live-expired'
                WHERE status IN ('Pending', 'Running', 'RetryableFailure') AND execution_id IN (
                    SELECT execution_id FROM processing_executions
                    WHERE execution_class = 'Live' AND status = 'Expired');
                """;
            expire.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            await expire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var rawRow in rawRows)
        {
            await RecomputeRetentionHoldAsync(connection, transaction, rawRow, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> UpdateLiveExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        ProcessingGraphExecutionStatus status,
        string? reason,
        bool terminal,
        DateTimeOffset? observedUtc,
        CancellationToken cancellationToken)
    {
        var now = observedUtc ?? _timeProvider.GetUtcNow();
        if (status == ProcessingGraphExecutionStatus.Completed)
        {
            using var completionGuard = connection.CreateCommand();
            completionGuard.Transaction = transaction;
            completionGuard.CommandText = """
                SELECT COUNT(*) FROM processing_executions
                WHERE execution_id = $execution AND execution_class = 'Live'
                  AND status = 'Running' AND deadline_unix_ms > $now;
                """;
            completionGuard.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            completionGuard.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            if (Convert.ToInt64(
                    await completionGuard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                return false;
            }
        }
        var rawRows = new List<long>();
        if (terminal)
        {
            using var readPins = connection.CreateCommand();
            readPins.Transaction = transaction;
            readPins.CommandText = """
                SELECT raw_capture_row_id FROM processing_execution_input_pins
                WHERE execution_id = $execution AND released_flag = 0;
                """;
            readPins.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            using var reader = await readPins.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rawRows.Add(reader.GetInt64(0));
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_execution_outputs
            SET published_flag = 1
            WHERE execution_id = $execution AND $status = 'Completed'
              AND EXISTS (
                  SELECT 1 FROM processing_executions execution
                  WHERE execution.execution_id = $execution
                    AND execution.execution_class = 'Live'
                    AND execution.status = 'Running'
                    AND execution.deadline_unix_ms > $now);
            UPDATE processing_executions
            SET status = $status, failure_reason = $reason,
                completed_unix_ms = CASE WHEN $terminal = 1 THEN $now ELSE NULL END
            WHERE execution_id = $execution AND execution_class = 'Live'
              AND ($status != 'Completed' OR (status = 'Running' AND deadline_unix_ms > $now));
            UPDATE processing_execution_input_pins
            SET released_flag = 1, released_unix_ms = $now
            WHERE execution_id = $execution AND released_flag = 0 AND $terminal = 1;
            UPDATE processing_execution_output_input_pins
            SET released_flag = 1, released_unix_ms = $now
            WHERE execution_id = $execution AND released_flag = 0 AND $terminal = 1;
            UPDATE processing_node_attempts
            SET status = 'Interrupted', completed_unix_ms = $now,
                reason = COALESCE($reason, 'processing.execution-terminal')
            WHERE execution_id = $execution AND status = 'Running'
              AND $terminal = 1 AND $status != 'Completed';
            UPDATE processing_execution_nodes
            SET status = 'TerminalFailure', completed_unix_ms = $now,
                reason = COALESCE($reason, 'processing.execution-terminal')
            WHERE execution_id = $execution
              AND status IN ('Pending', 'Running', 'RetryableFailure')
              AND $terminal = 1 AND $status != 'Completed';
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var rawRow in rawRows)
        {
            await RecomputeRetentionHoldAsync(connection, transaction, rawRow, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The optional clause is a fixed internal schema capability and all data remains parameterized.")]
    private async Task RecomputeRetentionHoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var executionPinClause = _hasExecutionSchema
            ? "OR EXISTS (SELECT 1 FROM processing_execution_input_pins WHERE raw_capture_row_id = $raw AND released_flag = 0)"
            : string.Empty;
        command.CommandText = $"""
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
                {executionPinClause}
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

    private const string ValidTransientAbandonmentsCteSql = """
        WITH valid_abandonments(raw_capture_row_id, lane_work_id, completed_unix_ms) AS (
            SELECT operation.raw_capture_row_id, operation.lane_work_id, operation.completed_unix_ms
            FROM transient_runtime_operations operation
            JOIN transient_capture_work work
              ON work.raw_capture_row_id = operation.raw_capture_row_id
             AND work.lane_work_id = operation.lane_work_id
            JOIN capture_lane_work lane ON lane.work_id = operation.outer_lane_work_id
            JOIN raw_captures raw ON raw.raw_capture_row_id = operation.raw_capture_row_id
            WHERE operation.outer_lane_work_id = work.lane_work_id
              AND operation.agent_id = raw.agent_id AND operation.agent_id = lane.agent_id
              AND operation.capture_sequence = raw.capture_sequence
              AND operation.capture_sequence = lane.capture_sequence
              AND operation.capture_id = raw.capture_id
              AND operation.artifact_id = raw.raw_artifact_id
              AND operation.artifact_id = work.artifact_id
              AND operation.manifest_sha256 = raw.manifest_sha256
              AND operation.manifest_sha256 = work.manifest_sha256
              AND operation.payload_sha256 = raw.payload_sha256
              AND operation.processing_profile_sha256 = json_extract(
                  CAST(raw.manifest_json AS TEXT), '$.descriptor.profiles.processing.sha256')
              AND operation.mode = work.mode
              AND operation.required = work.required AND operation.required = lane.required
              AND operation.expected_outer_lane_state = lane.state
              AND operation.expected_outer_lane_state = 'completed'
              AND operation.expected_work_state = 'quarantined'
              AND operation.expected_frame_state = 'quarantined'
              AND length(operation.expected_failure_reason) BETWEEN 1 AND 128
              AND operation.expected_outer_lane_updated_unix_ms = lane.updated_unix_ms
              AND operation.expected_work_updated_unix_ms > 0
              AND operation.expected_work_updated_unix_ms <= operation.completed_unix_ms
              AND operation.expected_frame_updated_unix_ms > 0
              AND operation.expected_frame_updated_unix_ms <= operation.completed_unix_ms
              AND work.state = 'abandoned' AND work.updated_unix_ms = operation.completed_unix_ms
              AND length(operation.idempotency_key) BETWEEN 1 AND 128
              AND operation.action = 'abandon' AND operation.result_state = 'abandoned'
              AND length(operation.deployment_run_id) BETWEEN 1 AND 128
              AND substr(operation.deployment_run_id, 1, 1) GLOB '[A-Za-z0-9]'
              AND operation.deployment_run_id NOT GLOB '*[^A-Za-z0-9._-]*'
              AND length(operation.inventory_sha256) = 64
              AND operation.inventory_sha256 NOT GLOB '*[^0-9A-F]*'
              AND operation.legacy_ownership_externally_established = 1
              AND length(operation.actor) BETWEEN 1 AND 128
              AND operation.reason_code IN (
                  'invalid-source', 'irrecoverable-evidence', 'operator-approved-loss')
              AND operation.receipt_identity_sha256 = hvo_sha256(json_array(
                  operation.idempotency_key, operation.raw_capture_row_id, operation.lane_work_id,
                  operation.outer_lane_work_id, operation.agent_id, operation.capture_sequence,
                  operation.capture_id, operation.artifact_id, operation.manifest_sha256,
                  operation.payload_sha256, operation.processing_profile_sha256, operation.mode,
                  operation.required, operation.expected_outer_lane_state, operation.expected_work_state,
                  operation.expected_frame_state, operation.expected_failure_reason,
                  operation.expected_outer_lane_updated_unix_ms, operation.expected_work_updated_unix_ms,
                  operation.expected_frame_updated_unix_ms, operation.deployment_run_id,
                  operation.inventory_sha256, operation.legacy_ownership_externally_established,
                  operation.action, operation.actor, operation.reason_code, operation.result_state,
                  operation.completed_unix_ms))
        )
        """;

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
        connection.CreateFunction<string?, string>(
            "hvo_sha256",
            static value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))),
            isDeterministic: true);
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
        string? ContextSha256,
        Guid? ExecutionId,
        string? GraphRevisionId,
        string? LocalPlanIdentitySha256,
        bool AllowAutomaticPublication,
        DateTimeOffset? ExecutionAcceptedUtc,
        DateTimeOffset? ExecutionDeadlineUtc,
        DateTimeOffset? ExecutionStartedUtc);

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
