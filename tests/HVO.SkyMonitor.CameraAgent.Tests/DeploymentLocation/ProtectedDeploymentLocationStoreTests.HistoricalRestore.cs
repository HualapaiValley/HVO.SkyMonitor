extern alias InstallerRuntime;

using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SnapshotApi = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateSnapshot;
using InstallerPaths = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallationPaths;
using InstallerLayout = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateLayout;
using InstallerFiles = InstallerRuntime::HVO.SkyMonitor.Deployment.SafeFileSystem;
using InstallerJson = InstallerRuntime::HVO.SkyMonitor.Deployment.DeploymentJsonContext;
using InstallerNative = InstallerRuntime::HVO.SkyMonitor.Deployment.NativeLinux;
using RealInstallerException = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerException;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [DataRow(true, false, "none")]
    [DataRow(true, true, "none")]
    [DataRow(false, false, "none")]
    [DataRow(false, true, "none")]
    [DataRow(true, false, "history-directory")]
    [DataRow(true, true, "history-directory")]
    [DataRow(true, false, "parent-file")]
    [DataRow(true, true, "parent-file")]
    [DataRow(true, false, "lookup-denied")]
    [DataRow(true, true, "lookup-denied")]
    [DataRow(true, false, "tampered-history")]
    [DataRow(true, true, "tampered-history")]
    [DataRow(true, false, "foreign-keys")]
    [DataRow(true, true, "foreign-keys")]
    [DataRow(true, false, "missing-keys")]
    [DataRow(true, true, "missing-keys")]
    [DataRow(true, false, "non-object")]
    [DataRow(true, true, "non-object")]
    public async Task HistoricalDatabaseOnlyRestoreRequiresAuthenticatedLegacyAuthority(bool sealedHistory, bool restore, string fault)
    {
        var id = Guid.NewGuid();
        var paths = InstallerPaths.Create(Path.Combine(_root, "historical-installer"), id, "synthetic");
        foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
            InstallerFiles.CreateOwnerDirectory(directory);
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(paths.StateRoot, "raw"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _protector = new DataProtectionDeploymentLocationProtector(_options);
        var database = Path.Combine(paths.StateRoot, "identity", "cameraagent_identity.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await ExecuteHistoricalFixtureSqlAsync(database, "CREATE TABLE fixture_rows(value INTEGER); INSERT INTO fixture_rows VALUES(1);").ConfigureAwait(false);
        var image = new ImageInstallationIdentity("registry", "synthetic@sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64), "amd64", null);
        var snapshot = await SnapshotApi.CreateAsync(paths, id, Guid.NewGuid(), image,
            InstallerLayout.SqliteStores, CancellationToken.None).ConfigureAwait(false);
        var manifest = await SnapshotApi.VerifyAsync(paths, id, snapshot, CancellationToken.None).ConfigureAwait(false);
        var historical = manifest with
        {
            Databases = manifest.Databases.Where(static entry => entry.RelativePath != SnapshotApi.RetainedIntegrityStore).ToArray(),
            ProtectedFiles = null
        };
        var manifestPath = Path.Combine(SnapshotApi.SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId), "manifest.json");
        await InstallerFiles.WriteJsonAtomicAsync(manifestPath, historical, InstallerJson.Default.StateSnapshotManifest, CancellationToken.None).ConfigureAwait(false);
        snapshot = snapshot with { ManifestSha256 = await InstallerFiles.ComputeSha256Async(manifestPath, CancellationToken.None).ConfigureAwait(false) };
        // A real earlier plan must not bypass the restore's own authority check.
        var plan = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false);
        using (var store = CreateStore())
            _ = await store.InitializeAsync(CreateSeed(35, -113, 0, "America/Phoenix"), CancellationToken.None).ConfigureAwait(false);
        var history = await ReadHistoryObjectAsync().ConfigureAwait(false);
        Assert.AreEqual(32, history["retainedIntegrityNonce"]!.GetValue<string>().Length);
        if (!sealedHistory)
        {
            Assert.IsTrue(history.Remove("retainedIntegrityNonce"));
            await WriteHistoryObjectAsync(history).ConfigureAwait(false);
            Assert.IsFalse((await ReadHistoryObjectAsync().ConfigureAwait(false)).ContainsKey("retainedIntegrityNonce"));
        }
        DeleteFixtureLedger();
        Assert.IsFalse(File.Exists(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.DatabaseName)));
        Assert.IsFalse(File.Exists(Path.Combine(_options.Value.RawIngressRoot, ".location", RetainedIntegrityLedger.CheckpointName)));
        await ExecuteHistoricalFixtureSqlAsync(database, "INSERT INTO fixture_rows VALUES(2);").ConfigureAwait(false);
        var locationDirectory = Path.GetDirectoryName(HistoryPath())!;
        var preservedHistory = HistoryPath();
        if (fault == "history-directory")
        {
            preservedHistory += ".sealed-original";
            File.Move(HistoryPath(), preservedHistory);
            Directory.CreateDirectory(HistoryPath());
        }
        else if (fault == "parent-file")
        {
            Directory.Move(locationDirectory, locationDirectory + ".saved");
            await File.WriteAllTextAsync(locationDirectory, "obstructed authority").ConfigureAwait(false);
            preservedHistory = Path.Combine(locationDirectory + ".saved", Path.GetFileName(HistoryPath()));
        }
        else if (fault == "tampered-history")
        {
            var damaged = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false);
            damaged[^1] ^= 1;
            await File.WriteAllBytesAsync(HistoryPath(), damaged).ConfigureAwait(false);
        }
        else if (fault is "missing-keys" or "foreign-keys")
        {
            var keys = Path.Combine(locationDirectory, "keys");
            Directory.Move(keys, keys + ".saved");
            if (fault == "foreign-keys")
            {
                Directory.CreateDirectory(keys);
                var foreign = DataProtectionProvider.Create(new DirectoryInfo(keys),
                    builder => builder.SetApplicationName("HVO.SkyMonitor.CameraAgent.DeploymentLocation"));
                _ = foreign.CreateProtector("CameraAgent", "DeploymentLocation", "v1").Protect("fixture key generation");
            }
        }
        else if (fault == "non-object")
            await File.WriteAllBytesAsync(HistoryPath(), _protector.Protect("[]"u8.ToArray())).ConfigureAwait(false);
        var before = HistoricalStateFingerprint(paths.StateRoot);
        var originalHistory = await File.ReadAllBytesAsync(preservedHistory).ConfigureAwait(false);
        var originalMode = fault == "lookup-denied" ? File.GetUnixFileMode(locationDirectory) : (UnixFileMode?)null;
        const UnixFileMode deniedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Exception? refusal = null;
        try
        {
            if (originalMode is not null)
            {
                File.SetUnixFileMode(locationDirectory, deniedMode);
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
                    _ = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false)).ConfigureAwait(false);
            }
            try
            {
                if (restore)
                    _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                        [], plan, CancellationToken.None).ConfigureAwait(false);
                else
                    _ = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RealInstallerException or IOException or UnauthorizedAccessException) { refusal = exception; }
        }
        finally
        {
            if (originalMode is { } mode)
            {
                var observedMode = File.GetUnixFileMode(locationDirectory);
                File.SetUnixFileMode(locationDirectory, mode);
                Assert.AreEqual(deniedMode, observedMode, "Restore must not repair an uninspectable authority path.");
            }
        }
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT COUNT(*) FROM fixture_rows;";
        Assert.AreEqual(sealedHistory || !restore ? 2L : 1L, await rows.ExecuteScalarAsync().ConfigureAwait(false),
            "Missing established authority must refuse before replacing any database.");
        CollectionAssert.AreEqual(originalHistory, await File.ReadAllBytesAsync(preservedHistory).ConfigureAwait(false));
        if (sealedHistory)
        {
            Assert.IsTrue(before.SequenceEqual(HistoricalStateFingerprint(paths.StateRoot)), "Refusal must leave every current state file unchanged.");
            Assert.IsNotNull(refusal, "A sealed history cannot become legacy merely because both ledger files were deleted.");
        }
        else Assert.IsNull(refusal, "Authenticated unsealed legacy history must retain database-only restore compatibility.");
    }

    private static async Task ExecuteHistoricalFixtureSqlAsync(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Only fixed statements from this disposable fixture.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string[] HistoricalStateFingerprint(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal).ToArray();
}
