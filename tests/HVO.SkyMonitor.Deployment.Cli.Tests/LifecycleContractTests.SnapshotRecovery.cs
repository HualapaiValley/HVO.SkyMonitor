using System.Globalization;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 1 of #1314 (PR #1340): restoring a database snapshot keeps every counter that hands out
/// identities (R1); <c>--resume</c> keeps capture paused through its retry (R4); a snapshot reference is published
/// only after every directory above it is durable (R5); an interrupted operation recovers onto its snapshot even when
/// the candidate left the live stores unreadable (R6).
/// The capture boundary here is read from the real stores, as the CameraAgent lifecycle state endpoint computes it.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string EnvironmentStore = "raw/.environment/environmental-observations.db";
    private const string EnvironmentalSequence = "SELECT seq FROM sqlite_sequence WHERE name = 'environmental_observation_journal';";
    private const string EnvironmentalFixtureSchema = "CREATE TABLE environmental_observation_journal(record_id INTEGER PRIMARY KEY AUTOINCREMENT);";
    private const string AgentId = "agent-1";
    private static readonly string[] ComposeFileNames = ["compose.yml", "instance.env"];
    private static readonly string[] PausedThroughTheRetry = ["original-up:Paused:resumes=0", "candidate-up:Paused:resumes=0"];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PendingUpgradeRecovery_IdleCaptureWithNewObservations_KeepsTheJournalSequence()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, environmentRecordId: 10);
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);
        await RecoverFromCandidateWriteAsync(fixture, () => AdvanceEnvironmentalJournal(fixture.Paths, recordId: 60), lifecycle: lifecycle);
        // The environmental journal continues at 60 even while capture remains idle.
        Assert.AreEqual(60, ReadCounter(fixture.Paths, EnvironmentStore, EnvironmentalSequence));
        Assert.AreEqual(5L, lifecycle.ReadBoundary().CaptureSequence);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PendingUpgradeRecovery_CaptureSequencesReservedWithoutFiles_AreNeverHandedOutAgain()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 40, environmentRecordId: 1);
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);
        var versionBefore = lifecycle.ReadBoundary().CaptureVersion;
        await RecoverFromCandidateWriteAsync(fixture, () => ExecuteSql(RawIngressPath(fixture.Paths),
            $"UPDATE raw_capture_sequences SET last_sequence = 47 WHERE agent_id = '{AgentId}';"), lifecycle: lifecycle);

        Assert.AreEqual(47, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", $"SELECT last_sequence FROM raw_capture_sequences WHERE agent_id = '{AgentId}';"));
        // The capture-control version a client holds is not handed out again either.
        Assert.IsGreaterThan(versionBefore, lifecycle.ReadBoundary().CaptureVersion);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        CollectionAssert.Contains(journal.StateCounterFloors!.ToArray(), new StateCounterFloor("raw/journal/raw-ingress.db", "capture-sequence", AgentId, 47));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateThatRecordedObservationsWhilePaused_KeepsTheirSequenceAfterTheRestore()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 12, environmentRecordId: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) AdvanceEnvironmentalJournal(fixture.Paths, recordId: 30);
        };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(30, ReadCounter(fixture.Paths, EnvironmentStore, EnvironmentalSequence));
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
        SeedCounters(fixture.Paths, captureSequence: 3, environmentRecordId: 1);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture), fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
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
            UpgradeRequest(fixture) with { Resume = true }, fixture.Runner, _ => lifecycle,
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
                UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
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
    public async Task InterruptedUpgradeWithAnUnreadableCandidateJournal_RecoversOntoItsSnapshot()
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
        var request = UpgradeRequest(fixture);
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
        var lifecycle = new StoreBackedLifecycleClient(fixture.Paths);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            request with { Resume = true },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_CheckpointUsesTheFixedInventoryEvenWhenAProfileNamesARetiredStorageRoot()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        const string unrelatedStore = "archive/products/unmanaged.db";
        var unrelated = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, unrelatedStore);
        ExecuteSql(unrelated, "CREATE TABLE example(value TEXT NOT NULL); INSERT INTO example VALUES('preserved');");
        ExecuteSql(RawIngressPath(fixture.Paths),
            """
            CREATE TABLE capture_schedule_revisions (revision_id TEXT PRIMARY KEY, profile_json BLOB NOT NULL);
            INSERT INTO capture_schedule_revisions VALUES ('r1', CAST('{"storageRoot":"/app/data/archive/../raw"}' AS BLOB));
            """);

        await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var manifest = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, journal.StateSnapshot!, CancellationToken.None);
        CollectionAssert.DoesNotContain(manifest.Databases.Select(static database => database.RelativePath).ToArray(), unrelatedStore);
        Assert.AreEqual(1, ReadCounter(fixture.Paths, unrelatedStore, "SELECT COUNT(*) FROM example WHERE value = 'preserved';"));
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

    /// <summary>The counters the real stores carry: per-agent capture sequences, the capture-control version, and environmental observations.</summary>
    private static void SeedCounters(InstallationPaths paths, long captureSequence, long environmentRecordId)
    {
        ExecuteSql(RawIngressPath(paths),
            "CREATE TABLE raw_capture_sequences(agent_id TEXT PRIMARY KEY, last_sequence INTEGER NOT NULL CHECK (last_sequence >= 0)) STRICT;" +
            $"INSERT INTO raw_capture_sequences VALUES('{AgentId}', {captureSequence});" +
            "CREATE TABLE capture_control_state(state_key INTEGER PRIMARY KEY CHECK (state_key = 1)," +
            " state TEXT NOT NULL CHECK (state IN ('running', 'pause_requested', 'paused')), version INTEGER NOT NULL CHECK (version >= 0)," +
            " updated_unix_ms INTEGER NOT NULL) STRICT;" +
            "INSERT INTO capture_control_state VALUES(1, 'running', 3, 0);");
        ExecuteSql(CameraAgentStateSnapshot.StorePath(paths.StateRoot, EnvironmentStore),
            EnvironmentalFixtureSchema +
            FormattableString.Invariant($"INSERT INTO environmental_observation_journal VALUES({environmentRecordId});"));
    }

    private static void AdvanceEnvironmentalJournal(InstallationPaths paths, long recordId)
        => ExecuteSql(CameraAgentStateSnapshot.StorePath(paths.StateRoot, EnvironmentStore),
            FormattableString.Invariant($"INSERT INTO environmental_observation_journal VALUES({recordId});"));

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
            "INSERT INTO __EFMigrationsHistory VALUES('20261128144428_InitialIdentity', '10.0.0');");
    }

    /// <summary>
    /// The CameraAgent lifecycle endpoints over the fixture's real stores: the boundary is computed as the state endpoint
    /// computes it, from the per-agent capture sequences, and pause and resume move the
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
            return new LifecycleContinuity(state == "running" ? "Running" : "Paused", version, sequence,
                0, 0, 0, 0, 0, 0, ReplayCatalogIndependent: true);
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
