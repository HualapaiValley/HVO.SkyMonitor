using System.Globalization;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 1 of #1314 (PR #1340): restoring a database snapshot keeps every counter that hands out
/// identities (R1); <c>--resume</c> keeps capture paused through its retry (R4); a snapshot reference is published
/// only after every directory above it is durable (R5); an interrupted operation recovers onto its snapshot even when
/// the candidate left the live stores unreadable (R6); and an outbox in any configured storage root is snapshotted (R8).
/// The capture boundary here is read from the real stores, as the CameraAgent lifecycle state endpoint computes it.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string AgentId = "agent-1";
    private static readonly string[] ComposeFileNames = ["compose.yml", "instance.env"];
    private static readonly string[] PausedThroughTheRetry = ["original-up:Paused:resumes=0", "candidate-up:Paused:resumes=0"];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_IdleCaptureWithAdvancingFleetReports_KeepsTheFleetSequenceAndPassesContinuity()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);
        Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        // Capture stays idle under the upgraded release while fleet reports keep leaving the instance.
        AdvanceFleet(fixture.Paths, toNextSequence: 60);
        Assert.AreEqual(59, lifecycle.ReadBoundary().CaptureSequence);

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        // The restored fleet store would number its next report 10 again; it continues from 60.
        Assert.AreEqual(60, ReadCounter(fixture.Paths, "raw/.fleet/fleet-status.db", "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
        Assert.IsGreaterThanOrEqualTo(59, lifecycle.ReadBoundary().CaptureSequence);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_CaptureSequencesReservedWithoutFiles_AreNeverHandedOutAgain()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 40, fleetNextSequence: 1);
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);
        Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        // The upgraded release reserves seven more capture identities whose captures never produced files.
        ExecuteSql(RawIngressPath(fixture.Paths), $"UPDATE raw_capture_sequences SET last_sequence = 47 WHERE agent_id = '{AgentId}';");
        var versionBefore = lifecycle.ReadBoundary().CaptureVersion;

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(47, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", $"SELECT last_sequence FROM raw_capture_sequences WHERE agent_id = '{AgentId}';"));
        // The capture-control version a client holds is not handed out again either.
        Assert.IsGreaterThan(versionBefore, lifecycle.ReadBoundary().CaptureVersion);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        CollectionAssert.Contains(journal.StateCounterFloors!.ToArray(), new StateCounterFloor("raw/journal/raw-ingress.db", "capture-sequence", AgentId, 47));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateThatReportedWhilePaused_KeepsItsFleetSequenceAfterTheRestore()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 12, fleetNextSequence: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) AdvanceFleet(fixture.Paths, toNextSequence: 30);
        };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(30, ReadCounter(fixture.Paths, "raw/.fleet/fleet-status.db", "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ResumeKeepsCapturePausedFromTheRecoveryToTheRetry()
    {
        // A power loss after the candidate started. --resume restores the snapshot and restarts the original, then
        // retries. Between the two, capture admission must stay paused, so no retention or cleanup can run.
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 3, fleetNextSequence: 1);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:CandidateVerified"));
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);
        var observed = new List<string>();
        fixture.Runner.OnComposeUp = image => observed.Add(
            $"{(image == CandidateImageId ? "candidate" : "original")}-up:{lifecycle.ReadBoundary().CaptureState}:resumes={lifecycle.ResumeCount}");

        Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null)).Outcome);

        // The original came back paused, capture was never resumed before the candidate's retry started, and the only
        // resume is the retry's final one after its commit.
        CollectionAssert.AreEqual(PausedThroughTheRetry, observed.ToArray());
        Assert.AreEqual(1, lifecycle.ResumeCount);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_PublishesRecoveryRecordsAndTheSnapshotOnlyAfterEveryDirectoryToTheProductRootIsSynced()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var flushed = new List<string>();
        var atPrepared = new List<string>();
        var atSnapshot = new List<string>();
        LifecycleOperationState? snapshotRecord = null;
        NativeLinux.DirectoryFlushed = path => flushed.Add(Path.TrimEndingDirectorySeparator(path));
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            // The journal write itself flushes the operations directory last; what counts is what preceded it.
            var before = flushed.Take(flushed.Count - 1).ToArray();
            if (state is { Phase: LifecycleOperationPhase.Prepared, MutationStarted: true } && atPrepared.Count == 0) atPrepared.AddRange(before);
            if (state.Phase == LifecycleOperationPhase.StateSnapshotRecorded)
            {
                atSnapshot.AddRange(before);
                snapshotRecord = state;
            }
            flushed.Clear();
        };
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        }
        finally
        {
            NativeLinux.DirectoryFlushed = static _ => { };
            CameraAgentLifecycleManager.JournalRecorded = static _ => { };
        }
        Assert.IsNotNull(snapshotRecord);
        var operationRoot = Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", snapshotRecord.OperationId.ToString("D"));
        var snapshotRoot = CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, snapshotRecord.OperationId, snapshotRecord.StateSnapshot!.SnapshotId);
        var chainToProductRoot = Ancestors(operationRoot, fixture.Paths.ProductRoot);
        CollectionAssert.IsSubsetOf(chainToProductRoot, atPrepared.ToArray(),
            $"Synced before the recovery records were published: {string.Join(", ", atPrepared)}");
        CollectionAssert.IsSubsetOf(Ancestors(snapshotRoot, fixture.Paths.ProductRoot), atSnapshot.ToArray(),
            $"Synced before the snapshot reference was published: {string.Join(", ", atSnapshot)}");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedUpgradeWithAnUnreadableCandidateJournal_RecoversOntoItsSnapshot(bool restoreOnly)
    {
        // The installed image and the candidate both run canonical raw-ingress schema 15. The candidate leaves a
        // journal the canonical-schema check rejects, and the installer loses power before its own recovery. Both
        // recovery commands must authenticate the recorded snapshot and recover onto it rather than refuse.
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, recoveryState: true,
            installedImage: static image => CurrentStateContract(image) with { RawIngressSchema = "15" });
        fixture.Runner.CandidateRawIngressSchema = "15";
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        WriteCanonicalV15RawIngressJournal(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths), "INSERT INTO capture_control_state(state_key, state, version, updated_unix_ms) VALUES(1, 'running', 3, 0);");
        WriteIdentityLineage(fixture.Paths);
        // An installed instance's Compose files are owner-only, as the installer writes them; restore-only checks that.
        foreach (var file in ComposeFileNames.Select(name => Path.Combine(fixture.Paths.ConfigRoot, "compose", name)))
            SafeFileSystem.WriteTextAtomic(file, await File.ReadAllTextAsync(file));
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) ExecuteSql(RawIngressPath(fixture.Paths), "CREATE TABLE IF NOT EXISTS candidate_drift(value INTEGER);");
        };
        var request = UpgradeRequest(fixture, assertMigration: false);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        var crash = images.First(static image => image.Step == "journal:CandidateVerified");
        RestoreCrashImage(fixture, crash);
        Assert.IsTrue(TableExists(RawIngressPath(fixture.Paths), "candidate_drift"));
        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            restoreOnly
                ? request with { Resume = true, RestoreOnly = true, RecoveryOperationId = interrupted.OperationId }
                : request with { Resume = true },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        if (restoreOnly)
        {
            Assert.AreEqual("restored-previous-healthy-admission-resumed", result.Outcome);
            Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
            Assert.IsFalse(TableExists(RawIngressPath(fixture.Paths), "candidate_drift"));
            var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
            Assert.AreEqual(LifecycleOperationPhase.Restored, settled.Phase);
            Assert.IsNotNull(settled.StateSnapshotRestoredUtc);
        }
        else
        {
            Assert.AreEqual("completed", result.Outcome);
            Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidate_RestoresTheOutboxOfEveryConfiguredStorageRoot()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        // The active capture profile stores products under a root the generated configuration does not name.
        var configured = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, "archive/products/outbox/artifact-outbox.db");
        ExecuteSql(configured, "CREATE TABLE outbox(id INTEGER PRIMARY KEY, artifact TEXT NOT NULL); INSERT INTO outbox(artifact) VALUES('before');");
        ExecuteSql(RawIngressPath(fixture.Paths),
            "CREATE TABLE capture_schedule_revisions(revision_id TEXT PRIMARY KEY, revision_number INTEGER NOT NULL UNIQUE, profile_json BLOB NOT NULL);" +
            "INSERT INTO capture_schedule_revisions VALUES('r1', 1, CAST('{\"processingSteps\":[{\"type\":\"FileStorage\",\"options\":{\"storageRoot\":\"/app/data/archive/products\"}}]}' AS BLOB));");
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) ExecuteSql(configured, "ALTER TABLE outbox ADD COLUMN migrated INTEGER; INSERT INTO outbox(artifact) VALUES('candidate');");
        };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        Assert.IsFalse(ColumnExists(configured, "outbox", "migrated"), "The outbox under the configured storage root was left migrated.");
        Assert.AreEqual(1, ReadCounter(fixture.Paths, "archive/products/outbox/artifact-outbox.db", "SELECT COUNT(*) FROM outbox;"));
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var manifest = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, journal.StateSnapshot!, CancellationToken.None);
        CollectionAssert.Contains(manifest.Databases.Select(static database => database.RelativePath).ToArray(),
            "archive/products/outbox/artifact-outbox.db");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Snapshot_RefusesAStorageRootThatIsNotAnAbsoluteNormalizedPathBeforeAnythingChanges()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths),
            "CREATE TABLE capture_schedule_revisions(revision_id TEXT PRIMARY KEY, revision_number INTEGER NOT NULL UNIQUE, profile_json BLOB NOT NULL);" +
            "INSERT INTO capture_schedule_revisions VALUES('r1', 1, CAST('{\"processingSteps\":[{\"options\":{\"storageRoot\":\"/app/data/archive/../raw\"}}]}' AS BLOB));");
        var lifecycle = new FakeLifecycleClient();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "not an absolute, normalized path", StringComparison.Ordinal);
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    /// <summary>The directory and each of its ancestors up to and including <paramref name="stopAt"/>.</summary>
    private static string[] Ancestors(string directory, string stopAt)
    {
        var chain = new List<string>();
        for (var current = Path.TrimEndingDirectorySeparator(directory); ; current = Path.GetDirectoryName(current)!)
        {
            chain.Add(current);
            if (current == Path.TrimEndingDirectorySeparator(stopAt)) return [.. chain];
        }
    }

    /// <summary>The counters the real stores carry: per-agent capture sequences, the capture-control version, and fleet reports.</summary>
    private static void SeedCounters(InstallationPaths paths, long captureSequence, long fleetNextSequence)
    {
        ExecuteSql(RawIngressPath(paths),
            "CREATE TABLE raw_capture_sequences(agent_id TEXT PRIMARY KEY, last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)) STRICT;" +
            $"INSERT INTO raw_capture_sequences VALUES('{AgentId}', {captureSequence});" +
            "CREATE TABLE capture_control_state(state_key INTEGER PRIMARY KEY CHECK (state_key = 1)," +
            " state TEXT NOT NULL CHECK (state IN ('running', 'pause_requested', 'paused')), version INTEGER NOT NULL CHECK (version >= 0)," +
            " updated_unix_ms INTEGER NOT NULL) STRICT;" +
            "INSERT INTO capture_control_state VALUES(1, 'running', 3, 0);");
        ExecuteSql(CameraAgentStateSnapshot.StorePath(paths.StateRoot, "raw/.fleet/fleet-status.db"),
            "CREATE TABLE fleet_status_metadata(metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1)," +
            " schema_version INTEGER NOT NULL, agent_instance_id TEXT NULL, next_sequence INTEGER NOT NULL CHECK(next_sequence > 0)," +
            " overflow_count INTEGER NOT NULL DEFAULT 0) STRICT;" +
            $"INSERT INTO fleet_status_metadata VALUES(1, 1, NULL, {fleetNextSequence}, 0);" +
            "CREATE TABLE fleet_status_records(record_id INTEGER PRIMARY KEY AUTOINCREMENT, sequence INTEGER NOT NULL UNIQUE CHECK(sequence > 0)) STRICT;" +
            string.Concat(Enumerable.Range(1, (int)fleetNextSequence - 1).Select(static sequence => $"INSERT INTO fleet_status_records(sequence) VALUES({sequence});")));
    }

    private static void AdvanceFleet(InstallationPaths paths, long toNextSequence)
    {
        var path = CameraAgentStateSnapshot.StorePath(paths.StateRoot, "raw/.fleet/fleet-status.db");
        var next = ReadCounter(paths, "raw/.fleet/fleet-status.db", "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;");
        ExecuteSql(path, string.Concat(Enumerable.Range((int)next, (int)(toNextSequence - next))
                .Select(static sequence => $"INSERT INTO fleet_status_records(sequence) VALUES({sequence});")) +
            $"UPDATE fleet_status_metadata SET next_sequence = {toNextSequence} WHERE metadata_key = 1;");
    }

    private static long ReadCounter(InstallationPaths paths, string store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={CameraAgentStateSnapshot.StorePath(paths.StateRoot, store)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
        command.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ExecuteSql(string database, string sql) => Execute(database, sql);

    private static bool TableExists(string database, string table)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool ColumnExists(string database, string table, string column)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>A canonical raw-ingress journal at schema 15: the frozen v14 definitions with the v15 reader version.</summary>
    private static void WriteCanonicalV15RawIngressJournal(InstallationPaths paths)
    {
        var path = RawIngressPath(paths);
        foreach (var file in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => path + suffix).Prepend(path))
        {
            if (File.Exists(file)) File.Delete(file);
        }
        using var stream = typeof(LifecycleContractTests).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Deployment.Cli.Tests.FrozenRawIngressV14.sql")!;
        using var reader = new StreamReader(stream);
        Execute(path, reader.ReadToEnd() + "PRAGMA user_version = 15;");
        Assert.AreEqual(15L, CameraAgentStatePreflight.ReadRawIngressVersion(path));
    }

    private static void WriteIdentityLineage(InstallationPaths paths)
    {
        var path = CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot);
        if (File.Exists(path)) File.Delete(path);
        Execute(path,
            "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
            "INSERT INTO __EFMigrationsHistory VALUES('20260827053715_InitialIdentity', '10.0.0');");
    }

    /// <summary>
    /// The CameraAgent lifecycle endpoints over the fixture's real stores: the boundary is computed as the state endpoint
    /// computes it, from the per-agent capture sequences and the fleet report sequence, and pause and resume move the
    /// durable capture-control state.
    /// </summary>
    private sealed class StoreBackedLifecycleClient(InstallationPaths paths) : ICameraAgentLifecycleClient
    {
        public int ResumeCount { get; private set; }

        public LifecycleContinuity ReadBoundary()
        {
            var raw = RawIngressPath(paths);
            long version = 0;
            var state = "running";
            long sequence = 0;
            using (var connection = new SqliteConnection($"Data Source={raw};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using var control = connection.CreateCommand();
                control.CommandText = "SELECT state, version FROM capture_control_state WHERE state_key = 1;";
                using (var reader = control.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        state = reader.GetString(0);
                        version = reader.GetInt64(1);
                    }
                }
                using var captures = connection.CreateCommand();
                captures.CommandText = "SELECT COALESCE(MAX(last_sequence), 0) FROM raw_capture_sequences;";
                sequence = Convert.ToInt64(captures.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var fleet = CameraAgentStateSnapshot.StorePath(paths.StateRoot, "raw/.fleet/fleet-status.db");
            if (File.Exists(fleet))
            {
                using var connection = new SqliteConnection($"Data Source={fleet};Mode=ReadOnly;Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT next_sequence - 1, (SELECT COALESCE(MAX(sequence), 0) FROM fleet_status_records) FROM fleet_status_metadata WHERE metadata_key = 1;";
                using var reader = command.ExecuteReader();
                if (reader.Read()) sequence = Math.Max(sequence, Math.Max(reader.GetInt64(0), reader.GetInt64(1)));
            }
            return new LifecycleContinuity(state == "running" ? "Running" : "Paused", version, sequence,
                0, 0, 0, 0, 0, 0, 0, 0, ReplayCatalogIndependent: true);
        }

        private void Move(string state)
            => Execute(RawIngressPath(paths),
                $"UPDATE capture_control_state SET state = '{state}', version = version + 1 WHERE state_key = 1 AND state <> '{state}';");

        public Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            Move("paused");
            return Task.FromResult(ReadBoundary());
        }

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(ReadBoundary());

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
        {
            ResumeCount++;
            Move("running");
            return Task.CompletedTask;
        }

        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(ReadBoundary());

        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(
            Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken)
        {
            Assert.AreEqual(expectedVersion, ReadBoundary().CaptureVersion);
            ResumeCount++;
            Move("running");
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new LifecycleResumeReceipt("Running", expectedVersion + 1, true, false, now, now));
        }
    }
}
