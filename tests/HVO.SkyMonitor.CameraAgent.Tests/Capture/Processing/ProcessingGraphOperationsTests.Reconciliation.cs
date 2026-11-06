using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task ReplayAdmissionDuringDuplicateDeleteComparisonCannotLoseAcceptedFrozenInput()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-replay-delete-admission");
        try
        {
            var live = await CompleteProjectedSceneLiveAsync(root, new string('7', 64), new string('6', 64))
                .ConfigureAwait(false);
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            var destinationRelative = $"processing-tombstones/{Guid.NewGuid():N}";
            var destination = Path.Combine(root, destinationRelative);
            Directory.CreateDirectory(destination);
            foreach (var relative in new[] { live.PayloadRelativePath, live.SidecarRelativePath })
                File.Copy(Path.Combine(root, relative), Path.Combine(destination, Path.GetFileName(relative)));
            var payloadHash = Sha256OfFile(Path.Combine(root, live.PayloadRelativePath));
            var sidecarHash = Sha256OfFile(Path.Combine(root, live.SidecarRelativePath));
            await store.PlanLifecycleOperationAsync(new(
                $"delete:{Guid.NewGuid():N}", "delete", live.OutputIdentity,
                live.PayloadRelativePath, live.SidecarRelativePath, destinationRelative, "crash-recovery", 0),
                CancellationToken.None).ConfigureAwait(false);

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = true;
            var reconciler = new DerivedProductReconciler(root, store, observationCompleted: async token =>
            {
                if (!first) return;
                first = false;
                reached.SetResult();
                await resume.Task.WaitAsync(token).ConfigureAwait(false);
            });
            var commandCount = CountRows(root, "processing_graph_commands");
            var rawPins = ReadRawPinEvidence(root);
            ProcessingReplaySubmissionResult? accepted = null;
            ProcessingGraphStoreConflictException? rejected = null;
            var pass = reconciler.RunAsync(CancellationToken.None).AsTask();
            try
            {
                // This is the real duplicate comparison callback, after hashing and before lock reacquisition.
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Assert.HasCount(1, ReadProjectedSceneOutputs(root, live.CaptureId), "the output is still Available");
                Assert.AreEqual(payloadHash, Sha256OfFile(Path.Combine(destination, Path.GetFileName(live.PayloadRelativePath))));
                Assert.AreEqual(sidecarHash, Sha256OfFile(Path.Combine(destination, Path.GetFileName(live.SidecarRelativePath))));
                try
                {
                    accepted = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().SubmitReplayAsync(
                        new ProcessingReplaySubmission(live.CaptureId, live.RevisionId, live.ArtifactId),
                        "duplicate-delete-replay", "reconciliation-test", CancellationToken.None).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    Assert.HasCount(1, ReadOutputPins(root, accepted.Execution.ExecutionId, "projected-scene"));
                }
                catch (ProcessingGraphStoreConflictException exception)
                {
                    rejected = exception;
                }
                Assert.IsFalse(pass.IsCompleted, "admission must finish inside the unlocked comparison window");
            }
            finally
            {
                resume.TrySetResult();
                _ = await pass.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }

            Assert.IsEmpty((await store.ReadActionableLifecyclePageAsync(null, 16, CancellationToken.None)
                .ConfigureAwait(false)).Items, "delete recovery and tombstone cleanup completed");
            Assert.IsFalse(Directory.Exists(destination));
            if (accepted is not null)
            {
                // A successful admission is a durable promise, including after recovery removes its last copy.
                Assert.HasCount(1, ReadOutputPins(root, accepted.Execution.ExecutionId, "projected-scene"),
                    "An ACCEPTED replay lost its frozen input pin during duplicate-delete recovery.");
                Assert.HasCount(1, ReadProjectedSceneOutputs(root, live.CaptureId));
                Assert.AreEqual(payloadHash, Sha256OfFile(Path.Combine(root, live.PayloadRelativePath)));
                Assert.AreEqual(sidecarHash, Sha256OfFile(Path.Combine(root, live.SidecarRelativePath)));
            }
            else
            {
                Assert.IsNotNull(rejected);
                AssertReplayAdmissionRolledBack(root, commandCount, rawPins);
                Assert.IsEmpty(ReadProjectedSceneOutputs(root, live.CaptureId));
                Assert.IsFalse(File.Exists(Path.Combine(root, live.PayloadRelativePath)));
                Assert.IsFalse(File.Exists(Path.Combine(root, live.SidecarRelativePath)));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("planned", false)]
    [DataRow("moved", false)]
    [DataRow("database-completed", true)]
    [DataRow("files-deleted", true)]
    public async Task ReplayPinAdmissionOnlyRejectsDeletePhasesThatCanStillRemoveOutput(string phase, bool admits)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-replay-delete-phase");
        try
        {
            var live = await CompleteProjectedSceneLiveAsync(root, new string('7', 64), new string('6', 64))
                .ConfigureAwait(false);
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            var operationId = $"delete:{Guid.NewGuid():N}";
            var destinationRelative = $"processing-tombstones/{Guid.NewGuid():N}";
            await store.PlanLifecycleOperationAsync(new(
                operationId, "delete", live.OutputIdentity, live.PayloadRelativePath,
                live.SidecarRelativePath, destinationRelative, "phase-control", 0), CancellationToken.None)
                .ConfigureAwait(false);
            await store.SetLifecyclePhaseAsync(operationId, phase, CancellationToken.None).ConfigureAwait(false);
            var commandCount = CountRows(root, "processing_graph_commands");
            var rawPins = ReadRawPinEvidence(root);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var submission = new ProcessingReplaySubmission(live.CaptureId, live.RevisionId, live.ArtifactId);
            if (!admits)
            {
                _ = await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.SubmitReplayAsync(submission, "delete-phase-replay", "reconciliation-test",
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                AssertReplayAdmissionRolledBack(root, commandCount, rawPins);
            }
            else
            {
                // A recreated valid output can coexist with the previous delete's tombstone-only phases.
                var payloadHash = Sha256OfFile(Path.Combine(root, live.PayloadRelativePath));
                var sidecarHash = Sha256OfFile(Path.Combine(root, live.SidecarRelativePath));
                if (phase == "database-completed") Directory.CreateDirectory(Path.Combine(root, destinationRelative));
                var accepted = await operations.SubmitReplayAsync(
                    submission, "delete-phase-replay", "reconciliation-test", CancellationToken.None).ConfigureAwait(false);
                _ = await new DerivedProductReconciler(root, store).RunAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(1, ReadOutputPins(root, accepted.Execution.ExecutionId, "projected-scene"));
                Assert.HasCount(1, ReadProjectedSceneOutputs(root, live.CaptureId));
                Assert.AreEqual(payloadHash, Sha256OfFile(Path.Combine(root, live.PayloadRelativePath)));
                Assert.AreEqual(sidecarHash, Sha256OfFile(Path.Combine(root, live.SidecarRelativePath)));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, destinationRelative)));
                Assert.IsEmpty((await store.ReadActionableLifecyclePageAsync(null, 16, CancellationToken.None)
                    .ConfigureAwait(false)).Items);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record RawPinEvidence(
        string ExecutionId, long CaptureRowId, string ArtifactId, bool Released, long? ReleasedUnixMs);

    private static RawPinEvidence[] ReadRawPinEvidence(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_id, raw_capture_row_id, artifact_id, released_flag, released_unix_ms
            FROM processing_execution_input_pins ORDER BY execution_id, artifact_id;
            """;
        using var reader = command.ExecuteReader();
        var pins = new List<RawPinEvidence>();
        while (reader.Read())
            pins.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetInt64(3) == 1, reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        return pins.ToArray();
    }

    private static void AssertReplayAdmissionRolledBack(string root, long commandCount, RawPinEvidence[] rawPins)
    {
        Assert.AreEqual(0L, CountRows(root, "replay_executions"));
        Assert.AreEqual(0L, CountRows(root, "processing_replay_work"));
        Assert.AreEqual(0L, CountRows(root, "processing_execution_output_input_pins"));
        CollectionAssert.AreEqual(rawPins, ReadRawPinEvidence(root),
            "Rejected admission must preserve raw-pin identities, ownership and released history.");
        Assert.AreEqual(commandCount, CountRows(root, "processing_graph_commands"));
    }
}
