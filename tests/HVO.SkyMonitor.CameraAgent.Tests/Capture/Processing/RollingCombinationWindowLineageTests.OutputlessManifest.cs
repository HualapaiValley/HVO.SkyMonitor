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
    public async Task OutputlessResolvedWindowRejectsPrunedFrozenInputsAcrossRestart()
    {
        var root = CreateRoot();
        Guid id;
        Guid sourceId;
        string manifest;
        try
        {
            var configuration = CreateConfiguration(root, syntheticReferences: false);
            configuration = configuration with
            {
                Pipeline = configuration.Pipeline with
                {
                    Steps = configuration.Pipeline.Steps.Select(step => step.Id == RollingNodeId ? step with { Required = false } : step).ToArray()
                }
            };
            using (var provider = CreateProvider(root, new ArmableNodeFaultInjector(RollingNodeId)))
            {
                var module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
                await using var lifetime = module.ConfigureAwait(false);
                await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
                var receipt = (await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(configuration,
                    await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false))!;
                var fault = (ArmableNodeFaultInjector)provider.GetRequiredService<ICaptureProcessingFaultInjector>();
                fault.Arm();
                await DrainOneAsync(provider, configuration).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var detail = (await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                var rolling = detail.Nodes.Single(static node => node.NodeId == RollingNodeId);
                Assert.AreEqual("TerminalFailure", rolling.Status);
                Assert.AreEqual(0, rolling.CommittedOutputCount);
                Assert.IsEmpty(rolling.Outputs);
                Assert.HasCount(WindowSize - 1, rolling.Inputs);
                sourceId = rolling.Inputs[0].ArtifactId;
                manifest = await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using (var restarted = CreateProvider(root))
            {
                await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                Assert.AreEqual(manifest, await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false));
                var store = restarted.GetRequiredService<SqliteCaptureProcessingStore>();
                var target = (await store.ReadOutputByArtifactIdAsync(sourceId, CancellationToken.None).ConfigureAwait(false))!;
                var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                    target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
                await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(await store.ReadOutputByArtifactIdAsync(sourceId, CancellationToken.None).ConfigureAwait(false));
                var journal = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                Assert.HasCount(WindowSize - 2, journal.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs);
                await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var reopened = CreateProvider(root);
            await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>(), id,
                CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Cleanup(root); }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("negative")]
    [DataRow("deep")]
    [DataRow("unicode")]
    public async Task CorruptFrozenInputBookkeepingCannotRollBackResolutionOrCompletion(string variant)
    {
        var root = CreateRoot();
        try
        {
            Guid id;
            using (var provider = CreateProvider(root))
            {
                var configuration = CreateConfiguration(root, syntheticReferences: false);
                configuration = configuration with
                {
                    Pipeline = configuration.Pipeline with
                    {
                        Steps = configuration.Pipeline.Steps.Select(step => step.Id == RollingNodeId ? step with { Required = false } : step).ToArray()
                    }
                };
                var module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
                await using var lifetime = module.ConfigureAwait(false);
                await RunBacklogAsync(provider, configuration, module).ConfigureAwait(false);
                var receipt = (await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(configuration,
                    await CreateSubmissionAsync(module, CaptureCount, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false))!;
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                await ProcessingGraphOperationsTests.ReplaceFrozenInputEvidenceAsync(root, id,
                    ProcessingGraphOperationsTests.FrozenInputCorruption(variant)).ConfigureAwait(false);
                await DrainOneAsync(provider, configuration).ConfigureAwait(false);
                var journal = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                var rolling = journal.Nodes.Single(static node => node.NodeId == RollingNodeId);
                Assert.AreEqual("Completed", rolling.Status);
                Assert.HasCount(1, rolling.Outputs);
                Assert.HasCount(WindowSize - 1, rolling.Inputs);
                await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var reopened = CreateProvider(root);
            await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            Assert.AreEqual("Completed", (await recovered.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!
                .Nodes.Single(static node => node.NodeId == RollingNodeId).Status);
            await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            // Repair only the optional input member, preserving the worker's committed-output ledger.
            await ProcessingGraphOperationsTests.ReplaceFrozenInputEvidenceAsync(root, id,
                "{\"" + RollingNodeId + "\":" + (WindowSize - 1) + "}").ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Cleanup(root); }
    }

}
