using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly string[] SelectedAnnotationCornerTokens = ["utc", "catalog"];

    [TestMethod]
    public async Task AnnotationSelectionDraftPersistsAcrossRestartActivationAndRollback()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-annotation-selections");
        try
        {
            var config = AnnotationStyleConfiguration();
            string sourceId;
            string draftId;
            string retainedSource;
            using (var provider = CreateProvider(root))
            {
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
                sourceId = initial.ActiveRevisionId;
                retainedSource = JsonSerializer.Serialize(await operations.ReadRevisionPipelineAsync(sourceId, CancellationToken.None).ConfigureAwait(false));
                var before = await CaptureAnnotationStyleAsync(provider, config, 0).ConfigureAwait(false);
                var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, sourceId, "annotation-selections", "2");
                model.Nodes[2].OptionsForm!.SetCollectionItem("topLeftTokens", 0, "utc");
                Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
                var draft = await operations.CreateRevisionAsync("annotation-selections", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
                draftId = draft.RevisionId;
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.ActivateRevisionAsync(draftId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await operations.ValidateRevisionAsync(draftId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
                var active = await operations.ActivateRevisionAsync(draftId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
                var edited = await CaptureAnnotationStyleAsync(provider, config, 1).ConfigureAwait(false);
                Assert.AreEqual(draftId, edited.Detail.Execution.GraphRevisionId);
                CollectionAssert.AreNotEqual(before.Pixels, edited.Pixels);
                await operations.RollbackRevisionAsync(sourceId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
                var restored = await CaptureAnnotationStyleAsync(provider, config, 2).ConfigureAwait(false);
                CollectionAssert.AreEqual(before.Pixels, restored.Pixels);
            }
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var reopened = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var retained = await reopened.ReadRevisionPipelineAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(retainedSource, JsonSerializer.Serialize(retained));
            var saved = await reopened.ReadRevisionPipelineAsync(draftId, CancellationToken.None).ConfigureAwait(false);
            var reload = ProcessingGraphDraftModel.FromPipeline(saved!, draftId, "annotation-selections", "3");
            CollectionAssert.AreEqual(SelectedAnnotationCornerTokens, reload.Nodes[2].OptionsForm!.Collection("topLeftTokens").Items.ToArray());
            Assert.AreEqual(sourceId, (await reopened.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
