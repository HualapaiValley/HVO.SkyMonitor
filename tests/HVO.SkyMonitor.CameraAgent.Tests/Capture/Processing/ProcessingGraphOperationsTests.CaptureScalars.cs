using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task QualityRouteDraftRequiresValidationAndActivationAndRollbackReconstructsRetainedOutput()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-quality-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([
                    new("ImageQuality", "quality", 17, JsonSerializer.SerializeToElement(new { future = new { keep = 17 } }), ["$raw"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            async Task<ProcessingGraphExecutionDetail> Capture(int sequence)
            {
                // Repeat the same retained bytes; only capture identity/time varies.
                var submission = CreateSubmission(0, capturedUtc: new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence * 2));
                var receipt = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, config).ConfigureAwait(false);
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                return (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
            }
            var original = await Capture(0).ConfigureAwait(false);
            Assert.AreEqual("image-quality-v1", original.Nodes.Single().Outputs.Single().Variant);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "quality", "2");
            model.Nodes[0].OptionsForm!.Set("outputVariant", "quality-custom");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(17, pipeline.Steps[0].Order);
            var draft = await operations.CreateRevisionAsync("quality", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await Capture(1).ConfigureAwait(false)).Execution.GraphRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await Capture(2).ConfigureAwait(false);
            Assert.AreEqual("quality-custom", changed.Nodes.Single().Outputs.Single().Variant);
            Assert.AreEqual(draft.RevisionId, changed.Execution.GraphRevisionId);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(config.Pipeline.Steps[0].Options!.Value.GetRawText(), retained!.Steps[0].Options!.Value.GetRawText());
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await Capture(3).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Execution.GraphRevisionId);
            Assert.AreEqual("image-quality-v1", restored.Nodes.Single().Outputs.Single().Variant);
            Console.WriteLine($"Original={original.Execution.GraphRevisionId}; edited={changed.Execution.GraphRevisionId}; rollback={restored.Execution.GraphRevisionId}; retained options SHA256={Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(retained.Steps[0].Options!.Value.GetRawText())))}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
