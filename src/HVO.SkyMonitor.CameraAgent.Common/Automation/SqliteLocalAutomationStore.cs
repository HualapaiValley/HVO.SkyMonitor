using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>One definition and its progress as the runner needs it.</summary>
public sealed record LocalAutomationRunnerEntry(
    LocalAutomationDefinition Definition,
    long Version,
    string RevisionSha256,
    DateTimeOffset? LastOccurrenceUtc,
    long? LastCaptureSequence);

/// <summary>The durable owner of local automation definitions and their run-history journal.</summary>
public interface ILocalAutomationStore
{
    /// <summary>Creates or validates the store and settles runs interrupted by a stop.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>The complete operator projection, including the registry and the next-run calendar.</summary>
    ValueTask<LocalAutomationOperatorState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Records a new immutable revision of one definition.</summary>
    ValueTask<LocalAutomationCommandResult> SaveAsync(
        LocalAutomationSaveRequest request,
        CancellationToken cancellationToken);

    /// <summary>Removes one definition, retaining its revisions and recorded runs.</summary>
    ValueTask<LocalAutomationCommandResult> RemoveAsync(
        LocalAutomationRemoveRequest request,
        CancellationToken cancellationToken);

    /// <summary>The enabled definitions and their durable progress.</summary>
    ValueTask<IReadOnlyList<LocalAutomationRunnerEntry>> GetRunnerViewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Claims one occurrence and advances the definition's durable progress in the same transaction.
    /// Returns false when the occurrence was already recorded, which makes a retry idempotent.
    /// </summary>
    ValueTask<bool> TryBeginRunAsync(
        LocalAutomationRunnerEntry entry,
        string runKey,
        DateTimeOffset scheduledForUtc,
        long? observedCaptureSequence,
        CancellationToken cancellationToken);

    /// <summary>Settles a claimed run and applies run retention for its definition.</summary>
    ValueTask CompleteRunAsync(
        string runKey,
        LocalAutomationRunOutcome outcome,
        string detail,
        CancellationToken cancellationToken);

