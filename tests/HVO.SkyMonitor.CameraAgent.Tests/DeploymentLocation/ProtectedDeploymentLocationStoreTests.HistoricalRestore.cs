extern alias InstallerRuntime;

using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;
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
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task HistoricalDatabaseOnlyRestoreRequiresAuthenticatedLegacyAuthority(bool sealedHistory, bool restore)
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
        var before = HistoricalStateFingerprint(paths.StateRoot);
        var originalHistory = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false);
        RealInstallerException? refusal = null;
        try
        {
            if (restore)
                _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                    [], plan, CancellationToken.None).ConfigureAwait(false);
            else
                _ = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false);
        }
        catch (RealInstallerException exception) { refusal = exception; }
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT COUNT(*) FROM fixture_rows;";
        Assert.AreEqual(sealedHistory || !restore ? 2L : 1L, await rows.ExecuteScalarAsync().ConfigureAwait(false),
            "Missing established authority must refuse before replacing any database.");
        CollectionAssert.AreEqual(originalHistory, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
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
