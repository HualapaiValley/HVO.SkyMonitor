using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private static readonly JsonSerializerOptions ExecutionSerializerOptions = CreateExecutionSerializerOptions();

    internal async ValueTask<ProcessingGraphRegistryState> UpsertConfiguredBasicRevisionAsync(
        ProcessingGraphRevisionSnapshot revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await InsertRevisionAsync(
            connection, transaction, revision, ProcessingGraphRevisionLifecycle.Validated, cancellationToken)
            .ConfigureAwait(false);
        var state = await ReadRegistryRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (state is null)
        {
            await SetRevisionLifecycleAsync(
                connection, transaction, revision.State.RevisionId, ProcessingGraphRevisionLifecycle.Active, now,
                cancellationToken).ConfigureAwait(false);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO processing_graph_registry_state(
                    state_key, selection_mode, active_revision_id, configured_basic_revision_id,
                    state_version, updated_unix_ms)
                VALUES (1, 'ConfiguredBasic', $revision, $revision, 1, $now);
                """;
            insert.Parameters.AddWithValue("$revision", revision.State.RevisionId);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(state.ConfiguredBasicRevisionId, revision.State.RevisionId, StringComparison.Ordinal))
        {
            if (state.Mode == ProcessingGraphRegistryMode.ConfiguredBasic)
            {
                await SetRevisionLifecycleAsync(
                    connection, transaction, state.ActiveRevisionId, ProcessingGraphRevisionLifecycle.Validated, now,
                    cancellationToken).ConfigureAwait(false);
                await SetRevisionLifecycleAsync(
                    connection, transaction, revision.State.RevisionId, ProcessingGraphRevisionLifecycle.Active, now,
                    cancellationToken).ConfigureAwait(false);
            }
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_graph_registry_state
                SET configured_basic_revision_id = $configured,
                    active_revision_id = CASE WHEN selection_mode = 'ConfiguredBasic' THEN $configured ELSE active_revision_id END,
                    state_version = state_version + 1,
                    updated_unix_ms = $now
                WHERE state_key = 1;
                """;
            update.Parameters.AddWithValue("$configured", revision.State.RevisionId);
            update.Parameters.AddWithValue("$now", now);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRevisionState> ValidateRevisionAsync(
        string revisionId,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateRevisionId(revisionId);
        ValidateCommand(idempotencyKey, actor, reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var commandSha256 = CommandSha256("validate", revisionId, actor, reason);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, "validate", commandSha256);
            var replayed = await ReadRevisionAsync(connection, transaction, prior.ResultReference, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replayed.State;
        }
        var target = await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
        if (target.State.Lifecycle == ProcessingGraphRevisionLifecycle.Retired)
            throw new ProcessingGraphStoreConflictException("A retired processing graph revision cannot be validated.");
        if (target.State.Lifecycle == ProcessingGraphRevisionLifecycle.Draft)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_graph_revisions
                SET lifecycle = 'Validated', validated_unix_ms = $now
                WHERE revision_id = $revision AND lifecycle = 'Draft';
                """;
            update.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$revision", revisionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new ProcessingGraphStoreConflictException("The processing graph revision lifecycle changed.");
        }
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, "validate", commandSha256, actor, reason,
            revisionId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false)).State;
    }

    internal async ValueTask<ProcessingGraphRevisionState> InsertNamedRevisionAsync(
        ProcessingGraphRevisionSnapshot revision,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateCommand(idempotencyKey, actor, reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var commandSha256 = CommandSha256("create", revision.State.RevisionId, actor, reason);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, "create", commandSha256);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await ReadRevisionAsync(connection, null, prior.ResultReference, cancellationToken).ConfigureAwait(false)).State;
        }
        await InsertRevisionAsync(
            connection, transaction, revision, ProcessingGraphRevisionLifecycle.Draft, cancellationToken)
            .ConfigureAwait(false);
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, "create", commandSha256, actor, reason,
            revision.State.RevisionId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await ReadRevisionAsync(revision.State.RevisionId, cancellationToken).ConfigureAwait(false)).State;
    }

    internal async ValueTask<ProcessingGraphRegistryState> ActivateRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateRevisionId(revisionId);
        ValidateCommand(idempotencyKey, actor, reason);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var commandSha256 = CommandSha256("activate", $"{revisionId}:{expectedVersion}", actor, reason);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, "activate", commandSha256);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
        }
        var state = await ReadRegistryRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The processing graph registry has not been initialized.");
        if (state.StateVersion != expectedVersion)
        {
            throw new ProcessingGraphStoreConflictException("The processing graph registry version has changed.");
        }
        var target = await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
        if (target.State.Lifecycle != ProcessingGraphRevisionLifecycle.Validated &&
            target.State.Lifecycle != ProcessingGraphRevisionLifecycle.Active)
        {
            throw new ProcessingGraphStoreConflictException("Only a validated processing graph revision can be activated.");
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (!string.Equals(state.ActiveRevisionId, revisionId, StringComparison.Ordinal))
        {
            await SetRevisionLifecycleAsync(
                connection, transaction, state.ActiveRevisionId, ProcessingGraphRevisionLifecycle.Validated, now,
                cancellationToken).ConfigureAwait(false);
            await SetRevisionLifecycleAsync(
                connection, transaction, revisionId, ProcessingGraphRevisionLifecycle.Active, now,
                cancellationToken).ConfigureAwait(false);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_graph_registry_state
                SET selection_mode = 'Named', active_revision_id = $revision,
                    state_version = state_version + 1, updated_unix_ms = $now
                WHERE state_key = 1 AND state_version = $expected;
                """;
            update.Parameters.AddWithValue("$revision", revisionId);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$expected", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProcessingGraphStoreConflictException("The processing graph registry version has changed.");
            }
        }
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, "activate", commandSha256, actor, reason,
            revisionId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRegistryState> RollbackRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateRevisionId(revisionId);
        ValidateCommand(idempotencyKey, actor, reason);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var commandSha256 = CommandSha256("rollback", $"{revisionId}:{expectedVersion}", actor, reason);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, "rollback", commandSha256);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
        }
        var state = await ReadRegistryRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The processing graph registry has not been initialized.");
        if (state.StateVersion != expectedVersion)
        {
            throw new ProcessingGraphStoreConflictException("The processing graph registry version has changed.");
        }
        var current = await ReadRevisionAsync(
            connection, transaction, state.ActiveRevisionId, cancellationToken).ConfigureAwait(false);
        var target = await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
        if (target.State.Lifecycle != ProcessingGraphRevisionLifecycle.Validated ||
            !string.Equals(target.State.RevisionId, state.ConfiguredBasicRevisionId, StringComparison.Ordinal) &&
            target.State.ActivatedUtc is null)
        {
            throw new ProcessingGraphStoreConflictException(
                "Rollback requires the configured basic graph or a previously activated revision.");
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SetRevisionLifecycleAsync(
            connection, transaction, current.State.RevisionId, ProcessingGraphRevisionLifecycle.Validated, now,
            cancellationToken).ConfigureAwait(false);
        await SetRevisionLifecycleAsync(
            connection, transaction, target.State.RevisionId, ProcessingGraphRevisionLifecycle.Active, now,
            cancellationToken).ConfigureAwait(false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_graph_registry_state
                SET selection_mode = 'Named', active_revision_id = $revision,
                    state_version = state_version + 1, updated_unix_ms = $now
                WHERE state_key = 1 AND state_version = $expected;
                """;
            update.Parameters.AddWithValue("$revision", revisionId);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$expected", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProcessingGraphStoreConflictException("The processing graph registry version has changed.");
            }
        }
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, "rollback", commandSha256, actor, reason,
            revisionId, cancellationToken).ConfigureAwait(false);
        var queued = await QueueRevisionFactsAsync(
            connection,
            transaction,
            target.State.RevisionId,
            ProcessingGraphDeliveryFactKind.Activated,
            null,
            excludeRevision: false,
            RevisionFactBatchSize,
            cancellationToken).ConfigureAwait(false);
        if (queued < RevisionFactBatchSize)
        {
            _ = await QueueRevisionFactsAsync(
                connection,
                transaction,
                current.State.RevisionId,
                ProcessingGraphDeliveryFactKind.RolledBack,
                "local-rollback",
                excludeRevision: false,
                RevisionFactBatchSize - queued,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRegistryState> RetireRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        ValidateRevisionId(revisionId);
        ValidateCommand(idempotencyKey, actor, reason);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var commandSha256 = CommandSha256("retire", $"{revisionId}:{expectedVersion}", actor, reason);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, "retire", commandSha256);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
        }
        var state = await ReadRegistryRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The processing graph registry has not been initialized.");
        if (state.StateVersion != expectedVersion || string.Equals(state.ActiveRevisionId, revisionId, StringComparison.Ordinal) ||
            string.Equals(state.ConfiguredBasicRevisionId, revisionId, StringComparison.Ordinal))
        {
            throw new ProcessingGraphStoreConflictException("The processing graph revision cannot be retired from the current state.");
        }
        var revision = await ReadRevisionAsync(connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
        if (revision.State.Lifecycle == ProcessingGraphRevisionLifecycle.Retired)
        {
            throw new ProcessingGraphStoreConflictException("The processing graph revision is already retired.");
        }
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await SetRevisionLifecycleAsync(
            connection, transaction, revisionId, ProcessingGraphRevisionLifecycle.Retired, now, cancellationToken)
            .ConfigureAwait(false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_graph_registry_state
                SET state_version = state_version + 1, updated_unix_ms = $now
                WHERE state_key = 1 AND state_version = $expected;
                """;
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$expected", expectedVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProcessingGraphStoreConflictException("The processing graph registry version has changed.");
            }
        }
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, "retire", commandSha256, actor, reason,
            revisionId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRegistryState> ReadRegistryAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadRegistryRowAsync(connection, null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The processing graph registry has not been initialized.");
        using var command = connection.CreateCommand();
        command.CommandText = RevisionSelectSql + " ORDER BY created_unix_ms, revision_id;";
        var revisions = new List<ProcessingGraphRevisionState>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(ReadRevisionState(reader));
        }
        return new(row.Mode, row.ActiveRevisionId, row.ConfiguredBasicRevisionId, row.StateVersion, revisions);
    }

    internal async ValueTask<ProcessingGraphRevisionSnapshot> ReadRevisionAsync(
        string revisionId,
        CancellationToken cancellationToken)
    {
        ValidateRevisionId(revisionId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRevisionAsync(connection, null, revisionId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRevisionSnapshot> ReadActiveRevisionAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadRegistryRowAsync(connection, null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The processing graph registry has not been initialized.");
        return await ReadRevisionAsync(connection, null, state.ActiveRevisionId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask BeginNodeAttemptAsync(
        ProcessingExecutionContext execution,
        CaptureProcessingGraphNode node,
        int attempt,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(node);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await EnsureExecutionLeaseAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        using (var interrupt = connection.CreateCommand())
        {
            interrupt.Transaction = transaction;
            interrupt.CommandText = """
                UPDATE processing_node_attempts
                SET status = 'Interrupted', completed_unix_ms = $started,
                    reason = 'processing.lease-reclaimed'
                WHERE execution_id = $execution AND node_id = $node AND status = 'Running';
                """;
            interrupt.Parameters.AddWithValue("$started", startedUtc.ToUnixTimeMilliseconds());
            interrupt.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            interrupt.Parameters.AddWithValue("$node", node.Id);
            await interrupt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_execution_nodes
                SET status = 'Running', reason = NULL, attempt_count = $attempt,
                    started_unix_ms = COALESCE(started_unix_ms, $started), completed_unix_ms = NULL
                WHERE execution_id = $execution AND node_id = $node AND plan_sha256 = $plan;
                INSERT INTO processing_node_attempts(
                    execution_id, node_id, attempt_number, lease_owner, lease_token,
                    started_unix_ms, status)
                VALUES ($execution, $node, $attempt, $owner, $token, $started, 'Running')
                ON CONFLICT(execution_id, node_id, attempt_number) DO UPDATE SET
                    lease_owner = excluded.lease_owner,
                    lease_token = excluded.lease_token,
                    started_unix_ms = excluded.started_unix_ms,
                    completed_unix_ms = NULL,
                    status = 'Running',
                    outcome = NULL,
                    reason = NULL,
                    duration_ticks = NULL;
                UPDATE processing_executions
                SET status = 'Running', started_unix_ms = COALESCE(started_unix_ms, $started)
                WHERE execution_id = $execution AND status = 'Pending';
                """;
            update.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            update.Parameters.AddWithValue("$node", node.Id);
            update.Parameters.AddWithValue("$plan", node.PlanSha256);
            update.Parameters.AddWithValue("$attempt", attempt);
            update.Parameters.AddWithValue("$owner", execution.LeaseOwner ?? execution.ExecutionClass.ToString());
            update.Parameters.AddWithValue("$token", execution.LeaseToken ?? "ephemeral");
            update.Parameters.AddWithValue("$started", startedUtc.ToUnixTimeMilliseconds());
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<DurableProcessingNode?> ReadExecutionNodeAsync(
        Guid executionId,
        Guid captureId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT required, status, reason, attempt_count, plan_sha256,
                   started_unix_ms, completed_unix_ms
            FROM processing_execution_nodes
            WHERE execution_id = $execution AND node_id = $node;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var status = Enum.Parse<DurableProcessingNodeStatus>(reader.GetString(1));
        var reason = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2);
        DateTimeOffset? started = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
        var completed = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
            ? started ?? DateTimeOffset.UnixEpoch
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
        var required = reader.GetBoolean(0);
        var attempt = reader.GetInt32(3);
        var plan = reader.GetString(4);
        await reader.DisposeAsync().ConfigureAwait(false);
        var outputs = await ReadExecutionOutputsAsync(connection, executionId, nodeId, cancellationToken).ConfigureAwait(false);
        return new(captureId, nodeId, required, status, reason, attempt, plan, null, started, completed,
            started is null ? null : completed - started, null, null, outputs);
    }

    internal async ValueTask CompleteOutputlessExecutionNodeAsync(
        ProcessingExecutionContext execution,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await CompleteExecutionNodeCoreAsync(
            connection,
            transaction,
            execution,
            node,
            status,
            reason,
            attempt,
            completedUtc,
            duration,
            outcome,
            [],
            [],
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteExecutionNodeCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessingExecutionContext execution,
        CaptureProcessingGraphNode node,
        DurableProcessingNodeStatus status,
        string? reason,
        int attempt,
        DateTimeOffset completedUtc,
        TimeSpan? duration,
        ProcessingOutcomeStatus? outcome,
        IReadOnlyList<DurableProcessingOutput> outputs,
        bool[] outputPublication,
        CancellationToken cancellationToken)
    {
        await EnsureExecutionLeaseAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE processing_execution_nodes
                SET status = $status, reason = $reason, attempt_count = $attempt,
                    completed_unix_ms = $completed
                WHERE execution_id = $execution AND node_id = $node AND plan_sha256 = $plan;
                UPDATE processing_node_attempts
                SET completed_unix_ms = $completed, status = $status, outcome = $outcome,
                    reason = $reason, duration_ticks = $duration
                WHERE execution_id = $execution AND node_id = $node
                  AND attempt_number = $attempt AND status = 'Running';
                """;
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            update.Parameters.AddWithValue("$attempt", attempt);
            update.Parameters.AddWithValue("$completed", completedUtc.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            update.Parameters.AddWithValue("$node", node.Id);
            update.Parameters.AddWithValue("$plan", node.PlanSha256);
            update.Parameters.AddWithValue("$outcome", outcome?.ToString() ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$duration", duration?.Ticks ?? (object)DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        for (var ordinal = 0; ordinal < outputs.Count; ordinal++)
        {
            using var association = connection.CreateCommand();
            association.Transaction = transaction;
            association.CommandText = """
                INSERT INTO processing_execution_outputs(
                    execution_id, node_id, output_ordinal, output_identity_sha256, published_flag)
                VALUES ($execution, $node, $ordinal, $output, $published)
                ON CONFLICT(execution_id, node_id, output_ordinal) DO UPDATE SET
                    output_identity_sha256 = excluded.output_identity_sha256,
                    published_flag = MAX(processing_execution_outputs.published_flag, excluded.published_flag);
                """;
            association.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            association.Parameters.AddWithValue("$node", node.Id);
            association.Parameters.AddWithValue("$ordinal", ordinal);
            association.Parameters.AddWithValue("$output", outputs[ordinal].OutputIdentitySha256);
            association.Parameters.AddWithValue("$published", outputPublication[ordinal] ? 1 : 0);
            await association.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<IReadOnlyList<ProcessingGraphExecutionState>> ReadExecutionsAsync(
        ProcessingGraphExecutionClass? executionClass,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = ExecutionSelectSql + " " + """
            WHERE ($class IS NULL OR execution_class = $class)
            ORDER BY accepted_unix_ms DESC, execution_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$class", executionClass?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", maximumCount);
        var results = new List<ProcessingGraphExecutionState>(maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadExecutionState(reader));
        return results;
    }

    internal async ValueTask<ProcessingGraphExecutionState?> ReadExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExecutionAsync(connection, null, executionId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var execution = await ReadExecutionAsync(connection, null, executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null) return null;
        var nodes = new List<ProcessingGraphExecutionNodeState>();
        using var nodeCommand = connection.CreateCommand();
        nodeCommand.CommandText = """
            SELECT node_id, required, plan_sha256, status, reason, attempt_count,
                   started_unix_ms, completed_unix_ms
            FROM processing_execution_nodes
            WHERE execution_id = $execution
            ORDER BY rowid;
            """;
        nodeCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        var rows = new List<(string NodeId, bool Required, string Plan, string Status, string? Reason,
            int AttemptCount, DateTimeOffset? Started, DateTimeOffset? Completed)>();
        using (var reader = await nodeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                    reader.GetInt32(5),
                    await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                        ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                    await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                        ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))));
            }
        }
        foreach (var row in rows)
        {
            var attempts = new List<ProcessingGraphNodeAttemptState>();
            using (var attemptCommand = connection.CreateCommand())
            {
                attemptCommand.CommandText = """
                    SELECT attempt_number, lease_owner, started_unix_ms, completed_unix_ms,
                           status, outcome, reason, duration_ticks
                    FROM processing_node_attempts
                    WHERE execution_id = $execution AND node_id = $node
                    ORDER BY attempt_number;
                    """;
                attemptCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
                attemptCommand.Parameters.AddWithValue("$node", row.NodeId);
                using var reader = await attemptCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    attempts.Add(new(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                        await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                            ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                        reader.GetString(4),
                        await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                            ? null : Enum.Parse<ProcessingOutcomeStatus>(reader.GetString(5)),
                        await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                        await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                            ? null : TimeSpan.FromTicks(reader.GetInt64(7))));
                }
            }
            var durableOutputs = await ReadExecutionOutputsAsync(
                connection, executionId, row.NodeId, cancellationToken).ConfigureAwait(false);
            var inputs = await ReadExecutionInputsAsync(
                connection, executionId, row.NodeId, cancellationToken).ConfigureAwait(false);
            var outputs = durableOutputs.Select((output, ordinal) => new ProcessingGraphExecutionOutputState(
                ordinal,
                output.OutputIdentitySha256,
                output.ArtifactId,
                output.Artifact.Role,
                output.Artifact.Variant,
                output.AvailabilityState,
                output.AvailabilityReason)).ToArray();
            nodes.Add(new(
                row.NodeId, row.Required, row.Plan, row.Status, row.Reason, row.AttemptCount,
                row.Started, row.Completed, inputs, attempts, outputs));
        }
        return new(execution, nodes);
    }

    private static async ValueTask<IReadOnlyList<ProcessingGraphExecutionInputState>> ReadExecutionInputsAsync(
        SqliteConnection connection,
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_ordinal, window_position, 'RawCapture', capture_id, artifact_id,
                   descriptor_sha256, payload_sha256, NULL
            FROM processing_execution_inputs
            WHERE execution_id = $execution AND node_id = $node
            UNION ALL
            SELECT pin.input_ordinal, pin.window_position, 'ProcessingOutput',
                   output.capture_id, output.artifact_id, NULL, NULL, pin.output_identity_sha256
            FROM processing_execution_output_input_pins pin
            JOIN processing_outputs output
              ON output.output_identity_sha256 = pin.output_identity_sha256
            WHERE pin.execution_id = $execution AND pin.node_id = $node
            ORDER BY input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        var inputs = new List<ProcessingGraphExecutionInputState>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            inputs.Add(new(
                reader.GetInt32(0),
                reader.GetInt32(1),
                Enum.Parse<ProcessingGraphExecutionInputKind>(reader.GetString(2)),
                Guid.ParseExact(reader.GetString(3), "N"),
                Guid.ParseExact(reader.GetString(4), "N"),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7)));
        }
        return inputs;
    }

    private static async ValueTask InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProcessingGraphRevisionSnapshot revision,
        ProcessingGraphRevisionLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_graph_revisions(
                revision_id, graph_name, revision_name, lifecycle,
                definition_identity_sha256, shared_plan_identity_sha256, local_plan_identity_sha256,
                pipeline_json, definition_json, frozen_plan_json, nodes_json,
                created_unix_ms, validated_unix_ms)
            VALUES ($id, $name, $revision, $lifecycle, $definition, $shared_plan, $local_plan,
                    $pipeline, $definition_json, $plan_json, $nodes_json, $created, $validated)
            ON CONFLICT(revision_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", revision.State.RevisionId);
        command.Parameters.AddWithValue("$name", revision.State.Name);
        command.Parameters.AddWithValue("$revision", revision.State.Revision);
        command.Parameters.AddWithValue("$lifecycle", lifecycle.ToString());
        command.Parameters.AddWithValue("$definition", revision.State.DefinitionIdentitySha256);
        command.Parameters.AddWithValue("$shared_plan", revision.State.SharedPlanIdentitySha256);
        command.Parameters.AddWithValue("$local_plan", revision.State.LocalPlanIdentitySha256);
        command.Parameters.AddWithValue("$pipeline", revision.PipelineJson);
        command.Parameters.AddWithValue("$definition_json", revision.DefinitionJson);
        command.Parameters.AddWithValue("$plan_json", revision.FrozenPlanJson);
        command.Parameters.AddWithValue("$nodes_json", JsonSerializer.SerializeToUtf8Bytes(revision.Nodes, ExecutionSerializerOptions));
        command.Parameters.AddWithValue("$created", revision.State.CreatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$validated", revision.State.ValidatedUtc?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ProcessingGraphStoreConflictException(
                "The processing graph revision name and revision already identify different content.", exception);
        }
        var stored = await ReadRevisionAsync(
            connection, transaction, revision.State.RevisionId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored.State.Name, revision.State.Name, StringComparison.Ordinal) ||
            !string.Equals(stored.State.Revision, revision.State.Revision, StringComparison.Ordinal) ||
            !string.Equals(stored.State.DefinitionIdentitySha256, revision.State.DefinitionIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(stored.State.SharedPlanIdentitySha256, revision.State.SharedPlanIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(stored.State.LocalPlanIdentitySha256, revision.State.LocalPlanIdentitySha256, StringComparison.Ordinal) ||
            !stored.PipelineJson.AsSpan().SequenceEqual(revision.PipelineJson) ||
            !stored.DefinitionJson.AsSpan().SequenceEqual(revision.DefinitionJson) ||
            !stored.FrozenPlanJson.AsSpan().SequenceEqual(revision.FrozenPlanJson) ||
            !stored.Nodes.SequenceEqual(revision.Nodes))
        {
            throw new ProcessingGraphStoreConflictException(
                "The processing graph revision identity has different immutable content.");
        }
    }

    private static async ValueTask<ProcessingGraphRevisionSnapshot> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string revisionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RevisionSelectSql + " WHERE revision_id = $revision;";
        command.Parameters.AddWithValue("$revision", revisionId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("The processing graph revision was not found.");
        }
        var state = ReadRevisionState(reader);
        var pipelineJson = await reader.GetFieldValueAsync<byte[]>(11, cancellationToken).ConfigureAwait(false);
        var definitionJson = await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false);
        var frozenPlanJson = await reader.GetFieldValueAsync<byte[]>(13, cancellationToken).ConfigureAwait(false);
        var nodesJson = await reader.GetFieldValueAsync<byte[]>(14, cancellationToken).ConfigureAwait(false);
        var pipeline = JsonSerializer.Deserialize<CapturePipelineConfig>(pipelineJson, ExecutionSerializerOptions)
            ?? throw new InvalidDataException("The frozen processing pipeline is invalid.");
        var nodes = JsonSerializer.Deserialize<ImmutableArray<ProcessingExecutionNodeSeed>>(
            nodesJson, ExecutionSerializerOptions);
        if (nodes.IsDefault) throw new InvalidDataException("The frozen processing node plan is invalid.");
        return new(state, pipeline, pipelineJson, definitionJson, frozenPlanJson, nodes);
    }

    private static ProcessingGraphRevisionState ReadRevisionState(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<ProcessingGraphRevisionLifecycle>(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
        reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
        reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)));

    private static async ValueTask<RegistryRow?> ReadRegistryRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT selection_mode, active_revision_id, configured_basic_revision_id, state_version
            FROM processing_graph_registry_state WHERE state_key = 1;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(Enum.Parse<ProcessingGraphRegistryMode>(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private static async ValueTask SetRevisionLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string revisionId,
        ProcessingGraphRevisionLifecycle lifecycle,
        long now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_graph_revisions
            SET lifecycle = $lifecycle,
                activated_unix_ms = CASE WHEN $lifecycle = 'Active' THEN $now ELSE activated_unix_ms END,
                retired_unix_ms = CASE WHEN $lifecycle = 'Retired' THEN $now ELSE retired_unix_ms END
            WHERE revision_id = $revision;
            """;
        command.Parameters.AddWithValue("$lifecycle", lifecycle.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$revision", revisionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException("The processing graph revision was not found.");
        }
    }

    private static async ValueTask<StoredCommand?> ReadCommandAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT command_kind, command_sha256, result_reference
            FROM processing_graph_commands WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async ValueTask InsertCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string kind,
        string commandSha256,
        string actor,
        string? reason,
        string resultReference,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_graph_commands(
                idempotency_key, command_kind, command_sha256, actor, reason,
                result_reference, completed_unix_ms)
            VALUES ($key, $kind, $sha, $actor, $reason, $result, $completed);
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$sha", commandSha256);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", resultReference);
        command.Parameters.AddWithValue("$completed", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask EnsureExecutionLeaseAsync(
        ProcessingExecutionContext execution,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureExecutionLeaseAsync(connection, null, execution, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureExecutionLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProcessingExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (execution.ExecutionClass == ProcessingGraphExecutionClass.Live)
        {
            using var liveCommand = connection.CreateCommand();
            liveCommand.Transaction = transaction;
            liveCommand.CommandText = """
                SELECT COUNT(*)
                FROM capture_lane_work work
                JOIN processing_executions execution ON execution.execution_id = $execution
                WHERE work.work_id = $work AND work.state = 'leased'
                  AND work.lease_token = $token AND work.lease_owner = $owner
                  AND work.lease_expires_unix_ms > $now
                  AND execution.execution_class = 'Live' AND execution.status = 'Running'
                  AND execution.started_unix_ms IS NOT NULL AND execution.deadline_unix_ms > $now;
                """;
            liveCommand.Parameters.AddWithValue("$work", execution.WorkId);
            liveCommand.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            liveCommand.Parameters.AddWithValue("$token", execution.LeaseToken ?? string.Empty);
            liveCommand.Parameters.AddWithValue("$owner", execution.LeaseOwner ?? string.Empty);
            liveCommand.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            if (Convert.ToInt64(await liveCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "Live processing execution is stale, terminal, or expired and cannot publish durable state.");
            }
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM processing_replay_work
            WHERE work_id = $work AND execution_id = $execution AND state = 'Leased'
              AND lease_token = $token AND lease_owner = $owner
              AND lease_expires_unix_ms > $now;
            """;
        command.Parameters.AddWithValue("$work", execution.WorkId);
        command.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$token", execution.LeaseToken ?? string.Empty);
        command.Parameters.AddWithValue("$owner", execution.LeaseOwner ?? string.Empty);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new CaptureLaneLeaseLostException();
        }
    }

    private static async ValueTask<IReadOnlyList<DurableProcessingOutput>> ReadExecutionOutputsAsync(
        SqliteConnection connection,
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.output_identity_sha256, output.artifact_id, output.payload_relative_path,
                   output.sidecar_relative_path, output.descriptor_json, output.capture_id,
                   output.agent_id, output.node_id, output.role, output.variant,
                   output.recipe_identity_sha256, output.algorithms_json, output.compatibility_json,
                   output.total_integration_ticks, output.capture_sequence, output.product_kind,
                   output.product_schema_version, output.content_identity_sha256,
                   output.availability_state, output.availability_reason,
                   output.frame_artifact_recipe_version
            FROM processing_execution_outputs association
            JOIN processing_outputs output
              ON output.output_identity_sha256 = association.output_identity_sha256
            WHERE association.execution_id = $execution AND association.node_id = $node
            ORDER BY association.output_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        return (await ReadOutputRowsAsync(command, cancellationToken).ConfigureAwait(false))
            .Select(static row => row.Output).ToArray();
    }

    private async ValueTask ReleaseExecutionPinsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        var rawRows = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT DISTINCT raw_capture_row_id FROM processing_execution_input_pins
                WHERE execution_id = $execution AND released_flag = 0;
                """;
            read.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rawRows.Add(reader.GetInt64(0));
        }
        using (var release = connection.CreateCommand())
        {
            release.Transaction = transaction;
            release.CommandText = """
                UPDATE processing_execution_input_pins
                SET released_flag = 1, released_unix_ms = $now
                WHERE execution_id = $execution AND released_flag = 0;
                """;
            release.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            release.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var releaseOutputs = connection.CreateCommand())
        {
            releaseOutputs.Transaction = transaction;
            releaseOutputs.CommandText = """
                UPDATE processing_execution_output_input_pins
                SET released_flag = 1, released_unix_ms = $now
                WHERE execution_id = $execution AND released_flag = 0;
                """;
            releaseOutputs.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            releaseOutputs.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            await releaseOutputs.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var rawRow in rawRows)
        {
            await RecomputeRawRetentionHoldAsync(connection, transaction, rawRow, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask RecomputeRawRetentionHoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE raw_captures SET retention_hold = CASE WHEN
                EXISTS (SELECT 1 FROM capture_lane_work WHERE raw_capture_row_id = $raw
                    AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                OR EXISTS (SELECT 1 FROM transient_candidate_sources source
                    JOIN transient_candidates candidate ON candidate.candidate_id = source.candidate_id
                    WHERE source.raw_capture_row_id = $raw AND candidate.source_hold_released = 0)
                OR EXISTS (SELECT 1 FROM transient_capture_work
                    WHERE raw_capture_row_id = $raw AND state = 'pending')
                OR EXISTS (SELECT 1 FROM processing_execution_input_pins
                    WHERE raw_capture_row_id = $raw AND released_flag = 0)
                THEN 1 ELSE 0 END
            WHERE raw_capture_row_id = $raw;
            """;
        command.Parameters.AddWithValue("$raw", rawRowId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ProcessingGraphExecutionState?> ReadExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ExecutionSelectSql + " WHERE execution_id = $execution;";
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadExecutionState(reader) : null;
    }

    private static ProcessingGraphExecutionState ReadExecutionState(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        Enum.Parse<ProcessingGraphExecutionClass>(reader.GetString(1)),
        Enum.Parse<ProcessingGraphExecutionStatus>(reader.GetString(2)),
        Guid.ParseExact(reader.GetString(3), "N"),
        Guid.ParseExact(reader.GetString(4), "N"),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetInt32(11),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)),
        reader.IsDBNull(16) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16)),
        reader.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(17)),
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.GetBoolean(19),
        reader.GetInt32(20));

    private static void ValidateRevisionId(string revisionId)
    {
        if (revisionId is not { Length: 64 } || revisionId.Any(static value => !Uri.IsHexDigit(value)))
            throw new ArgumentException("The processing graph revision identity is invalid.", nameof(revisionId));
    }

    private static void ValidateCommand(string idempotencyKey, string actor, string? reason)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || reason?.Length > 256)
            throw new ArgumentException("The processing graph command is invalid.");
    }

    private static string CommandSha256(string kind, string value, string actor, string? reason) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{value}\n{actor}\n{reason}")));

    private static void EnsureIdempotent(StoredCommand prior, string kind, string commandSha256)
    {
        if (!string.Equals(prior.Kind, kind, StringComparison.Ordinal) ||
            !string.Equals(prior.CommandSha256, commandSha256, StringComparison.Ordinal))
            throw new ProcessingGraphStoreConflictException("The idempotency key was used for a different command.");
    }

    private static JsonSerializerOptions CreateExecutionSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private sealed record RegistryRow(
        ProcessingGraphRegistryMode Mode,
        string ActiveRevisionId,
        string ConfiguredBasicRevisionId,
        long StateVersion);

    private sealed record StoredCommand(string Kind, string CommandSha256, string ResultReference);

    private const string RevisionSelectSql = """
        SELECT revision_id, graph_name, revision_name, lifecycle,
               definition_identity_sha256, shared_plan_identity_sha256, local_plan_identity_sha256,
               created_unix_ms, validated_unix_ms, activated_unix_ms, retired_unix_ms,
               pipeline_json, definition_json, frozen_plan_json, nodes_json
        FROM processing_graph_revisions
        """;

    private const string ExecutionSelectSql = """
        SELECT execution_id, execution_class, status, capture_id, primary_artifact_id,
               graph_revision_id, definition_identity_sha256, shared_plan_identity_sha256,
               local_plan_identity_sha256, trigger_kind, trigger_reference, priority,
               accepted_unix_ms, available_unix_ms, deadline_unix_ms, maximum_age_unix_ms,
               started_unix_ms, completed_unix_ms, failure_reason, cancellation_requested,
               attempt_count
        FROM processing_executions
        """;
}
