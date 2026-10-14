using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task EvidenceRelease_WriteOffWinsAgainstRetryWaitingForLifecycleLock()
    {
        var root = CreateRoot();
        using var fault = new EvidenceRetryPause();
        var ingress = CreateIngress(root, new(TimeProvider.System), fault);
        Task<RawCaptureReceipt?>? retry = null;
        try
        {
            var submission = CreateSubmission(Timestamp(6), [1, 2, 3, 4]);
            var receipt = await ingress.AcceptAsync(CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            fault.Arm();
            retry = Task.Run(async () => await ingress.AcceptAsync(CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(fault.Entered.Wait(TimeSpan.FromSeconds(10)), "Retry did not reach its pre-lock snapshot.");
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            var gate = HVO.SkyMonitor.CameraAgent.Common.Storage.RawIngressLifecycleLock.ForRoot(root);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await journal.MarkEvidenceFailureAsync(receipt!.Manifest.Descriptor.Capture.CaptureId,
                    "missing_evidence", "required-file-missing", CancellationToken.None).ConfigureAwait(false);
            }
            finally { gate.Release(); }
            var target = (await ingress.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single().Target;
            await ingress.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "loss-race", "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
            fault.Release();
            await Assert.ThrowsExactlyAsync<RawIngressConflictException>(() => retry).ConfigureAwait(false);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false));
        }
        finally
        {
            fault.Release();
            if (retry is not null)
            {
                try { await retry.ConfigureAwait(false); }
                catch (RawIngressConflictException) { }
            }
            ingress.Dispose();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EvidenceRelease_PageIsBoundedAndUnreleasedFailuresContinueToBlockAdmission()
    {
        var root = CreateRoot();
        try
        {
            var payloads = new List<string>();
            using (var original = CreateIngress(root, new(TimeProvider.System)))
            {
                for (var index = 1; index <= 3; index++)
                    payloads.Add((await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(index), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath);
            }
            foreach (var payload in payloads) File.Move(payload, Path.Combine(root, Path.GetFileName(payload)));
            using var restarted = CreateIngress(root, new(TimeProvider.System));
            var first = await restarted.ReadEvidencePageAsync(2, null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(2, first.Items.Count);
            Assert.IsNotNull(first.NextCursor);
            var second = await restarted.ReadEvidencePageAsync(2, first.NextCursor, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, second.Items.Count);
            Assert.IsNull(second.NextCursor);
            Assert.AreEqual(3, first.Items.Concat(second.Items).Select(item => item.Target.CaptureId).Distinct().Count());
            await restarted.ReleaseEvidenceAsync(first.Items[0].Target, RawEvidenceReleaseAction.WriteOff, "loss-1", "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.AcceptAsync(CreateConfiguration(),
                CreateSubmission(Timestamp(7), [1, 2, 3, 4]), CancellationToken.None).AsTask()).ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task EvidenceRelease_WriteOffCommitFaultRollsBackDispositionAuditAndLanes()
    {
        var root = CreateRoot();
        try
        {
            string payload;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                payload = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(6), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath;
            File.Move(payload, Path.Combine(root, "saved.bin"));
            using var restarted = CreateIngress(root, new(TimeProvider.System), new OneShotFaultInjector(RawIngressFaultPoint.BeforeJournalTransactionCommit));
            var target = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single().Target;
            await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(() => restarted.ReleaseEvidenceAsync(target,
                RawEvidenceReleaseAction.WriteOff, "loss-1", "owner", "operator-approved-loss", CancellationToken.None).AsTask()).ConfigureAwait(false);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_capture_stage_events WHERE stage_key GLOB 'raw-evidence*';").ConfigureAwait(false));
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'abandoned';").ConfigureAwait(false));
            var receipt = await restarted.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.WriteOff, "loss-1", "owner", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(receipt.Duplicate);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EvidenceRelease_WriteOffPersistsAuditPreservesBytesAndResumesAdmission(bool missing)
    {
        var root = CreateRoot();
        try
        {
            var originalBytes = new byte[] { 1, 2, 3, 4 };
            string payload;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                payload = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(6), originalBytes), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath;
            // Move missing evidence aside rather than deleting raw data; corrupt evidence is retained in quarantine.
            if (missing) File.Move(payload, Path.Combine(root, "recovery-backup.bin"));
            else await File.WriteAllBytesAsync(payload, new byte[] { 9, 8, 7, 6 }).ConfigureAwait(false);
            using (var restarted = CreateIngress(root, new(TimeProvider.System)))
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(7), originalBytes), CancellationToken.None).AsTask()).ConfigureAwait(false);
                var item = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
                var retained = await SnapshotEvidenceFilesAsync(root, "*.bin").ConfigureAwait(false);
                var receipt = await restarted.ReleaseEvidenceAsync(item.Target, RawEvidenceReleaseAction.WriteOff,
                    "loss-1", "owner-alice", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(receipt.Duplicate);
                Assert.AreEqual("owner-alice", receipt.Audit.Actor);
                Assert.AreEqual("operator-approved-loss", receipt.Audit.Reason);
                var duplicate = await restarted.ReleaseEvidenceAsync(item.Target, RawEvidenceReleaseAction.WriteOff,
                    "loss-1", "owner-alice", "operator-approved-loss", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(duplicate.Duplicate);
                Assert.AreEqual(receipt.Audit, duplicate.Audit);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => restarted.ReleaseEvidenceAsync(item.Target,
                    RawEvidenceReleaseAction.WriteOff, "loss-1", "owner-bob", "operator-approved-loss", CancellationToken.None).AsTask()).ConfigureAwait(false);
                await restarted.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(7), originalBytes), CancellationToken.None).ConfigureAwait(false);
                foreach (var file in retained) CollectionAssert.AreEqual(file.Value, await File.ReadAllBytesAsync(file.Key).ConfigureAwait(false));
            }
            using var afterRestart = CreateIngress(root, new(TimeProvider.System));
            await afterRestart.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var terminal = (await afterRestart.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
            Assert.IsTrue(terminal.WrittenOff);
            Assert.AreEqual("owner-alice", terminal.LatestAudit!.Actor);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => afterRestart.ReleaseEvidenceAsync(terminal.Target,
                RawEvidenceReleaseAction.Revalidate, "restore-terminal", "owner-alice", "evidence-restored", CancellationToken.None).AsTask()).ConfigureAwait(false);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM capture_lane_work WHERE capture_sequence = 1 AND state != 'abandoned';").ConfigureAwait(false));
            // A later hold recomputation must not make written-off bytes eligible for retention.
            await ExecuteTestSqlAsync(verify, "UPDATE raw_captures SET retention_hold = 0 WHERE capture_sequence = 1;").ConfigureAwait(false);
            var holds = await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1).ReadRetentionHoldsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(holds.Any(hold => hold.ArtifactId == terminal.ArtifactId));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task EvidenceRelease_RevalidationRejectsInvalidBytesAndAuditsOnlyExactRecoveredEvidence()
    {
        var root = CreateRoot();
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            string payload;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                payload = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(6), bytes), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath;
            var sidecar = Path.ChangeExtension(payload, ".json");
            var manifest = await File.ReadAllBytesAsync(sidecar).ConfigureAwait(false);
            await File.WriteAllBytesAsync(payload, new byte[] { 9, 9, 9, 9 }).ConfigureAwait(false);
            using var restarted = CreateIngress(root, new(TimeProvider.System));
            var target = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single().Target;
            var quarantineBytes = await SnapshotEvidenceFilesAsync(Path.Combine(root, "quarantine"), "*").ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.ReleaseEvidenceAsync(target,
                RawEvidenceReleaseAction.Revalidate, "recover-1", "owner-alice", "evidence-restored", CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.IsNull((await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single().LatestAudit);
            await File.WriteAllBytesAsync(payload, new byte[] { 8, 8, 8, 8 }).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sidecar, manifest).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.ReleaseEvidenceAsync(target,
                RawEvidenceReleaseAction.Revalidate, "recover-1", "owner-alice", "evidence-restored", CancellationToken.None).AsTask()).ConfigureAwait(false);
            File.Move(payload, Path.Combine(root, "rejected-recovery.bin"));
            await File.WriteAllBytesAsync(payload, bytes).ConfigureAwait(false);
            target = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single().Target;
            await restarted.ReleaseEvidenceAsync(target, RawEvidenceReleaseAction.Revalidate, "recover-1", "owner-alice", "evidence-restored", CancellationToken.None).ConfigureAwait(false);
            await restarted.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(7), bytes), CancellationToken.None).ConfigureAwait(false);
            var item = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
            Assert.AreEqual("committed", item.Target.State);
            Assert.AreEqual(RawEvidenceReleaseAction.Revalidate, item.LatestAudit!.Action);
            foreach (var file in quarantineBytes) CollectionAssert.AreEqual(file.Value, await File.ReadAllBytesAsync(file.Key).ConfigureAwait(false));
            CollectionAssert.AreEqual(manifest, await File.ReadAllBytesAsync(sidecar).ConfigureAwait(false));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(payload).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task EvidenceRelease_RejectsLeasedLanesAndMalformedCommandsWithoutAudit()
    {
        var root = CreateRoot();
        try
        {
            string payload;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                payload = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(6), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!.StoredFrame.AbsolutePath;
            File.Move(payload, Path.Combine(root, "saved.bin"));
            using var restarted = CreateIngress(root, new(TimeProvider.System));
            var target = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single().Target;
            using var setup = await OpenJournalAsync(root).ConfigureAwait(false);
            await ExecuteTestSqlAsync(setup, "UPDATE capture_lane_work SET state = 'leased', lease_token = 'lease', lease_owner = 'worker', lease_expires_unix_ms = 9999999999999;").ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => restarted.ReleaseEvidenceAsync(target,
                RawEvidenceReleaseAction.WriteOff, "loss", "owner", "irrecoverable-evidence", CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => restarted.ReleaseEvidenceAsync(target,
                RawEvidenceReleaseAction.WriteOff, "loss", "owner", "evidence-restored", CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(setup, "SELECT COUNT(*) FROM raw_capture_stage_events WHERE stage_key GLOB 'raw-evidence*';").ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }
    private static async Task<Dictionary<string, byte[]>> SnapshotEvidenceFilesAsync(string root, string pattern)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var path in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
            files.Add(path, await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        return files;
    }

    private sealed class EvidenceRetryPause : IRawIngressFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim _continue = new(false);
        private int _armed;
        private int _paused;
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _continue.Set();
        public void Inject(RawIngressFaultPoint point)
        {
            if (point != RawIngressFaultPoint.BeforeAcceptanceLifecycleLock || Volatile.Read(ref _armed) == 0 ||
                Interlocked.Exchange(ref _paused, 1) != 0) return;
            Entered.Set();
            if (!_continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Retry pause was not released.");
        }
        public void Dispose()
        {
            _continue.Set();
            _continue.Dispose();
            Entered.Dispose();
        }
    }

}
