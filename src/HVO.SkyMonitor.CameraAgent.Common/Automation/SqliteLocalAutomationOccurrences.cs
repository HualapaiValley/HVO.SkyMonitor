using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

public sealed partial class SqliteLocalAutomationStore
{
    public const int MaximumQueuedOccurrencesPerDefinition = 32;
    public const int MaximumOccurrenceAttempts = 3;

    public async ValueTask<LocalAutomationRun?> TryClaimQueuedAsync(
        LocalAutomationRunnerEntry currentEntry, CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(currentEntry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, false, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadDefinitionAsync(connection, transaction,
                currentEntry.Definition.DefinitionId, cancellationToken).ConfigureAwait(false);
            if (current is null || !current.Definition.Enabled || current.Version != currentEntry.Version ||
                current.RevisionSha256 != currentEntry.RevisionSha256 ||
                await ScalarLongAsync(connection, transaction,
                    "SELECT COUNT(*) FROM automation_runs WHERE definition_id = $id AND outcome = 'Running';",
                    cancellationToken, ("$id", currentEntry.Definition.DefinitionId)).ConfigureAwait(false) != 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                       started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence,
                       occurrence_json, attempt, occurrence_id, preparation_json
                FROM automation_runs
                WHERE definition_id = $id AND outcome = 'Queued' ORDER BY sequence LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", currentEntry.Definition.DefinitionId);
            LocalAutomationRun? run;
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                run = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? await ReadRunAsync(reader, cancellationToken).ConfigureAwait(false) : null;
            }
            if (run?.Occurrence is not { } occurrence || occurrence.ScheduledForUtc > Now())
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var now = Now();
            var rejection = _registry.Validate(occurrence.Definition);
            if (rejection is not null)
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE automation_runs SET outcome = 'Skipped', completed_unix_ms = $now,
                        detail = 'The retained task adapter or target is unavailable.'
                    WHERE run_key = $key AND outcome = 'Queued';
                    """, cancellationToken, ("$now", ToUnixMilliseconds(now)), ("$key", run.RunKey)).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var claimed = await ExecuteAsync(connection, transaction, """
                UPDATE automation_runs SET outcome = 'Running', started_unix_ms = $now,
                    detail = '', claimant = $claimant
                WHERE run_key = $key AND outcome = 'Queued';
                """, cancellationToken, ("$now", ToUnixMilliseconds(now)), ("$claimant", _claimant),
                ("$key", run.RunKey)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimed == 1 ? run with { Outcome = LocalAutomationRunOutcome.Running, StartedAtUtc = now, Detail = string.Empty } : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LocalAutomationCommandResult> BackfillAsync(
        LocalAutomationBackfillRequest request, CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(request);
        using var activity = LocalAutomationTelemetry.ActivitySource.StartActivity("automation.backfill");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, false, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var result = await BackfillCoreAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Record(activity, "backfill", result.Status);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<LocalAutomationCommandResult> BackfillCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, LocalAutomationBackfillRequest request, CancellationToken token)
    {
        if (!ValidOccurrenceCommand(request.DefinitionId, request.ExpectedVersion, request.IdempotencyKey,
                request.Actor, request.Reason) || request.HourStartUtc is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode, token).ConfigureAwait(false);
        }
        var payload = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = LocalAutomationContract.SchemaLabel,
            Operation = "backfill",
            Request = request with { IdempotencyKey = string.Empty }
        });
        if (await ReplayAsync(connection, transaction, request.IdempotencyKey, payload, null, token)
                .ConfigureAwait(false) is { } replay)
        {
            return replay;
        }
        var definition = await ReadDefinitionAsync(connection, transaction, request.DefinitionId, token).ConfigureAwait(false);
        if (definition is null)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.NotFound,
                LocalAutomationContract.UnknownDefinitionReasonCode, token).ConfigureAwait(false);
        }
        if (definition.Version != request.ExpectedVersion)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                LocalAutomationContract.ExpectedVersionConflictReasonCode, token).ConfigureAwait(false);
        }
        if (!definition.Definition.Enabled || definition.Definition.SourceWindow is not { } policy ||
            _registry.Validate(definition.Definition) is not null || _windowPlanner is null)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Invalid,
                "automation.window-task-unavailable", token).ConfigureAwait(false);
        }
        IReadOnlyList<LocalAutomationSourceWindow> windows;
        try
        {
            windows = _windowPlanner.ResolveWindows(request.ReportDate, policy);
        }
        catch (ReportingPeriodUnavailableException exception)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Invalid,
                exception.ReasonCode, token).ConfigureAwait(false);
        }
        var window = policy.Kind == LocalAutomationSourceWindowKind.SunriseDay
            ? request.HourStartUtc is null ? windows.Single() : null
            : windows.SingleOrDefault(item => item.StartUtc == request.HourStartUtc);
        if (window is null)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Invalid,
                "automation.source-window-not-found", token).ConfigureAwait(false);
        }
        var now = Now();
        if (!window.IsEligibleForFinal(now))
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                "automation.finality-not-reached", token).ConfigureAwait(false);
        }
        if (window.EndUtc < LocalAutomationWindowPlanner.LookbackStart(now))
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Invalid,
                "automation.backfill-outside-lookback", token).ConfigureAwait(false);
        }
        var entry = new LocalAutomationRunnerEntry(definition.Definition, definition.Version,
            definition.RevisionSha256, null, null);
        var occurrence = LocalAutomationWindowPlanner.CreateOccurrence(entry, window);
        return await QueueOccurrenceAsync(connection, transaction, occurrence, 1, request.IdempotencyKey,
            payload, "backfill", request.Actor, request.Reason, token).ConfigureAwait(false);
    }

    public async ValueTask<LocalAutomationCommandResult> RetryAsync(
        LocalAutomationRetryRequest request, CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(request);
        using var activity = LocalAutomationTelemetry.ActivitySource.StartActivity("automation.retry");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, false, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var result = await RetryCoreAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Record(activity, "retry", result.Status);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<LocalAutomationCommandResult> RetryCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, LocalAutomationRetryRequest request, CancellationToken token)
    {
        if (!ValidOccurrenceCommand(request.DefinitionId, request.ExpectedVersion, request.IdempotencyKey,
                request.Actor, request.Reason) || !LocalAutomationDefinitionValidator.IsText(request.PreviousRunKey, 128))
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode, token).ConfigureAwait(false);
        }
        var payload = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = LocalAutomationContract.SchemaLabel,
            Operation = "retry",
            Request = request with { IdempotencyKey = string.Empty }
        });
        if (await ReplayAsync(connection, transaction, request.IdempotencyKey, payload, null, token)
                .ConfigureAwait(false) is { } replay)
        {
            return replay;
        }
        var definition = await ReadDefinitionAsync(connection, transaction, request.DefinitionId, token).ConfigureAwait(false);
        if (definition is null)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.NotFound,
                LocalAutomationContract.UnknownDefinitionReasonCode, token).ConfigureAwait(false);
        }
        if (definition.Version != request.ExpectedVersion)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                LocalAutomationContract.ExpectedVersionConflictReasonCode, token).ConfigureAwait(false);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                   started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence,
                   occurrence_json, attempt, occurrence_id, preparation_json
            FROM automation_runs WHERE definition_id = $id AND run_key = $key;
            """;
        command.Parameters.AddWithValue("$id", request.DefinitionId);
        command.Parameters.AddWithValue("$key", request.PreviousRunKey);
        LocalAutomationRun? previous;
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            previous = await reader.ReadAsync(token).ConfigureAwait(false)
                ? await ReadRunAsync(reader, token).ConfigureAwait(false) : null;
        }
        if (previous is null)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.NotFound,
                "automation.run-not-found", token).ConfigureAwait(false);
        }
        if (!definition.Definition.Enabled || previous.Occurrence is not { } occurrence ||
            previous.Outcome is not (LocalAutomationRunOutcome.Failed or LocalAutomationRunOutcome.Interrupted) ||
            _registry.Validate(occurrence.Definition) is not null || occurrence.ScheduledForUtc > Now())
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                "automation.run-not-retryable", token).ConfigureAwait(false);
        }
        var latest = await ScalarLongAsync(connection, transaction,
            "SELECT MAX(attempt) FROM automation_runs WHERE occurrence_id = $identity;", token,
            ("$identity", occurrence.IdentitySha256)).ConfigureAwait(false);
        if (latest != previous.Attempt || latest >= MaximumOccurrenceAttempts)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                "automation.retry-limit-or-stale-attempt", token).ConfigureAwait(false);
        }
        return await QueueOccurrenceAsync(connection, transaction, occurrence, checked((int)latest + 1),
            request.IdempotencyKey, payload, "retry", request.Actor, request.Reason, token).ConfigureAwait(false);
    }

    private async ValueTask<LocalAutomationCommandResult> QueueOccurrenceAsync(SqliteConnection connection,
        SqliteTransaction transaction, LocalAutomationOccurrence occurrence, int attempt, string idempotencyKey,
        string payloadSha256, string operation, string actor, string reason, CancellationToken token)
    {
        var queued = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM automation_runs WHERE definition_id = $id AND outcome = 'Queued';", token,
            ("$id", occurrence.Definition.DefinitionId)).ConfigureAwait(false);
        var existing = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM automation_runs WHERE run_key = $key;", token,
            ("$key", AttemptRunKey(occurrence.RunKey, attempt))).ConfigureAwait(false);
        if (existing == 0 && queued >= MaximumQueuedOccurrencesPerDefinition)
        {
            return await OccurrenceResultAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                "automation.queue-capacity", token).ConfigureAwait(false);
        }
        var inserted = await ExecuteAsync(connection, transaction, """
            INSERT INTO automation_runs(definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence, claimant,
                occurrence_json, occurrence_id, attempt, preparation_json)
            VALUES ($id, $key, $revision, $trigger, $scheduled, NULL, NULL, 'Queued',
                'Explicitly prepared occurrence; awaiting the local runner.', NULL, $claimant, $json, $identity, $attempt, $preparation)
            ON CONFLICT(run_key) DO NOTHING;
            """, token, ("$id", occurrence.Definition.DefinitionId),
            ("$key", AttemptRunKey(occurrence.RunKey, attempt)), ("$revision", occurrence.RevisionSha256),
            ("$trigger", occurrence.Definition.TriggerKind.ToString()), ("$scheduled", ToUnixMilliseconds(occurrence.ScheduledForUtc)),
            ("$claimant", _claimant),
            ("$json", JsonSerializer.Serialize(occurrence, SerializerOptions)), ("$identity", occurrence.IdentitySha256),
            ("$attempt", attempt),
            ("$preparation", JsonSerializer.Serialize(LocalAutomationRunPreparation.Create(
                operation, actor, idempotencyKey, reason, payloadSha256, Now()), SerializerOptions))).ConfigureAwait(false);
        await RecordCommandAsync(connection, transaction, idempotencyKey, operation, payloadSha256,
            occurrence.Definition.DefinitionId, token).ConfigureAwait(false);
        await RetainRunsAsync(connection, transaction, occurrence.Definition.DefinitionId, token).ConfigureAwait(false);
        return await OccurrenceResultAsync(connection, transaction,
            inserted == 1 ? LocalAutomationCommandStatus.Applied : LocalAutomationCommandStatus.Unchanged,
            null, token).ConfigureAwait(false);
    }

    private ValueTask<LocalAutomationCommandResult> OccurrenceResultAsync(SqliteConnection connection,
        SqliteTransaction transaction, LocalAutomationCommandStatus status, string? reason, CancellationToken token)
        => ResultAsync(connection, transaction, status, reason, reason is null ? null : "command", null, token);

    private static bool ValidOccurrenceCommand(string id, long expectedVersion, string key, string actor, string reason)
        => LocalAutomationDefinitionValidator.IsIdentifier(id, LocalAutomationContract.MaximumDefinitionIdLength) &&
            expectedVersion > 0 && LocalAutomationDefinitionValidator.IsText(key, 128) &&
            LocalAutomationDefinitionValidator.IsText(actor, 256) && LocalAutomationDefinitionValidator.IsText(reason, 512);

    private static string AttemptRunKey(string runKey, int attempt)
        => attempt == 1 ? runKey : runKey + ":retry" + attempt.ToString(CultureInfo.InvariantCulture);
}
