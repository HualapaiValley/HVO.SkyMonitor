using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private static readonly JsonSerializerOptions ExecutionSerializerOptions = CreateExecutionSerializerOptions();

    internal async ValueTask<ProcessingGraphRevisionSnapshot> EnsureSceneCompatibilityRevisionAsync(
        SceneCompatibilityRevisionSearch search, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using (var readTransaction = connection.BeginTransaction(deferred: true))
#pragma warning restore CA1849
        {
            var (_, selected) = await FindSceneCompatibilityRevisionAsync(connection, readTransaction, search, cancellationToken)
                .ConfigureAwait(false);
            if (selected is not null) return selected;
        }

#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        // A writer may have filled the free name or validated an earlier draft since the read snapshot.
        var (name, current) = await FindSceneCompatibilityRevisionAsync(connection, transaction, search, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            var expected = search.ExpectedRevision!(name);
            await InsertRevisionAsync(connection, transaction, expected, ProcessingGraphRevisionLifecycle.Validated, cancellationToken)
                .ConfigureAwait(false);
            current = await ReadRevisionAsync(connection, transaction, expected.State.RevisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    private static async ValueTask<(string Name, ProcessingGraphRevisionSnapshot? Selected)> FindSceneCompatibilityRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SceneCompatibilityRevisionSearch search,
        CancellationToken cancellationToken)
    {
        for (var generation = 0; ; generation++)
        {
            var name = generation == 0 ? search.SourceId : FormattableString.Invariant($"{search.SourceId}-{generation}");
            var expected = search.ExpectedRevision?.Invoke(name);
            var (occupied, eligible) = await ReadSceneCompatibilityRevisionAsync(
                connection, transaction, search.GraphName, name, expected, cancellationToken).ConfigureAwait(false);
            // Live selection creates a revision at the first free name, before considering any later generation.
            if (!occupied || eligible is not null && search.MatchesFrozen?.Invoke(eligible) != false)
                return (name, eligible);
        }
    }

    private static async ValueTask<(bool Occupied, ProcessingGraphRevisionSnapshot? Eligible)> ReadSceneCompatibilityRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string graphName,
        string name,
        ProcessingGraphRevisionSnapshot? expected,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RevisionSelectSql + " WHERE graph_name = $name AND revision_name = $revision;";
        command.Parameters.AddWithValue("$name", graphName);
        command.Parameters.AddWithValue("$revision", name);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return (false, null);
        var state = ReadRevisionState(reader);
        if (!IsValidatedCompatibilityRevision(state) || expected is not null && state.RevisionId != expected.State.RevisionId)
            return (true, null);

        var stored = await ReadRevisionSnapshotAsync(reader, state, cancellationToken).ConfigureAwait(false);
        return (true, expected is null || HasSameRevisionContent(stored, expected) ? stored : null);
    }

    private static bool IsValidatedCompatibilityRevision(ProcessingGraphRevisionState state)
        => state.Lifecycle is (ProcessingGraphRevisionLifecycle.Validated or ProcessingGraphRevisionLifecycle.Active) &&
           state.ValidatedUtc is not null && state.RetiredUtc is null;

    private static bool HasSceneCompatibilityNameFor(ProcessingGraphRevisionState state, string sourceId)
    {
        if (state.Name is not (ProcessingGraphOperationsCoordinator.VirtualSkySceneCompatibilityGraphName or
                ProcessingGraphOperationsCoordinator.VirtualSkySharedFitCompatibilityGraphName or
                ProcessingGraphOperationsCoordinator.AstrometryConsumerCompatibilityGraphName))
            return false;

        var name = state.Revision.AsSpan();
        if (name.SequenceEqual(sourceId)) return true;
        return name.Length > sourceId.Length + 1 && name.StartsWith(sourceId, StringComparison.Ordinal) &&
               name[sourceId.Length] == '-' && name[sourceId.Length + 1] is >= '1' and <= '9' &&
               int.TryParse(name[(sourceId.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

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
        CancellationToken cancellationToken,
        Action<ProcessingGraphRevisionSnapshot>? ensureCompatible = null)
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
        // Replayed acknowledgements above remain immutable; new validation commands recheck current local policy.
        ensureCompatible?.Invoke(target);
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

    internal async ValueTask<(ProcessingGraphRevisionSnapshot Revision, bool Legacy)?> ReadReferenceAstrometryCreationAsync(
        string idempotencyKey, string requestSha256, string actor, string? reason, CancellationToken cancellationToken)
    {
        ValidateCommand(idempotencyKey, actor, reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var prior = await ReadCommandAsync(connection, null, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (prior is null) return null;

        var legacy = prior.Kind == LegacyReferenceAstrometryCreateCommand;
        if (legacy)
        {
            EnsureIdempotent(prior, LegacyReferenceAstrometryCreateCommand,
                CommandSha256(LegacyReferenceAstrometryCreateCommand, prior.ResultReference, actor, reason));
        }
        else
        {
            EnsureIdempotent(prior, ReferenceAstrometryCreateCommand, requestSha256);
        }
        var revision = await ReadRevisionAsync(connection, null, prior.ResultReference, cancellationToken).ConfigureAwait(false);
        return (revision, legacy);
    }

    internal async ValueTask<ProcessingGraphRevisionState> InsertNamedRevisionAsync(
        ProcessingGraphRevisionSnapshot revision,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken,
        Action<ProcessingGraphRevisionSnapshot>? ensureCompatible = null)
        => (await InsertNamedRevisionCommandAsync(revision, idempotencyKey, actor, reason,
            ensureCompatible is null ? null : (candidate, _) => ensureCompatible(candidate),
            referenceAstrometryRequestSha256: null, cancellationToken).ConfigureAwait(false)).State;

    internal ValueTask<ProcessingGraphRevisionSnapshot> InsertReferenceAstrometryRevisionAsync(
        ProcessingGraphRevisionSnapshot revision,
        string idempotencyKey,
        string actor,
        string? reason,
        string requestSha256,
        Action<ProcessingGraphRevisionSnapshot, LocalCaptureProfileDefinition?> ensureCompatible,
        CancellationToken cancellationToken)
        => InsertNamedRevisionCommandAsync(revision, idempotencyKey, actor, reason,
            ensureCompatible, requestSha256, cancellationToken);

    private async ValueTask<ProcessingGraphRevisionSnapshot> InsertNamedRevisionCommandAsync(
        ProcessingGraphRevisionSnapshot revision,
        string idempotencyKey,
        string actor,
        string? reason,
        Action<ProcessingGraphRevisionSnapshot, LocalCaptureProfileDefinition?>? ensureCompatible,
        string? referenceAstrometryRequestSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateCommand(idempotencyKey, actor, reason);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var referenceAstrometryPreset = referenceAstrometryRequestSha256 is not null;
        var kind = referenceAstrometryPreset ? ReferenceAstrometryCreateCommand : "create";
        var commandSha256 = referenceAstrometryRequestSha256 ?? CommandSha256(kind, revision.State.RevisionId, actor, reason);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        if (await ReadCommandAsync(connection, transaction, idempotencyKey, cancellationToken).ConfigureAwait(false) is { } prior)
        {
            EnsureIdempotent(prior, kind, commandSha256);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRevisionAsync(connection, null, prior.ResultReference, cancellationToken).ConfigureAwait(false);
        }
        // A committed acknowledgement precedes current-policy checks, just as validate/activate/rollback do.
        // The command fingerprint above still rejects a different request reusing the key.
        if (ensureCompatible is not null)
        {
            // The immediate transaction also fences schedule activation while the draft and receipt are written.
            var durableProfile = referenceAstrometryPreset
                ? (await SqliteCaptureScheduleStore.ReadSnapshotAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false))?.ActiveRevision.Profile
                : null;
            ensureCompatible(revision, durableProfile);
        }
        var inserted = await InsertRevisionAsync(
            connection, transaction, revision, ProcessingGraphRevisionLifecycle.Draft, cancellationToken).ConfigureAwait(false);
        // A repeat may acknowledge the same immutable revision, but may not change how it was created.
        if (!inserted && referenceAstrometryPreset != await WasCreatedByReferenceAstrometryPresetAsync(
                connection, transaction, revision.State.RevisionId, cancellationToken).ConfigureAwait(false))
        {
            throw new ProcessingGraphStoreConflictException("The processing graph revision already exists with different creation provenance.");
        }
        // The private command kind persists provenance in the same transaction as the revision.
        await InsertCommandAsync(
            connection, transaction, idempotencyKey, kind, commandSha256, actor, reason,
            revision.State.RevisionId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRevisionAsync(connection, null, revision.State.RevisionId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRegistryState> ActivateRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken,
        Func<ProcessingGraphRevisionSnapshot, bool, CancellationToken, ValueTask>? ensureCompatible = null)
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
        // Acknowledged commands above remain replayable after compiler changes; only new selections are checked.
        if (ensureCompatible is not null)
        {
            var preset = await WasCreatedByReferenceAstrometryPresetAsync(
                connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
            await ensureCompatible(target, preset, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        Func<ProcessingGraphRevisionSnapshot, bool, CancellationToken, ValueTask>? ensureCompatible = null)
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
        if (ensureCompatible is not null)
        {
            var preset = await WasCreatedByReferenceAstrometryPresetAsync(
                connection, transaction, revisionId, cancellationToken).ConfigureAwait(false);
            await ensureCompatible(target, preset, cancellationToken).ConfigureAwait(false);
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRegistryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<ProcessingGraphRegistryState> RetireRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        Func<ProcessingGraphRevisionSnapshot, ProcessingGraphRevisionSnapshot, CancellationToken,
            ValueTask<SceneCompatibilityRevisionSearch?>> createCompatibilitySearch,
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
        // Names narrow the lookup. The selected source and its derived revision are checked in this transaction.
        if (IsValidatedCompatibilityRevision(revision.State) && HasSceneCompatibilityNameFor(revision.State, state.ActiveRevisionId))
        {
            var source = await ReadRevisionAsync(connection, transaction, state.ActiveRevisionId, cancellationToken)
                .ConfigureAwait(false);
            var search = await createCompatibilitySearch(source, revision, cancellationToken).ConfigureAwait(false);
            if (search is not null)
            {
                var (_, selected) = await FindSceneCompatibilityRevisionAsync(connection, transaction, search, cancellationToken)
                    .ConfigureAwait(false);
                if (selected?.State.RevisionId == revisionId)
                {
                    throw new ProcessingGraphStoreConflictException(
                        "A compatibility revision for the selected processing graph cannot be retired.");
                }
            }
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

    /// <summary>
    /// Returns the revision ID currently occupying the unique <c>(graph_name, revision_name)</c> pair, or
    /// <see langword="null"/> when the name is free. Used to detect a persisted revision whose immutable content was
    /// produced by an earlier compiler contract so the caller can supersede it deterministically instead of
    /// colliding on insert.
    /// </summary>
    internal async ValueTask<string?> ReadRevisionIdByNameAsync(
        string graphName,
        string revisionName,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision_id FROM processing_graph_revisions
            WHERE graph_name = $name AND revision_name = $revision;
            """;
        command.Parameters.AddWithValue("$name", graphName);
        command.Parameters.AddWithValue("$revision", revisionName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
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

    /// <summary>
    /// Resolves and pins the derived input window a live execution node consumes. A live execution is created
    /// when its own raw capture is accepted, before the preceding captures in a trailing window have finished
    /// processing, so the window is selected here - when the consuming node runs, under the execution lease -
    /// and the unreleased pins are replaced so the durable evidence records the window the attempt resolved.
    /// </summary>
    internal async ValueTask<IReadOnlyList<DurableProcessingOutput>> ResolveLiveExecutionOutputWindowAsync(
        ProcessingExecutionContext execution,
        string nodeId,
        ReconstructionDescriptor current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(current);
        if (execution.ExecutionClass != ProcessingGraphExecutionClass.Live)
        {
            throw new ArgumentException("Only a live execution resolves its derived window at run time.", nameof(execution));
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var plan = await ReadLiveWindowPlanAsync(connection, execution.ExecutionId, nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (plan is null)
        {
            // The node declares no window, so it consumes only its own capture's dependency artifact.
            return [];
        }
        // Select the inputs and check their catalog membership before opening the write transaction.
        // Raw acceptance shares the lane database. The pin insert rechecks the journal's availability
        // within the transaction, and the consumer verifies the exact Silo versions when it reads them.
        var selected = await ProcessingOutputWindowSelector.SelectAsync(
            connection, null, current, plan.ProducerId, plan.RevisionId, plan.ProducerPlanSha256,
            plan.InputsJson, plan.Requirement, _executionOptions.MaximumWindowInputs,
            includeUnpublishedRevisionOutputs: false, cancellationToken).ConfigureAwait(false);
        foreach (var input in selected)
        {
            await EnsureFrozenOutputMembersExistAsync(
                connection, null, input.OutputIdentitySha256, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        await EnsureExecutionLeaseAsync(connection, transaction, execution, cancellationToken).ConfigureAwait(false);
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            // Released pins stay as evidence of an earlier attempt; only the active window is replaced.
            clear.CommandText = """
                DELETE FROM processing_execution_output_input_pins
                WHERE execution_id = $execution AND node_id = $node AND released_flag = 0;
                """;
            clear.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
            clear.Parameters.AddWithValue("$node", nodeId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertFrozenOutputPinsAsync(
            connection, transaction, execution.ExecutionId, nodeId, selected, cancellationToken,
            validateRetainedMembers: false).ConfigureAwait(false);
        var outputs = await ReadPinnedOutputsAsync(
            connection, transaction, execution.ExecutionId, nodeId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outputs;
    }

    private sealed record LiveWindowPlan(
        string RevisionId,
        string ProducerId,
        string ProducerPlanSha256,
        string InputsJson,
        ProcessingGraphWindowRequirement Requirement);

    private static async ValueTask<LiveWindowPlan?> ReadLiveWindowPlanAsync(
        SqliteConnection connection,
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        string revisionId;
        string dependenciesJson;
        string inputsJson;
        string? windowJson;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT execution.graph_revision_id, node.dependencies_json, node.inputs_json, node.window_json
                FROM processing_executions execution
                JOIN processing_execution_nodes node ON node.execution_id = execution.execution_id
                WHERE execution.execution_id = $execution AND execution.execution_class = 'Live'
                  AND node.node_id = $node;
                """;
            command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            command.Parameters.AddWithValue("$node", nodeId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ProcessingGraphStoreConflictException(
                    "The live processing execution node is not part of the frozen execution plan.");
            }
            revisionId = reader.GetString(0);
            dependenciesJson = reader.GetString(1);
            inputsJson = reader.GetString(2);
            windowJson = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(3);
        }
        if (windowJson is null ||
            ProcessingOutputWindowSelector.ReadFirstProducerId(dependenciesJson) is not { } producerId)
        {
            return null;
        }
        var requirement = JsonSerializer.Deserialize<ProcessingGraphWindowRequirement>(
            windowJson, ExecutionSerializerOptions)
            ?? throw new InvalidDataException($"Processing graph node '{nodeId}' has an invalid window.");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT plan_sha256 FROM processing_execution_nodes
                WHERE execution_id = $execution AND node_id = $node;
                """;
            command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            command.Parameters.AddWithValue("$node", producerId);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string producerPlan)
            {
                throw new ProcessingGraphStoreConflictException(
                    "The live processing window producer is not part of the frozen execution plan.");
            }
            return new(revisionId, producerId, producerPlan, inputsJson, requirement);
        }
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
        ProcessingNodeExecutionRoute executionRoute,
        CancellationToken cancellationToken,
        IReadOnlyList<CenteredProductSettlement>? centeredProducts = null)
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
            executionRoute,
            cancellationToken).ConfigureAwait(false);
        var centeredRevisions = await PublishCenteredProductsAsync(connection, transaction, execution,
            centeredProducts, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (centeredRevisions is { Count: > 0 })
            await ObserveCommittedCenteredProductsAsync(connection, centeredRevisions).ConfigureAwait(false);
        else
            NotifyCenteredOutputlessMeasurement(execution, node, status);
    }

    private void NotifyCenteredOutputlessMeasurement(ProcessingExecutionContext? execution,
        CaptureProcessingGraphNode node, DurableProcessingNodeStatus status)
    {
        if (execution is { ExecutionClass: ProcessingGraphExecutionClass.Live, AllowAutomaticPublication: true } &&
            node.Id == ExposureMeasurementProducts.StepAlias &&
            status is DurableProcessingNodeStatus.Skipped or DurableProcessingNodeStatus.TerminalFailure)
            _centeredWindowWakeup?.NotifyCommitted();
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
        ProcessingNodeExecutionRoute executionRoute,
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
                    reason = $reason, duration_ticks = $duration, execution_route = $route
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
            // The route is written on completion rather than on the attempt insert, because the dispatch
            // decision is made inside the node's own execution and does not exist when the attempt opens.
            // A node interrupted before completing therefore keeps 'Unknown', which is the honest value.
            update.Parameters.AddWithValue("$route", executionRoute.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await RecordCommittedOutputCountAsync(connection, transaction, execution.ExecutionId,
            node.Id, outputs.Count, cancellationToken).ConfigureAwait(false);
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
            FROM processing_executions
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

    /// <summary>
    /// The durable <c>status</c> values that are terminal, and the ones that are not. Together they must be exactly
    /// the set the <c>processing_executions</c> CHECK constraint admits, or the sweep would silently never see a
    /// status outside both lists; <c>ProcessingGraphExecutionStatusCoverageTests</c> asserts that.
    /// </summary>
    internal static readonly string[] TerminalStatuses = ["Completed", "Failed", "Cancelled", "Expired"];

    internal static readonly string[] ActiveStatuses = ["Pending", "Running"];

    internal async ValueTask<ProcessingGraphExecutionState?> ReadExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExecutionAsync(connection, null, executionId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<Guid?> ReadLiveExecutionIdAsync(Guid captureId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(captureId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_id FROM processing_executions INDEXED BY ix_processing_executions_live_capture
            WHERE capture_id = $capture AND execution_class = 'Live';
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text ? Guid.ParseExact(text, "N") : null;
    }

    internal async ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken,
        bool manifestOnly = false)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExecutionDetailAsync(connection, executionId, manifestOnly, cancellationToken).ConfigureAwait(false);
    }

    // The same read on the caller's connection, so a caller can keep it on the snapshot of its other reads.
    private async ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        SqliteConnection connection,
        Guid executionId,
        bool manifestOnly,
        CancellationToken cancellationToken)
    {
        if (manifestOnly)
        {
            // A SQL transaction keeps the size check and subsequent reads on one WAL snapshot.
            // No managed transaction is created: all existing read helpers use this connection.
            using var snapshot = connection.CreateCommand();
            snapshot.CommandText = "BEGIN;";
            await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            if (manifestOnly) await CheckRunManifestBoundsAsync(connection, executionId, cancellationToken).ConfigureAwait(false);
            var execution = await ReadExecutionAsync(connection, null, executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null) return null;
            var nodes = new List<ProcessingGraphExecutionNodeState>();
            using var nodeCommand = connection.CreateCommand();
            nodeCommand.CommandText = """
            SELECT node_id, required, plan_sha256, status, reason, attempt_count,
                   started_unix_ms, completed_unix_ms, dependencies_json, outputs_json, window_json IS NOT NULL
            FROM processing_execution_nodes
            WHERE execution_id = $execution
            ORDER BY rowid;
            """;
            nodeCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            var rows = new List<(string NodeId, bool Required, string Plan, string Status, string? Reason,
                int AttemptCount, DateTimeOffset? Started, DateTimeOffset? Completed, string DependenciesJson, string OutputsJson, bool HasWindow)>();
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
                            ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                        reader.GetString(8), reader.GetString(9),
                        reader.GetBoolean(10)));
                }
            }
            var frozenPlanJson = await ReadExecutionFrozenPlanJsonAsync(
                connection, null, executionId, cancellationToken).ConfigureAwait(false);
            int? frozenNodeCount = null;
            if (manifestOnly)
            {
                frozenNodeCount = ReadRunManifestNodeCount(frozenPlanJson);
            }
            // Optional export bookkeeping must not make the ordinary journal unreadable.
            // Manifest reads still validate it and fail closed with the corrupt-evidence reason.
            var committedOutputCounts = manifestOnly ? await ReadManifestCountsAsync(connection, executionId, ManifestOutputLedger, cancellationToken).ConfigureAwait(false) : [];
            var frozenInputCounts = manifestOnly ? await ReadManifestCountsAsync(connection, executionId, ManifestInputLedger, cancellationToken).ConfigureAwait(false) : [];
            var legacyWindows = manifestOnly && rows.Any(row => !frozenInputCounts.ContainsKey(row.NodeId))
                ? ReadLegacyManifestWindows(frozenPlanJson) : [];
            var frozenPlans = ReadNormalizationPlans(frozenPlanJson, nodeId: null)
                .ToDictionary(static plan => plan.NodeId, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var attempts = new List<ProcessingGraphNodeAttemptState>();
                using (var attemptCommand = connection.CreateCommand())
                {
                    attemptCommand.CommandText = """
                    SELECT attempt_number, lease_owner, started_unix_ms, completed_unix_ms,
                           status, outcome, reason, duration_ticks, execution_route
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
                                ? null : TimeSpan.FromTicks(reader.GetInt64(7)),
                            // The column is NOT NULL with a checked value set, so an unparsable value means the
                            // schema validation that guards initialization did not run or did not hold.
                            Enum.Parse<ProcessingNodeExecutionRoute>(reader.GetString(8))));
                    }
                }
                var durableOutputs = await ReadExecutionOutputsAsync(
                    connection, executionId, row.NodeId, cancellationToken).ConfigureAwait(false);
                if (_silo is { } silo && durableOutputs.Count > 0)
                {
                    var retained = new List<DurableProcessingOutput>(durableOutputs.Count);
                    foreach (var output in durableOutputs)
                    {
                        if (await silo.IsArtifactRetainedAsync(output.ArtifactId, cancellationToken).ConfigureAwait(false))
                            retained.Add(output);
                    }
                    durableOutputs = retained;
                }
                var published = await ReadExecutionOutputAssociationsAsync(
                    connection, executionId, row.NodeId, cancellationToken).ConfigureAwait(false);
                var inputs = await ReadExecutionInputsAsync(
                    connection, executionId, row.NodeId, manifestOnly, cancellationToken).ConfigureAwait(false);
                var outputs = durableOutputs.Select((output, ordinal) =>
                {
                    (int Ordinal, bool Published) association = published.TryGetValue(output.OutputIdentitySha256, out var saved)
                        ? saved : (ordinal, false);
                    return new ProcessingGraphExecutionOutputState(
                        manifestOnly ? association.Ordinal : ordinal, output.OutputIdentitySha256, output.ArtifactId,
                        output.Artifact.Role, output.Artifact.Variant, output.AvailabilityState,
                        output.AvailabilityReason, association.Published)
                    {
                        PayloadSha256 = output.Artifact.ChecksumSha256,
                        SourceArtifactIds = output.Artifact.SourceArtifactIds
                    };
                }).ToArray();
                frozenPlans.TryGetValue(row.NodeId, out var normalization);
                IReadOnlyList<ProcessingGraphExecutionCalibrationInputState> masters = normalization?.Plan.Inputs.Where(static input => input.Masters is not null)
                    .SelectMany(static input => input.Masters!).DistinctBy(static master => master.ArtifactId)
                    .Select(static master => new ProcessingGraphExecutionCalibrationInputState(master.ArtifactId, master.PayloadSha256, master.Kind))
                    .ToArray() ?? [];
                var dependencies = JsonSerializer.Deserialize<ProcessingGraphDependencyDefinition[]>(row.DependenciesJson, ExecutionSerializerOptions)
                    ?? throw new ProcessingGraphStoreConflictException("Frozen execution dependencies are missing.");
                var bindingsRetained = true;
                var inputEvidenceIncomplete = false;
                if (manifestOnly)
                {
                    (inputs, masters, bindingsRetained, inputEvidenceIncomplete) = await ReadRunManifestBindingsAsync(
                        connection, execution, row.NodeId, row.HasWindow, inputs, masters, outputs, attempts.Count, dependencies,
                        frozenInputCounts, legacyWindows, cancellationToken).ConfigureAwait(false);
                }
                var contracts = JsonSerializer.Deserialize<ProcessingGraphProductContract[]>(row.OutputsJson, ExecutionSerializerOptions)
                    ?? throw new ProcessingGraphStoreConflictException("Frozen execution outputs are missing.");
                int? committedCount = committedOutputCounts.TryGetValue(row.NodeId, out var recordedCount) ? recordedCount : null;
                // Outputs commit atomically with a completed attempt. No attempts, or only
                // running/interrupted/skipped attempts without a producing/failing outcome,
                // prove zero even when a terminal sweep changed the node's status.
                if (committedCount is null &&
                    ((outputs.Length == contracts.Length && contracts.All(contract =>
                         outputs.Any(output => output.Role == contract.Role && output.Variant == contract.Variant))) ||
                     (outputs.Length == 0 && attempts.All(static attempt =>
                         (attempt.Status is "Running" or "Interrupted" && attempt.Outcome is null) ||
                         (attempt.Status == "Skipped" && attempt.Outcome is null or ProcessingOutcomeStatus.Skipped)))))
                    committedCount = outputs.Length;
                var diagnostics = manifestOnly ? (Value: (MixedGainDiagnosticsV1?)null, Reason: (string?)null)
                    : await ReadMixedGainDiagnosticsAsync(durableOutputs, normalization, cancellationToken).ConfigureAwait(false);
                nodes.Add(new ProcessingGraphExecutionNodeState(
                    row.NodeId, row.Required, row.Plan, row.Status, row.Reason, row.AttemptCount,
                    row.Started, row.Completed, inputs, attempts, outputs)
                {
                    CalibrationInputs = masters,
                    ArtifactBindingsRetained = bindingsRetained,
                    InputEvidenceIncomplete = inputEvidenceIncomplete,
                    CommittedOutputCount = committedCount,
                    NormalizationPlanIdentitySha256 = normalization?.IdentitySha256,
                    NormalizationInputs = normalization?.Plan.Inputs.Select(static input => new ProcessingNormalizationInputDiagnostic(
                        input.WindowPosition, input.CaptureId, input.ArtifactId, input.Gain, input.Disposition,
                        input.ExclusionReason)).ToArray() ?? [],
                    MixedGainDiagnostics = diagnostics.Value,
                    MixedGainDiagnosticsUnavailableReason = diagnostics.Reason,
                    Dependencies = dependencies,
                    OutputContracts = contracts
                });
            }
            return new(execution, nodes) { FrozenNodeCount = frozenNodeCount };
        }
        finally
        {
            if (manifestOnly)
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<IReadOnlyList<ProcessingGraphExecutionInputState>> ReadExecutionInputsAsync(
        SqliteConnection connection,
        Guid executionId,
        string nodeId,
        bool manifestOnly,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_ordinal, window_position, 'RawCapture', capture_id, artifact_id,
                   descriptor_sha256, payload_sha256, NULL, 1
            FROM processing_execution_inputs
            WHERE execution_id = $execution AND node_id = $node
            UNION ALL
            SELECT pin.input_ordinal, pin.window_position, 'ProcessingOutput',
                   output.capture_id, output.artifact_id, NULL,
                   CASE WHEN $manifest THEN CASE WHEN json_valid(CAST(output.descriptor_json AS TEXT)) THEN
                       COALESCE(json_extract(CAST(output.descriptor_json AS TEXT), '$.descriptor.artifact.checksumSha256'),
                                json_extract(CAST(output.descriptor_json AS TEXT), '$.artifact.checksumSha256')) END END,
                   pin.output_identity_sha256,
                   CASE WHEN $manifest THEN json_valid(CAST(output.descriptor_json AS TEXT)) ELSE 1 END
            FROM processing_execution_output_input_pins pin
            JOIN processing_outputs output
              ON output.output_identity_sha256 = pin.output_identity_sha256
            WHERE pin.execution_id = $execution AND pin.node_id = $node
            ORDER BY input_ordinal;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$manifest", manifestOnly);
        var inputs = new List<ProcessingGraphExecutionInputState>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.GetBoolean(8)) throw new InvalidDataException("A retained manifest input descriptor is corrupt.");
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

    private static async ValueTask<bool> InsertRevisionAsync(
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
        bool inserted;
        try
        {
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ProcessingGraphStoreConflictException(
                "The processing graph revision name and revision already identify different content.", exception);
        }
        var stored = await ReadRevisionAsync(
            connection, transaction, revision.State.RevisionId, cancellationToken).ConfigureAwait(false);
        if (!HasSameRevisionContent(stored, revision))
        {
            throw new ProcessingGraphStoreConflictException(
                "The processing graph revision identity has different immutable content.");
        }
        return inserted;
    }

    internal static bool HasSameRevisionContent(ProcessingGraphRevisionSnapshot stored, ProcessingGraphRevisionSnapshot expected)
        => stored.State.RevisionId == expected.State.RevisionId &&
           stored.State.Name == expected.State.Name &&
           stored.State.Revision == expected.State.Revision &&
           stored.State.DefinitionIdentitySha256 == expected.State.DefinitionIdentitySha256 &&
           stored.State.SharedPlanIdentitySha256 == expected.State.SharedPlanIdentitySha256 &&
           stored.State.LocalPlanIdentitySha256 == expected.State.LocalPlanIdentitySha256 &&
           stored.PipelineJson.AsSpan().SequenceEqual(expected.PipelineJson) &&
           stored.DefinitionJson.AsSpan().SequenceEqual(expected.DefinitionJson) &&
           stored.FrozenPlanJson.AsSpan().SequenceEqual(expected.FrozenPlanJson) &&
           stored.Nodes.SequenceEqual(expected.Nodes);

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
        return await ReadRevisionSnapshotAsync(reader, ReadRevisionState(reader), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ProcessingGraphRevisionSnapshot> ReadRevisionSnapshotAsync(
        SqliteDataReader reader,
        ProcessingGraphRevisionState state,
        CancellationToken cancellationToken)
    {
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

    private static async ValueTask<bool> WasCreatedByReferenceAstrometryPresetAsync(
        SqliteConnection connection, SqliteTransaction transaction, string revisionId, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM processing_graph_commands
            WHERE command_kind IN ($kind, $legacyKind) AND result_reference = $revision LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", ReferenceAstrometryCreateCommand);
        command.Parameters.AddWithValue("$legacyKind", LegacyReferenceAstrometryCreateCommand);
        command.Parameters.AddWithValue("$revision", revisionId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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

    /// <summary>
    /// Whether this execution published each output it associated for the node. The association
    /// row's <c>published_flag</c> is an identity-level fact: an execution that re-associates an
    /// output some earlier execution already published inherits <c>1</c>, so a replay of the live
    /// revision (which reproduces identical output identities) carries the live publication on its
    /// own rows. The per-execution fact is that flag joined with the execution's own permission to
    /// publish; a replay is inserted with <c>allow_automatic_publication = 0</c> and so reports
    /// <c>false</c> for every output however the identity was published. Read separately from the
    /// output rows so the shared output projection stays untouched.
    /// </summary>
    private static async ValueTask<Dictionary<string, (int Ordinal, bool Published)>> ReadExecutionOutputAssociationsAsync(
        SqliteConnection connection,
        Guid executionId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT association.output_identity_sha256,
                   association.published_flag * execution.allow_automatic_publication,
                   association.output_ordinal
            FROM processing_execution_outputs association
            JOIN processing_executions execution ON execution.execution_id = association.execution_id
            WHERE association.execution_id = $execution AND association.node_id = $node;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$node", nodeId);
        var published = new Dictionary<string, (int Ordinal, bool Published)>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            published[reader.GetString(0)] = (reader.GetInt32(2), reader.GetInt64(1) != 0);
        }
        return published;
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
                    AND ((required = 1 AND state NOT IN ('completed', 'abandoned')) OR state = 'leased'))
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
        command.CommandText = ExecutionSelectSql + " FROM processing_executions WHERE execution_id = $execution;";
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

    private const string LegacyReferenceAstrometryCreateCommand = "create-reference-astrometry";
    private const string ReferenceAstrometryCreateCommand = "create-reference-astrometry-v2";

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
        """;


}

internal sealed record SceneCompatibilityRevisionSearch(
    string GraphName,
    string SourceId,
    Func<string, ProcessingGraphRevisionSnapshot>? ExpectedRevision = null,
    Func<ProcessingGraphRevisionSnapshot, bool>? MatchesFrozen = null);
