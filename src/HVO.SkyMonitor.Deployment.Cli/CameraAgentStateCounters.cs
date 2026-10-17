using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Every CameraAgent counter and version token a snapshot restore must never let be handed out again, because each
/// leaves the instance or is sent back by a client.
/// </summary>
/// <remarks>
/// <para>
/// Three kinds, each carried forward differently:
/// <list type="bullet">
/// <item><see cref="CounterKind.HighWater"/>: a sequence of identities handed out. The restored value is raised to at
/// least the highest value reached, so the next identity is new. The capture-control version is carried this way too:
/// the installer pauses capture before every snapshot and every restore, so the restored admission state and the live
/// one are the same paused state, and the installer's own recovery resumes against that version.</item>
/// <item><see cref="CounterKind.AutoIncrement"/>: the <c>sqlite_sequence</c> high-water of an AUTOINCREMENT table whose
/// row IDs are put in client action or reference tokens, raised the same way so a rewound table never reissues one.</item>
/// <item><see cref="CounterKind.StateToken"/>: an optimistic-concurrency version a client sends back and that is
/// compared equal to the current state's. Raising it to the highest value reached would make a token issued for the
/// lost state match the restored one, so a restored value below the highest value reached becomes one above it: every
/// token issued after the snapshot is stale, and the next change issues versions no client has seen.</item>
/// </list>
/// </para>
/// <para>
/// A state token whose singleton row a store creates lazily on first start may be missing from a snapshot. The previous
/// release then creates it at version 1 from the same configuration, which is the state a token of 1 describes, so a
/// floor of at most 1 needs no carrying; a higher one refuses the restore before any store is replaced. Automation
/// definitions are versioned per definition: a definition the snapshot lacks is recorded in the restored store as
/// removed at a version above its highest, so the store's own rule that a recreated definition continues its revision
/// line keeps every version it handed out from being issued again.
/// </para>
/// <para>
/// <see cref="Local"/> lists every other counter-like column the stores have, with why it never leaves the instance or
/// is never sent back. A guard test fails when a store gains a counter-like column in neither list.
/// </para>
/// </remarks>
internal static class CameraAgentStateCounters
{
    internal enum CounterKind
    {
        HighWater,
        AutoIncrement,
        StateToken
    }

    /// <param name="Store">The store's location under the state root.</param>
    /// <param name="Name">The counter's name in the journal.</param>
    /// <param name="Table">The table holding the counter; <c>sqlite_sequence</c> for <see cref="CounterKind.AutoIncrement"/>.</param>
    /// <param name="KeyColumn">The column that keys a row.</param>
    /// <param name="ValueColumn">The counter column.</param>
    /// <param name="Kind">How the restore carries it.</param>
    /// <param name="Upsert">Whether a missing row is inserted with the floor.</param>
    /// <param name="FixedKey">The one key the counter covers, when the table holds other rows too.</param>
    /// <param name="MissingRowAllowance">The highest floor a snapshot without the row may carry by doing nothing.</param>
    internal sealed record Counter(
        string Store,
        string Name,
        string Table,
        string KeyColumn,
        string ValueColumn,
        CounterKind Kind,
        bool Upsert = false,
        string? FixedKey = null,
        long MissingRowAllowance = 0);

    private const string RawIngress = "raw/journal/raw-ingress.db";
    private const string Fleet = "raw/.fleet/fleet-status.db";
    private const string Environment = "raw/.environment/environmental-observation-outbox.db";
    private const string Evidence = "raw/evidence/execution-evidence-outbox.db";
    private const string Automation = "raw/.automation/local-automations.db";
    internal const string AutomationDefinitionVersion = "automation-definition-version";

