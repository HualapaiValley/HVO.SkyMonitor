extern alias InstallerRuntime;

using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;
using SnapshotApi = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateSnapshot;
using InstallerPaths = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallationPaths;
using InstallerLayout = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateLayout;
using InstallerFiles = InstallerRuntime::HVO.SkyMonitor.Deployment.SafeFileSystem;
using InstallerNative = InstallerRuntime::HVO.SkyMonitor.Deployment.NativeLinux;
using RealInstallerException = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerException;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [DataRow("normal")]
    [DataRow("corrupt")]
    [DataRow("cancel")]
    [DataRow("incomplete-overlay")]
    [DataRow("cancel-after-ledger")]
    [DataRow("new-file-after-restore")]
    [DataRow("live-owner")]
    public async Task DatabaseRestorePreservesAuthenticatedPostSnapshotFilesAndRejectsUntrustedProofs(string fault)
    {
        var id = Guid.NewGuid();
        var paths = InstallerPaths.Create(Path.Combine(_root, "installer"), id, "synthetic");
        foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
            InstallerFiles.CreateOwnerDirectory(directory);
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.Combine(paths.StateRoot, "raw"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });
        _protector = new DataProtectionDeploymentLocationProtector(_options);
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        using var first = CreateStore();
        var active = await first.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        _ = await WriteSummaryCaptureAsync(active, journal: true).ConfigureAwait(false);
        first.Dispose();
        var image = new ImageInstallationIdentity("registry", "synthetic@sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64), "amd64", null);
        var snapshot = await SnapshotApi.CreateAsync(paths, id, Guid.NewGuid(), image,
            InstallerLayout.SqliteStores, CancellationToken.None).ConfigureAwait(false);
        using var current = CreateStore();
        _ = await current.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
        var added = Path.Combine(_options.Value.RawIngressRoot, "post-snapshot.json");
        await RetainedFileMutations.WriteAllBytesAsync(added, "{\"sequence\":1}"u8.ToArray()).ConfigureAwait(false);
        Assert.AreEqual(active, await current.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        current.Dispose();
        var ledger = Path.Combine(_options.Value.RawIngressRoot, ".location", "retained-integrity.v1.db");
        if (fault == "corrupt")
        {
            using var connection = new SqliteConnection($"Data Source={ledger};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var edit = connection.CreateCommand();
            edit.CommandText = "UPDATE entries SET digest=zeroblob(32) WHERE key='F|post-snapshot.json';";
            _ = await edit.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var before = await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false);
        var plan = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], CancellationToken.None).ConfigureAwait(false);
        if (fault is "corrupt" or "live-owner")
        {
            using var live = fault == "live-owner" ? CreateStore() : null;
            if (live is not null) _ = await live.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<RealInstallerException>(async () =>
                _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                    [], plan, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
        }
        else
        {
            if (fault is "cancel" or "incomplete-overlay" or "cancel-after-ledger")
            {
                using var cancelled = new CancellationTokenSource();
                var previous = SnapshotApi.StepCompleted;
                SnapshotApi.StepCompleted = step =>
                {
                    if (step == (fault == "cancel-after-ledger"
                        ? "restore:raw/.location/retained-integrity.v1.db" : "restore-integrity-overlay-ready")) cancelled.Cancel();
                };
                try
                {
                    await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                        _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                            [], plan, cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);
                }
                finally { SnapshotApi.StepCompleted = previous; }
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(HistoryPath()).ConfigureAwait(false));
                if (fault == "incomplete-overlay")
                {
                    var seal = Path.Combine(SnapshotApi.SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId),
                        "retained-files-overlay.v1.protected");
                    await File.WriteAllBytesAsync(seal, [1, 2, 3]).ConfigureAwait(false);
                }
            }
            _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                [], plan, CancellationToken.None).ConfigureAwait(false);
            var once = await File.ReadAllBytesAsync(ledger).ConfigureAwait(false);
            if (fault == "new-file-after-restore")
            {
                using var changed = CreateStore();
                _ = await changed.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
                await RetainedFileMutations.WriteAllBytesAsync(Path.Combine(_options.Value.RawIngressRoot, "second-post-snapshot.json"),
                    "{\"sequence\":2}"u8.ToArray()).ConfigureAwait(false);
            }
            _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                [], plan, CancellationToken.None).ConfigureAwait(false);
            if (fault != "new-file-after-restore")
                CollectionAssert.AreEqual(once, await File.ReadAllBytesAsync(ledger).ConfigureAwait(false));
            using var restarted = CreateStore();
            Assert.AreEqual(active, await restarted.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(active, await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false));
        }
        Assert.IsTrue(File.Exists(added));
    }
}
