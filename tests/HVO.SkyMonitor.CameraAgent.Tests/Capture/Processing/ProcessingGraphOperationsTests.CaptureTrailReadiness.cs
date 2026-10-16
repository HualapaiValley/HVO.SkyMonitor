using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task CaptureTrailQueryPreservesUnhealthyRecoveryAndRecoversAfterAuditedEvidenceRestore()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-trail-readiness");
        using var schedule = new InitializationReadinessSchedule();
        try
        {
            var configuration = CreateConfiguration();
            var submission = CreateSubmission();
            RawCaptureReceipt receipt;
            using (var original = CreateProvider(root, configureServices:
                       services => services.RemoveAll<ProcessingGraphOperationsCoordinator>()))
            {
                receipt = (await original.GetRequiredService<IRawCaptureIngress>().AcceptAsync(
                    configuration, submission, CancellationToken.None).ConfigureAwait(false))!;
            }
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
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
            var projection = provider.GetRequiredService<ICameraAgentTransientOperatorProjection>();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                ingress.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);

            // Exercise the actual overlay caller during the controlled readiness/reset interleaving.
            schedule.Arm();
            var operations = Task.Run(async () =>
                await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            Task<InvalidDataException>? trailRead = null;
            bool ready;
            try
            {
                await schedule.OperationsEntered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                trailRead = Task.Run(async () => await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    projection.GetCaptureTrailsAsync(captureId, CancellationToken.None).AsTask()).ConfigureAwait(false));
                await schedule.ResetEntered.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                schedule.OperationsContinue.Set();
                ready = await operations.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            finally
            {
                schedule.OperationsContinue.Set();
                schedule.ResetContinue.Set();
                await operations.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (trailRead is not null) await trailRead.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            Assert.IsNotNull(await trailRead!.ConfigureAwait(false));
            Assert.IsFalse(ready, "A concurrent real trail query must not make missing evidence ready.");
            Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                ingress.BindRecoveredLiveExecutionsAsync(configuration, CancellationToken.None).AsTask()).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                ingress.AcceptAsync(configuration, CreateSubmission(sequenceOffset: 1), CancellationToken.None).AsTask()).ConfigureAwait(false);
            await AssertTrailRecoveryJournalAsync(root, missingEvidence: true, expectedExecutions: 0).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(payload));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(backup).ConfigureAwait(false));

            var evidenceOperations = provider.GetRequiredService<IRawEvidenceOperations>();
            var evidence = (await evidenceOperations.ReadEvidencePageAsync(10, null, CancellationToken.None)
                .ConfigureAwait(false)).Items.Single();
            Assert.AreEqual(captureId, evidence.Target.CaptureId);
            File.Move(backup, payload);
            var audit = await evidenceOperations.ReleaseEvidenceAsync(evidence.Target, RawEvidenceReleaseAction.Revalidate,
                "capture-trail-readiness", "issue-1005-test", "evidence-restored", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual("issue-1005-test", audit.Audit.Actor);
            Assert.IsTrue(await ingress.InitializeForOperationsAsync(CancellationToken.None).ConfigureAwait(false));
            var trails = await projection.GetCaptureTrailsAsync(captureId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(captureId, trails.CaptureId);
            Assert.AreEqual("NoRecordedCandidate", trails.State);
            Assert.IsEmpty(trails.Trails);
            Assert.IsFalse(trails.Incomplete);
            Assert.IsNull(trails.Reason);
            Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);

            // Recovery binds exactly once; the read must not acknowledge work or manufacture a candidate.
            var graphOperations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await graphOperations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            await ingress.BindRecoveredLiveExecutionsAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            await ingress.BindRecoveredLiveExecutionsAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var executions = await graphOperations.ReadExecutionsAsync(
                ProcessingGraphExecutionClass.Live, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, executions);
            Assert.AreEqual(captureId, executions[0].CaptureId);
            var retry = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(retry);
            Assert.AreEqual(RawIngressOutcome.Existing, retry.Outcome);
            Assert.AreEqual(receipt.CommittedManifestSha256, retry.CommittedManifestSha256);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 },
                await File.ReadAllBytesAsync(payload).ConfigureAwait(false));
            await AssertTrailRecoveryJournalAsync(root, missingEvidence: false, expectedExecutions: 1).ConfigureAwait(false);
            Console.WriteLine($"Real capture trail recovery: capture={captureId:N}; resetGeneration={schedule.ResetCount}; unhealthyReady={ready}; restoredState={trails.State}; retry={retry.Outcome}; manifest={retry.CommittedManifestSha256}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertTrailRecoveryJournalAsync(string root, bool missingEvidence, long expectedExecutions)
    {
        using var journal = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await journal.OpenAsync().ConfigureAwait(false);
        using var command = journal.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_captures;";
        Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        if (missingEvidence)
        {
            command.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE state='missing_evidence' AND retention_hold=1;";
        }
        else
        {
            command.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE state='committed';";
        }
        Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state IN ('leased', 'completed') OR completed_unix_ms IS NOT NULL;";
        Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='processing_executions';";
        var hasExecutions = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))! != 0;
        long executions = 0;
        if (hasExecutions)
        {
            command.CommandText = "SELECT COUNT(*) FROM processing_executions;";
            executions = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        }
        Assert.AreEqual(expectedExecutions, executions);
    }
}