    public static IReadOnlyList<Counter> All { get; } =
    [
        new(RawIngress, "capture-sequence", "raw_capture_sequences", "agent_id", "last_sequence", CounterKind.HighWater, Upsert: true),
        new(RawIngress, "capture-control-version", "capture_control_state", "state_key", "version", CounterKind.HighWater),
        new(Fleet, "fleet-report-sequence", "fleet_status_metadata", "metadata_key", "next_sequence", CounterKind.HighWater),
        new(RawIngress, "capture-schedule-version", "capture_schedule_state", "state_key", "version", CounterKind.StateToken, MissingRowAllowance: 1),
        new(RawIngress, "named-rig-selection-version", "named_rig_selection", "state_key", "version", CounterKind.StateToken),
        new(RawIngress, "calibration-library-version", "calibration_library_state", "state_key", "version", CounterKind.StateToken),
        new(RawIngress, "processing-graph-registry-version", "processing_graph_registry_state", "state_key", "state_version", CounterKind.StateToken, MissingRowAllowance: 1),
        new(Automation, AutomationDefinitionVersion, "automation_definitions", "definition_id", "version", CounterKind.StateToken),
        new(Environment, "environmental-outbox-record-id", "sqlite_sequence", "name", "seq", CounterKind.AutoIncrement, FixedKey: "environmental_observation_outbox"),
        new(Environment, "environmental-journal-record-id", "sqlite_sequence", "name", "seq", CounterKind.AutoIncrement, FixedKey: "environmental_observation_journal"),
        new(Evidence, "execution-evidence-record-id", "sqlite_sequence", "name", "seq", CounterKind.AutoIncrement, FixedKey: "execution_evidence_units")
    ];

