using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>The states a persisted CameraAgent database can be in when a preflight reads it.</summary>
public enum JournalShape
{
    /// <summary>Stopped and checkpointed: no wal-index and no log beside either database.</summary>
    Checkpointed,

    /// <summary>The raw-ingress journal is held by a writer, so its wal-index and log are both present.</summary>
    LiveRawIngressWal,

    /// <summary>A wal-index that outlived its log, which an in-place read-only open would recreate.</summary>
    WalIndexWithoutLog,

    /// <summary>
    /// The Identity database, which uses a rollback journal rather than WAL, has an uncommitted transaction open.
    /// An in-flight preflight runs before the drain, so this is the live shape it most often meets.
    /// </summary>
    HotIdentityRollbackJournal
}

/// <summary>
/// The operator-facing <c>cameraagent preflight</c> command reading the persisted databases of an instance that may
/// still be running: whatever journal shape it meets, it reads the state through a private copy, never writes beside a
/// database, and discards a copy a concurrent commit straddled. The candidate is an image already in Docker, named by
/// <c>--image-ref</c>; a scripted daemon describes it, and the preflight never pulls, loads or starts anything.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PreflightJournalTests
{
    private const string CurrentIdentityMigration = "20260827053715_InitialIdentity";
    private const int CurrentRawIngressSchema = 13;
    private const int CurrentCatalogManifestVersion = 2;
    private static readonly string CandidateImageId = $"sha256:{new string('c', 64)}";
    private static readonly string[] InspectionOnly = ["info", "compose", "image inspect"];

    private static readonly uint RuntimeUid = NativeLinux.getuid();
    private static readonly uint RuntimeGid = NativeLinux.getgid();

    /// <summary>
    /// The read-only guarantee: a preflight of a stopped, checkpointed instance creates, modifies and deletes nothing
    /// anywhere under the product root, and asks Docker only to describe the candidate it already has.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ExecuteAsync_CheckpointedInstance_WritesNothingAndOnlyInspectsTheCandidate()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync();
        var before = Snapshot(instance.Root);
        var runner = new InspectingProcessRunner();

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(CandidateImageId), runner, CancellationToken.None);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(CandidateImageId, report.CandidateImageId);
        CollectionAssert.AreEqual(
            InspectionOnly, runner.Commands.Distinct().ToArray(), "preflight must not pull, load or start anything");
        CollectionAssert.AreEqual(before, Snapshot(instance.Root), "preflight must leave every path untouched");
        Assert.IsFalse(
            File.Exists(Path.Combine(instance.Paths.DeploymentStateRoot, "state-preflight.json")),
            "the on-demand preflight retains no report");
    }

    /// <summary>
    /// A journal carrying recovery state must not gain a file either. The wal-index legitimately changes content
    /// as readers register, so this pins the set of paths rather than their bytes; the checkpointed shape above
    /// pins the bytes.
    /// </summary>
    [TestMethod]
    [DataRow(JournalShape.LiveRawIngressWal)]
    [DataRow(JournalShape.WalIndexWithoutLog)]
    [DataRow(JournalShape.HotIdentityRollbackJournal)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ExecuteAsync_JournalCarryingRecoveryState_CreatesNoFileBesideTheDatabase(
        JournalShape shape)
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync(shape);
        var before = SnapshotPaths(instance.Root);

        var report = await CameraAgentStatePreflightManager.ExecuteAsync(
            instance.Request(CandidateImageId),
            new InspectingProcessRunner(),
            CancellationToken.None);

        // Reading the shape correctly is half the guarantee: the boundaries this report compares are the ones
        // held in those databases, so an unreadable or rolled-forward read would not produce a compatible report.
        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        CollectionAssert.AreEqual(before, SnapshotPaths(instance.Root), "preflight must create no file");
    }

    /// <summary>
    /// A database read through a private copy must be copied whole or not at all. Preflight runs before the
    /// drain, so the instance can still commit while the copy is being made, and the copy is made in two steps:
    /// the main database first, then the journal beside it. A commit landing between those two steps leaves a
    /// pair belonging to two different generations, and replaying it does not fail. It succeeds, and the report
    /// then describes durable state the instance never had. Here the commit records a migration the candidate
    /// image does not declare, so the copy that straddles it rolls the migration back and calls an incompatible
    /// lineage compatible. That is worse than refusing to read, because nothing about the answer looks wrong.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ExecuteAsync_CommitBetweenTheCopiedHalves_IsRetriedAndThenReportedRatherThanRead()
    {
        using var instance = await InstalledInstanceFixture.CreateCurrentAsync(
            JournalShape.HotIdentityRollbackJournal);
        var before = SnapshotPaths(instance.Root);
        var commits = 0;

        try
        {
            // One commit under the copy: the straddled pair is discarded, the read is taken again, and the
            // second copy reads the settled database and reports the incompatibility the first would have hidden.
            CameraAgentStatePreflight.SnapshotCopyBarrier = () =>
            {
                if (++commits == 1)
                {
                    instance.AdvanceGeneration();
                }
            };

            var report = await CameraAgentStatePreflightManager.ExecuteAsync(
                instance.Request(CandidateImageId),
                new InspectingProcessRunner(),
                CancellationToken.None);

            Assert.AreEqual(2, commits, "the straddled copy must be discarded and the read attempted again");
            Assert.IsFalse(report.Compatible, CameraAgentStatePreflight.Render(report));
            var lineage = report.Findings.Single(finding => finding.Code == "identity-migration-lineage");
            StringAssert.Contains(lineage.Observed, "99999999999999_Uncommitted", StringComparison.Ordinal);

            // An instance that never settles is reported as a database preflight could not read, in the same
            // consolidated report as every other boundary, rather than through a snapshot it cannot trust.
            commits = 0;
            CameraAgentStatePreflight.SnapshotCopyBarrier = () =>
            {
                commits++;
                instance.AdvanceGeneration();
            };

            report = await CameraAgentStatePreflightManager.ExecuteAsync(
                instance.Request(CandidateImageId),
                new InspectingProcessRunner(),
                CancellationToken.None);

            Assert.AreEqual(3, commits, "the read must be retried a bounded number of times, then given up on");
            Assert.IsFalse(report.Compatible, CameraAgentStatePreflight.Render(report));
            var unreadable = report.Findings.Single(finding => finding.Code == "identity-lineage-unreadable");
            Assert.IsTrue(unreadable.Blocking);
            StringAssert.Contains(
                unreadable.Observed, "kept changing while preflight was copying it", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentStatePreflight.SnapshotCopyBarrier = static () => { };
        }

        CollectionAssert.AreEqual(before, SnapshotPaths(instance.Root), "preflight must create no file");
    }

    private static string[] SnapshotPaths(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Every path beneath a root with its mode and, for a file, a checksum of its content. Length alone would
    /// miss a same-length rewrite — a rewritten SQLite page, for instance — so the content itself is hashed.
    /// </summary>
    private static string[] Snapshot(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                if (File.Exists(path))
                {
                    return $"{relative}:{File.GetUnixFileMode(path)}:" +
                        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                }
                // A dangling link resolves to no mode at all, so it is recorded by name.
                return Directory.Exists(path) ? $"{relative}/:{File.GetUnixFileMode(path)}" : $"{relative}?";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// A Docker daemon that describes itself and the candidate image, with the boundaries the fixture's persisted state
    /// satisfies, and fails anything else a read-only preflight must never run.
    /// </summary>
    private sealed class InspectingProcessRunner : IProcessRunner
    {
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
            {
                return Success("unix:///var/run/docker.sock");
            }
            var effective = arguments.Count >= 2 && arguments[0] == "--host" ? arguments.Skip(2).ToArray() : arguments;
            var architecture = ReleaseAcquirer.HostImageArchitecture();
            switch (effective)
            {
                case ["info", ..]:
                    Commands.Add("info");
                    return Success(
                        $"{{\"OSType\":\"linux\",\"Architecture\":\"{architecture}\",\"ID\":\"daemon\",\"Name\":\"host\",\"ServerVersion\":\"29.7.2\"," +
                        "\"MemoryLimit\":true,\"CpuCfsQuota\":true,\"PidsLimit\":true,\"NCPU\":64}");
                case ["compose", "version", ..]:
                    Commands.Add("compose");
                    return Success("2.40.0");
                case ["image", "inspect", var reference] when reference == CandidateImageId:
                    Commands.Add("image inspect");
                    var labels = new Dictionary<string, string>(ReleaseFixture.ContractLabels)
                    {
                        ["org.opencontainers.image.revision"] = new string('a', 40)
                    };
                    return Success(JsonSerializer.Serialize(new[]
                    {
                        new { Id = CandidateImageId, Architecture = architecture, Os = "linux", RepoDigests = Array.Empty<string>(), Config = new { Labels = labels } }
                    }));
                default:
                    throw new InvalidOperationException(
                        $"A read-only preflight must not run: {fileName} {string.Join(' ', arguments)}");
            }
        }

        private static Task<ProcessResult> Success(string output) => Task.FromResult(new ProcessResult(0, output, string.Empty));
    }

    /// <summary>An installed instance whose persisted state matches the current durable boundaries.</summary>
    private sealed class InstalledInstanceFixture : IDisposable
    {
        private SqliteConnection? writer;
        private SqliteTransaction? pending;
        private string pendingMigration = "99999999999999_Uncommitted";
        private int generation;

        private InstalledInstanceFixture(string root, Guid instanceId, InstallationPaths paths)
        {
            Root = root;
            InstanceId = instanceId;
            Paths = paths;
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public InstallationPaths Paths { get; }

        public CameraAgentStatePreflightRequest Request(string imageReference)
            => new(InstanceId, Root, imageReference, Json: false)
            {
                AllowTestProductRoot = true
            };

        public static async Task<InstalledInstanceFixture> CreateCurrentAsync(
            JournalShape shape = JournalShape.Checkpointed,
            string? daemonArchitecture = null,
            ContractReplayProfile replayProfile = ContractReplayProfile.InProcess)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-preflight-journal-{Guid.NewGuid():N}");
            var instanceId = Guid.NewGuid();
            var paths = InstallationPaths.Create(root, instanceId, HygV42.CatalogId);
            foreach (var directory in new[]
            {
                paths.ProductRoot, Path.Combine(paths.ProductRoot, "cameraagents"), paths.InstanceRoot,
                paths.ConfigRoot, paths.StateRoot, paths.DeploymentStateRoot, paths.OperationsRoot
            })
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
            var fixture = new InstalledInstanceFixture(root, instanceId, paths);
            fixture.WriteCatalogManifest();
            foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
            {
                SafeFileSystem.CreateRuntimeDirectory(directory, RuntimeUid, RuntimeGid);
            }
            fixture.WriteIdentityDatabase();
            fixture.WriteRawIngressDatabase();
            fixture.ShapeJournals(shape);
            await fixture.WriteInstanceManifestAsync(daemonArchitecture, replayProfile).ConfigureAwait(false);
            return fixture;
        }

        /// <summary>
        /// Puts the persisted databases into one of the shapes a preflight can meet. Which one it is decides how
        /// each database may be read without writing beside it, so each is reproduced exactly rather than
        /// approximated, and the reproduction is asserted.
        /// </summary>
        private void ShapeJournals(JournalShape shape)
        {
            var database = CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot);
            var identity = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            if (shape == JournalShape.LiveRawIngressWal)
            {
                // A running instance holds the journal open, so the wal-index and its log are both present.
                writer = new SqliteConnection($"Data Source={database}");
                writer.Open();
                using var command = writer.CreateCommand();
                command.CommandText = "INSERT INTO raw_capture (id) VALUES (1);";
                command.ExecuteNonQuery();
                AssertJournalFiles(database, "-wal", "-shm");
                return;
            }

            var walIndex = File.Exists(database + "-shm") ? File.ReadAllBytes(database + "-shm") : null;
            SqliteConnection.ClearAllPools();
            if (shape == JournalShape.WalIndexWithoutLog)
            {
                // A wal-index outliving its log: an in-place read-only open would recreate the log here.
                Assert.IsNotNull(walIndex, "the fixture must harvest a live wal-index, not fabricate one");
                File.WriteAllBytes(database + "-shm", walIndex);
                if (File.Exists(database + "-wal")) File.Delete(database + "-wal");
                AssertJournalFiles(database, "-shm");
                return;
            }
            if (shape == JournalShape.HotIdentityRollbackJournal)
            {
                // The Identity database is created through plain UseSqlite, so an open transaction leaves a
                // rollback journal and no wal-index. Only a private copy can be rolled back to read it.
                writer = new SqliteConnection($"Data Source={identity}");
                writer.Open();
                pending = writer.BeginTransaction();
                using var command = writer.CreateCommand();
                command.Transaction = pending;
                command.CommandText =
                    "INSERT INTO __EFMigrationsHistory VALUES ($migration, '10.0.0');";
                command.Parameters.AddWithValue("$migration", pendingMigration);
                command.ExecuteNonQuery();
                AssertJournalFiles(identity, "-journal");
                AssertJournalFiles(database);
                return;
            }
            // A stopped, checkpointed instance: the shape an operator preflights before an upgrade.
            AssertJournalFiles(database);
            AssertJournalFiles(identity);
        }

        private static void AssertJournalFiles(string database, params string[] expected)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                Assert.AreEqual(
                    expected.Contains(suffix, StringComparer.Ordinal),
                    File.Exists(database + suffix),
                    $"the fixture did not reproduce the requested journal shape at {suffix}");
            }
        }

        /// <summary>
        /// Commits the pending migration row and immediately opens another transaction holding a different one,
        /// so the database advances a generation and keeps the shape it was in: the same rollback journal still
        /// sits beside the same database, and every file a copy expects is still there. That is the quiet half
        /// of the copy race. A writer that merely committed and stopped would take the journal away and raise
        /// the loud half, which the retry already handles.
        ///
        /// The committed row matters. It leaves the durable lineage carrying a migration the candidate image
        /// does not declare, which is a blocking incompatibility. A copy made across this commit takes the
        /// database from before it and the journal from after, rolls the row back, and reports the lineage as
        /// compatible: the wrong answer, in the direction that lets an incompatible upgrade proceed.
        /// </summary>
        public void AdvanceGeneration()
        {
            pending!.Commit();
            pending.Dispose();
            pendingMigration = $"99999999999999_Uncommitted{++generation}";
            pending = writer!.BeginTransaction();
            using var command = writer.CreateCommand();
            command.Transaction = pending;
            command.CommandText =
                "INSERT INTO __EFMigrationsHistory VALUES ($migration, '10.0.0');";
            command.Parameters.AddWithValue("$migration", pendingMigration);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            pending?.Dispose();
            writer?.Dispose();
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(Root))
            {
                return;
            }
            SafeFileSystem.MakeTreeOwnerWritable(Root);
            Directory.Delete(Root, recursive: true);
        }

        private void WriteCatalogManifest()
        {
            var versionRoot = Path.Combine(Paths.CatalogRoot, "versions", HygV42.PackageVersion);
            SafeFileSystem.CreateOwnerDirectory(versionRoot);
            File.WriteAllText(
                Path.Combine(versionRoot, "manifest.json"),
                "{\"manifestVersion\":" +
                CurrentCatalogManifestVersion.ToString(CultureInfo.InvariantCulture) +
                ",\"catalog\":{\"id\":\"" + HygV42.CatalogId + "\"}}");
            Directory.CreateSymbolicLink(
                Path.Combine(Paths.CatalogRoot, "current"), $"versions/{HygV42.PackageVersion}");
        }

        private void WriteIdentityDatabase()
        {
            var path = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The fixture composes a fixed lineage row for a private disposable database.
            command.CommandText =
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
                $"INSERT INTO __EFMigrationsHistory VALUES ('{CurrentIdentityMigration}', '10.0.0');";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        private void WriteRawIngressDatabase()
        {
            var path = CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot);
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // PRAGMA user_version cannot be parameterized; the value is a test constant.
            // The runtime journal is WAL at rest, and a read-only SQLite handle creates the wal-index unless the
            // reader opts out, so the fixture must carry the production journal mode for the read-only guarantee
            // below to mean anything.
            command.CommandText =
                "PRAGMA journal_mode = WAL;" +
                "CREATE TABLE raw_capture (id INTEGER PRIMARY KEY);" +
                $"PRAGMA user_version = {CurrentRawIngressSchema.ToString(CultureInfo.InvariantCulture)};";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        private Task WriteInstanceManifestAsync(
            string? daemonArchitecture = null,
            ContractReplayProfile replayProfile = ContractReplayProfile.InProcess)
        {
            var applicationIdentity = Guid.NewGuid();
            var catalog = new CatalogInstallationIdentity(
                HygV42.CatalogId, HygV42.PackageVersion, "2", "3",
                HygV42.DatabaseSha256, HygV42.DatabaseLength, HygV42.RowCount,
                Paths.CatalogRoot, new string('a', 64), "local-offline");
            // The installed image's architecture agrees with the recorded daemon's, as a real installation guarantees.
            var architecture = daemonArchitecture ?? ReleaseAcquirer.HostImageArchitecture();
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('9', 64)}", architecture, null,
                UpgradeCompatibility: CameraAgentStateContract.LegacyUnbounded, SourceRevision: new string('8', 40),
                Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: "hyg-v42-production-p3-s2");
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent, InstanceId,
                "Preflight Camera", applicationIdentity, $"installer-{InstanceId:D}", 1, new string('d', 64),
                "owner@example.test", 0, 0, 0, "UTC", Guid.NewGuid(), RuntimeUid, RuntimeGid, Paths.ProductRoot,
                Paths.ConfigRoot, Paths.StateRoot, ComposeDeployment.TemplateVersion, new string('e', 64),
                new string('f', 64), new string('1', 64), "default", "1", "1", "active", new string('2', 64),
                new string('3', 64), catalog, image, null,
                new DockerDaemonIdentity("daemon", "host", architecture, "29.7.2"),
                CameraAgentStateContract.LegacyUnbounded, DateTimeOffset.UtcNow,
                LifecycleCondition: InstanceLifecycleCondition.Installed,
                ReplayProfile: replayProfile);
            return SafeFileSystem.WriteJsonAtomicAsync(
                Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        }
    }
}
