using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The calibration-family store (#1475) in a full backup. It is not one of the snapshotted stores, so the backup archives
/// it with the instance's other files while the instance is stopped, and a restore puts the backup's copy back.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAndRestore_CarryTheCalibrationFamiliesStore()
    {
        using var fixture = await BackupFixtureAsync();
        var families = Path.Combine(
            fixture.Paths.StateRoot, CameraAgentStateLayout.CalibrationFamiliesStore.Replace('/', Path.DirectorySeparatorChar));
        Execute(families, "CREATE TABLE activations(idempotency_key TEXT PRIMARY KEY) STRICT; INSERT INTO activations VALUES('at-backup');");
        var atBackup = await SafeFileSystem.ComputeSha256Async(families, CancellationToken.None);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        Assert.AreEqual("completed", backup.Outcome);
        var (_, names) = await ReadArchiveAsync(Path.Combine(fixture.Root, BackupVault.FolderName, backup.Backup!.Archive));
        CollectionAssert.Contains(names, "instance/state/" + CameraAgentStateLayout.CalibrationFamiliesStore,
            "the store is archived with the instance's files");
        // After the backup another activation is recorded.
        Execute(families, "INSERT INTO activations VALUES('after-backup');");
        Assert.AreNotEqual(atBackup, await SafeFileSystem.ComputeSha256Async(families, CancellationToken.None));
        await UninstallAsync(fixture);

        var restored = await RestoreAsync(fixture, backup.Backup.Archive);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.AreEqual(atBackup, await SafeFileSystem.ComputeSha256Async(families, CancellationToken.None),
            "the restore puts the backup's calibration-family store back");
    }
}