    /// <summary>
    /// Every other counter-like column in a CameraAgent store, keyed <c>table.column</c>, with why a rewind of it can
    /// never let a client or anything outside the instance act on different state. Counter-like means an INTEGER
    /// column named for a sequence, version, revision, generation, epoch, serial, next or last value (timestamps
    /// excluded), or an integer row-ID primary key that is not a singleton key. Artifact outboxes share one schema under
    /// every storage root. <c>CameraAgentStateCountersAuditTests</c> holds this list and <see cref="All"/> to the
    /// previous-release store images.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Local { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // raw-ingress.db: raw capture, lanes and transients.
        ["raw_capture_assignments.capture_sequence"] = "a copy of a sequence the carried capture-sequence counter handed out",
        ["raw_captures.capture_sequence"] = "a copy of a sequence the carried capture-sequence counter handed out",
        ["raw_captures.raw_capture_row_id"] = "a row ID that leaves only in read-only paging cursors and in transient action tokens that also bind the capture, artifact, hashes and expected states",
        ["raw_ingress_reconciliation.reconciliation_id"] = "a local row ID, never in a response or token",
        ["capture_lane_contexts.raw_capture_row_id"] = "the raw_captures row ID, keyed one to one",
        ["capture_lane_work.work_id"] = "a local row ID, never in a response or token",
        ["capture_lane_work.capture_sequence"] = "a copy of a capture sequence",
        ["capture_control_commands.expected_version"] = "a receipt copy of the carried capture-control version",
        ["capture_control_commands.result_version"] = "a receipt copy of the carried capture-control version",
        ["transient_capture_work.raw_capture_row_id"] = "the raw_captures row ID, keyed one to one",
        ["transient_candidate_conflicts.conflict_id"] = "a local row ID, never in a response or token",
        ["transient_runtime_operations.operation_id"] = "a local row ID, never in a response or token on its own",
        ["transient_runtime_operations.capture_sequence"] = "a copy of a capture sequence",
        ["transient_worker_frames.raw_capture_row_id"] = "the raw_captures row ID, keyed one to one",
        // raw-ingress.db: schedule and rig.
        ["capture_schedule_revisions.revision_number"] = "leaves only inside revision_id, which also carries a hash prefix of the profile, so a reissued number names a different ID unless the profile is the same",
        ["capture_schedule_state.last_decision_admitted"] = "a 0/1 decision flag, not a counter",
        ["capture_schedule_activations.activation_id"] = "a local row ID, never in a response or token",
        ["capture_schedule_activations.state_version"] = "a history copy of the carried schedule version",
        ["capture_schedule_commands.result_state_version"] = "a receipt copy of the carried schedule version",
        ["capture_schedule_expansions.deployment_location_version"] = "a cache key copied from the deployment location file, which no restore touches",
        ["named_equipment_revisions.revision_number"] = "a display number; concurrency uses random revision GUIDs",
        ["named_rig_revisions.revision_number"] = "a display number and read-only paging bound; concurrency uses random revision GUIDs",
        ["named_rig_selection.pending_schedule_version"] = "an internal copy of the schedule version, compared only inside the same store",
        ["named_rig_selection_commands.expected_version"] = "a receipt copy of the carried rig-selection version",
        ["named_rig_selection_receipts.state_version"] = "a receipt copy of the carried rig-selection version",
        // raw-ingress.db: calibration and processing.
        ["calibration_library_activations.activation_id"] = "a local row ID, never in a response or token",
        ["calibration_library_activations.state_version"] = "a history copy of the carried calibration version",
        ["calibration_library_commands.result_state_version"] = "a receipt copy of the carried calibration version",
        ["calibration_library_reconciliation.reconciliation_id"] = "a local row ID, never in a response or token",
        ["capture_processing_schema.version"] = "the store's schema version, which the migration rule compares",
        ["processing_nodes.input_evidence_version"] = "an evidence format version, not a counter",
        ["processing_outputs.capture_sequence"] = "a copy of a capture sequence",
        ["processing_replay_work.work_id"] = "a local AUTOINCREMENT row ID; replay execution IDs are content- and request-derived",
        ["processing_output_diagnostics.diagnostic_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        // Fleet.
        ["fleet_status_metadata.schema_version"] = "the store's schema version, which the migration rule compares",
        ["fleet_status_records.sequence"] = "a copy of a sequence the carried fleet-report counter handed out",
        ["fleet_status_records.record_id"] = "a local AUTOINCREMENT row ID; reports are identified by their sequence",
        // Environment.
        ["environmental_observation_outbox_audit.audit_id"] = "a local AUTOINCREMENT row ID, used only in a read-only audit cursor",
        ["environmental_capture_regime_state.capture_sequence"] = "a copy of a capture sequence",
        ["environmental_acquisition_attempts.attempt_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        ["environmental_acquisition_attempts.capture_sequence"] = "a copy of a capture sequence",
        ["environmental_capture_associations.association_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        ["environmental_capture_associations.capture_sequence"] = "a copy of a capture sequence",
        ["environmental_observation_central_projection.projection_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        // Evidence: an origin is per boot session, and every restore runs with the instance stopped.
        ["execution_evidence_schema.version"] = "the store's schema version, which the migration rule compares",
        ["execution_evidence_origins.next_sequence"] = "a per-origin sequence; the origin hash covers the boot session, new at every start, so no restored origin issues again",
        ["execution_evidence_origins.acknowledged_through_sequence"] = "a per-origin acknowledgement mark under a per-boot origin",
        ["execution_evidence_units.origin_sequence"] = "a copy of a per-boot origin sequence",
        ["execution_evidence_conflicts.origin_sequence"] = "a copy of a per-boot origin sequence",
        ["execution_evidence_audit.audit_id"] = "a local AUTOINCREMENT row ID, used only in a read-only audit cursor",
        ["execution_evidence_conflicts.conflict_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        // Artifact outboxes, under every storage root.
        ["artifact_outbox_schema.version"] = "the store's schema version, which the migration rule compares",
        ["artifact_outbox_records.record_id"] = "a local AUTOINCREMENT row ID in a read-only paging cursor; actions key on the content-derived idempotency key",
        ["artifact_outbox_audit.audit_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        ["artifact_outbox_conflicts.conflict_id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        // Time-lapses: job IDs are content-derived, and the revision is accepted back only by in-process Blazor
        // circuits, which the stop before every restore ends.
        ["jobs.revision"] = "accepted back only by in-process Blazor circuits, which the stop before every restore ends",
        ["retries.expected_revision"] = "a copy of a job revision",
        // Automation.
        ["automation_definition_revisions.version"] = "the revision line the carried definition version continues",
        ["automation_definition_revisions.sequence"] = "an AUTOINCREMENT returned as storeVersion for display; no endpoint or UI sends it back",
        ["automation_runs.sequence"] = "a local AUTOINCREMENT row ID, used only as a UI element key",
        ["automation_runs.observed_capture_sequence"] = "a copy of a capture sequence",
        ["automation_progress.last_capture_sequence"] = "a copy of a capture sequence",
        // Identity (EF Core).
        ["AspNetRoleClaims.Id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        ["AspNetUserClaims.Id"] = "a local AUTOINCREMENT row ID, never in a response or token",
        ["__EFMigrationsLock.Id"] = "EF Core's migration lock row",
    };

    /// <summary>Every counter's current value in the stores under <paramref name="stateRoot"/>; the instance is stopped.</summary>
    public static IReadOnlyList<StateCounterFloor> Read(string stateRoot)
    {
        var (floors, unreadable) = ReadReadable(stateRoot);
        if (unreadable.Count > 0)
            throw new InstallerException($"The CameraAgent store '{unreadable[0]}' could not be read for its counters.");
        return floors;
    }

    /// <summary>
    /// Every counter's current value in the stores that can be read, and the stores that cannot. A restore uses this so
    /// that a store a failed candidate damaged beyond reading never blocks its own recovery; that store's counters are
    /// carried only as far as the journal already recorded them.
    /// </summary>
    public static (IReadOnlyList<StateCounterFloor> Floors, IReadOnlyList<string> Unreadable) ReadReadable(string stateRoot)
    {
        var floors = new List<StateCounterFloor>();
        var unreadable = new List<string>();
        foreach (var counter in All)
        {
            var path = CameraAgentStateSnapshot.StorePath(stateRoot, counter.Store);
            if (!File.Exists(path) || unreadable.Contains(counter.Store)) continue;
            try
            {
                using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(path);
                floors.AddRange(counter.Name == AutomationDefinitionVersion
                    ? ReadAutomationHighWater(database.Connection, counter)
                    : Read(database.Connection, counter));
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InstallerException)
            {
                unreadable.Add(counter.Store);
                floors.RemoveAll(floor => floor.Store == counter.Store);
            }
        }
        return (floors, unreadable);
    }

    /// <summary>The higher of each counter's two values, so a repeated restore keeps the highest mark it ever saw.</summary>
    public static IReadOnlyList<StateCounterFloor> Merge(
        IEnumerable<StateCounterFloor>? recorded,
        IEnumerable<StateCounterFloor> observed)
        => (recorded ?? []).Concat(observed)
            .GroupBy(static floor => (floor.Store, floor.Counter, floor.Key))
            .Select(static group =>
            {
                var highest = group.MaxBy(static floor => floor.Value)!;
                // A retry reads the restored store, where a carried definition is now a removed revision with the
                // same content, so either record's carry continues the line.
                return highest.Carry is null && group.FirstOrDefault(static floor => floor.Carry is not null) is { } carried
                    ? highest with { Carry = carried.Carry }
                    : highest;
            })
            .OrderBy(static floor => floor.Store, StringComparer.Ordinal)
            .ThenBy(static floor => floor.Counter, StringComparer.Ordinal)
            .ThenBy(static floor => floor.Key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Drops each automation definition's carried revision unless the target snapshot needs it: only a definition the
    /// snapshot neither has nor already versions past the floor is recorded as removed, so only its latest revision is
    /// kept in the journal.
    /// </summary>
    public static IReadOnlyList<StateCounterFloor> TrimCarries(IReadOnlyList<StateCounterFloor> floors, string? automationSnapshotFile)
    {
        ArgumentNullException.ThrowIfNull(floors);
        var counter = All.Single(static counter => counter.Name == AutomationDefinitionVersion);
        Dictionary<string, long> snapshotMarks = [];
        HashSet<string> snapshotRows = [];
        if (automationSnapshotFile is not null && File.Exists(automationSnapshotFile))
        {
            using var connection = OpenImmutable(automationSnapshotFile);
            snapshotMarks = ReadAutomationHighWater(connection, counter).ToDictionary(static mark => mark.Key, static mark => mark.Value, StringComparer.Ordinal);
            snapshotRows = Read(connection, counter).Select(static row => row.Key).ToHashSet(StringComparer.Ordinal);
        }
        return floors.Select(floor =>
            {
                if (floor.Counter != AutomationDefinitionVersion || floor.Carry is null) return floor;
                var needed = !snapshotRows.Contains(floor.Key) &&
                             !(snapshotMarks.TryGetValue(floor.Key, out var reached) && reached >= floor.Value);
                return needed ? floor : floor with { Carry = null };
            })
            .ToArray();
    }

    /// <summary>
    /// Whether every floor recorded for <paramref name="relative"/> can be carried into the store at
    /// <paramref name="path"/>: its table and columns exist, and a counter without an upsert has its row, or a floor the
    /// missing row's lazy creation already respects, or (for an automation definition) a carried latest revision and a
    /// revision table it can be written to.
    /// </summary>
    public static void EnsureApplicable(string path, string relative, IReadOnlyList<StateCounterFloor> floors)
    {
        var pending = floors.Where(floor => floor.Store == relative).ToArray();
        if (pending.Length == 0) return;
        using var connection = OpenImmutable(path);
        foreach (var floor in pending)
        {
            var counter = Find(floor);
            if (!HasColumns(connection, null, counter.Table, counter.KeyColumn, counter.ValueColumn))
                throw Uncarriable(relative, counter);
            if (counter.Kind == CounterKind.AutoIncrement || counter.Upsert) continue;
            if (counter.Name == AutomationDefinitionVersion)
            {
                var restored = ReadAutomationHighWater(connection, counter).SingleOrDefault(value => value.Key == floor.Key);
                var rowExists = Read(connection, counter).Any(value => value.Key == floor.Key);
                if (!rowExists && (restored is null || restored.Value < floor.Value) &&
                    (floor.Carry is null || !HasColumns(connection, null, "automation_definition_revisions",
                        "definition_id", "version", "revision_sha256", "definition_json", "removed", "recorded_unix_ms", "actor", "reason")))
                    throw Uncarriable(relative, counter);
                continue;
            }
            if (Read(connection, counter).All(value => value.Key != floor.Key) && floor.Value > counter.MissingRowAllowance)
                throw Uncarriable(relative, counter);
        }
    }

    /// <summary>
    /// Carries each floor into the restored store at <paramref name="path"/>, in one transaction, and proves the result.
    /// SQLite syncs the database on commit; the caller syncs the directory.
    /// </summary>
    public static void Apply(string path, string relative, IReadOnlyList<StateCounterFloor> floors)
    {
        var pending = floors.Where(floor => floor.Store == relative).ToArray();
        if (pending.Length == 0) return;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using (var synchronous = connection.CreateCommand())
            {
                synchronous.CommandText = "PRAGMA synchronous = FULL;";
                synchronous.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction();
            foreach (var floor in pending)
            {
                var counter = Find(floor);
                switch (counter.Kind)
                {
                    case CounterKind.HighWater:
                        ApplyHighWater(connection, transaction, counter, floor, relative);
                        break;
                    case CounterKind.AutoIncrement:
                        ApplyAutoIncrement(connection, transaction, counter, floor);
                        break;
                    case CounterKind.StateToken when counter.Name == AutomationDefinitionVersion:
                        ApplyAutomationDefinition(connection, transaction, counter, floor, relative);
                        break;
                    case CounterKind.StateToken:
                        ApplyStateToken(connection, transaction, counter, floor, relative);
                        break;
                }
            }
            transaction.Commit();
        }
        using var verify = OpenImmutable(path);
        foreach (var floor in pending)
        {
            var counter = Find(floor);
            var carried = counter.Name == AutomationDefinitionVersion
                ? ReadAutomationHighWater(verify, counter).SingleOrDefault(value => value.Key == floor.Key)
                : Read(verify, counter).SingleOrDefault(value => value.Key == floor.Key);
            if (carried is null)
            {
                if (counter.Kind == CounterKind.StateToken && floor.Value <= counter.MissingRowAllowance) continue;
                throw new InstallerException($"The restored '{relative}' lost its {counter.Name} counter.");
            }
            if (carried.Value < floor.Value)
                throw new InstallerException($"The restored '{relative}' rewound its {counter.Name} counter.");
        }
    }

    private static void ApplyHighWater(SqliteConnection connection, SqliteTransaction transaction, Counter counter, StateCounterFloor floor, string relative)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Table and column names come from the fixed counter list above, never from input.
        command.CommandText = counter.Upsert
            ? $"INSERT INTO {counter.Table}({counter.KeyColumn}, {counter.ValueColumn}) VALUES ($key, $value) " +
              $"ON CONFLICT({counter.KeyColumn}) DO UPDATE SET {counter.ValueColumn} = MAX({counter.ValueColumn}, excluded.{counter.ValueColumn});"
            : $"UPDATE {counter.Table} SET {counter.ValueColumn} = MAX({counter.ValueColumn}, $value) WHERE {counter.KeyColumn} = $key;";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$key", KeyValue(connection, transaction, counter, floor.Key));
        command.Parameters.AddWithValue("$value", floor.Value);
        if (command.ExecuteNonQuery() != 1)
            throw new InstallerException($"The restored '{relative}' did not accept its {counter.Name} counter floor.");
    }

    /// <summary>
    /// Raises <c>sqlite_sequence</c> for one table. It has no unique key, and a table gets its row only on its first
    /// insert, so the row is updated when present and inserted otherwise.
    /// </summary>
    private static void ApplyAutoIncrement(SqliteConnection connection, SqliteTransaction transaction, Counter counter, StateCounterFloor floor)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE sqlite_sequence SET seq = MAX(seq, $value) WHERE name = $key;";
        update.Parameters.AddWithValue("$key", counter.FixedKey!);
        update.Parameters.AddWithValue("$value", floor.Value);
        if (update.ExecuteNonQuery() > 0) return;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO sqlite_sequence(name, seq) VALUES ($key, $value);";
        insert.Parameters.AddWithValue("$key", counter.FixedKey!);
        insert.Parameters.AddWithValue("$value", floor.Value);
        insert.ExecuteNonQuery();
    }

    private static void ApplyStateToken(SqliteConnection connection, SqliteTransaction transaction, Counter counter, StateCounterFloor floor, string relative)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Table and column names come from the fixed counter list above, never from input.
        command.CommandText =
            $"UPDATE {counter.Table} SET {counter.ValueColumn} = CASE WHEN {counter.ValueColumn} >= $value THEN {counter.ValueColumn} ELSE $value + 1 END " +
            $"WHERE {counter.KeyColumn} = $key;";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$key", KeyValue(connection, transaction, counter, floor.Key));
        command.Parameters.AddWithValue("$value", floor.Value);
        var updated = command.ExecuteNonQuery();
        if (updated == 0 && floor.Value > counter.MissingRowAllowance)
            throw new InstallerException($"The restored '{relative}' did not accept its {counter.Name} counter floor.");
    }

    /// <summary>
    /// Carries one automation definition's version line. A restored definition behind the highest version handed out
    /// moves above it. A definition the snapshot lacks gets a removed revision above it, the state the store keeps for a
    /// removed definition, so recreating it continues the line instead of restarting it at one.
    /// </summary>
    private static void ApplyAutomationDefinition(SqliteConnection connection, SqliteTransaction transaction, Counter counter, StateCounterFloor floor, string relative)
    {
        var restored = ReadAutomationHighWater(connection, counter, transaction).SingleOrDefault(value => value.Key == floor.Key);
        if (restored is not null && restored.Value >= floor.Value) return;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE automation_definitions SET version = $value + 1 WHERE definition_id = $key;";
            update.Parameters.AddWithValue("$key", floor.Key);
            update.Parameters.AddWithValue("$value", floor.Value);
            if (update.ExecuteNonQuery() == 1) return;
        }
        if (floor.Carry is not { } carry)
            throw new InstallerException($"The restored '{relative}' cannot continue the version line of automation definition '{floor.Key}'.");
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO automation_definition_revisions(
                definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor, reason)
            VALUES ($key, $value + 1, $revision, $json, 1, $now, 'hvo-sky-install', $reason);
            """;
        insert.Parameters.AddWithValue("$key", floor.Key);
        insert.Parameters.AddWithValue("$value", floor.Value);
        insert.Parameters.AddWithValue("$revision", carry.RevisionSha256);
        insert.Parameters.AddWithValue("$json", carry.DefinitionJson);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$reason",
            "Created after the restored database snapshot; recorded as removed so its version line continues.");
        insert.ExecuteNonQuery();
    }

    private static Counter Find(StateCounterFloor floor)
        => All.SingleOrDefault(counter => counter.Store == floor.Store && counter.Name == floor.Counter)
           ?? throw new InstallerException($"The journal records an unknown CameraAgent counter '{floor.Counter}'.");

    private static InstallerException Uncarriable(string relative, Counter counter)
        => new($"The snapshot of '{relative}' cannot carry the {counter.Name} counter forward, so restoring it could hand out a sequence or version twice.");

    private static List<StateCounterFloor> Read(SqliteConnection connection, Counter counter, SqliteTransaction? transaction = null)
    {
        if (!HasColumns(connection, transaction, counter.Table, counter.KeyColumn, counter.ValueColumn)) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Table and column names come from the fixed counter list above, never from input.
        command.CommandText = counter.FixedKey is null
            ? $"SELECT CAST({counter.KeyColumn} AS TEXT), {counter.ValueColumn} FROM {counter.Table};"
            : $"SELECT CAST({counter.KeyColumn} AS TEXT), {counter.ValueColumn} FROM {counter.Table} WHERE {counter.KeyColumn} = $key;";
#pragma warning restore CA2100
        if (counter.FixedKey is not null) command.Parameters.AddWithValue("$key", counter.FixedKey);
        using var reader = command.ExecuteReader();
        var values = new List<StateCounterFloor>();
        while (reader.Read())
        {
            values.Add(new StateCounterFloor(counter.Store, counter.Name, reader.GetString(0), reader.GetInt64(1)));
        }
        return values;
    }

    /// <summary>
    /// The highest version each automation definition has reached: its row's, or a higher one its revision line holds
    /// (a removed definition keeps only its line), with the latest revision a restore needs to continue a line the
    /// snapshot lacks.
    /// </summary>
    private static List<StateCounterFloor> ReadAutomationHighWater(SqliteConnection connection, Counter counter, SqliteTransaction? transaction = null)
    {
        if (!HasColumns(connection, transaction, "automation_definition_revisions", "definition_id", "version", "revision_sha256", "definition_json"))
            return Read(connection, counter, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH marks(definition_id, version) AS (
                SELECT definition_id, version FROM automation_definitions
                UNION ALL
                SELECT definition_id, version FROM automation_definition_revisions)
            SELECT m.definition_id, MAX(m.version),
                   (SELECT r.revision_sha256 FROM automation_definition_revisions r
                    WHERE r.definition_id = m.definition_id ORDER BY r.version DESC, r.sequence DESC LIMIT 1),
                   (SELECT r.definition_json FROM automation_definition_revisions r
                    WHERE r.definition_id = m.definition_id ORDER BY r.version DESC, r.sequence DESC LIMIT 1)
            FROM marks m GROUP BY m.definition_id;
            """;
        using var reader = command.ExecuteReader();
        var values = new List<StateCounterFloor>();
        while (reader.Read())
        {
            values.Add(new StateCounterFloor(
                counter.Store, counter.Name, reader.GetString(0), reader.GetInt64(1),
                reader.IsDBNull(2) || reader.IsDBNull(3) ? null : new StateCounterCarry(reader.GetString(2), reader.GetString(3))));
        }
        return values;
    }

    private static bool HasColumns(SqliteConnection connection, SqliteTransaction? transaction, string table, params string[] columns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var present = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) present.Add(reader.GetString(0));
        return columns.All(present.Contains);
    }

    /// <summary>Binds the key with the column's own type, so an integer key matches its row.</summary>
    private static object KeyValue(SqliteConnection connection, SqliteTransaction transaction, Counter counter, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", counter.Table);
        command.Parameters.AddWithValue("$column", counter.KeyColumn);
        var type = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        return type.Contains("INT", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(key, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : key;
    }

    private static SqliteConnection OpenImmutable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(path).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
