using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task FullPlanBudgetRetainsIndependentCountsAndTheSizeReasonAcrossRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-budget");
        Guid id;
        try
        {
            using (var provider = CreateProvider(root))
            {
                var configuration = CreateConfiguration() with
                {
                    Pipeline = new CapturePipelineConfig([new("Preview", "preview", DependsOn: ["$raw"])],
                        CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
                };
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var receipt = (await ingress.AcceptAsync(configuration, CreateSubmission(), CancellationToken.None).ConfigureAwait(false))!;
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var bytes = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
                using var plan = JsonDocument.Parse(bytes);
                var primary = new Dictionary<string, JsonElement>();
                foreach (var property in plan.RootElement.EnumerateObject())
                    if (property.Name is not "frozenOutputInputCounts" and not "committedOutputCounts")
                        primary.Add(property.Name, property.Value.Clone());
                primary.Add("padding", JsonSerializer.SerializeToElement(""));
                var json = JsonSerializer.Serialize(primary);
                primary["padding"] = JsonSerializer.SerializeToElement(new string('x', 2 * 1024 * 1024 - Encoding.UTF8.GetByteCount(json)));
                var full = JsonSerializer.SerializeToUtf8Bytes(primary);
                Assert.AreEqual(2 * 1024 * 1024, full.Length);
                await WriteFrozenExecutionJsonAsync(root, id, full).ConfigureAwait(false);
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                var completed = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
                CollectionAssert.AreEqual(full, completed);
                Assert.AreEqual(0, await ReadManifestCounterAsync(root, id, "frozenOutputInputCounts", "preview").ConfigureAwait(false));
                Assert.AreEqual(1, await ReadManifestCounterAsync(root, id, "committedOutputCounts", "preview").ConfigureAwait(false));
                Assert.AreEqual("Completed", (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!.Execution.Status.ToString());
                Assert.IsNotNull(await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false));
                await RunManifestTestAssertions.ResponseAsync(operations, id, ProcessingRunManifestBounds.UnavailableReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.IsNotNull(await recovered.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(1, await ReadManifestCounterAsync(root, id, "committedOutputCounts", "preview").ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(recovered, id, ProcessingRunManifestBounds.UnavailableReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NormalizationFreezeUsesFullPrimaryBudgetIndependentlyOfManifestCounts()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-normalization-budget");
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var provider = CreateProvider(root, PlanSettings, clock);
            var fixture = await CreatePlanFixtureAsync(provider, root, clock).ConfigureAwait(false);
            var calibration = provider.GetRequiredService<SqliteCalibrationLibraryStore>();
            var original = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);
            var plan = PlanFor(fixture.Inputs, fixture.Bundle, 0);
            await fixture.Store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None).ConfigureAwait(false);
            var frozen = await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false);
            // Derive padding from the exact primary JSON the freezer emits, leaving one byte of headroom.
            using var document = JsonDocument.Parse(frozen);
            Assert.IsFalse(document.RootElement.TryGetProperty("frozenOutputInputCounts", out _));
            var primaryBytes = frozen.Length;
            var padding = new string('x', 2 * 1024 * 1024 - primaryBytes - Encoding.UTF8.GetByteCount(",\"padding\":\"\"") - 1);
            var padded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original)[..^1] + ",\"padding\":\"" + padding + "\"}");
            await WriteFrozenExecutionJsonAsync(root, fixture.ExecutionId, padded).ConfigureAwait(false);
            var tooLarge = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(padded)[..^1] + ",\"extra\":\"too large\"}");
            await WriteFrozenExecutionJsonAsync(root, fixture.ExecutionId, tooLarge).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await fixture.Store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            CollectionAssert.AreEqual(tooLarge, await ReadFrozenExecutionJsonAsync(root, fixture.ExecutionId).ConfigureAwait(false));
            await WriteFrozenExecutionJsonAsync(root, fixture.ExecutionId, padded).ConfigureAwait(false);
            await fixture.Store.FreezeNormalizationPlanAsync(calibration, fixture.Context, PlanNode, plan, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(await fixture.Store.ReadNormalizationPlanAsync(fixture.ExecutionId, PlanNode, CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(0, await ReadManifestCounterAsync(root, fixture.ExecutionId, "frozenOutputInputCounts", PlanNode).ConfigureAwait(false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryEvidenceUsesDenseOrdinalsWhileBothManifestReadsKeepStoredOrdinals(bool replay)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-ordinals");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            if (replay)
            {
                var live = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                id = (await ExecuteReplayAsync(provider, new(live.Execution.CaptureId, live.Execution.GraphRevisionId,
                    live.Execution.PrimaryArtifactId), "manifest-ordinals").ConfigureAwait(false)).Execution.ExecutionId;
            }
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE processing_execution_outputs SET output_ordinal=3 WHERE execution_id=$id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            var ordinary = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(0, ordinary.Nodes.Single().Outputs.Single().Ordinal);
            var combined = (await operations.ReadExecutionDetailWithRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(0, combined.Detail.Nodes.Single().Outputs.Single().Ordinal);
            Assert.AreEqual(3, combined.RunManifest!.Nodes.Single().Outputs.Single().Ordinal);
            Assert.AreEqual(3, (await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!.Nodes.Single().Outputs.Single().Ordinal);
            foreach (var consumer in replay ? new[] { "api", "ui", "replay", "export" } : new[] { "api", "ui", "export" })
            {
                using var body = JsonDocument.Parse(await ReadManifestJournalConsumerAsync(root, operations, id, consumer, false).ConfigureAwait(false));
                var json = body.RootElement;
                var nodes = json.ValueKind == JsonValueKind.Array ? json
                    : json.TryGetProperty("nodes", out var camelNodes) ? camelNodes : json.GetProperty("Nodes");
                var node = nodes[0];
                var outputs = node.TryGetProperty("outputs", out var camelOutputs) ? camelOutputs : node.GetProperty("Outputs");
                var output = outputs[0];
                Assert.AreEqual(0, (output.TryGetProperty("ordinal", out var camelOrdinal) ? camelOrdinal : output.GetProperty("Ordinal")).GetInt32(), consumer);
            }
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            // Emulate the retained maximum after three earlier output associations were pruned.
            await ReplaceManifestCounterAsync(root, id, "committedOutputCounts", "{\"preview\":4}").ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
