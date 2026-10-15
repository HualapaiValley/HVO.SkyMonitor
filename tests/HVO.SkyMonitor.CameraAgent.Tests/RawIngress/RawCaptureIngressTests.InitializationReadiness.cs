using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedStrictAndOperationsCallersPreserveReadinessAndRecoveredEvidence(bool missingEvidence)
    {
        var root = CreateRoot();
        try
        {
            var submission = CreateSubmission(Timestamp(1), [1, 2, 3, 4]);
            RawCaptureReceipt receipt;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                receipt = (await original.AcceptAsync(CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false))!;
            var backup = Path.Combine(root, "retained-original.bin");
            if (missingEvidence) File.Move(receipt.StoredFrame.AbsolutePath, backup);
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);
            IRawCaptureIngress ingress = restarted;
            for (var round = 0; round < 3; round++)
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var callers = Enumerable.Range(0, 8).Select(async index =>
                {
                    await start.Task.ConfigureAwait(false);
                    if (index % 2 == 0)
                        Assert.AreEqual(!missingEvidence, await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
                    else if (missingEvidence)
                        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ingress.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
                    else
                        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                }).ToArray();
                start.SetResult();
                await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            if (missingEvidence)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(2), [5, 6, 7, 8]), CancellationToken.None).AsTask()).ConfigureAwait(false);
                var item = (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
                Assert.AreEqual(receipt.Manifest.Descriptor.Capture.CaptureId, item.Target.CaptureId);
                File.Move(backup, receipt.StoredFrame.AbsolutePath);
                var audit = await restarted.ReleaseEvidenceAsync(item.Target, RawEvidenceReleaseAction.Revalidate,
                    "readiness-recovery", "owner", "evidence-restored", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("owner", audit.Audit.Actor);
            }
            Assert.IsTrue(await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var retry = await ingress.AcceptAsync(CreateConfiguration(), submission, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(RawIngressOutcome.Existing, retry!.Outcome);
            Assert.AreEqual(receipt.CommittedManifestSha256, retry.CommittedManifestSha256);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false));
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
            using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow("future-schema")]
    [DataRow("schema-drift")]
    [DataRow("malformed-journal")]
    public async Task OperationsInitializationDoesNotTolerateInvalidJournal(string defect)
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt receipt;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                receipt = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(1), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!;
            if (defect == "malformed-journal")
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await File.WriteAllTextAsync(Path.Combine(root, "journal", "raw-ingress.db"), "not a SQLite journal").ConfigureAwait(false);
            }
            else
            {
                using var setup = await OpenJournalAsync(root).ConfigureAwait(false);
                await ExecuteTestSqlAsync(setup, defect == "future-schema"
                    ? "PRAGMA user_version=2147483647;" : "CREATE TABLE unexpected_readiness_table(id INTEGER);").ConfigureAwait(false);
            }
            var state = new RawIngressState(TimeProvider.System);
            using var restarted = CreateIngress(root, state);
            IRawCaptureIngress ingress = restarted;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsAsync<Exception>(() => ingress.InitializeForOperationsAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
                await Assert.ThrowsAsync<Exception>(() => restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).AsTask()).ConfigureAwait(false);
                Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
                Assert.AreEqual("initialization-failed", state.Snapshot.Reason);
            }
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task OperationsReadinessHonorsCancellationWhileStrictInitializationOwnsGate()
    {
        var root = CreateRoot();
        using var schedule = new InitializationResetPause();
        using var strictCancellation = new CancellationTokenSource();
        try
        {
            using var ingress = CreateIngress(root, new(TimeProvider.System), schedule);
            var strict = Task.Run(async () => await ingress.InitializeAsync(strictCancellation.Token).ConfigureAwait(false));
            try
            {
                await schedule.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                using var operationsCancellation = new CancellationTokenSource();
                var operations = ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(operationsCancellation.Token).AsTask();
                Assert.IsFalse(operations.IsCompleted);
                await operationsCancellation.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsAsync<OperationCanceledException>(() => operations).ConfigureAwait(false);
                await strictCancellation.CancelAsync().ConfigureAwait(false);
            }
            finally { schedule.Continue.Set(); }
            await Assert.ThrowsAsync<OperationCanceledException>(() => strict).ConfigureAwait(false);
            Assert.IsTrue(await ((IRawCaptureIngress)ingress).InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task FailedRootValidationCannotReuseEarlierMissingEvidenceReadiness()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = CreateRoot();
        var retained = root + "-retained";
        try
        {
            RawCaptureReceipt receipt;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                receipt = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(1), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!;
            File.Move(receipt.StoredFrame.AbsolutePath, Path.Combine(root, "retained-original.bin"));
            using var restarted = CreateIngress(root, new(TimeProvider.System));
            IRawCaptureIngress ingress = restarted;
            Assert.IsFalse(await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            Directory.Move(root, retained);
            Directory.CreateSymbolicLink(root, retained);
            await Assert.ThrowsExactlyAsync<IOException>(() => ingress.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<IOException>(() => ingress.InitializeForOperationsAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<IOException>(() => restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Directory.Delete(root);
            Directory.Move(retained, root);
            Assert.IsFalse(await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(1, (await restarted.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Count);
        }
        finally
        {
            if (Directory.Exists(retained))
            {
                if (Directory.Exists(root)) Directory.Delete(root);
                Directory.Move(retained, root);
            }
            DeleteRoot(root);
        }
    }

    private sealed class InitializationResetPause : IRawIngressFaultInjector, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Continue { get; } = new();
        public void Inject(RawIngressFaultPoint point) { }
        public void AfterInitializationStateReset()
        {
            Entered.TrySetResult();
            if (!Continue.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Initialization schedule not released.");
        }
        public void Dispose() => Continue.Dispose();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializationReadinessCostAndStateAreBounded(bool missingEvidence)
    {
        var root = CreateRoot();
        try
        {
            RawCaptureReceipt receipt;
            using (var original = CreateIngress(root, new(TimeProvider.System)))
                receipt = (await original.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(1), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false))!;
            if (missingEvidence) File.Move(receipt.StoredFrame.AbsolutePath, Path.Combine(root, "retained-original.bin"));
            var state = new RawIngressState(TimeProvider.System);
            using var telemetry = new RawIngressTelemetry(state);
            using var restarted = new RawCaptureIngress(Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1
            }), new FixedCapacityProvider(long.MaxValue), state, TimeProvider.System, telemetry,
                NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector());
            IRawCaptureIngress ingress = restarted;
            var coldStart = Stopwatch.GetTimestamp();
            Assert.AreEqual(!missingEvidence, await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            var coldMilliseconds = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
            for (var warmup = 0; warmup < 100; warmup++)
                Assert.AreEqual(!missingEvidence, await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            var reconciliation = state.LastReconciliation;
            var checkpoints = telemetry.CheckpointCount;
            var transactions = telemetry.TransactionCount;
            var flushes = telemetry.FileFlushCount;
            var syncs = telemetry.DirectorySyncCount;
            var samples = new double[100];
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var workingSetBefore = Environment.WorkingSet;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var allReady = !missingEvidence;
            var start = Stopwatch.GetTimestamp();
            for (var batch = 0; batch < samples.Length; batch++)
            {
                var batchStart = Stopwatch.GetTimestamp();
                for (var operation = 0; operation < 100; operation++)
                {
                    var ready = await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false);
                    if (ready != !missingEvidence) allReady = ready;
                }
                samples[batch] = Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds;
            }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Array.Sort(samples);
            Assert.AreEqual(!missingEvidence, allReady);
            Assert.AreSame(reconciliation, state.LastReconciliation, "Steady readiness must not reconcile the journal again.");
            Assert.AreEqual(checkpoints, telemetry.CheckpointCount);
            Assert.AreEqual(transactions, telemetry.TransactionCount);
            Assert.AreEqual(flushes, telemetry.FileFlushCount);
            Assert.AreEqual(syncs, telemetry.DirectorySyncCount);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                MissingEvidence = missingEvidence,
                Operations = 10000,
                BatchSize = 100,
                ColdMilliseconds = coldMilliseconds,
                ElapsedMilliseconds = elapsed,
                MedianBatchMilliseconds = samples[50],
                P95BatchMilliseconds = samples[94],
                AllocatedBytes = allocated,
                CpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                WorkingSetBefore = workingSetBefore,
                WorkingSetAfter = Environment.WorkingSet,
                AdditionalCheckpoints = telemetry.CheckpointCount - checkpoints,
                AdditionalTransactions = telemetry.TransactionCount - transactions,
                AdditionalFileFlushes = telemetry.FileFlushCount - flushes,
                AdditionalDirectorySyncs = telemetry.DirectorySyncCount - syncs,
                Health = state.Snapshot.Availability.ToString(),
                state.Snapshot.PendingCount,
                state.Snapshot.PendingBytes
            }));
        }
        finally { DeleteRoot(root); }
    }
}
