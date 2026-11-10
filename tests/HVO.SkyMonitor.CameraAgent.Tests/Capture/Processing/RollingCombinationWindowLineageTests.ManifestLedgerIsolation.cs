using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class RollingCombinationWindowLineageTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("deep", true)]
    [DataRow("deep", false)]
    [DataRow("negative", true)]
    [DataRow("negative", false)]
    [DataRow("unicode", true)]
    [DataRow("unicode", false)]
    public async Task CorruptOutputCountsCannotStaleFrozenInputEvidenceAcrossRestart(string variant, bool removeBeforeRestart)
    {
        var root = CreateRoot();
        Guid id;
        string? manifest = null;
        try
        {
            using (var provider = CreateProvider(root))
            {
                var configuration = CreateConfiguration(root, syntheticReferences: false);
                var module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
                await using var lifetime = module.ConfigureAwait(false);
                await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
                var receipt = (await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(configuration,
                    await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false))!;
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                Assert.AreEqual(0, await ProcessingGraphOperationsTests.ReadManifestCounterAsync(root, id, "frozenOutputInputCounts", RollingNodeId).ConfigureAwait(false));
                await ProcessingGraphOperationsTests.ReplaceManifestCounterAsync(root, id, "committedOutputCounts",
                    ProcessingGraphOperationsTests.FrozenInputCorruption(variant)).ConfigureAwait(false);
                await DrainOneAsync(provider, configuration).ConfigureAwait(false);
                var journal = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                var rolling = journal.Nodes.Single(static node => node.NodeId == RollingNodeId);
                Assert.AreEqual("Completed", rolling.Status);
                Assert.HasCount(1, rolling.Outputs);
                Assert.HasCount(WindowSize - 1, rolling.Inputs);
                await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
                foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                    await ProcessingGraphOperationsTests.ReadManifestJournalConsumerAsync(root, operations, id, consumer, true).ConfigureAwait(false);
                if (removeBeforeRestart)
                {
                    await ProcessingGraphOperationsTests.ReplaceManifestCounterAsync(root, id, "committedOutputCounts", null).ConfigureAwait(false);
                    manifest = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                }
            }
            SqliteConnection.ClearAllPools();
            using (var reopened = CreateProvider(root))
            {
                await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var recovered = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                    await ProcessingGraphOperationsTests.ReadManifestJournalConsumerAsync(root, recovered, id, consumer, !removeBeforeRestart).ConfigureAwait(false);
                if (!removeBeforeRestart)
                {
                    await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
                    await ProcessingGraphOperationsTests.ReplaceManifestCounterAsync(root, id, "committedOutputCounts", null).ConfigureAwait(false);
                    manifest = await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
                }
                Assert.AreEqual(WindowSize - 1, await ProcessingGraphOperationsTests.ReadManifestCounterAsync(root, id, "frozenOutputInputCounts", RollingNodeId).ConfigureAwait(false));
                Assert.AreEqual(manifest, await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false));
                var journal = (await recovered.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                var sourceId = journal.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs[0].ArtifactId;
                var store = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
                var target = (await store.ReadOutputByArtifactIdAsync(sourceId, CancellationToken.None).ConfigureAwait(false))!;
                var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                    target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
                await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
                Assert.HasCount(WindowSize - 2, (await recovered.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!
                    .Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs);
            }
            SqliteConnection.ClearAllPools();
            using var again = CreateProvider(root);
            await again.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(again.GetRequiredService<ProcessingGraphOperationsCoordinator>(), id,
                CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Cleanup(root); }
    }
}
