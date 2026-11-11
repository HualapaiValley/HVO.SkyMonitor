using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class CameraAgentStateSnapshotTests
{
    private static readonly string[] PairedPaths =
    [
        "raw/.location/deployment-location.v1.protected",
        "raw/.location/manual-deployment-location.v1.protected",
        "raw/.deployment-location.v1.identity",
        "raw/.location/retained-integrity.v1.protected"
    ];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PairedProtectedStateAndLedgerRestoreExactlyWithoutTouchingRetainedEvidence()
    {
        using var root = new SnapshotRoot();
        await WritePairedAsync(root, "original");
        var ledger = CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, CameraAgentStateSnapshot.RetainedIntegrityStore);
        CreateDatabase(ledger, 3);
        var originalRows = Rows(ledger);
        var retained = await WriteRetainedSentinelAsync(root);
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(root.Paths, root.InstanceId, Guid.NewGuid(), Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);
        await WritePairedAsync(root, "candidate");
        File.Delete(ledger);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // This tests opaque backup restoration after loss of the current store.
            // The real agent interaction test covers authenticated file-proof carry.
            File.Delete(ledger);
            await RestorePairedAsync(root, snapshot, CancellationToken.None);
            foreach (var relative in PairedPaths) Assert.AreEqual("original", await File.ReadAllTextAsync(CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, relative)));
            CollectionAssert.AreEqual(originalRows, Rows(ledger));
            Assert.AreEqual("retained", await File.ReadAllTextAsync(retained));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PreLedgerSnapshotRefusesToDiscardCurrentProofsOrCaptureEvidence()
    {
        using var root = new SnapshotRoot();
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(root.Paths, root.InstanceId, Guid.NewGuid(), Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);
        await WritePairedAsync(root, "candidate");
        var ledger = CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, CameraAgentStateSnapshot.RetainedIntegrityStore);
        CreateDatabase(ledger, 1);
        var retained = await WriteRetainedSentinelAsync(root);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => RestorePairedAsync(root, snapshot, CancellationToken.None));
        Assert.IsTrue(File.Exists(ledger));
        Assert.IsTrue(PairedPaths.All(relative => File.Exists(CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, relative))));
        Assert.AreEqual("retained", await File.ReadAllTextAsync(retained));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("corrupt")]
    [DataRow("missing")]
    [DataRow("escape")]
    [DataRow("incomplete")]
    public async Task InvalidPairedSnapshotRefusesBeforeAnyRestore(string fault)
    {
        using var root = new SnapshotRoot();
        await WritePairedAsync(root, "original");
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(root.Paths, root.InstanceId, Guid.NewGuid(), Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);
        var manifest = await CameraAgentStateSnapshot.VerifyAsync(root.Paths, root.InstanceId, snapshot, CancellationToken.None);
        var snapshotRoot = CameraAgentStateSnapshot.SnapshotRoot(root.Paths, snapshot.OperationId, snapshot.SnapshotId);
        var saved = manifest.ProtectedFiles!.ToArray();
        if (fault == "corrupt") await File.WriteAllTextAsync(Path.Combine(snapshotRoot, saved[0].SnapshotFile!), "bad");
        else if (fault == "missing") File.Delete(Path.Combine(snapshotRoot, saved[0].SnapshotFile!));
        else
        {
            if (fault == "escape") saved[0] = saved[0] with { RelativePath = "../outside" };
            else saved = saved[1..];
            snapshot = await RewriteSnapshotManifestAsync(root, snapshot, manifest with { ProtectedFiles = saved });
        }
        await WritePairedAsync(root, "current");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => RestorePairedAsync(root, snapshot, CancellationToken.None));
        foreach (var relative in PairedPaths) Assert.AreEqual("current", await File.ReadAllTextAsync(CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, relative)));
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CancelledPairedRestoreIsResumableAndNeverRecordsPartialCompletion()
    {
        using var root = new SnapshotRoot();
        await WritePairedAsync(root, "original");
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(root.Paths, root.InstanceId, Guid.NewGuid(), Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);
        await WritePairedAsync(root, "candidate");
        using var cancelled = new CancellationTokenSource();
        var previous = CameraAgentStateSnapshot.StepCompleted;
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step.StartsWith("restore-protected:", StringComparison.Ordinal)) cancelled.Cancel();
        };
        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RestorePairedAsync(root, snapshot, cancelled.Token));
        }
        finally { CameraAgentStateSnapshot.StepCompleted = previous; }
        await RestorePairedAsync(root, snapshot, CancellationToken.None);
        foreach (var relative in PairedPaths) Assert.AreEqual("original", await File.ReadAllTextAsync(CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, relative)));
        Assert.IsFalse(Directory.EnumerateFiles(root.Paths.StateRoot, "*.paired-restore.tmp", SearchOption.AllDirectories).Any());
    }

    private static async Task WritePairedAsync(SnapshotRoot root, string contents)
    {
        foreach (var relative in PairedPaths)
        {
            var path = CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);
        }
    }
    private static async Task<string> WriteRetainedSentinelAsync(SnapshotRoot root)
    {
        var path = Path.Combine(root.Paths.StateRoot, "raw", "capture-sentinel.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "retained");
        return path;
    }
    private static async Task RestorePairedAsync(SnapshotRoot root, LifecycleStateSnapshot snapshot, CancellationToken token)
        => _ = await CameraAgentStateSnapshot.RestoreAsync(root.Paths, root.InstanceId, snapshot, root.Uid, root.Gid, [],
            await CameraAgentStateSnapshot.PlanRestoreAsync(root.Paths, root.InstanceId, snapshot, [], token), token);
    private static async Task<LifecycleStateSnapshot> RewriteSnapshotManifestAsync(SnapshotRoot root, LifecycleStateSnapshot snapshot, StateSnapshotManifest manifest)
    {
        var path = Path.Combine(CameraAgentStateSnapshot.SnapshotRoot(root.Paths, snapshot.OperationId, snapshot.SnapshotId), "manifest.json");
        await SafeFileSystem.WriteJsonAtomicAsync(path, manifest, DeploymentJsonContext.Default.StateSnapshotManifest, CancellationToken.None);
        return snapshot with { ManifestSha256 = await SafeFileSystem.ComputeSha256Async(path, CancellationToken.None) };
    }
}
