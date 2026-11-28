using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task ReplayAdmissionWaitsForSiloRetentionBeforeCommittingPins()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-replay-retention");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            var live = await CompleteProjectedSceneLiveAsync(root, new string('7', 64), new string('6', 64))
                .ConfigureAwait(false);
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var storage = provider.GetRequiredService<SiloInstalledStorage>();
            var submission = new ProcessingReplaySubmission(live.CaptureId, live.RevisionId, live.ArtifactId);
            var lifecycle = RawIngressLifecycleLock.ForRoot(root);
            var commandCount = CountRows(root, "processing_graph_commands");
            var rawPins = ReadRawPinEvidence(root);
            Task<ProcessingReplaySubmissionResult> pending;
            await lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                pending = operations.SubmitReplayAsync(submission, "retention-race", "retention-test", token).AsTask();
                // Simulate expiry committing its delete fence while replay is preparing its inputs.
                var locator = await SiloInfrastructureFixture.ReadLocalLocatorAsync(provider,
                    ProcessingIdentity.CreateArtifactId(live.OutputIdentity), live.OutputIdentity, token).ConfigureAwait(false);
                var state = SiloRetentionState.Read(storage.PhysicalState);
                await (state with { Attempt = new("expiry-test", locator, [], [], default) })
                    .WriteAsync(storage.PhysicalState, token).ConfigureAwait(false);
                AssertReplayAdmissionRolledBack(root, commandCount, rawPins);
            }
            finally
            {
                lifecycle.Release();
            }

            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                Assert.Fail("Replay accepted an input fenced for deletion.");
            }
            catch (Exception exception) when (exception is SiloStoreUnavailableException or ProcessingGraphStoreConflictException)
            {
                AssertReplayAdmissionRolledBack(root, commandCount, rawPins);
            }

            // Retention released the fence without deleting the version. The same command can now pin it.
            var current = SiloRetentionState.Read(storage.PhysicalState);
            await (current with { Attempt = null }).WriteAsync(storage.PhysicalState, token).ConfigureAwait(false);
            var accepted = await operations.SubmitReplayAsync(submission, "retention-race", "retention-test", token)
                .ConfigureAwait(false);
            Assert.HasCount(1, ReadOutputPins(root, accepted.Execution.ExecutionId, "projected-scene"));
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
