using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class MixedGainRollingWindowEndToEndTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public Task ArchivedReplayUsesTheCompletedArchivedProducerWhenLiveProcessingSkippedTheNode()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            var live = await LiveExecutionAsync(run).ConfigureAwait(false);
            var original = await SubmitOriginalReplayAsync(run, live).ConfigureAwait(false);
            var detail = await RunReplayAsync(run, original.Execution.ExecutionId).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status);
            var plan = await run.Store.ReadNormalizationPlanAsync(original.Execution.ExecutionId, NodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(plan);

            // Recreate a capture whose live node skipped before a later archived job first produced this stack.
            // The completed archived job has the original plan; a non-producing live node must not mask it.
            using var connection = await OpenReplayEvidenceAsync(run).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$execution", live.ExecutionId.ToString("N"));
            command.CommandText = "SELECT frozen_plan_json FROM processing_executions WHERE execution_id = $execution;";
            var json = JsonNode.Parse((byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!)!;
            json.AsObject().Remove("normalizationPlans");
            command.CommandText = """
                UPDATE processing_executions SET frozen_plan_json = $json WHERE execution_id = $execution;
                UPDATE processing_execution_nodes SET status = 'Skipped'
                WHERE execution_id = $execution AND node_id = 'mixed-gain';
                """;
            command.Parameters.AddWithValue("$json", JsonSerializer.SerializeToUtf8Bytes(json));
            Assert.AreEqual(2, await command.ExecuteNonQueryAsync().ConfigureAwait(false));

            var replay = await SubmitOriginalReplayAsync(run, live, "reproduce-archived-original").ConfigureAwait(false);
            var copied = await run.Store.ReadNormalizationPlanAsync(replay.Execution.ExecutionId, NodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(copied);
            CollectionAssert.AreEqual(plan.CanonicalJson, copied.CanonicalJson);
        });

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("Trailing", false)]
    [DataRow("Centered", false)]
    [DataRow("Trailing", true)]
    public Task ArchivedReplayReproducesOriginalBytesAfterFamilyMastersAndConfigurationChange(string kind, bool ordinaryMean)
        => RunAsync(
            ordinaryMean ? [LowGain, LowGain, LowGain] : [LowGain, HighGain, LowGain],
            [LowGain, HighGain],
            [ordinaryMean ? MeanStep(NodeId, "$raw", 2) : MixedGainStep()],
            async run =>
            {
                var nodeId = kind == "Centered" ? "centered" : NodeId;
                var source = await LiveExecutionAsync(run).ConfigureAwait(false);
                var original = (await run.ReadNodeAsync(1, NodeId).ConfigureAwait(false)).Outputs.Single();
                if (kind == "Centered")
                {
                    var seeded = await SeedOriginalCenteredStackAsync(run).ConfigureAwait(false);
                    var completed = await RunReplayAsync(run, seeded.Execution.ExecutionId).ConfigureAwait(false);
                    source = completed.Execution;
                    original = (await run.Store.ReadOutputByArtifactIdAsync(
                        completed.Nodes.Single(node => node.NodeId == nodeId).Outputs.Single().ArtifactId, CancellationToken.None)
                        .ConfigureAwait(false))!;
                }
                var expected = await File.ReadAllBytesAsync(Path.Combine(run.Root, original.PayloadRelativePath)).ConfigureAwait(false);
                var plan = ordinaryMean ? null : await run.Store.ReadNormalizationPlanAsync(source.ExecutionId, nodeId, CancellationToken.None)
                    .ConfigureAwait(false);

                // Publish new masters and activate a different family after capture. The old masters stay retained.
                var acquisition = run.Provider.GetRequiredService<VirtualCalibrationAcquisitionCoordinator>();
                var library = run.Provider.GetRequiredService<SqliteCalibrationLibraryStore>();
                var replacements = new Dictionary<double, CalibrationLibraryBundleV1>();
                foreach (var gain in new[] { LowGain, HighGain })
                {
                    var request = AcquisitionRequest(gain) with
                    {
                        IdempotencyKey = FormattableString.Invariant($"replacement-{gain}"),
                        SourceModel = VirtualCalibration.SourceModel with { Seed = 1514 }
                    };
                    var job = await acquisition.AcquireAsync(request, CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(CalibrationAcquisitionStates.Published, job.State, job.FailureReason);
                    replacements.Add(gain, (await library.GetBundleAsync(job.BundleId!, CancellationToken.None)
                        .ConfigureAwait(false))!.Bundle);
                }
                var family = Family(run.Receipts[1].Manifest.Descriptor, replacements) with { FamilyId = "replacement-family" };
                var stored = await run.Provider.GetRequiredService<SqliteCalibrationFamilyStore>()
                    .StoreAsync(family, CancellationToken.None).ConfigureAwait(false);
                Assert.AreNotEqual(run.FamilyIdentitySha256, stored.IdentitySha256);
                Assert.IsTrue((await run.Provider.GetRequiredService<CalibrationFamilyActivationService>().ActivateAsync(
                    new CalibrationFamilyActivationRequest("replace-family",
                        new CalibrationFamilyActivationTarget(family.AgentId, family.RigId, family.RigProfileSha256,
                            family.SensorProfileSha256, family.AcquisitionModel),
                        stored.IdentitySha256, "owner-test", null), CancellationToken.None).ConfigureAwait(false)).IsAccepted);
                var changed = CreateConfiguration(run.Root, [MixedGainStep()]);
                await run.Provider.DisposeAsync().ConfigureAwait(false);
                using var restarted = CreateProvider(run.Root);
                ((CameraAgentConfigurationAccessor)restarted.GetRequiredService<ICameraAgentConfigurationAccessor>())
                    .SetConfiguration(changed with
                    {
                        Rig = changed.Rig with
                        {
                            Sensor = changed.Rig.Sensor with { SimulationResponse = null, SensorRecipeVersion = "changed-sensor" },
                            Pipeline = changed.Rig.Pipeline with { DayGain = 300, NightGain = 300 }
                        }
                    });

                await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                await restarted.GetRequiredService<CaptureAdmissionCoordinator>().InitializeAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                _ = await restarted.GetRequiredService<SqliteCalibrationLibraryStore>().InitializeAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var restartedRun = run with { Provider = restarted };
                var replay = await SubmitOriginalReplayAsync(restartedRun, source).ConfigureAwait(false);
                if (plan is not null)
                {
                    var copied = await restartedRun.Store.ReadNormalizationPlanAsync(replay.Execution.ExecutionId, nodeId, CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(copied);
                    CollectionAssert.AreEqual(plan.CanonicalJson, copied.CanonicalJson);
                    Assert.AreEqual(plan.IdentitySha256, copied.IdentitySha256);
                    CollectionAssert.AreEqual(
                        plan.Plan.Inputs.Select(static input => input.ArtifactId).ToArray(),
                        (await restartedRun.Store.ReadFrozenReplayRawInputsAsync(replay.Execution.ExecutionId, nodeId, CancellationToken.None)
                            .ConfigureAwait(false)).Select(static input => input.Descriptor.Artifact.ArtifactId).ToArray());
                }
                var detail = await RunReplayAsync(restartedRun, replay.Execution.ExecutionId).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status, detail.Execution.FailureReason);
                var output = detail.Nodes.Single(node => node.NodeId == nodeId).Outputs.Single();
                var produced = await restartedRun.Store.ReadOutputByArtifactIdAsync(output.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(produced);
                var actual = await File.ReadAllBytesAsync(Path.Combine(run.Root, produced.PayloadRelativePath)).ConfigureAwait(false);
                CollectionAssert.AreEqual(expected, actual);
                Assert.AreEqual(original.OutputIdentitySha256, output.OutputIdentitySha256);
                Assert.AreEqual(original.RecipeIdentitySha256, produced.RecipeIdentitySha256);
            });

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("missing-plan", ProcessingReasonCodes.MissingNormalizationPlan, false)]
    [DataRow("corrupt-plan", ProcessingReasonCodes.InvalidNormalizationPlan, false)]
    [DataRow("missing-input", ProcessingReasonCodes.NormalizationPlanInputMismatch, false)]
    [DataRow("changed-node", ProcessingReasonCodes.NormalizationPlanInputMismatch, false)]
    [DataRow("changed-node", ProcessingReasonCodes.NormalizationPlanInputMismatch, true)]
    public Task ArchivedReplayRejectsDamagedOriginalEvidence(string damage, string expectedReason, bool completedPriorReplay)
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            var live = await LiveExecutionAsync(run).ConfigureAwait(false);
            if (completedPriorReplay)
            {
                var prior = await SubmitOriginalReplayAsync(run, live, "completed-prior-replay").ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed,
                    (await RunReplayAsync(run, prior.Execution.ExecutionId).ConfigureAwait(false)).Execution.Status);
            }
            using var connection = await OpenReplayEvidenceAsync(run).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$execution", live.ExecutionId.ToString("N"));
            if (damage is "missing-plan" or "corrupt-plan")
            {
                command.CommandText = "SELECT frozen_plan_json FROM processing_executions WHERE execution_id = $execution;";
                var json = JsonNode.Parse((byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!)!;
                if (damage == "missing-plan")
                    json.AsObject().Remove("normalizationPlans");
                else
                    json["normalizationPlans"]![0]!["planIdentitySha256"] = new string('0', 64);
                command.CommandText = "UPDATE processing_executions SET frozen_plan_json = $json WHERE execution_id = $execution;";
                command.Parameters.AddWithValue("$json", JsonSerializer.SerializeToUtf8Bytes(json));
            }
            else if (damage == "missing-input")
            {
                command.CommandText = "DELETE FROM processing_execution_inputs WHERE execution_id = $execution AND node_id = 'mixed-gain' AND input_ordinal = 0;";
            }
            else
            {
                command.CommandText = "UPDATE processing_execution_nodes SET plan_sha256 = '0000000000000000000000000000000000000000000000000000000000000000' WHERE execution_id = $execution AND node_id = 'mixed-gain';";
            }
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            var exception = await Assert.ThrowsExactlyAsync<NormalizationPlanException>(
                async () => await SubmitOriginalReplayAsync(run, live).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(expectedReason, exception.ReasonCode);
            Assert.AreEqual(completedPriorReplay ? 1 : 0,
                (await run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().ReadExecutionsAsync(
                    ProcessingGraphExecutionClass.Replay, 10, CancellationToken.None).ConfigureAwait(false)).Count);
        });

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(false, CalibrationFamilyReasonCodes.MasterMissing)]
    [DataRow(true, CalibrationFamilyReasonCodes.MasterCorrupt)]
    public Task ArchivedReplayFailsExplicitlyWhenAnOriginalMasterIsMissingOrDamaged(bool corrupt, string expectedReason)
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            var live = await LiveExecutionAsync(run).ConfigureAwait(false);
            var replay = await SubmitOriginalReplayAsync(run, live).ConfigureAwait(false);
            var frozen = await run.Store.ReadNormalizationPlanAsync(replay.Execution.ExecutionId, NodeId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            var master = LibraryMasters(run.Bundles[LowGain]).First();
            var path = Path.Combine(run.Root, Path.ChangeExtension(master.ManifestRelativePath, ".bin"));
            if (corrupt)
            {
                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                bytes[0] ^= 1;
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            }
            else
            {
                File.Delete(path);
            }
            var detail = await RunReplayAsync(run, replay.Execution.ExecutionId).ConfigureAwait(false);
            var node = detail.Nodes.Single(static node => node.NodeId == NodeId);
            Assert.AreEqual("TerminalFailure", node.Status, node.Reason);
            Assert.AreEqual(expectedReason, node.Reason);
            Assert.IsEmpty(node.Outputs);
            CollectionAssert.AreEqual(frozen.CanonicalJson,
                (await run.Store.ReadNormalizationPlanAsync(replay.Execution.ExecutionId, NodeId, CancellationToken.None)
                    .ConfigureAwait(false))!.CanonicalJson);
        });

    private static async Task<ProcessingGraphExecutionState> LiveExecutionAsync(Run run)
        => (await run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().ReadExecutionsAsync(
                ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false))
            .Single(state => state.CaptureId == run.CaptureId(1));

    // Centered windows are archived-only. Seed the original durable slice-F execution before G required an archived
    // plan at submission; then freeze and execute it through the real freezer, worker and recipe. This is fixture
    // construction for retained evidence, not a production path that synthesizes a missing replay plan.
    private static async Task<ProcessingReplaySubmissionResult> SeedOriginalCenteredStackAsync(Run run)
    {
        var operations = run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var state = await operations.CreateRevisionAsync("centered-mixed-gain", "v1",
            Pipeline(run.Root, [MixedGainStep(3, "centered", "Centered")]),
            "centered-create-key", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
        _ = await operations.ValidateRevisionAsync(
            state.RevisionId, "centered-validate-key", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
        var revision = await run.Store.ReadRevisionAsync(state.RevisionId, CancellationToken.None).ConfigureAwait(false);
        var source = await run.Store.ReadReplaySourceAsync(run.CaptureId(1), null, CancellationToken.None).ConfigureAwait(false);
        var replay = await run.Store.InsertReplayAsync(
            new ProcessingReplaySubmission(run.CaptureId(1), state.RevisionId), source,
            revision with { Nodes = [.. revision.Nodes.Select(node => node with { WindowJson = null })] },
            Guid.NewGuid(), Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(
                source.Configuration with { Pipeline = revision.Pipeline })).GetRawText()),
            "centered-original", "owner-test", CancellationToken.None).ConfigureAwait(false);
        using (var connection = await OpenReplayEvidenceAsync(run).ConfigureAwait(false))
        {
            using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM processing_execution_inputs WHERE execution_id = $execution AND node_id = 'centered';";
            remove.Parameters.AddWithValue("$execution", replay.Execution.ExecutionId.ToString("N"));
            _ = await remove.ExecuteNonQueryAsync().ConfigureAwait(false);
            for (var ordinal = 0; ordinal < run.Receipts.Count; ordinal++)
            {
                var descriptor = run.Receipts[ordinal].Manifest.Descriptor;
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO processing_execution_inputs(execution_id, node_id, input_ordinal, window_position,
                        capture_id, artifact_id, descriptor_sha256, payload_sha256, selected_flag)
                    SELECT $execution, 'centered', $ordinal, $position, capture_id, raw_artifact_id,
                           descriptor_sha256, payload_sha256, 1
                    FROM raw_captures WHERE raw_artifact_id = $artifact;
                    INSERT OR IGNORE INTO processing_execution_input_pins(execution_id, raw_capture_row_id, artifact_id)
                    SELECT $execution, raw_capture_row_id, raw_artifact_id
                    FROM raw_captures WHERE raw_artifact_id = $artifact;
                    """;
                insert.Parameters.AddWithValue("$execution", replay.Execution.ExecutionId.ToString("N"));
                insert.Parameters.AddWithValue("$ordinal", ordinal);
                insert.Parameters.AddWithValue("$position", ordinal - 1);
                insert.Parameters.AddWithValue("$artifact", descriptor.Artifact.ArtifactId.ToString("N"));
                _ = await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        var lease = await run.Store.ClaimReplayAsync("centered-original", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var context = new ProcessingExecutionContext(lease.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
            lease.Execution.GraphRevisionId, lease.Execution.LocalPlanIdentitySha256, false, lease.WorkId,
            lease.LeaseToken, lease.LeaseOwner, lease.Execution.DeadlineUtc, lease.ClaimCount);
        var selection = await run.Provider.GetRequiredService<ActiveCalibrationFamilySelector>().SelectAsync(
            source.Configuration, source.RawCapture.Manifest.Descriptor, CancellationToken.None).ConfigureAwait(false);
        _ = await run.Provider.GetRequiredService<NormalizationPlanFreezer>().FreezeAsync(
            context, "centered", selection.FamilyJson, GainNormalizationPolicyV1.Approved, selection.Response!,
            CancellationToken.None, shape: new NormalizationWindowShape(
                ProcessingGraphWindowKind.Centered, [-1, 0, 1], 3, null, null)).ConfigureAwait(false);
        _ = await run.Store.CompleteReplayAsync(lease, CaptureLaneHandlerResult.Wait("seeded-original"), CancellationToken.None)
            .ConfigureAwait(false);
        return replay;
    }

    private static ValueTask<ProcessingReplaySubmissionResult> SubmitOriginalReplayAsync(
        Run run, ProcessingGraphExecutionState live, string idempotencyKey = "archived-replay")
        => run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().SubmitReplayAsync(
            new ProcessingReplaySubmission(run.CaptureId(1), live.GraphRevisionId,
                run.Receipts[1].Manifest.Descriptor.Artifact.ArtifactId), idempotencyKey, "owner-test", CancellationToken.None);

    private static async Task<SqliteConnection> OpenReplayEvidenceAsync(Run run)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(run.Root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }
}
