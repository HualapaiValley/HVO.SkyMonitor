using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task MissingEvidenceReadinessCannotBeChangedByConcurrentStrictInitialization()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-readiness-race");
        using var schedule = new InitializationReadinessSchedule();
        try
        {
            RawCaptureReceipt receipt;
            using (var original = CreateProvider(root, configureServices:
                       services => services.RemoveAll<ProcessingGraphOperationsCoordinator>()))
            {
                receipt = (await original.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                    CreateConfiguration(), CreateSubmission(), CancellationToken.None).ConfigureAwait(false))!;
            }
            var payload = receipt.StoredFrame.AbsolutePath;
            var backup = Path.Combine(root, "operator-retained-original.bin");
            File.Move(payload, backup);
            using var provider = CreateProvider(root, configureServices: services =>
            {
                services.RemoveAll<IRawIngressFaultInjector>();
                services.AddSingleton<IRawIngressFaultInjector>(schedule);
            });
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            var state = provider.GetRequiredService<RawIngressState>();
            // The real journal and reconciler classify a committed capture whose payload is absent.
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ingress.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            schedule.Arm();
            var operations = Task.Run(async () => await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            Task<Exception?>? strictRead = null;
            bool ready;
            try
            {
                await schedule.OperationsEntered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                // Equivalent to GetCaptureTrailsAsync's initial strict InitializeAsync: its SQL read is unreachable on failure.
                strictRead = Task.Run(async () =>
                {
                    try { await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false); return (Exception?)null; }
                    catch (InvalidDataException exception) { return exception; }
                });
                await schedule.ResetEntered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                schedule.OperationsContinue.Set();
                ready = await operations.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            finally
            {
                schedule.OperationsContinue.Set();
                schedule.ResetContinue.Set();
                await operations.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (strictRead is not null) await strictRead.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            var strictFailure = await strictRead!.ConfigureAwait(false);
            Exception? bindingFailure = null;
            var bindingAttempts = 0;
            if (ready)
            {
                bindingAttempts++;
                try { await ingress.BindRecoveredLiveExecutionsAsync(CreateConfiguration(), CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidDataException exception) { bindingFailure = exception; }
            }
            using var journal = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await journal.OpenAsync().ConfigureAwait(false);
            using var command = journal.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE state='missing_evidence' AND retention_hold=1;";
            var missing = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
            Console.WriteLine($"Controlled schedule: operations paused before readiness; strict reader reset generation={schedule.ResetCount}; ready={ready}; health={state.Snapshot.Availability}; retained missing={missing}; strict={strictFailure}; binding={bindingFailure}");
            Assert.AreEqual(1L, missing);
            Assert.IsFalse(File.Exists(payload));
            Assert.IsTrue(File.Exists(backup));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(backup).ConfigureAwait(false));
            command.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state IN ('leased', 'completed') OR completed_unix_ms IS NOT NULL;";
            Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
            command.CommandText = "SELECT COUNT(*) FROM raw_captures;";
            Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
            Assert.IsInstanceOfType<InvalidDataException>(strictFailure);
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            Assert.IsFalse(ready, $"Missing committed evidence was reported ready; subsequent binding failed: {bindingFailure}");
            Assert.AreEqual(0, bindingAttempts);
            Assert.IsNull(bindingFailure);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InitializationReadinessSchedule : IRawIngressFaultInjector, IDisposable
    {
        private int _armed;
        private int _resetCount;
        public TaskCompletionSource OperationsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResetEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim OperationsContinue { get; } = new();
        public ManualResetEventSlim ResetContinue { get; } = new();
        public int ResetCount => Volatile.Read(ref _resetCount);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Inject(RawIngressFaultPoint point) { }
        public void BeforeOperationsReadinessDecision()
        {
            if (Volatile.Read(ref _armed) == 0) return;
            OperationsEntered.TrySetResult();
            if (!OperationsContinue.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Operations schedule not released.");
        }
        public void AfterInitializationStateReset()
        {
            Interlocked.Increment(ref _resetCount);
            if (Volatile.Read(ref _armed) == 0) return;
            ResetEntered.TrySetResult();
            if (!ResetContinue.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Strict initialization schedule not released.");
        }
        public void Dispose()
        {
            OperationsContinue.Dispose();
            ResetContinue.Dispose();
        }
    }
}
