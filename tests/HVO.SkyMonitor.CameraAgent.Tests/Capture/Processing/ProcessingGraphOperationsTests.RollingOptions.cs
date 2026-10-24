using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task RollingCenteredEditorRequiresAllRetainedPositionsAndRejectsLiveActivation(int size)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var baseline = CreateConfiguration();
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig([new("RollingCombination", "rolling", 17, DependsOn: ["$raw"])])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "centered", "2");
            model.Nodes[0].OptionsForm!.Set("windowSize", size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            model.Nodes[0].OptionsForm!.Set("windowKind", "Centered");
            Assert.IsTrue(model.TryBuild(out var pipeline, out _));
            var draft = await operations.CreateRevisionAsync("centered", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var captures = new List<RawCaptureReceipt>();
            for (var index = 0; index < size - 1; index++)
                captures.Add((await ingress.AcceptAsync(config, CreateSubmission(index, CameraPixelFormat.Mono16), CancellationToken.None).ConfigureAwait(false))!);
            var center = captures[size / 2];
            var submission = new ProcessingReplaySubmission(center.Manifest.Descriptor.Capture.CaptureId,
                draft.RevisionId, center.Manifest.Descriptor.Artifact.ArtifactId);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.SubmitReplayAsync(submission, "incomplete", "test", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            captures.Add((await ingress.AcceptAsync(config, CreateSubmission(size - 1, CameraPixelFormat.Mono16), CancellationToken.None).ConfigureAwait(false))!);
            var replay = await operations.SubmitReplayAsync(submission, "complete", "test", CancellationToken.None).ConfigureAwait(false);
            var frozen = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                .ReadFrozenReplayRawInputsAsync(replay.Execution.ExecutionId, "rolling", CancellationToken.None).ConfigureAwait(false);
            CollectionAssert.AreEqual(Enumerable.Range(-(size / 2), size).ToArray(), frozen.Select(static input => input.WindowPosition).ToArray());
            CollectionAssert.AreEqual(captures.Select(static capture => capture.Manifest.Descriptor.Artifact.ArtifactId).ToArray(),
                frozen.Select(static input => input.Descriptor.Artifact.ArtifactId).ToArray());
            await operations.CancelReplayAsync(replay.Execution.ExecutionId, "cancel", "test", null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("maximumIntegrationMilliseconds", "1000.5")]
    [DataRow("maximumAgeMilliseconds", "1999.5")]
    public async Task RollingEditedLimitsRequireActivationAndRollbackReconstructsRetainedWindows(string key, string limit)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-rolling-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var baseline = CreateConfiguration();
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig([new("RollingCombination", "rolling", 17,
                    JsonSerializer.SerializeToElement(new { WindowSize = 3, OutputVariant = "kept", Recipe = (string?)null, future = new { keep = 17 } }), ["$raw"])])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            async Task<ProcessingGraphExecutionDetail> Capture(int sequence)
            {
                var receipt = await ingress.AcceptAsync(config,
                    CreateSubmission(sequence, CameraPixelFormat.Mono16, new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero)),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, config).ConfigureAwait(false);
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                return (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
            }
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var warmup = await Capture(0).ConfigureAwait(false);
            var warmupOutput = (await store.ReadNodeAsync(warmup.Execution.CaptureId, "rolling", CancellationToken.None).ConfigureAwait(false))!.Outputs.Single();
            Assert.AreEqual(TimeSpan.FromSeconds(1), warmupOutput.TotalIntegration);
            await Capture(1).ConfigureAwait(false);
            var original = await Capture(2).ConfigureAwait(false);
            var originalOutput = (await store.ReadNodeAsync(original.Execution.CaptureId, "rolling", CancellationToken.None).ConfigureAwait(false))!.Outputs.Single();
            Assert.AreEqual(TimeSpan.FromSeconds(3), originalOutput.TotalIntegration);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "rolling", "2");
            model.Nodes[0].OptionsForm!.Set(key, limit);
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            var draft = await operations.CreateRevisionAsync("rolling", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await Capture(3).ConfigureAwait(false)).Execution.GraphRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await Capture(4).ConfigureAwait(false);
            var changedOutput = (await store.ReadNodeAsync(changed.Execution.CaptureId, "rolling", CancellationToken.None).ConfigureAwait(false))!.Outputs.Single();
            Assert.AreEqual(draft.RevisionId, changed.Execution.GraphRevisionId);
            Assert.AreEqual(TimeSpan.FromSeconds(1), changedOutput.TotalIntegration);
            Assert.HasCount(1, changedOutput.Descriptor!.Artifact.SourceArtifactIds);
            Assert.AreEqual("kept", changed.Nodes.Single().Outputs.Single().Variant);
            Assert.AreNotEqual(Sha256OfFile(Path.Combine(root, originalOutput.PayloadRelativePath)),
                Sha256OfFile(Path.Combine(root, changedOutput.PayloadRelativePath)));
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(config.Pipeline.Steps[0].Options!.Value.GetRawText(), retained!.Steps[0].Options!.Value.GetRawText());
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await Capture(5).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Execution.GraphRevisionId);
            Assert.AreEqual(TimeSpan.FromSeconds(3), (await store.ReadNodeAsync(restored.Execution.CaptureId, "rolling", CancellationToken.None).ConfigureAwait(false))!.Outputs.Single().TotalIntegration);
            Console.WriteLine($"{key}: warmup=1s; original=3s; edited=1s; rollback=3s; old={initial.ActiveRevisionId}; new={draft.RevisionId}; payload={Sha256OfFile(Path.Combine(root, changedOutput.PayloadRelativePath))}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RollingPanelMatchesRuntimeDefaultsAndLeavesMixedGainOpaque()
    {
        var descriptor = RollingCombinationOptionDescriptors.Descriptor;
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
        var runtime = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new RollingCombinationProcessingStepOptions()));
        Assert.IsTrue(runtime.IsValid);
        foreach (var option in defaults.Options)
            Assert.AreEqual(option.Value, runtime.Get(option.Descriptor.Key).Value);
        var source = JsonSerializer.SerializeToElement(new { Recipe = BuiltInProcessingRecipes.MixedGainNormalizedMean, WindowSize = 7, normalizationPlan = new { keep = 17 } });
        var pipeline = new CapturePipelineConfig([new("RollingCombination", "mixed", 17, source, ["$raw"])]);
        var model = ProcessingGraphDraftModel.FromPipeline(pipeline, "source", "rolling", "2");
        Assert.IsNull(model.Nodes.Single().OptionsForm);
        Assert.IsTrue(model.TryBuild(out var unchanged, out _));
        Assert.IsFalse(model.HasGraphChanges(unchanged));
        Assert.AreEqual(source.GetRawText(), unchanged.Steps[0].Options!.Value.GetRawText());
    }
}
