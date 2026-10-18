using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Manual volume measurement for #1314: an upgrade and a rollback, each with an unchanged state contract and with a
/// declared migration, against a realistic retained volume of many files across raw, archive and derived trees. Run
/// it inside a 1 CPU / 1 GiB limit, for example
/// <c>systemd-run --user --scope -p CPUQuota=100% -p MemoryMax=1G -p MemorySwapMax=0 -- dotnet test ...</c>, which
/// also bounds the <c>tar</c> and <c>gzip</c> children an earlier installer ran.
/// </summary>
/// <remarks>
/// <para>
/// <c>HVO_UPGRADE_VOLUME_SOURCE</c> names a directory holding the volume as <c>raw/</c> and <c>archive/</c> trees.
/// When it is absent the test generates it first, outside the timings: <c>HVO_UPGRADE_VOLUME_GIB</c> GiB (default 2)
/// of captures of sixteen files each, from 64 KiB to 1 MiB, of mostly dark data with one random byte in twelve. gzip
/// compresses it about 6.5 times at about 35 MB/s on one core of home-dev-01, close to the 6.6 times and 30 MiB/s that
/// #1044 measured on real captures. <c>HVO_UPGRADE_VOLUME_PREPARE_ONLY=1</c> stops after generating. The volume is copied
/// into the instance before the timings start; a hard link would not do, because the earlier backup path refuses a
/// linked file.
/// </para>
/// <para>
/// The report, written to <c>HVO_UPGRADE_VOLUME_REPORT</c> and to standard output, gives each operation's wall time,
/// outcome, and the process and cgroup memory peaks. The test uses only lifecycle APIs that predate #1314, so the
/// same file measures the earlier backup path for comparison.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class UpgradeVolumeTests
{
    private const string CandidateReference = "ghcr.io/example/cameraagent@sha256:4444444444444444444444444444444444444444444444444444444444444444";
    private const string CandidateImageId = "sha256:5555555555555555555555555555555555555555555555555555555555555555";
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAndRollback_OnARealisticRetainedVolume_ReportTheirTimings()
    {
        var source = Environment.GetEnvironmentVariable("HVO_UPGRADE_VOLUME_SOURCE");
        if (string.IsNullOrEmpty(source))
        {
            Assert.Inconclusive("Set HVO_UPGRADE_VOLUME_SOURCE to run the upgrade volume measurement.");
            return;
        }
        var gib = double.Parse(Environment.GetEnvironmentVariable("HVO_UPGRADE_VOLUME_GIB") ?? "2", CultureInfo.InvariantCulture);
        if (!Directory.Exists(source)) GenerateVolume(source, gib);
        if (Environment.GetEnvironmentVariable("HVO_UPGRADE_VOLUME_PREPARE_ONLY") == "1") return;

        using var fixture = await LifecycleContractTests.LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            installedImage: static image => image with
            {
                UpgradeCompatibility = "cameraagent-state-v2",
                MinimumCompatibleRevision = new string('7', 40),
                IdentityMigration = "20260827053715_InitialIdentity",
                RawIngressSchema = "12",
                CatalogManifestVersion = "2",
                RawIngressMigration = null
            });
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        foreach (var directory in ComposeDeployment.WritableStateDirectories(fixture.Paths.StateRoot))
            SafeFileSystem.CreateRuntimeDirectory(directory, fixture.Uid, fixture.Gid);
        foreach (var tree in new[] { "raw", "archive" })
            await RunAsync("cp", "-a", Path.Combine(source, tree, "."), Path.Combine(fixture.Paths.StateRoot, tree));
        SeedStores(fixture.Paths);
        var volume = Measure(fixture.Paths.StateRoot);
        var lifecycle = new VolumeLifecycleClient();
        var operations = new List<object>();

        async Task TimeAsync(string name, LifecycleRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            string outcome;
            try
            {
                outcome = (await CameraAgentLifecycleManager.ExecuteAsync(
                    request, fixture.Runner, _ => lifecycle, _ => new VolumeOwnerClient(fixture.ApplicationIdentity),
                    fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null)).Outcome;
            }
            catch (InstallerException exception)
            {
                outcome = $"failed: {exception.Message} {exception.InnerException?.Message}".Trim();
            }
            stopwatch.Stop();
            operations.Add(new { name, seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2), outcome, backups = fixture.Runner.BackupCount });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {stopwatch.Elapsed.TotalSeconds:F2} s, {outcome}"));
        }

        LifecycleRequest Upgrade() => fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = CandidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };
        await TimeAsync("upgrade-unchanged-contract", Upgrade());
        await TimeAsync("rollback-unchanged-contract", fixture.Request(LifecycleOperationKind.Rollback));
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        await TimeAsync("upgrade-declared-migration", Upgrade());
        await TimeAsync("rollback-declared-migration", fixture.Request(LifecycleOperationKind.Rollback));

        var report = JsonSerializer.Serialize(new
        {
            volume,
            operations,
            processPeakResidentBytes = ProcessPeakResidentBytes(),
            cgroupMemoryPeakBytes = CgroupValue("memory.peak"),
            cgroupMemoryMax = CgroupText("memory.max"),
            cgroupCpuMax = CgroupText("cpu.max")
        }, ReportOptions);
        Console.WriteLine(report);
        if (Environment.GetEnvironmentVariable("HVO_UPGRADE_VOLUME_REPORT") is { Length: > 0 } reportPath)
            await File.WriteAllTextAsync(reportPath, report);
    }

    private static object Measure(string stateRoot)
    {
        long files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(stateRoot, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += new FileInfo(file).Length;
        }
        return new { files, bytes, gib = Math.Round(bytes / (1024d * 1024 * 1024), 2) };
    }

    /// <summary>
    /// Captures of sixteen files across raw frames, archived products and derived products, until the volume holds
    /// <paramref name="gib"/> GiB.
    /// </summary>
    private static void GenerateVolume(string root, double gib)
    {
        // Dark background with one random byte in twelve, which compresses and gzips like real captures.
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        var noise = new byte[random.Length];
        for (var index = 0; index < noise.Length; index += 12) noise[index] = random[index];
        var target = (long)(gib * 1024 * 1024 * 1024);
        var layout = new (string Tree, int Count, int Bytes)[]
        {
            ("raw/frames", 4, 1024 * 1024), ("archive/products", 4, 256 * 1024), ("raw/derived", 8, 64 * 1024)
        };
        var staging = root + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        long written = 0;
        for (var capture = 0; written < target; capture++)
        {
            var day = new DateOnly(2026, 10, 1).AddDays(capture / 3456).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var (tree, count, length) in layout)
            {
                var directory = Path.Combine(staging, tree, day, capture.ToString("D6", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory, OwnerDirectory);
                for (var file = 0; file < count; file++)
                {
                    using var stream = new FileStream(Path.Combine(directory, $"{file:D2}.bin"), new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    });
                    stream.Write(noise, System.Security.Cryptography.RandomNumberGenerator.GetInt32(noise.Length - length), length);
                    written += length;
                }
            }
        }
        // CameraAgent's state is owner-only, and the earlier backup path refuses a group- or world-writable directory.
        foreach (var directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).Prepend(staging))
            File.SetUnixFileMode(directory, OwnerDirectory);
        Directory.Move(staging, root);
    }

    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static void SeedStores(InstallationPaths paths)
    {
        Execute(CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot),
            "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
            "INSERT INTO __EFMigrationsHistory VALUES('20260827053715_InitialIdentity', '10.0.0');");
        // A raw journal of a realistic size: two hundred thousand capture rows.
        Execute(CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot),
            "PRAGMA journal_mode=WAL; CREATE TABLE captures(id INTEGER PRIMARY KEY, path TEXT NOT NULL, sha256 TEXT NOT NULL);" +
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200000) " +
            "INSERT INTO captures(path, sha256) SELECT printf('frames/2026-10-01/%06d/00.bin', i), hex(randomblob(32)) FROM n;" +
            "PRAGMA user_version=12;");
    }

    private static void Execute(string database, string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!, OwnerDirectory);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static async Task RunAsync(string fileName, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { RedirectStandardError = true })!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, error);
    }

    private static long? ProcessPeakResidentBytes()
    {
        var line = File.ReadLines("/proc/self/status").FirstOrDefault(static line => line.StartsWith("VmHWM:", StringComparison.Ordinal));
        return line is null ? null : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
    }

    private static string? CgroupText(string name)
    {
        var relative = File.ReadAllText("/proc/self/cgroup").Trim().Split(':').Last();
        var path = Path.Combine("/sys/fs/cgroup" + relative, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static long? CgroupValue(string name)
        => long.TryParse(CgroupText(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private sealed class VolumeLifecycleClient : ICameraAgentLifecycleClient
    {
        private static LifecycleContinuity Paused() => new("Paused", 42, 100, 0, 0, 0, 0, 0, 0, 0, 0, ReplayCatalogIndependent: true);

        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(Paused());

        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(
            Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(Paused());

        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(Paused());

        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class VolumeOwnerClient(Guid expectedAgentId) : IOwnerBootstrapClient
    {
        public Task WaitForHealthAsync(ApprovedCatalogContract expectedCatalog, CancellationToken cancellationToken, TimeSpan? timeout = null)
            => Task.CompletedTask;

        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");

        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");

        public Task<string> VerifyInstallationAsync(
            string verificationToken, InstallationVerificationExpectation expectation, CancellationToken cancellationToken)
            => expectation.AgentId == expectedAgentId.ToString("D")
                ? Task.FromResult("owner-password-change-required")
                : throw new InstallerException("unexpected application identity");
    }
}
