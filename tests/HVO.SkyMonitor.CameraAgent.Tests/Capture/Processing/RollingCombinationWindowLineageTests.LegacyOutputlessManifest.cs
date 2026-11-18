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
    [DataRow(4, false, false)]
    [DataRow(2, false, false)]
    [DataRow(0, false, false)]
    [DataRow(4, true, false)]
    [DataRow(4, false, true)]
    public async Task LegacyOutputlessWindowRequiresProvableCompleteHistoryAcrossRestart(int history, bool recorded, bool retry)
    {
        var root = CreateRoot();
        Guid id;
        Guid? sourceId;
        var initialReason = recorded || (history == WindowSize - 1 && !retry) ? null : CameraAgentRunManifest.UnrecordedInputReason;
        var prunedReason = recorded ? CameraAgentRunManifest.ArtifactEvidenceUnavailableReason : CameraAgentRunManifest.UnrecordedInputReason;
        try
        {
            var configuration = CreateConfiguration(root, syntheticReferences: false);
            configuration = configuration with
            {
                Pipeline = configuration.Pipeline with
                {
                    Steps = configuration.Pipeline.Steps.Select(step => step.Id == RollingNodeId ? step with { Required = retry } : step).ToArray()
                }
            };
            using (var provider = CreateProvider(root, new ArmableNodeFaultInjector(RollingNodeId)))
            {
                var module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
                await using var lifetime = module.ConfigureAwait(false);
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                for (var index = 0; index < history; index++)
                {
                    Assert.IsNotNull(await ingress.AcceptAsync(configuration,
                        await CreateSubmissionAsync(module, index, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false));
                    await DrainOneAsync(provider, configuration).ConfigureAwait(false);
                }
                var receipt = (await ingress.AcceptAsync(configuration,
                    await CreateSubmissionAsync(module, history, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false))!;
                var fault = (ArmableNodeFaultInjector)provider.GetRequiredService<ICaptureProcessingFaultInjector>();
                if (retry)
                {
                    var lanes = provider.GetRequiredService<ICaptureLaneStore>();
                    var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard");
                    var handler = provider.GetServices<ICaptureLaneHandler>().Single(static candidate => candidate.Lane == "standard");
                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        var lease = (await lanes.ClaimAsync(standard, "legacy-window-retry", configuration, CancellationToken.None).ConfigureAwait(false))!;
                        fault.Arm();
                        var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                        Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome);
                        if (attempt == 1) await lanes.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                        else Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, await lanes.FailAsync(lease,
                            CaptureLaneHandlerResult.Terminal("legacy-window-retry-ended"), CancellationToken.None).ConfigureAwait(false));
                    }
                }
                else
                {
                    fault.Arm();
                    await DrainOneAsync(provider, configuration).ConfigureAwait(false);
                }
                id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
                var detail = (await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                var rolling = detail.Nodes.Single(static node => node.NodeId == RollingNodeId);
                Assert.AreEqual("TerminalFailure", rolling.Status);
                Assert.AreEqual(0, rolling.CommittedOutputCount);
                Assert.IsEmpty(rolling.Outputs);
                Assert.HasCount(retry ? 2 : 1, rolling.Attempts);
                Assert.HasCount(history, rolling.Inputs);
                CollectionAssert.AreEquivalent(Enumerable.Range(-history, history).ToArray(), rolling.Inputs.Select(static input => input.WindowPosition).ToArray());
                sourceId = rolling.Inputs.Count == 0 ? null : rolling.Inputs[0].ArtifactId;
                await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                if (!recorded)
                    await ProcessingGraphOperationsTests.ReplaceManifestCounterAsync(root, id, "frozenOutputInputCounts", null).ConfigureAwait(false);
                await RunManifestTestAssertions.ResponseAsync(operations, id, initialReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using (var reopened = CreateProvider(root))
            {
                await reopened.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await RunManifestTestAssertions.ResponseAsync(operations, id, initialReason).ConfigureAwait(false);
                if (!recorded)
                {
                    // Restoring the actual cardinality proves that a short window was valid,
                    // but removing it cannot prove whether an older short window lost pins.
                    await ProcessingGraphOperationsTests.ReplaceManifestCounterAsync(root, id, "frozenOutputInputCounts", $"{{\"rolling\":{history}}}").ConfigureAwait(false);
                    await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
                    await ProcessingGraphOperationsTests.ReplaceManifestCounterAsync(root, id, "frozenOutputInputCounts", null).ConfigureAwait(false);
                }
                if (sourceId is { } artifactId)
                {
                    var store = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
                    var target = (await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false))!;
                    var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                        target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
                    await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                    await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNull(await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false));
                    var journal = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                    Assert.HasCount(history - 1, journal.Nodes.Single(static node => node.NodeId == RollingNodeId).Inputs);
                }
                await RunManifestTestAssertions.ResponseAsync(operations, id, prunedReason).ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            using var again = CreateProvider(root);
            await again.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(again.GetRequiredService<ProcessingGraphOperationsCoordinator>(), id, prunedReason).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Cleanup(root); }
    }
}
