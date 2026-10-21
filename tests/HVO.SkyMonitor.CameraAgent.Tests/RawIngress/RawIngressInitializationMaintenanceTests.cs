using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class RawIngressInitializationMaintenanceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReconcileAsync_PendingPauseFinishesOneQuarantineAndDefersRemainingOperations(bool planned)
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Directory synchronization is a Linux durability boundary.");
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: false).ConfigureAwait(false);
        var journal = new SqliteRawCaptureJournal(Path.Combine(hold.Path, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var rawRoot = Path.Combine(hold.Path, "frames", "2026", "01", "01", "Raw");
        Directory.CreateDirectory(rawRoot);
        for (var index = 0; index < 3; index++)
        {
            var source = $"frames/2026/01/01/Raw/{index}.bin";
            var directory = planned ? Path.Combine(hold.Path, "quarantine", $"planned-{index}") : rawRoot;
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{index}.bin"), [(byte)index]).ConfigureAwait(false);
            if (planned)
            {
                // Simulate a crash after moving the evidence but before completing its journaled operation.
                await journal.PlanQuarantineAsync(new RawIngressPlannedQuarantine(
                    $"planned-{index}", source, null, $"quarantine/planned-{index}", "payload-without-sidecar", 1),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        Task? pause = null;
        var reconciler = new RawIngressReconciler(hold.Path, journal, directorySyncRecorder: () =>
        {
            if (pause is not null) return;
            pause = admission.PauseAsync("quarantine-pause", null, "test", null, CancellationToken.None);
            Assert.IsFalse(pause.IsCompleted, "The active quarantine must finish durably before acknowledgement.");
        });
        var interrupted = await reconciler.RunAsync(
            _ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pause);
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(1, interrupted.Quarantined);
        Assert.AreEqual(2, interrupted.MaintenanceDeferred);
        Assert.HasCount(planned ? 2 : 0, await journal.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false));
        var held = await reconciler.RunAsync(
            _ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, held.Quarantined);
        Assert.AreEqual(2, held.MaintenanceDeferred);

        await admission.ResumeAsync("quarantine-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        var resumed = await reconciler.RunAsync(
            _ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, resumed.Quarantined);
        Assert.AreEqual(0, resumed.MaintenanceDeferred);
        Assert.IsEmpty(await journal.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(Directory.EnumerateFiles(rawRoot).Any());
        var evidence = Directory.EnumerateFiles(Path.Combine(hold.Path, "quarantine"), "*.bin", SearchOption.AllDirectories).ToArray();
        Assert.HasCount(3, evidence);
        for (var index = 0; index < 3; index++)
        {
            var path = evidence.Single(path => Path.GetFileName(path) == $"{index}.bin");
            CollectionAssert.AreEqual(new byte[] { (byte)index }, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        }
        var finished = await reconciler.RunAsync(
            _ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, finished.Quarantined);
    }
}
