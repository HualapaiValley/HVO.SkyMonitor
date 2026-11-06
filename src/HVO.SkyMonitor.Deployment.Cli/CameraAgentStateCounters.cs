using System.Diagnostics.CodeAnalysis;
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
/// floor of at most 1 needs no carrying. Automation definitions are versioned per definition: a definition the snapshot
/// lacks is recorded in the restored store as removed at a version above its highest, so the store's own rule that a
/// recreated definition continues its revision line keeps every version it handed out from being issued again.
/// </para>
/// <para>
/// Carrying is best effort and total (#1314 R17–R20). Every carry is planned before any store is replaced
/// (<see cref="PlanCarry"/>): each floor is screened against the target snapshot, and what remains for a store is
/// carried and verified on a scratch copy of it, which proves it against the store's real constraints. The restore then
/// carries the same floors into its staged copy of each store, before that copy replaces the live one
/// (<see cref="CarryStaged"/>), so the live store is only ever the snapshot's bytes or a verified carried copy installed
/// by rename. A floor that cannot be carried, or a store whose trial or staged carry fails in any way, is put back as the
/// snapshot recorded it and named on standard error, so a value a candidate or a snapshot holds can never stop a restore.
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
    /// <param name="IntegerKey">Whether the key column holds integers (a singleton's state key) rather than text.</param>
    internal sealed record Counter(
        string Store,
        string Name,
        string Table,
        string KeyColumn,
        string ValueColumn,
        CounterKind Kind,
        bool Upsert = false,
        string? FixedKey = null,
        long MissingRowAllowance = 0,
        bool IntegerKey = false);

    private const string RawIngress = "raw/journal/raw-ingress.db";
    private const string Fleet = "raw/.fleet/fleet-status.db";
    private const string Environment = "raw/.environment/environmental-observation-outbox.db";
    private const string Evidence = "raw/evidence/execution-evidence-outbox.db";
    private const string Automation = "raw/.automation/local-automations.db";
    internal const string AutomationDefinitionVersion = "automation-definition-version";
    private const int MaximumCarryBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<Counter> All { get; } =
    [
        new(RawIngress, "capture-sequence", "raw_capture_sequences", "agent_id", "last_sequence", CounterKind.HighWater, Upsert: true),
        new(RawIngress, "capture-control-version", "capture_control_state", "state_key", "version", CounterKind.HighWater, IntegerKey: true),
        new(Fleet, "fleet-report-sequence", "fleet_status_metadata", "metadata_key", "next_sequence", CounterKind.HighWater, IntegerKey: true),
        new(RawIngress, "capture-schedule-version", "capture_schedule_state", "state_key", "version", CounterKind.StateToken, MissingRowAllowance: 1, IntegerKey: true),
        new(RawIngress, "named-rig-selection-version", "named_rig_selection", "state_key", "version", CounterKind.StateToken, IntegerKey: true),
        new(RawIngress, "calibration-library-version", "calibration_library_state", "state_key", "version", CounterKind.StateToken, IntegerKey: true),
        new(RawIngress, "processing-graph-registry-version", "processing_graph_registry_state", "state_key", "state_version", CounterKind.StateToken, MissingRowAllowance: 1, IntegerKey: true),
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

    /// <summary>
    /// Runs at each step of reading, planning and applying a carry, named <c>read:&lt;store&gt;</c>, <c>trim</c>,
    /// <c>screen:&lt;store&gt;</c>, <c>trial:&lt;store&gt;</c>, <c>trial-verify:&lt;store&gt;</c>,
    /// <c>trial-commit:&lt;store&gt;</c>, and, on the staged copy, <c>apply:&lt;store&gt;</c>,
    /// <c>apply-verify:&lt;store&gt;</c>, <c>apply-commit:&lt;store&gt;</c>, <c>apply-closed:&lt;store&gt;</c> and
    /// <c>apply-readback:&lt;store&gt;</c>. Fault-injection tests throw from it, or damage the file being carried, to prove
    /// every step degrades rather than blocks a restore and never leaves the live store partly written; production leaves
    /// it empty.
    /// </summary>
    internal static Action<string> CarryStep { get; set; } = static _ => { };

    /// <summary>The filesystem measured before a trial; tests replace it without filling a real volume.</summary>
    internal static IHostEnvironment TrialHost { get; set; } = LinuxHostEnvironment.Instance;

    /// <summary>What a restore carries into the snapshot it puts back, and what it puts back without carrying, with why.</summary>
    /// <param name="Floors">The floors that were screened against the target snapshot and carried in a trial.</param>
    /// <param name="Uncarried">Each counter or store put back as the snapshot recorded it, and why.</param>
    internal sealed record CarryPlan(IReadOnlyList<StateCounterFloor> Floors, IReadOnlyList<(string What, string Reason)> Uncarried);

    /// <summary>
    /// Every counter's current value in the stores that can be read, and the stores that cannot with why. A restore
    /// uses this so that a store a failed candidate left damaged, in another encoding, or holding a counter in a shape
    /// the installer does not carry never blocks its own recovery: any failure of any kind makes that store unreadable,
    /// and its counters are carried only as far as the journal already recorded them.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A store a candidate wrote must never block its own recovery, whatever the failure; it is reported unreadable instead.")]
    public static (IReadOnlyList<StateCounterFloor> Floors, IReadOnlyList<(string Store, string Reason)> Unreadable) ReadReadable(string stateRoot)
    {
        var floors = new List<StateCounterFloor>();
        var unreadable = new List<(string Store, string Reason)>();
        foreach (var counter in All)
        {
            var path = CameraAgentStateSnapshot.StorePath(stateRoot, counter.Store);
            if (unreadable.Any(entry => entry.Store == counter.Store)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                CarryStep($"read:{counter.Store}");
                using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(path);
                floors.AddRange(counter.Name == AutomationDefinitionVersion
                    ? ReadAutomationHighWater(database.Connection, counter, includeCarry: true)
                    : Read(database.Connection, counter));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unreadable.Add((counter.Store, exception.Message));
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
    /// kept in the journal. Trimming only keeps the journal small, so a target it cannot read keeps every carry, and the
    /// carry plan decides what is applied.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Trimming only shrinks the journal; any failure keeps the carries and leaves the decision to the carry plan.")]
    public static IReadOnlyList<StateCounterFloor> TrimCarries(IReadOnlyList<StateCounterFloor> floors, string? automationSnapshotFile)
    {
        ArgumentNullException.ThrowIfNull(floors);
        try
        {
            CarryStep("trim");
            var counter = All.Single(static counter => counter.Name == AutomationDefinitionVersion);
            Dictionary<string, long> snapshotMarks = [];
            HashSet<string> snapshotRows = [];
            if (automationSnapshotFile is not null && File.Exists(automationSnapshotFile))
            {
                using var connection = OpenImmutable(automationSnapshotFile);
                snapshotMarks = ReadAutomationHighWater(connection, counter, includeCarry: false)
                    .ToDictionary(static mark => mark.Key, static mark => mark.Value, StringComparer.Ordinal);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return floors;
        }
    }

    /// <summary>
    /// Plans, before any store is replaced, what a restore carries into each store of the target snapshot. Each floor is
    /// first screened against the target store's own rows, schema and the range its carry writes and verifies; a floor
    /// that cannot be carried is put back as the snapshot recorded it. The floors that remain for a store are then
    /// carried, and verified, in one transaction on a scratch copy of the target store under
    /// <paramref name="trialDirectory"/>, which proves them against the store's real constraints; a store whose trial
    /// fails in any way is put back without carrying any of its counters. Authentication proves the snapshot's bytes,
    /// not that its values can be read, so a target store the installer cannot interpret is put back the same way. The
    /// plan never throws: carrying is best effort and never blocks a restore.
    /// </summary>
    /// <param name="floors">The highest marks recorded and observed.</param>
    /// <param name="snapshotStore">The target snapshot's copy of a store, or null when it records the store as absent.</param>
    /// <param name="trialDirectory">A directory the plan may create, fill and remove.</param>
    /// <param name="restoreBytes">Space kept for copying the target snapshot back after the trial.</param>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Carrying a counter is best effort; any failure puts that store back without its counters rather than blocking the restore.")]
    public static CarryPlan PlanCarry(
        IReadOnlyList<StateCounterFloor> floors, Func<string, string?> snapshotStore, string trialDirectory, long restoreBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        var carried = new List<StateCounterFloor>();
        var uncarried = new List<(string What, string Reason)>();
        try
        {
            foreach (var store in floors.GroupBy(static floor => floor.Store, StringComparer.Ordinal))
            {
                IReadOnlyList<StateCounterFloor> pending = [.. store];
                try
                {
                    // A store the snapshot records as absent receives nothing; its floors only keep the live store.
                    if (snapshotStore(store.Key) is not { } target)
                    {
                        carried.AddRange(store);
                        continue;
                    }
                    CarryStep($"screen:{store.Key}");
                    var (safe, refused) = Screen(target, [.. store]);
                    uncarried.AddRange(refused);
                    pending = safe;
                    if (safe.Count == 0) continue;
                    Trial(target, store.Key, safe, trialDirectory, restoreBytes);
                    carried.AddRange(safe);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    uncarried.AddRange(pending.Select(floor => (Describe(floor), exception.Message)));
                }
            }
        }
        finally
        {
            RemoveTrialDirectory(trialDirectory);
        }
        return new CarryPlan(carried, uncarried);
    }

    /// <summary>
    /// Carries each floor into <paramref name="staged"/>, a copy of the snapshot's store that has not replaced anything
    /// yet, and proves the copy fit to install: one transaction that commits only when every floor reads back, a clean
    /// close that leaves no rollback journal or write-ahead log beside it, <c>PRAGMA integrity_check</c>, and every floor
    /// read back again from the closed file. It throws on any failure, and the caller then discards the copy and installs
    /// a pure one, so a carry can never leave the live store partly written. SQLite syncs the database on commit; the
    /// caller syncs the file again before it is renamed into place.
    /// </summary>
    public static void CarryStaged(string staged, string relative, IReadOnlyList<StateCounterFloor> floors)
    {
        ArgumentNullException.ThrowIfNull(floors);
        var pending = floors.Where(floor => floor.Store == relative).ToArray();
        if (pending.Length == 0) return;
        ApplyAndVerify(staged, relative, pending, "apply");
        CarryStep($"apply-closed:{relative}");
        foreach (var sidecar in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => staged + suffix))
        {
            if (File.Exists(sidecar))
                throw new InstallerException($"The carried copy of '{relative}' left '{Path.GetFileName(sidecar)}' behind when it closed.");
        }
        using var connection = OpenImmutable(staged);
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new InstallerException($"The carried copy of '{relative}' does not pass its integrity check.");
        }
        CarryStep($"apply-readback:{relative}");
        VerifyCarried(connection, null, relative, pending);
    }

    /// <summary>
    /// The floors the target store can be given, and each it cannot with why: an unknown counter; a value whose carry
    /// would write a value the reader then rejects (a state token writes one above its floor, so its floor must leave
    /// room); a key the target column cannot hold (a NUL, which SQLite's own string functions stop at, or a definition ID
    /// outside the 1 to 64 characters the revision table checks); or a floor the target cannot carry at all (its table or
    /// columns are missing, its row is missing beyond the lazy-row allowance, or a definition the target lacks has no
    /// carried revision or no revision table to record it in). A floor the target already meets needs no carrying.
    /// </summary>
    private static (List<StateCounterFloor> Safe, List<(string What, string Reason)> Refused) Screen(
        string target, IReadOnlyList<StateCounterFloor> floors)
    {
        var safe = new List<StateCounterFloor>();
        var refused = new List<(string What, string Reason)>();
        using var connection = OpenImmutable(target);
        foreach (var floor in floors)
        {
            var counter = All.SingleOrDefault(counter => counter.Store == floor.Store && counter.Name == floor.Counter);
            string? reason;
            if (counter is null)
            {
                refused.Add((Describe(floor), "the installer does not know this counter"));
                continue;
            }
            reason = floor.Value < 0 || floor.Value > MaximumFloor(counter)
                ? "its value is outside the range a carry can write and verify"
                : KeyProblem(counter, floor.Key) ?? Uncarriable(connection, counter, floor);
            if (reason is null) safe.Add(floor);
            else refused.Add((Describe(floor), reason));
        }
        return (safe, refused);
    }

    /// <summary>Names a floor on one terminal-safe line, retaining every character of its key unless it reads as a credential.</summary>
    internal static string Describe(StateCounterFloor floor)
        => $"the {CameraAgentStateDiagnostics.Identity(floor.Counter)} counter of '{CameraAgentStateDiagnostics.Identity(floor.Store)}' with key \"{CameraAgentStateDiagnostics.Identity(floor.Key)}\"";

    /// <summary>
    /// The highest floor a counter's carry can write and the reader then accept: a state token writes one above its
    /// floor, everything else writes the floor itself, and the reader accepts up to <see cref="long.MaxValue"/> - 1.
    /// </summary>
    private static long MaximumFloor(Counter counter)
        => counter.Kind == CounterKind.StateToken ? long.MaxValue - 2 : long.MaxValue - 1;

    private static string? KeyProblem(Counter counter, string key)
    {
        if (counter.IntegerKey)
            return long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
                   integer.ToString(CultureInfo.InvariantCulture) == key
                ? null
                : "its key is not an integer";
        if (key.Contains('\0', StringComparison.Ordinal)) return "its key contains a NUL character";
        if (counter.Name == AutomationDefinitionVersion && key.EnumerateRunes().Count() is < 1 or > 64)
            return "its definition ID is outside the 1 to 64 characters the store accepts";
        return null;
    }

    /// <summary>Why the target cannot carry <paramref name="floor"/> at all, or null when it can or needs nothing.</summary>
    private static string? Uncarriable(SqliteConnection connection, Counter counter, StateCounterFloor floor)
    {
        if (!HasColumns(connection, null, counter.Table, counter.KeyColumn, counter.ValueColumn))
            return "the snapshot's store has no such counter column";
        if (counter.Kind == CounterKind.AutoIncrement || counter.Upsert) return null;
        if (counter.Name == AutomationDefinitionVersion)
        {
            var restored = ReadAutomationHighWater(connection, counter, includeCarry: false).FirstOrDefault(value => value.Key == floor.Key);
            if (restored is not null && restored.Value >= floor.Value) return null;
            if (Read(connection, counter).Any(value => value.Key == floor.Key)) return null;
            return floor.Carry is not null && HasColumns(connection, null, "automation_definition_revisions",
                       "definition_id", "version", "revision_sha256", "definition_json", "removed", "recorded_unix_ms", "actor", "reason")
                ? null
                : "the snapshot lacks the definition and the restore has no revision to record it as removed";
        }
        if (Read(connection, counter).Any(value => value.Key == floor.Key) || floor.Value <= counter.MissingRowAllowance) return null;
        return "the snapshot has no row to carry it into";
    }

    /// <summary>
    /// Carries and verifies <paramref name="floors"/> on a scratch copy of the target store, which is then removed. It
    /// throws on any failure; nothing it does touches the snapshot or the live stores.
    /// </summary>
    private static void Trial(
        string target, string relative, IReadOnlyList<StateCounterFloor> floors, string trialDirectory, long restoreBytes)
    {
        var bytes = new FileInfo(target).Length;
        // Scratch and its concurrent rollback journal are additional to the copy a restore still needs.
        // Eight bytes per minimum-size SQLite page cover journal record headers; headroom covers sector headers.
        // This is a current-size estimate, not a reservation against trigger growth or other filesystem users.
        var required = checked(2 * bytes + JournalRecordBytes(bytes) + Math.Max(bytes, restoreBytes) + 256L * 1024 * 1024);
        if (TrialHost.MeasureFileSystem(trialDirectory).AvailableBytes < required)
            throw new InstallerException("Insufficient free space for the carry trial and its rollback journal while keeping space for the restore.");
        SafeFileSystem.CreateOwnerDirectory(trialDirectory);
        var scratch = Path.Combine(trialDirectory, "store.db");
        RemoveScratch(scratch);
        try
        {
            File.Copy(target, scratch);
            File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ApplyAndVerify(scratch, relative, floors, "trial");
        }
        finally
        {
            RemoveScratch(scratch);
        }
    }

    internal static long JournalRecordBytes(long databaseBytes) => checked((databaseBytes / 512 + 1) * 8);

    private static void RemoveScratch(string scratch)
    {
        foreach (var file in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => scratch + suffix).Prepend(scratch))
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Removing the scratch directory is housekeeping; a leftover is removed by the next plan.")]
    private static void RemoveTrialDirectory(string trialDirectory)
    {
        try
        {
            if (!Directory.Exists(trialDirectory)) return;
            RemoveScratch(Path.Combine(trialDirectory, "store.db"));
            Directory.Delete(trialDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Carries each floor into the store at <paramref name="path"/> and reads every one back through the same reader,
    /// inside one transaction that commits only when all of them verify. Throws on any failure, which rolls back.
    /// </summary>
    private static void ApplyAndVerify(string path, string relative, IReadOnlyList<StateCounterFloor> pending, string phase)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var synchronous = connection.CreateCommand())
        {
            synchronous.CommandText = "PRAGMA synchronous = FULL;";
            synchronous.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        CarryStep($"{phase}:{relative}");
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
        CarryStep($"{phase}-verify:{relative}");
        VerifyCarried(connection, transaction, relative, pending);
        CarryStep($"{phase}-commit:{relative}");
        transaction.Commit();
    }

    /// <summary>Reads every floor back through the same readers, and throws unless each is carried or needs nothing.</summary>
    private static void VerifyCarried(
        SqliteConnection connection, SqliteTransaction? transaction, string relative, IReadOnlyList<StateCounterFloor> pending)
    {
        foreach (var floor in pending)
        {
            var counter = Find(floor);
            var carried = counter.Name == AutomationDefinitionVersion
                ? ReadAutomationHighWater(connection, counter, includeCarry: false, transaction).FirstOrDefault(value => value.Key == floor.Key)
                : Read(connection, counter, transaction).FirstOrDefault(value => value.Key == floor.Key);
            if (carried is null)
            {
                if (!counter.Upsert && floor.Value <= counter.MissingRowAllowance) continue;
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
        var key = KeyValue(connection, transaction, counter, floor.Key);
        command.Parameters.AddWithValue("$key", key ?? DBNull.Value);
        command.Parameters.AddWithValue("$value", floor.Value);
        var updated = key is null ? 0 : command.ExecuteNonQuery();
        // A row the snapshot lacks whose floor needs no carrying was let through by EnsureApplicable for the same reason.
        if (updated != 1 && !(updated == 0 && !counter.Upsert && floor.Value <= counter.MissingRowAllowance))
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
        var key = KeyValue(connection, transaction, counter, floor.Key);
        command.Parameters.AddWithValue("$key", key ?? DBNull.Value);
        command.Parameters.AddWithValue("$value", floor.Value);
        var updated = key is null ? 0 : command.ExecuteNonQuery();
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
        var restored = ReadAutomationHighWater(connection, counter, includeCarry: false, transaction).FirstOrDefault(value => value.Key == floor.Key);
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

    /// <summary>
    /// Each row's counter, read only once SQLite says the store is UTF-8, its key has the counter's key type and its
    /// value is an integer. A store written by a candidate may hold anything, so any other shape (another text encoding,
    /// a null, a real, text, a blob, a key of the other type or not UTF-8, or a value outside 0 to
    /// <see cref="long.MaxValue"/> - 1) is reported as an <see cref="InstallerException"/> naming the store and column,
    /// never cast. An integer key is read as an integer, so its text never depends on the store's encoding.
    /// </summary>
    private static List<StateCounterFloor> Read(SqliteConnection connection, Counter counter, SqliteTransaction? transaction = null)
    {
        EnsureUtf8(connection, transaction, counter);
        if (!HasColumns(connection, transaction, counter.Table, counter.KeyColumn, counter.ValueColumn)) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Table and column names come from the fixed counter list above, never from input.
        command.CommandText = counter.FixedKey is null
            ? $"SELECT typeof({counter.KeyColumn}), {counter.KeyColumn}, CAST({counter.KeyColumn} AS BLOB), typeof({counter.ValueColumn}), {counter.ValueColumn} FROM {counter.Table};"
            : $"SELECT typeof({counter.KeyColumn}), {counter.KeyColumn}, CAST({counter.KeyColumn} AS BLOB), typeof({counter.ValueColumn}), {counter.ValueColumn} FROM {counter.Table} WHERE {counter.KeyColumn} = $key;";
#pragma warning restore CA2100
        if (counter.FixedKey is not null) command.Parameters.AddWithValue("$key", counter.FixedKey);
        using var reader = command.ExecuteReader();
        var values = new List<StateCounterFloor>();
        while (reader.Read())
        {
            values.Add(new StateCounterFloor(
                counter.Store, counter.Name,
                ReadKey(reader, 0, counter, counter.KeyColumn),
                ReadValue(reader, 3, counter, counter.ValueColumn)));
        }
        return values;
    }

    /// <summary>
    /// The highest version each automation definition has reached: its row's, or a higher one its revision line holds
    /// (a removed definition keeps only its line). With <paramref name="includeCarry"/>, each comes with the latest
    /// revision a restore needs to continue a line the snapshot lacks, checked against what the revision table stores; a
    /// read of the target only needs the versions, so a revision in another shape there never stops the restore.
    /// </summary>
    private static List<StateCounterFloor> ReadAutomationHighWater(
        SqliteConnection connection, Counter counter, bool includeCarry, SqliteTransaction? transaction = null)
    {
        EnsureUtf8(connection, transaction, counter);
        if (!HasColumns(connection, transaction, "automation_definition_revisions", "definition_id", "version", "revision_sha256", "definition_json"))
            return Read(connection, counter, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // MAX() orders text above every integer, so a version that is not an integer is reported rather than compared.
        command.CommandText = """
            WITH marks(definition_id, version) AS (
                SELECT definition_id, version FROM automation_definitions
                UNION ALL
                SELECT definition_id, version FROM automation_definition_revisions),
            highest AS (
                SELECT m.definition_id AS definition_id, MAX(m.version) AS version,
                       MAX(CASE WHEN typeof(m.version) <> 'integer' THEN typeof(m.version) END) AS malformed
                FROM marks m GROUP BY m.definition_id)
            SELECT typeof(h.definition_id), h.definition_id, CAST(h.definition_id AS BLOB),
                   COALESCE(h.malformed, 'integer'), CASE WHEN h.malformed IS NULL THEN h.version END,
                   typeof(l.revision_sha256), CAST(l.revision_sha256 AS BLOB),
                   typeof(l.definition_json), CAST(l.definition_json AS BLOB)
            FROM highest h
            LEFT JOIN automation_definition_revisions l ON l.rowid = (
                SELECT r.rowid FROM automation_definition_revisions r
                WHERE r.definition_id = h.definition_id ORDER BY r.version DESC, r.rowid DESC LIMIT 1);
            """;
        using var reader = command.ExecuteReader();
        var values = new List<StateCounterFloor>();
        while (reader.Read())
        {
            var key = ReadKey(reader, 0, counter, "definition_id");
            values.Add(new StateCounterFloor(
                counter.Store, counter.Name, key, ReadValue(reader, 3, counter, "version"),
                includeCarry ? ReadCarry(reader, 5, counter) : null));
        }
        return values;
    }

    /// <summary>
    /// SQLite casts text to a blob in the store's own encoding, so the installer reads counters only from a UTF-8
    /// store, the encoding every CameraAgent store is created in; another encoding makes the store unreadable.
    /// </summary>
    private static void EnsureUtf8(SqliteConnection connection, SqliteTransaction? transaction, Counter counter)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA encoding;";
        var encoding = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.Equals(encoding, "UTF-8", StringComparison.Ordinal))
            throw new InstallerException($"The CameraAgent store '{counter.Store}' is encoded as {encoding}, not UTF-8, so its counters cannot be read.");
    }

    /// <summary>
    /// The latest revision of a definition, or null when it has none. A revision is carried only as the revision table
    /// stores one: a 64-character hexadecimal hash and well-formed JSON text, both valid UTF-8.
    /// </summary>
    private static StateCounterCarry? ReadCarry(SqliteDataReader reader, int ordinal, Counter counter)
    {
        var shaType = reader.GetString(ordinal);
        var jsonType = reader.GetString(ordinal + 2);
        if (shaType == "null" && jsonType == "null") return null;
        if (shaType != "text" || jsonType != "text")
            throw Malformed(counter, "automation_definition_revisions", $"a latest revision stored as {shaType} and {jsonType}");
        var sha = Utf8Text(reader.GetFieldValue<byte[]>(ordinal + 1), counter, "revision_sha256");
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigit))
            throw Malformed(counter, "revision_sha256", "a revision hash that is not 64 hexadecimal characters");
        var bytes = reader.GetFieldValue<byte[]>(ordinal + 3);
        if (bytes.Length > MaximumCarryBytes)
            throw Malformed(counter, "definition_json", "a revision larger than the bound");
        var json = Utf8Text(bytes, counter, "definition_json");
        try
        {
            using (System.Text.Json.JsonDocument.Parse(bytes))
            {
            }
        }
        catch (System.Text.Json.JsonException)
        {
            throw Malformed(counter, "definition_json", "a revision that is not well-formed JSON");
        }
        return new StateCounterCarry(sha, json);
    }

    /// <summary>
    /// A key SQLite stores as the counter's key type, an integer or valid UTF-8 text; an integer key reads as its decimal
    /// text. A key of another type is a store the restore cannot match to the snapshot's rows.
    /// </summary>
    private static string ReadKey(SqliteDataReader reader, int typeOrdinal, Counter counter, string column)
    {
        var type = reader.GetString(typeOrdinal);
        if (type != (counter.IntegerKey ? "integer" : "text"))
            throw Malformed(counter, column, $"a key stored as {type}");
        if (counter.IntegerKey) return reader.GetInt64(typeOrdinal + 1).ToString(CultureInfo.InvariantCulture);
        // The store is UTF-8, so the key's blob cast is its UTF-8 bytes, checked rather than decoded with replacement.
        return Utf8Text(reader.GetFieldValue<byte[]>(typeOrdinal + 2), counter, column);
    }

    /// <summary>
    /// A counter SQLite stores as an integer from 0 to <see cref="long.MaxValue"/> - 1, so that a state token one above
    /// it is still an integer.
    /// </summary>
    private static long ReadValue(SqliteDataReader reader, int typeOrdinal, Counter counter, string column)
    {
        var type = reader.GetString(typeOrdinal);
        if (type != "integer")
            throw Malformed(counter, column, $"a value stored as {type}");
        var value = reader.GetInt64(typeOrdinal + 1);
        if (value is < 0 or long.MaxValue)
            throw Malformed(counter, column, "a value outside the range a restore can carry");
        return value;
    }

    private static string Utf8Text(byte[] bytes, Counter counter, string column)
        => System.Text.Unicode.Utf8.IsValid(bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : throw Malformed(counter, column, "text that is not valid UTF-8");

    private static InstallerException Malformed(Counter counter, string column, string found)
        => new($"The CameraAgent store '{counter.Store}' holds {found} in {column} for its {counter.Name} counter, so the counter cannot be read.");

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

    /// <summary>
    /// Binds the key with the column's own type, so an integer key matches its row. A key that is not the decimal text of
    /// an integer cannot name a row of an integer column, so it binds to nothing (null) rather than being converted, and
    /// the statement that would carry it is not run, exactly as the string comparison in <see cref="EnsureApplicable"/>
    /// found no row for it.
    /// </summary>
    private static object? KeyValue(SqliteConnection connection, SqliteTransaction? transaction, Counter counter, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", counter.Table);
        command.Parameters.AddWithValue("$column", counter.KeyColumn);
        var type = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        if (!type.Contains("INT", StringComparison.OrdinalIgnoreCase)) return key;
        return long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
               integer.ToString(CultureInfo.InvariantCulture) == key
            ? integer
            : null;
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
