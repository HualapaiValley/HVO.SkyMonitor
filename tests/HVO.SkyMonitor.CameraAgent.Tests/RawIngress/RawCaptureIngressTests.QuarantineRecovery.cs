using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task ReconcileAsync_PlannedCommittedQuarantineRecoversEveryDurableBoundary(int boundary)
    {
        if (!OperatingSystem.IsLinux()) return;
        // Planned, one move, both moves, disposition written, plan completed.
        using var hold = new Capture.MaintenanceHoldRoot();
        string payload;
        Guid captureId;
        using (var original = CreateIngress(hold.Path, new RawIngressState(TimeProvider.System)))
        {
            var receipt = await original.AcceptAsync(CreateConfiguration(),
                CreateSubmission(Timestamp(6), new byte[] { 6, 6, 6, 6 }), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            payload = receipt.StoredFrame.AbsolutePath;
            captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
        }
        var sidecar = Path.ChangeExtension(payload, ".json");
        await File.WriteAllBytesAsync(payload, new byte[] { 7, 7, 7, 7 }).ConfigureAwait(false);
        var originalSidecar = await File.ReadAllBytesAsync(sidecar).ConfigureAwait(false);
        var db = Path.Combine(hold.Path, "journal", "raw-ingress.db");
        var crashed = false;
        var journal = new SqliteRawCaptureJournal(db, 1, transactionRecorder: (operation, succeeded) =>
        {
            if (!crashed && succeeded && (boundary == 3 && operation == "evidence-state" ||
                boundary == 4 && operation == "reconciliation-complete"))
            {
                crashed = true;
                throw new IOException("Simulated process loss after a durable journal write.");
            }
        });
        var operation = new RawIngressPlannedQuarantine("committed-recovery",
            Path.GetRelativePath(hold.Path, payload).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetRelativePath(hold.Path, sidecar).Replace(Path.DirectorySeparatorChar, '/'),
            "quarantine/committed-recovery", "committed-evidence-mismatch",
            new FileInfo(payload).Length + new FileInfo(sidecar).Length);
        await journal.PlanQuarantineAsync(operation, CancellationToken.None).ConfigureAwait(false);
        var destination = Path.Combine(hold.Path, "quarantine", "committed-recovery");
        if (boundary == 1)
        {
            Directory.CreateDirectory(destination);
            File.Move(payload, Path.Combine(destination, Path.GetFileName(payload)));
        }
        else if (boundary >= 2)
        {
            var interrupted = new RawIngressReconciler(hold.Path, journal, directorySyncRecorder: () =>
            {
                if (boundary == 2 && !crashed && !File.Exists(payload) && !File.Exists(sidecar))
                {
                    crashed = true;
                    throw new IOException("Simulated process loss after moves and before capture disposition.");
                }
            });
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await interrupted.RunAsync(_ => ValueTask.FromResult(default(HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureMaintenanceEntry)),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsTrue(crashed);
        }
        // Reopen the journal and reconstruct the plan; no in-memory discovery association survives.
        var reopened = new SqliteRawCaptureJournal(db, 1);
        await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var plans = await reopened.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(boundary == 4 ? 0 : 1, plans);
        if (boundary < 4) Assert.AreEqual(captureId, plans[0].CaptureId,
            "The persisted plan must recover its capture association after files move.");
        var recovery = new RawIngressReconciler(hold.Path, reopened);
        var result = await recovery.RunAsync(_ => ValueTask.FromResult(default(HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureMaintenanceEntry)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(boundary == 4 ? 0 : 1, result.Quarantined);
        Assert.AreEqual(0, result.MissingEvidence);
        Assert.IsEmpty(await reopened.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(payload));
        Assert.IsFalse(File.Exists(sidecar));
        CollectionAssert.AreEqual(new byte[] { 7, 7, 7, 7 },
            await File.ReadAllBytesAsync(Path.Combine(destination, Path.GetFileName(payload))).ConfigureAwait(false));
        CollectionAssert.AreEqual(originalSidecar,
            await File.ReadAllBytesAsync(Path.Combine(destination, Path.GetFileName(sidecar))).ConfigureAwait(false));
        Assert.HasCount(2, Directory.GetFiles(destination));
        using var connection = await OpenJournalAsync(hold.Path).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(connection, "SELECT state FROM raw_captures;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM raw_ingress_reconciliation WHERE operation_state = 'completed';").ConfigureAwait(false));
        var again = await recovery.RunAsync(_ => ValueTask.FromResult(default(HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureMaintenanceEntry)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, again.Quarantined);
        Assert.AreEqual(0, again.MissingEvidence);
        Assert.AreEqual("quarantined", await ScalarStringAsync(connection, "SELECT state FROM raw_captures;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReconcileAsync_PlannedQuarantineCompletesCaptureDispositionBeforePauseAcknowledgement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var hold = new Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: false).ConfigureAwait(false);
        string payload;
        using (var original = CreateIngress(hold.Path, new RawIngressState(TimeProvider.System)))
        {
            var receipt = await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(6), new byte[] { 6, 6, 6, 6 }), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            payload = receipt.StoredFrame.AbsolutePath;
        }
        var sidecar = Path.ChangeExtension(payload, ".json");
        await File.WriteAllBytesAsync(payload, new byte[] { 7, 7, 7, 7 }).ConfigureAwait(false);
        var journal = new SqliteRawCaptureJournal(Path.Combine(hold.Path, "journal", "raw-ingress.db"), 1);
        await journal.PlanQuarantineAsync(new RawIngressPlannedQuarantine("planned-committed",
            Path.GetRelativePath(hold.Path, payload).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetRelativePath(hold.Path, sidecar).Replace(Path.DirectorySeparatorChar, '/'),
            "quarantine/planned-committed", "committed-evidence-mismatch", new FileInfo(payload).Length + new FileInfo(sidecar).Length), CancellationToken.None).ConfigureAwait(false);
        Task? pause = null;
        var reconciler = new RawIngressReconciler(hold.Path, journal, directorySyncRecorder: () =>
        {
            pause ??= admission.PauseAsync("disposition-pause", null, "test", null, CancellationToken.None);
        });
        var first = await reconciler.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(pause);
        await pause.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        string atPause;
        using (var connection = await OpenJournalAsync(hold.Path).ConfigureAwait(false))
            atPause = await ScalarStringAsync(connection, "SELECT state FROM raw_captures;").ConfigureAwait(false);
        Assert.IsFalse(File.Exists(payload));
        Assert.IsFalse(File.Exists(sidecar));
        Assert.IsEmpty(await journal.ReadPlannedQuarantinesAsync(CancellationToken.None).ConfigureAwait(false));
        await admission.ResumeAsync("disposition-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        var next = await reconciler.RunAsync(_ => ValueTask.FromResult(hold.Gate.EnterIfInitialized()), CancellationToken.None).ConfigureAwait(false);
        string afterResume;
        using (var connection = await OpenJournalAsync(hold.Path).ConfigureAwait(false))
            afterResume = await ScalarStringAsync(connection, "SELECT state FROM raw_captures;").ConfigureAwait(false);
        Console.WriteLine($"At acknowledgement: state={atPause}, quarantined={first.Quarantined}, deferred={first.MaintenanceDeferred}; after resume: state={afterResume}, quarantined={next.Quarantined}, missing={next.MissingEvidence}");
        Assert.AreEqual("quarantined", atPause, "A completed planned quarantine moved both committed files but deferred the capture disposition until after acknowledgement.");
        Assert.AreEqual(1, first.Quarantined);
        Assert.AreEqual(0, first.MaintenanceDeferred, "Discovery must not queue a duplicate of the planned capture.");
        Assert.AreEqual("quarantined", afterResume);
        Assert.AreEqual(0, next.Quarantined);
        Assert.AreEqual(0, next.MissingEvidence);
    }
}