    /// <summary>Records one already-terminal run, such as a window missed while the host was stopped.</summary>
    ValueTask RecordTerminalRunAsync(
        LocalAutomationRunnerEntry entry,
        string runKey,
        DateTimeOffset scheduledForUtc,
        LocalAutomationRunOutcome outcome,
        string detail,
        long? observedCaptureSequence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Establishes the capture-sequence baseline of a capture-relative definition without recording a
    /// run, so enabling one never fires immediately for captures taken before it existed.
    /// </summary>
    ValueTask SetCaptureBaselineAsync(
        string definitionId,
        long captureSequence,
        CancellationToken cancellationToken);
}

/// <summary>
/// The versioned durable local automation store.
/// <para>
/// It owns its own SQLite file under <c>.automation/</c> rather than adding tables to the raw
/// ingress journal. The journal pins an exact schema version and object count and refuses a database
/// it does not recognize, so a forward-only change there would break the installer's
/// rollback-to-baseline contract. A baseline image simply never opens this file.
/// </para>
/// </summary>
public sealed partial class SqliteLocalAutomationStore : ILocalAutomationStore, ILocalAutomationOccurrenceStore,
    ILocalAutomationExecutionAuthority, IDisposable
{
    private const string DirectoryName = ".automation";
    private const string FileName = "local-automations.db";
    private const int ExpectedSchemaObjectCount = 8;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string SchemaSql = $"""
        CREATE TABLE IF NOT EXISTS automation_definitions (
            definition_id TEXT PRIMARY KEY CHECK (length(definition_id) BETWEEN 1 AND 64),
            name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 96),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            task_kind TEXT NOT NULL CHECK (length(task_kind) BETWEEN 1 AND 64),
            task_target TEXT NOT NULL CHECK (length(task_target) BETWEEN 1 AND 128),
            trigger_kind TEXT NOT NULL CHECK (length(trigger_kind) BETWEEN 1 AND 64),
            trigger_interval INTEGER NOT NULL CHECK (trigger_interval > 0),
            trigger_epoch_unix_ms INTEGER NOT NULL,
            version INTEGER NOT NULL CHECK (version > 0),
            revision_sha256 TEXT NOT NULL CHECK (length(revision_sha256) = 64),
            updated_unix_ms INTEGER NOT NULL,
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 256),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512),
            source_window_json TEXT
        ) STRICT;

        CREATE TABLE IF NOT EXISTS automation_definition_revisions (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            definition_id TEXT NOT NULL CHECK (length(definition_id) BETWEEN 1 AND 64),
            version INTEGER NOT NULL CHECK (version > 0),
            revision_sha256 TEXT NOT NULL CHECK (length(revision_sha256) = 64),
            definition_json TEXT NOT NULL,
            removed INTEGER NOT NULL CHECK (removed IN (0, 1)),
            recorded_unix_ms INTEGER NOT NULL,
            actor TEXT NOT NULL CHECK (length(actor) BETWEEN 1 AND 256),
            reason TEXT CHECK (reason IS NULL OR length(reason) <= 512)
        ) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_automation_revisions_version
            ON automation_definition_revisions(definition_id, version);

        CREATE TABLE IF NOT EXISTS automation_commands (
            idempotency_key TEXT PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 128),
            command_kind TEXT NOT NULL CHECK (length(command_kind) BETWEEN 1 AND 32),
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            definition_id TEXT NOT NULL CHECK (length(definition_id) BETWEEN 1 AND 64),
            recorded_unix_ms INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_automation_commands_recorded
            ON automation_commands(recorded_unix_ms);

        CREATE TABLE IF NOT EXISTS automation_runs (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            definition_id TEXT NOT NULL CHECK (length(definition_id) BETWEEN 1 AND 64),
            run_key TEXT NOT NULL UNIQUE CHECK (length(run_key) BETWEEN 1 AND 128),
            revision_sha256 TEXT NOT NULL CHECK (length(revision_sha256) = 64),
            trigger_kind TEXT NOT NULL CHECK (length(trigger_kind) BETWEEN 1 AND 64),
            scheduled_for_unix_ms INTEGER NOT NULL,
            started_unix_ms INTEGER,
            completed_unix_ms INTEGER,
            outcome TEXT NOT NULL CHECK (length(outcome) BETWEEN 1 AND 32),
            detail TEXT NOT NULL CHECK (length(detail) <= {LocalAutomationContract.MaximumDetailLength}),
            observed_capture_sequence INTEGER,
            claimant TEXT NOT NULL CHECK (length(claimant) BETWEEN 1 AND 64),
            occurrence_json TEXT,
            occurrence_id TEXT CHECK (occurrence_id IS NULL OR length(occurrence_id) = 64),
            attempt INTEGER NOT NULL DEFAULT 1 CHECK (attempt BETWEEN 1 AND 3),
            preparation_json TEXT
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_automation_runs_definition
            ON automation_runs(definition_id, sequence);

        CREATE TABLE IF NOT EXISTS automation_progress (
            definition_id TEXT PRIMARY KEY CHECK (length(definition_id) BETWEEN 1 AND 64),
            last_occurrence_unix_ms INTEGER,
            last_occurrence_utc_ticks INTEGER,
            last_capture_sequence INTEGER,
            updated_unix_ms INTEGER NOT NULL
        ) STRICT;
        """;

    private readonly ILocalAutomationTaskRegistry _registry;
    private readonly ILocalAutomationCaptureSequenceSource _captureSequence;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteLocalAutomationStore> _logger;
    private readonly LocalAutomationTelemetry? _telemetry;
    private readonly CameraAgentHostOptions _options;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The lifetime authority invokes _gate.Dispose only after every admitted execution lease drains.")]
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalAutomationExecutionAuthority _authority;
    private readonly LocalAutomationWindowPlanner? _windowPlanner;
    private readonly string _claimant = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private string? _connectionString;
    private string? _databasePath;
    private bool _schemaReady;
    private bool _interruptedRunsSettled;

    public SqliteLocalAutomationStore(
        ILocalAutomationTaskRegistry registry,
        ILocalAutomationCaptureSequenceSource captureSequence,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        ILogger<SqliteLocalAutomationStore> logger,
        LocalAutomationTelemetry? telemetry = null,
        LocalAutomationWindowPlanner? windowPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _captureSequence = captureSequence;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _telemetry = telemetry;
        _authority = new LocalAutomationExecutionAuthority(_gate.Dispose);
        _windowPlanner = windowPlanner;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates or verifies the schema once per process. Every gated operation calls it, so an
    /// operator read that arrives before the runner's hosted start still sees a valid store rather
    /// than a missing-table failure.
    /// </summary>
    private async ValueTask EnsureSchemaAsync(
        SqliteConnection connection,
        bool settleInterruptedRuns,
        CancellationToken cancellationToken)
    {
        if (_schemaReady && (!settleInterruptedRuns || _interruptedRunsSettled))
        {
            return;
        }
        using var transaction = BeginImmediate(connection);
        var version = await ReadUserVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (version > LocalAutomationContract.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Local automation schema {version} is newer than supported schema "
                + $"{LocalAutomationContract.CurrentSchemaVersion}.");
        }
        if (version == 0)
        {
            var objectCount = await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';",
                cancellationToken).ConfigureAwait(false);
            if (objectCount != 0)
            {
                throw new InvalidDataException(
                    "Local automation storage exists without a schema version; archive the database "
                    + "before starting this CameraAgent.");
            }
            await ExecuteAsync(connection, transaction, SchemaSql, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                transaction,
                $"PRAGMA user_version = {LocalAutomationContract.CurrentSchemaVersion};",
                cancellationToken).ConfigureAwait(false);
        }
        else if (version != LocalAutomationContract.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Local automation schema {version} is unsupported; archive the database and complete an "
                + "explicit state-disposition procedure before starting this CameraAgent.");
        }
        var schemaObjects = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';",
            cancellationToken).ConfigureAwait(false);
        if (schemaObjects != ExpectedSchemaObjectCount)
        {
            throw new InvalidDataException("Local automation SQLite schema is incomplete or drifted.");
        }
        var integrity = await ScalarStringAsync(
            connection, transaction, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Local automation SQLite storage failed its integrity check.");
        }
        var interrupted = 0;
        if (settleInterruptedRuns)
        {
            // The lifetime process lock was acquired before opening SQLite. A live owner cannot
            // be mistaken for a stopped owner, including while cancellation is still draining.
            interrupted = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE automation_runs
                SET outcome = 'Interrupted',
                    completed_unix_ms = $now,
                    detail = 'The CameraAgent stopped while this run was claimed.'
                WHERE outcome = 'Running' AND claimant <> $claimant;
                """,
                cancellationToken,
                ("$now", ToUnixMilliseconds(Now())), ("$claimant", _claimant)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _schemaReady = true;
        _interruptedRunsSettled |= settleInterruptedRuns;
        if (interrupted > 0)
        {
            RunsSettled(_logger, interrupted, null);
        }
    }

    public async ValueTask<LocalAutomationOperatorState> GetStateAsync(CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            var observed = await ReadCaptureSequenceIfNeededAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginRead(connection);
            return await ProjectAsync(connection, transaction, observed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }


    public async ValueTask<LocalAutomationCommandResult> SaveAsync(
        LocalAutomationSaveRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(request);
        using var activity = LocalAutomationTelemetry.ActivitySource.StartActivity("automation.save");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            // Read before the write transaction opens: this touches another database file and must never
            // happen while the automation store holds its IMMEDIATE write lock.
            var observed = await ReadCaptureSequenceIfNeededAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var result = await SaveCoreAsync(connection, transaction, request, observed, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Record(activity, "save", result.Status);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<LocalAutomationCommandResult> RemoveAsync(
        LocalAutomationRemoveRequest request,
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(request);
        using var activity = LocalAutomationTelemetry.ActivitySource.StartActivity("automation.remove");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            var observed = await ReadCaptureSequenceIfNeededAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var result = await RemoveCoreAsync(connection, transaction, request, observed, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Record(activity, "remove", result.Status);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<LocalAutomationRunnerEntry>> GetRunnerViewAsync(
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginRead(connection);
            var progress = await ReadProgressAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var definitions = await ReadDefinitionsAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            return
            [
                .. definitions
                    .Where(static record => record.Definition.Enabled)
                    .Select(record =>
                    {
                        progress.TryGetValue(record.Definition.DefinitionId, out var value);
                        return new LocalAutomationRunnerEntry(
                            record.Definition,
                            record.Version,
                            record.RevisionSha256,
                            value.LastOccurrenceUtc,
                            value.LastCaptureSequence);
                    })
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> TryBeginRunAsync(
        LocalAutomationRunnerEntry entry,
        string runKey,
        DateTimeOffset scheduledForUtc,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return await TryBeginOccurrenceCoreAsync(entry,
            LocalAutomationOccurrence.Create(entry, runKey, scheduledForUtc), observedCaptureSequence,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> TryBeginOccurrenceAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return await TryBeginOccurrenceCoreAsync(new(occurrence.Definition, occurrence.DefinitionVersion,
            occurrence.RevisionSha256, null, null), occurrence, null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryBeginOccurrenceCoreAsync(
        LocalAutomationRunnerEntry entry,
        LocalAutomationOccurrence occurrence,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(entry);
        if (!occurrence.IsValid() || occurrence.SourceWindow is { } planned && !planned.IsEligibleForFinal(Now()))
        {
            return false;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var now = Now();
            var current = await ReadDefinitionAsync(
                connection, transaction, entry.Definition.DefinitionId, cancellationToken).ConfigureAwait(false);
            if (current is null || !current.Definition.Enabled || current.Version != entry.Version ||
                current.RevisionSha256 != entry.RevisionSha256 || current.Definition != entry.Definition ||
                occurrence.SourceWindow is { } retained && !retained.IsEligibleForFinal(now) ||
                _registry.Validate(current.Definition) is not null ||
                await ScalarLongAsync(connection, transaction,
                    "SELECT COUNT(*) FROM automation_runs WHERE definition_id = $id AND outcome = 'Running';",
                    cancellationToken, ("$id", entry.Definition.DefinitionId)).ConfigureAwait(false) != 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            var inserted = await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO automation_runs(
                    definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                    started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence, claimant,
                    occurrence_json, occurrence_id)
                VALUES ($definition, $key, $revision, $trigger, $scheduled, $started, NULL, 'Running', '', $observed,
                        $claimant, $occurrence, $occurrenceId)
                ON CONFLICT(run_key) DO NOTHING;
                """,
                cancellationToken,
                ("$definition", entry.Definition.DefinitionId),
                ("$key", occurrence.RunKey),
                ("$revision", entry.RevisionSha256),
                ("$trigger", entry.Definition.TriggerKind.ToString()),
                ("$scheduled", ToUnixMilliseconds(occurrence.ScheduledForUtc)),
                ("$started", ToUnixMilliseconds(now)),
                ("$observed", observedCaptureSequence is { } sequence ? sequence : (object)DBNull.Value),
                ("$claimant", _claimant),
                ("$occurrence", JsonSerializer.Serialize(occurrence, SerializerOptions)),
                ("$occurrenceId", occurrence.IdentitySha256)).ConfigureAwait(false);
            if (inserted == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
            // Progress advances with the claim, not with the completion: a process that stops mid-run
            // must not re-run the same occurrence when it comes back.
            await AdvanceProgressAsync(
                connection,
                transaction,
                entry.Definition.DefinitionId,
                entry.Definition.TriggerKind != LocalAutomationTriggerKind.CaptureRelative ? occurrence.ScheduledForUtc : null,
                entry.Definition.TriggerKind == LocalAutomationTriggerKind.CaptureRelative
                    ? observedCaptureSequence
                    : null,
                now,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CompleteRunAsync(
        string runKey,
        LocalAutomationRunOutcome outcome,
        string detail,
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority(allowDrainingCompletion: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        ArgumentNullException.ThrowIfNull(detail);
        if (outcome is LocalAutomationRunOutcome.Running or LocalAutomationRunOutcome.Queued || !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var settled = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE automation_runs
                SET outcome = $outcome, detail = $detail, completed_unix_ms = $now
                WHERE run_key = $key AND outcome = 'Running' AND claimant = $claimant;
                """,
                cancellationToken,
                ("$outcome", outcome.ToString()),
                ("$detail", Bound(detail)),
                ("$now", ToUnixMilliseconds(Now())),
                ("$key", runKey),
                ("$claimant", _claimant)).ConfigureAwait(false);
            if (settled == 0)
            {
                // A stale completion has no authority to change a terminal or differently owned run.
                RunCompletionLost(_logger, runKey, outcome.ToString(), null);
            }
            var definitionId = await ScalarStringAsync(
                connection,
                transaction,
                "SELECT definition_id FROM automation_runs WHERE run_key = $key;",
                cancellationToken,
                ("$key", runKey)).ConfigureAwait(false);
            if (definitionId is not null)
            {
                await RetainRunsAsync(connection, transaction, definitionId, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RecordTerminalRunAsync(
        LocalAutomationRunnerEntry entry,
        string runKey,
        DateTimeOffset scheduledForUtc,
        LocalAutomationRunOutcome outcome,
        string detail,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKey);
        ArgumentNullException.ThrowIfNull(detail);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadDefinitionAsync(connection, transaction, entry.Definition.DefinitionId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null || !current.Definition.Enabled || current.Version != entry.Version ||
                current.RevisionSha256 != entry.RevisionSha256)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            var now = ToUnixMilliseconds(Now());
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO automation_runs(
                    definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                    started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence, claimant)
                VALUES ($definition, $key, $revision, $trigger, $scheduled, NULL, $now, $outcome, $detail, $observed,
                        $claimant)
                ON CONFLICT(run_key) DO NOTHING;
                """,
                cancellationToken,
                ("$definition", entry.Definition.DefinitionId),
                ("$key", runKey),
                ("$revision", entry.RevisionSha256),
                ("$trigger", entry.Definition.TriggerKind.ToString()),
                ("$scheduled", ToUnixMilliseconds(scheduledForUtc)),
                ("$now", now),
                ("$outcome", outcome.ToString()),
                ("$detail", Bound(detail)),
                ("$observed", observedCaptureSequence is { } sequence ? sequence : (object)DBNull.Value),
                ("$claimant", _claimant)).ConfigureAwait(false);
            await RetainRunsAsync(connection, transaction, entry.Definition.DefinitionId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetCaptureBaselineAsync(
        string definitionId,
        long captureSequence,
        CancellationToken cancellationToken)
    {
        using var operation = RetainOperationAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, settleInterruptedRuns: false, cancellationToken)
                .ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            await AdvanceProgressAsync(
                connection, transaction, definitionId, null, captureSequence, Now(), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IDisposable RetainExecutionAuthority() => _authority.Retain();

    public void Dispose() => _authority.Dispose();

    private async ValueTask<LocalAutomationCommandResult> SaveCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAutomationSaveRequest request,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        if (!LocalAutomationDefinitionValidator.IsText(request.Actor, LocalAutomationContract.MaximumActorLength) ||
            !LocalAutomationDefinitionValidator.IsText(
                request.IdempotencyKey, LocalAutomationContract.MaximumIdempotencyKeyLength) ||
            (request.Reason is not null &&
             !LocalAutomationDefinitionValidator.IsText(request.Reason, LocalAutomationContract.MaximumReasonLength)) ||
            request.ExpectedVersion < 0)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode, "command", observedCaptureSequence,
                cancellationToken).ConfigureAwait(false);
        }
        var existing = await ReadDefinitionAsync(connection, transaction, request.DefinitionId, cancellationToken)
            .ConfigureAwait(false);
        var epoch = existing?.Definition.TriggerEpochUtc ?? Truncate(Now());
        var definition = new LocalAutomationDefinition(
            request.DefinitionId,
            request.Name,
            request.Enabled,
            request.TaskKind,
            request.TaskTarget,
            request.TriggerKind,
            request.TriggerInterval,
            epoch,
            request.SourceWindow);
        if (LocalAutomationDefinitionValidator.Validate(definition) is { } invalid)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Invalid, invalid.ReasonCode,
                invalid.FieldPath, observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        if (_registry.Validate(definition) is { } unregistered)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Invalid, unregistered.ReasonCode,
                unregistered.FieldPath, observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        var payloadSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = LocalAutomationContract.SchemaLabel,
            CommandKind = "save",
            Definition = definition with { TriggerEpochUtc = DateTimeOffset.UnixEpoch },
            request.ExpectedVersion,
            request.Actor,
            request.Reason
        });
        if (await ReplayAsync(
                connection, transaction, request.IdempotencyKey, payloadSha256, observedCaptureSequence,
                cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }
        var currentVersion = existing?.Version ?? 0;
        if (request.ExpectedVersion != currentVersion)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Conflict,
                LocalAutomationContract.ExpectedVersionConflictReasonCode, "command.expectedVersion",
                observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        if (existing is null)
        {
            var count = await ScalarLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM automation_definitions;", cancellationToken)
                .ConfigureAwait(false);
            if (count >= LocalAutomationContract.MaximumDefinitions)
            {
                return await RejectAsync(
                    connection, transaction, LocalAutomationCommandStatus.Invalid,
                    LocalAutomationContract.DefinitionLimitReasonCode, "definition.definitionId",
                    observedCaptureSequence, cancellationToken).ConfigureAwait(false);
            }
        }
        var revisionSha256 = ComputeRevisionSha256(definition);
        if (existing is not null &&
            string.Equals(existing.RevisionSha256, revisionSha256, StringComparison.Ordinal))
        {
            await RecordCommandAsync(
                connection, transaction, request.IdempotencyKey, "save", payloadSha256, request.DefinitionId,
                cancellationToken).ConfigureAwait(false);
            return await ResultAsync(
                connection, transaction, LocalAutomationCommandStatus.Unchanged, null, null,
                observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        // The revision line is immutable and outlives the live row, so a definition identifier that was
        // removed and is being recreated must continue that line rather than restart it at one.
        var revisionCeiling = await ScalarLongAsync(
            connection,
            transaction,
            "SELECT COALESCE(MAX(version), 0) FROM automation_definition_revisions WHERE definition_id = $id;",
            cancellationToken,
            ("$id", request.DefinitionId)).ConfigureAwait(false);
        var version = Math.Max(currentVersion, revisionCeiling) + 1;
        var now = ToUnixMilliseconds(Now());
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_definitions(
                definition_id, name, enabled, task_kind, task_target, trigger_kind, trigger_interval,
                trigger_epoch_unix_ms, version, revision_sha256, updated_unix_ms, actor, reason, source_window_json)
            VALUES ($id, $name, $enabled, $taskKind, $target, $triggerKind, $interval, $epoch, $version,
                    $revision, $now, $actor, $reason, $sourceWindow)
            ON CONFLICT(definition_id) DO UPDATE SET
                name = excluded.name, enabled = excluded.enabled, task_kind = excluded.task_kind,
                task_target = excluded.task_target, trigger_kind = excluded.trigger_kind,
                trigger_interval = excluded.trigger_interval, trigger_epoch_unix_ms = excluded.trigger_epoch_unix_ms,
                version = excluded.version, revision_sha256 = excluded.revision_sha256,
                updated_unix_ms = excluded.updated_unix_ms, actor = excluded.actor, reason = excluded.reason,
                source_window_json = excluded.source_window_json;
            """,
            cancellationToken,
            ("$id", definition.DefinitionId),
            ("$name", definition.Name),
            ("$enabled", definition.Enabled ? 1L : 0L),
            ("$taskKind", definition.TaskKind.ToString()),
            ("$target", definition.TaskTarget),
            ("$triggerKind", definition.TriggerKind.ToString()),
            ("$interval", (long)definition.TriggerInterval),
            ("$epoch", ToUnixMilliseconds(definition.TriggerEpochUtc)),
            ("$version", version),
            ("$revision", revisionSha256),
            ("$now", now),
            ("$actor", request.Actor),
            ("$reason", request.Reason is { } saveReason ? saveReason : (object)DBNull.Value),
            ("$sourceWindow", definition.SourceWindow is { } policy
                ? JsonSerializer.Serialize(policy, SerializerOptions) : (object)DBNull.Value)).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection, transaction, definition, version, revisionSha256, removed: false, now, request.Actor,
            request.Reason, cancellationToken).ConfigureAwait(false);
        // A changed trigger kind invalidates the previous progress dimension, and a definition that was
        // disabled has no run history for the time it was off. Both are re-anchored to now rather than
        // simply cleared: clearing would make every boundary since the epoch look like a missed occurrence.
        if (existing is not null &&
            (existing.Definition.TriggerKind != definition.TriggerKind ||
             existing.Definition.SourceWindow != definition.SourceWindow ||
             (!existing.Definition.Enabled && definition.Enabled)))
        {
            await AnchorProgressAsync(connection, transaction, definition, Now(), cancellationToken)
                .ConfigureAwait(false);
        }
        await RecordCommandAsync(
            connection, transaction, request.IdempotencyKey, "save", payloadSha256, request.DefinitionId,
            cancellationToken).ConfigureAwait(false);
        return await ResultAsync(
            connection, transaction, LocalAutomationCommandStatus.Applied, null, null, observedCaptureSequence,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LocalAutomationCommandResult> RemoveCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAutomationRemoveRequest request,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        if (!LocalAutomationDefinitionValidator.IsText(request.Actor, LocalAutomationContract.MaximumActorLength) ||
            !LocalAutomationDefinitionValidator.IsText(
                request.IdempotencyKey, LocalAutomationContract.MaximumIdempotencyKeyLength) ||
            !LocalAutomationDefinitionValidator.IsIdentifier(
                request.DefinitionId, LocalAutomationContract.MaximumDefinitionIdLength) ||
            (request.Reason is not null &&
             !LocalAutomationDefinitionValidator.IsText(request.Reason, LocalAutomationContract.MaximumReasonLength)) ||
            request.ExpectedVersion < 1)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Invalid,
                LocalAutomationContract.InvalidCommandReasonCode, "command", observedCaptureSequence,
                cancellationToken).ConfigureAwait(false);
        }
        var payloadSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = LocalAutomationContract.SchemaLabel,
            CommandKind = "remove",
            request.DefinitionId,
            request.ExpectedVersion,
            request.Actor,
            request.Reason
        });
        if (await ReplayAsync(
                connection, transaction, request.IdempotencyKey, payloadSha256, observedCaptureSequence,
                cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }
        var existing = await ReadDefinitionAsync(connection, transaction, request.DefinitionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.NotFound,
                LocalAutomationContract.UnknownDefinitionReasonCode, "command.definitionId",
                observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        if (request.ExpectedVersion != existing.Version)
        {
            return await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Conflict,
                LocalAutomationContract.ExpectedVersionConflictReasonCode, "command.expectedVersion",
                observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        var version = existing.Version + 1;
        var now = ToUnixMilliseconds(Now());
        if (await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM automation_runs WHERE definition_id = $id AND outcome = 'Running';",
                cancellationToken, ("$id", request.DefinitionId)).ConfigureAwait(false) != 0)
        {
            return await RejectAsync(connection, transaction, LocalAutomationCommandStatus.Conflict,
                "automation.execution-still-running", "command", observedCaptureSequence, cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE automation_runs SET outcome = 'Skipped', completed_unix_ms = $now,
                detail = 'The definition was removed before this prepared occurrence started.'
            WHERE definition_id = $id AND outcome = 'Queued';
            """, cancellationToken, ("$now", now), ("$id", request.DefinitionId)).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, "DELETE FROM automation_definitions WHERE definition_id = $id;",
            cancellationToken, ("$id", request.DefinitionId)).ConfigureAwait(false);
        await ExecuteAsync(
            connection, transaction, "DELETE FROM automation_progress WHERE definition_id = $id;",
            cancellationToken, ("$id", request.DefinitionId)).ConfigureAwait(false);
        await InsertRevisionAsync(
            connection, transaction, existing.Definition, version, existing.RevisionSha256, removed: true, now,
            request.Actor, request.Reason, cancellationToken).ConfigureAwait(false);
        await PruneOrphansAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RecordCommandAsync(
            connection, transaction, request.IdempotencyKey, "remove", payloadSha256, request.DefinitionId,
            cancellationToken).ConfigureAwait(false);
        return await ResultAsync(
            connection, transaction, LocalAutomationCommandStatus.Applied, null, null, observedCaptureSequence,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LocalAutomationCommandResult?> ReplayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string payloadSha256,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT payload_sha256 FROM automation_commands WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (stored is null)
        {
            return null;
        }
        return string.Equals(stored, payloadSha256, StringComparison.Ordinal)
            ? await ResultAsync(
                connection, transaction, LocalAutomationCommandStatus.Replayed, null, null, observedCaptureSequence,
                cancellationToken).ConfigureAwait(false)
            : await RejectAsync(
                connection, transaction, LocalAutomationCommandStatus.Conflict,
                LocalAutomationContract.IdempotencyKeyConflictReasonCode, "command.idempotencyKey",
                observedCaptureSequence, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        string commandKind,
        string payloadSha256,
        string definitionId,
        CancellationToken cancellationToken)
    {
        var now = Now();
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_commands(
                idempotency_key, command_kind, payload_sha256, definition_id, recorded_unix_ms)
            VALUES ($key, $kind, $payload, $definition, $now);
            """,
            cancellationToken,
            ("$key", idempotencyKey),
            ("$kind", commandKind),
            ("$payload", payloadSha256),
            ("$definition", definitionId),
            ("$now", ToUnixMilliseconds(now))).ConfigureAwait(false);
        // The retained window is the replay window: a key older than it can no longer be replayed,
        // so the ledger cannot grow without bound from an authenticated caller.
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM automation_commands WHERE recorded_unix_ms < $cutoff;",
            cancellationToken,
            ("$cutoff", ToUnixMilliseconds(now - LocalAutomationContract.IdempotencyReplayWindow)))
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAutomationDefinition definition,
        long version,
        string revisionSha256,
        bool removed,
        long recordedUnixMs,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_definition_revisions(
                definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor, reason)
            VALUES ($id, $version, $revision, $json, $removed, $now, $actor, $reason);
            """,
            cancellationToken,
            ("$id", definition.DefinitionId),
            ("$version", version),
            ("$revision", revisionSha256),
            ("$json", JsonSerializer.Serialize(definition, SerializerOptions)),
            ("$removed", removed ? 1L : 0L),
            ("$now", recordedUnixMs),
            ("$actor", actor),
            ("$reason", reason is { } revisionReason ? revisionReason : (object)DBNull.Value)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM automation_definition_revisions
            WHERE definition_id = $id AND sequence NOT IN (
                SELECT sequence FROM automation_definition_revisions
                WHERE definition_id = $id ORDER BY sequence DESC LIMIT $keep);
            """,
            cancellationToken,
            ("$id", definition.DefinitionId),
            ("$keep", (long)LocalAutomationContract.MaximumRetainedRevisions)).ConfigureAwait(false);
    }

    /// <summary>
    /// Bounds the revisions and runs of definitions that no longer exist. Per-definition retention only
    /// runs from that definition's own write paths, so without this a removed definition would keep its
    /// tail forever and removal would free the definition slot for another one.
    /// </summary>
    private static async ValueTask PruneOrphansAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM automation_definition_revisions
            WHERE definition_id NOT IN (SELECT definition_id FROM automation_definitions)
              AND sequence NOT IN (
                SELECT sequence FROM automation_definition_revisions
                WHERE definition_id NOT IN (SELECT definition_id FROM automation_definitions)
                ORDER BY sequence DESC LIMIT $keep);
            """,
            cancellationToken,
            ("$keep", (long)LocalAutomationContract.MaximumRetainedOrphanRevisions)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM automation_runs
            WHERE definition_id NOT IN (SELECT definition_id FROM automation_definitions)
              AND sequence NOT IN (
                SELECT sequence FROM automation_runs
                WHERE definition_id NOT IN (SELECT definition_id FROM automation_definitions)
                ORDER BY sequence DESC LIMIT $keep);
            """,
            cancellationToken,
            ("$keep", (long)LocalAutomationContract.MaximumRetainedOrphanRuns)).ConfigureAwait(false);
    }

    private static async ValueTask RetainRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string definitionId,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM automation_runs
            WHERE definition_id = $id AND outcome NOT IN ('Queued', 'Running') AND sequence NOT IN (
                SELECT sequence FROM automation_runs
                WHERE definition_id = $id AND outcome NOT IN ('Queued', 'Running') ORDER BY sequence DESC LIMIT $keep);
            """,
            cancellationToken,
            ("$id", definitionId),
            ("$keep", (long)LocalAutomationContract.MaximumRetainedRuns)).ConfigureAwait(false);

    /// <summary>
    /// Resets a definition's progress to the present. A periodic definition is anchored to the most recent
    /// boundary so its next occurrence is one whole interval away; a capture-relative one is cleared so the
    /// runner establishes a fresh capture baseline.
    /// </summary>
    private static async ValueTask AnchorProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAutomationDefinition definition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? occurrenceUtc = definition.TriggerKind switch
        {
            LocalAutomationTriggerKind.Periodic => LocalAutomationSchedule.LatestPeriodicBoundary(
                definition.TriggerEpochUtc, definition.TriggerInterval, now),
            LocalAutomationTriggerKind.SourceWindowClosed => now,
            _ => null
        };
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_progress(
                definition_id, last_occurrence_unix_ms, last_occurrence_utc_ticks, last_capture_sequence, updated_unix_ms)
            VALUES ($id, $occurrence, $ticks, NULL, $now)
            ON CONFLICT(definition_id) DO UPDATE SET
                last_occurrence_unix_ms = excluded.last_occurrence_unix_ms,
                last_occurrence_utc_ticks = excluded.last_occurrence_utc_ticks,
                last_capture_sequence = NULL,
                updated_unix_ms = excluded.updated_unix_ms;
            """,
            cancellationToken,
            ("$id", definition.DefinitionId),
            ("$occurrence", occurrenceUtc is { } occurrence ? ToUnixMilliseconds(occurrence) : (object)DBNull.Value),
            ("$ticks", occurrenceUtc is { } exact ? exact.UtcTicks : (object)DBNull.Value),
            ("$now", ToUnixMilliseconds(now))).ConfigureAwait(false);
    }

    private static async ValueTask AdvanceProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string definitionId,
        DateTimeOffset? occurrenceUtc,
        long? captureSequence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_progress(
                definition_id, last_occurrence_unix_ms, last_occurrence_utc_ticks, last_capture_sequence, updated_unix_ms)
            VALUES ($id, $occurrence, $ticks, $capture, $now)
            ON CONFLICT(definition_id) DO UPDATE SET
                last_occurrence_unix_ms = COALESCE(excluded.last_occurrence_unix_ms, last_occurrence_unix_ms),
                last_occurrence_utc_ticks = COALESCE(excluded.last_occurrence_utc_ticks, last_occurrence_utc_ticks),
                last_capture_sequence = COALESCE(excluded.last_capture_sequence, last_capture_sequence),
                updated_unix_ms = excluded.updated_unix_ms;
            """,
            cancellationToken,
            ("$id", definitionId),
            ("$occurrence", occurrenceUtc is { } value ? ToUnixMilliseconds(value) : (object)DBNull.Value),
            ("$ticks", occurrenceUtc is { } exact ? exact.UtcTicks : (object)DBNull.Value),
            ("$capture", captureSequence is { } capture ? capture : (object)DBNull.Value),
            ("$now", ToUnixMilliseconds(now))).ConfigureAwait(false);

    private async ValueTask<LocalAutomationCommandResult> RejectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAutomationCommandStatus status,
        string reasonCode,
        string fieldPath,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
        => await ResultAsync(
            connection, transaction, status, reasonCode, fieldPath, observedCaptureSequence, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<LocalAutomationCommandResult> ResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalAutomationCommandStatus status,
        string? reasonCode,
        string? fieldPath,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
        => new(
            status,
            reasonCode,
            fieldPath,
            await ProjectAsync(connection, transaction, observedCaptureSequence, cancellationToken)
                .ConfigureAwait(false));

    private async ValueTask<LocalAutomationOperatorState> ProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? observedCaptureSequence,
        CancellationToken cancellationToken)
    {
        var definitions = await ReadDefinitionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var progress = await ReadProgressAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var runs = await ReadRunsAsync(
            connection, transaction, LocalAutomationContract.MaximumProjectedRuns, cancellationToken)
            .ConfigureAwait(false);
        var storeVersion = await ScalarLongAsync(
            connection, transaction,
            "SELECT COALESCE(MAX(sequence), 0) FROM automation_definition_revisions;", cancellationToken)
            .ConfigureAwait(false);
        // The last run is resolved per definition rather than from the projected page: the page is the
        // newest runs across all definitions, so a working automation outside that window would otherwise
        // be reported as having never run.
        var lastRuns = await ReadLatestRunsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var histories = await ReadRevisionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var states = new List<LocalAutomationDefinitionState>(definitions.Count);
        var calendar = new List<LocalAutomationCalendarEntry>(definitions.Count);
        foreach (var record in definitions)
        {
            progress.TryGetValue(record.Definition.DefinitionId, out var value);
            var periodic = record.Definition.TriggerKind == LocalAutomationTriggerKind.Periodic;
            var windowPlan = record.Definition.Enabled && record.Definition.TriggerKind == LocalAutomationTriggerKind.SourceWindowClosed
                ? _windowPlanner?.Resolve(new(record.Definition, record.Version, record.RevisionSha256,
                    value.LastOccurrenceUtc, value.LastCaptureSequence), Now()) : null;
            var nextRunUtc = periodic && record.Definition.Enabled
                ? LocalAutomationSchedule.NextPeriodicDueUtc(
                    record.Definition.TriggerEpochUtc, record.Definition.TriggerInterval, value.LastOccurrenceUtc)
                : windowPlan?.NextOccurrence?.ScheduledForUtc;
            var nextCapture = record.Definition.TriggerKind == LocalAutomationTriggerKind.CaptureRelative && record.Definition.Enabled
                ? LocalAutomationSchedule.NextCaptureSequence(
                    record.Definition.TriggerInterval, value.LastCaptureSequence)
                : null;
            histories.TryGetValue(record.Definition.DefinitionId, out var history);
            lastRuns.TryGetValue(record.Definition.DefinitionId, out var lastRun);
            states.Add(new LocalAutomationDefinitionState(
                record.Definition,
                record.Version,
                record.RevisionSha256,
                record.UpdatedAtUtc,
                record.Actor,
                record.Reason,
                nextRunUtc,
                nextCapture,
                lastRun,
                history ?? [])
            {
                NextOccurrence = windowPlan?.NextOccurrence,
                WindowUnavailableReasonCode = windowPlan?.UnavailableReasonCode ??
                    (record.Definition.SourceWindow is not null && _windowPlanner is null ? "automation.window-planner-unavailable" : null)
            });
            if (nextRunUtc is { } due)
            {
                calendar.Add(new LocalAutomationCalendarEntry(
                    record.Definition.DefinitionId,
                    record.Definition.Name,
                    record.Definition.TaskKind,
                    record.Definition.TaskTarget,
                    due)
                { Occurrence = windowPlan?.NextOccurrence });
            }
        }
        return new LocalAutomationOperatorState(
            storeVersion,
            Now(),
            observedCaptureSequence,
            _registry.Describe(),
            states,
            [.. calendar.OrderBy(static entry => entry.DueUtc)
                .Take(LocalAutomationContract.MaximumProjectedCalendarEntries)],
            runs)
        {
            RunningRunCount = checked((int)await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM automation_runs WHERE outcome = 'Running';", cancellationToken).ConfigureAwait(false)),
            QueuedRunCount = checked((int)await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM automation_runs WHERE outcome = 'Queued';", cancellationToken).ConfigureAwait(false))
        };
    }

    private sealed record DefinitionRecord(
        LocalAutomationDefinition Definition,
        long Version,
        string RevisionSha256,
        DateTimeOffset UpdatedAtUtc,
        string Actor,
        string? Reason);

    private static async ValueTask<IReadOnlyList<DefinitionRecord>> ReadDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT definition_id, name, enabled, task_kind, task_target, trigger_kind, trigger_interval,
                   trigger_epoch_unix_ms, version, revision_sha256, updated_unix_ms, actor, reason, source_window_json
            FROM automation_definitions
            ORDER BY definition_id;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<DefinitionRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DefinitionRecord(
                new LocalAutomationDefinition(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    ParseEnum<LocalAutomationTaskKind>(reader.GetString(3)),
                    reader.GetString(4),
                    ParseEnum<LocalAutomationTriggerKind>(reader.GetString(5)),
                    ReadInterval(reader.GetInt64(6)),
                    FromUnixMilliseconds(reader.GetInt64(7)),
                    await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false) ? null :
                        JsonSerializer.Deserialize<LocalAutomationSourceWindowPolicy>(reader.GetString(13), SerializerOptions)),
                reader.GetInt64(8),
                reader.GetString(9),
                FromUnixMilliseconds(reader.GetInt64(10)),
                reader.GetString(11),
                await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(12)));
        }
        return results;
    }

    private static async ValueTask<DefinitionRecord?> ReadDefinitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string definitionId,
        CancellationToken cancellationToken)
    {
        var all = await ReadDefinitionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(record =>
            string.Equals(record.Definition.DefinitionId, definitionId, StringComparison.Ordinal));
    }

    private static async ValueTask<Dictionary<string, (DateTimeOffset? LastOccurrenceUtc, long? LastCaptureSequence)>>
        ReadProgressAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT definition_id, last_occurrence_utc_ticks, last_capture_sequence FROM automation_progress;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, (DateTimeOffset?, long?)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results[reader.GetString(0)] = (
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(2));
        }
        return results;
    }

    private static async ValueTask<IReadOnlyList<LocalAutomationRun>> ReadRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                   started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence,
                   occurrence_json, attempt, occurrence_id, preparation_json
            FROM automation_runs
            ORDER BY sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maximumCount);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LocalAutomationRun>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(await ReadRunAsync(reader, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>
    /// Reads the most recent revisions of every live definition in one query. The per-definition cap keeps
    /// the projected response bounded no matter how many revisions retention holds.
    /// </summary>
    private static async ValueTask<Dictionary<string, IReadOnlyList<LocalAutomationRevision>>> ReadRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor, reason
            FROM automation_definition_revisions
            WHERE definition_id IN (SELECT definition_id FROM automation_definitions)
            ORDER BY definition_id, version DESC;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, IReadOnlyList<LocalAutomationRevision>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var definitionId = reader.GetString(0);
            if (!results.TryGetValue(definitionId, out var bucket))
            {
                bucket = new List<LocalAutomationRevision>();
                results[definitionId] = bucket;
            }
            var list = (List<LocalAutomationRevision>)bucket;
            if (list.Count >= LocalAutomationContract.MaximumProjectedRevisions)
            {
                continue;
            }
            var definition = JsonSerializer.Deserialize<LocalAutomationDefinition>(
                reader.GetString(3), SerializerOptions)
                ?? throw new InvalidDataException("A local automation revision is unreadable.");
            list.Add(new LocalAutomationRevision(
                reader.GetInt64(1),
                reader.GetString(2),
                definition,
                reader.GetInt64(4) != 0,
                FromUnixMilliseconds(reader.GetInt64(5)),
                reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7)));
        }
        return results;
    }

    private static async ValueTask<LocalAutomationRun> ReadRunAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var occurrence = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null :
            JsonSerializer.Deserialize<LocalAutomationOccurrence>(reader.GetString(11), SerializerOptions);
        var run = new LocalAutomationRun(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseEnum<LocalAutomationTriggerKind>(reader.GetString(4)),
            occurrence?.ScheduledForUtc ?? FromUnixMilliseconds(reader.GetInt64(5)),
            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null : FromUnixMilliseconds(reader.GetInt64(6)),
            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : FromUnixMilliseconds(reader.GetInt64(7)),
            ParseEnum<LocalAutomationRunOutcome>(reader.GetString(8)),
            reader.GetString(9),
            await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(10))
        {
            Occurrence = occurrence,
            Attempt = checked((int)reader.GetInt64(12)),
            Preparation = await reader.IsDBNullAsync(14, cancellationToken).ConfigureAwait(false) ? null :
                JsonSerializer.Deserialize<LocalAutomationRunPreparation>(reader.GetString(14), SerializerOptions)
        };
        if (occurrence is null
            ? run.Outcome is LocalAutomationRunOutcome.Running or LocalAutomationRunOutcome.Queued
            : !occurrence.IsValid() || occurrence.Definition.DefinitionId != run.DefinitionId ||
              occurrence.RevisionSha256 != run.RevisionSha256 || occurrence.Definition.TriggerKind != run.TriggerKind ||
              occurrence.IdentitySha256 != reader.GetString(13) ||
              ToUnixMilliseconds(occurrence.ScheduledForUtc) != reader.GetInt64(5) ||
              run.RunKey != AttemptRunKey(occurrence.RunKey, run.Attempt) ||
              run.Preparation is { } preparation && !preparation.IsValid() ||
              run.Outcome == LocalAutomationRunOutcome.Queued && (run.StartedAtUtc is not null || run.Preparation is null))
        {
            throw new InvalidDataException("A retained automation occurrence does not match its journal identity.");
        }
        return run;
    }

    /// <summary>Reads the newest run of every definition, independent of the projected run page.</summary>
    private static async ValueTask<Dictionary<string, LocalAutomationRun>> ReadLatestRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, definition_id, run_key, revision_sha256, trigger_kind, scheduled_for_unix_ms,
                   started_unix_ms, completed_unix_ms, outcome, detail, observed_capture_sequence,
                   occurrence_json, attempt, occurrence_id, preparation_json
            FROM automation_runs
            WHERE sequence IN (SELECT MAX(sequence) FROM automation_runs GROUP BY definition_id);
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, LocalAutomationRun>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var run = await ReadRunAsync(reader, cancellationToken).ConfigureAwait(false);
            results[run.DefinitionId] = run;
        }
        return results;
    }

    /// <summary>
    /// Reads the durable capture sequence only when a capture-relative definition exists. Without one the
    /// operator projection has nothing to say about it, and the acquisition journal is never opened.
    /// </summary>
    private async ValueTask<long?> ReadCaptureSequenceIfNeededAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var needed = await ScalarLongAsync(
            connection,
            null,
            "SELECT EXISTS(SELECT 1 FROM automation_definitions WHERE trigger_kind = $trigger);",
            cancellationToken,
            ("$trigger", LocalAutomationTriggerKind.CaptureRelative.ToString())).ConfigureAwait(false);
        return needed == 0 ? null : await ReadCaptureSequenceAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "An unreadable capture sequence degrades the capture-relative trigger; it never fails a read.")]
    private async ValueTask<long?> ReadCaptureSequenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _captureSequence.GetCaptureSequenceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            CaptureSequenceUnavailable(_logger, exception);
            return null;
        }
    }

    /// <summary>The revision hash covers the schema label and every definition field, and nothing else.</summary>
    internal static string ComputeRevisionSha256(LocalAutomationDefinition definition)
        => LocalAutomationContract.ComputeRevisionSha256(definition);

    private void Record(Activity? activity, string operation, LocalAutomationCommandStatus status)
    {
        activity?.SetTag("operation", operation);
        activity?.SetTag("outcome", status.ToString());
        _telemetry?.RecordCommand(operation, status);
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.")]
    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
        => connection.BeginTransaction(deferred: false);

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes deferred transactions only through the synchronous overload.")]
    private static SqliteTransaction BeginRead(SqliteConnection connection)
        => connection.BeginTransaction(deferred: true);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant PRAGMA statements with a validated numeric bound are executed.")]
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var path = ResolveDatabasePath();
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, path);
        _authority.Acquire(_options.RawIngressRoot, Path.Combine(Path.GetDirectoryName(path)!, "local-automations.lock"));
        // Re-checked on every open rather than once per process: a sidecar can be replaced with a link
        // between opens, and the WAL and shared-memory files carry committed data just as the database does.
        EnsureDatabaseFilesArePhysical(path);
        var connection = new SqliteConnection(ResolveConnectionString());
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
            connection,
            async (configuredConnection, token) =>
        {
            using var command = configuredConnection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {_options.RawIngressSqliteBusyTimeoutSeconds * 1000};";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA synchronous = FULL;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA journal_mode = WAL;";
            // The PRAGMA reports the mode actually in force; a silent fallback to rollback journaling
            // would lose the atomicity this contract depends on, so it is asserted rather than assumed.
            var mode = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Local automation SQLite storage could not enable write-ahead logging.");
            }
        }, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private IDisposable RetainOperationAuthority(bool allowDrainingCompletion = false)
    {
        var path = ResolveDatabasePath();
        _authority.Acquire(_options.RawIngressRoot, Path.Combine(Path.GetDirectoryName(path)!, "local-automations.lock"));
        return _authority.Retain(allowDrainingCompletion);
    }

    /// <summary>Rejects a database or sidecar that is a symbolic link rather than a real file.</summary>
    private static void EnsureDatabaseFilesArePhysical(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (new FileInfo(candidate).LinkTarget is not null)
            {
                throw new InvalidDataException(
                    "Local automation SQLite storage must not be a symbolic link.");
            }
        }
    }

    private string ResolveConnectionString()
    {
        _connectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = _options.RawIngressSqliteBusyTimeoutSeconds
        }.ToString();
        return _connectionString;
    }

    private string ResolveDatabasePath()
    {
        if (_databasePath is not null)
        {
            return _databasePath;
        }
        var root = Path.GetFullPath(_options.RawIngressRoot);
        Directory.CreateDirectory(root);
        var directory = Path.GetFullPath(Path.Combine(root, DirectoryName));
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        var path = Path.GetFullPath(Path.Combine(directory, FileName));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("The local automation store path escapes the CameraAgent data root.");
        }
        _databasePath = path;
        return _databasePath;
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static DateTimeOffset Truncate(DateTimeOffset value)
        => new(value.UtcDateTime.AddTicks(-(value.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond)), TimeSpan.Zero);

    /// <summary>Trims to the stored bound without splitting a surrogate pair.</summary>
    private static string Bound(string value)
    {
        const int Maximum = LocalAutomationContract.MaximumDetailLength;
        if (value.Length <= Maximum)
        {
            return value;
        }
        var length = char.IsHighSurrogate(value[Maximum - 1]) ? Maximum - 1 : Maximum;
        return value[..length];
    }

    private static long ToUnixMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static int ReadInterval(long value)
        => value is > 0 and <= int.MaxValue
            ? (int)value
            : throw new InvalidDataException("Local automation storage contains an unusable trigger interval.");

    private static T ParseEnum<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Local automation storage contains an unknown {typeof(T).Name} value.");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant schema and statement text is passed; values remain parameterized.")]
    private static async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant statement text is passed; values remain parameterized.")]
    private static async ValueTask<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant statement text is passed; values remain parameterized.")]
    private static async ValueTask<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async ValueTask<long> ReadUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
        => await ScalarLongAsync(connection, transaction, "PRAGMA user_version;", cancellationToken)
            .ConfigureAwait(false);

    private static readonly Action<ILogger, int, Exception?> RunsSettled =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(7420, "LocalAutomationRunsSettled"),
            "Settled {InterruptedRuns} interrupted local automation runs during restart recovery.");

    private static readonly Action<ILogger, string, string, Exception?> RunCompletionLost =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(7408, "LocalAutomationRunCompletionLost"),
            "Local automation run {RunKey} was no longer claimed, so outcome {Outcome} was not recorded.");

    private static readonly Action<ILogger, Exception?> CaptureSequenceUnavailable =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(7421, "LocalAutomationCaptureSequenceUnavailable"),
            "The durable capture sequence is unavailable; capture-relative automations stay pending.");
}
