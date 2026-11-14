using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    [TestMethod]
    [DataRow("missing")]
    [DataRow("corrupt")]
    [DataRow("mismatched")]
    public async Task OptionalSceneExhaustionUnderStorageMismatchNeverQuarantinesOrRefusesTheNextCapture(string stageFault)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-optional-exhaustion");
        try
        {
            var source = SceneProfile(root);
            var file = source with
            {
                Pipeline = source.Pipeline with
                {
                    Steps = [.. source.Pipeline.Steps.Select(step =>
                step.Type == "ProjectedScene" ? step with { Required = false } : step)]
                }
            };
            var clock = new EdgeUpgradeLaneTests.MutableTimeProvider(DateTimeOffset.UtcNow);
            using (var provider = ProcessingGraphOperationsTests.CreateProvider(root, timeProvider: clock))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                await ActivateNamedAsync(provider, file.Pipeline, "optional-scene").ConfigureAwait(false);
                var stageKey = new string('e', 64);
                var receipt = await AcceptSceneAsync(provider, file, stageKey, stage: false).ConfigureAwait(false);
                if (stageFault == "mismatched")
                    await provider.GetRequiredService<IProjectedSceneStagingStore>().StageAsync(stageKey, new string('f', 64),
                        await ProcessingGraphOperationsTests.CreateProjectedSceneAsync().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
                if (stageFault == "corrupt")
                {
                    await StageSceneAsync(provider, stageKey).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(root, "staging", "projected-scenes", stageKey + ".json"), "{").ConfigureAwait(false);
                }
                var current = await ChangeRetentionOnlyAsync(provider, file).ConfigureAwait(false);
                var store = provider.GetRequiredService<ICaptureLaneStore>();
                await store.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
                var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    var lease = await store.ClaimAsync(lane, "optional-exhaustion", current, CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(lease);
                    Assert.AreEqual(attempt, lease.Attempt);
                    var result = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                    var settled = await store.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
                    var backlog = (await store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false)).Single(item => item.Lane == "standard");
                    Assert.AreEqual(0L, backlog.QuarantineCount, "An optional scene failure must never create hard backpressure under a Storage mismatch.");
                    Assert.AreEqual(attempt < 5 ? CaptureLaneHandlerOutcome.RetryableFailure : CaptureLaneHandlerOutcome.Abandoned, settled);
                    if (attempt == 5) Assert.AreEqual("storage-policy-mismatch-scene-unavailable", result.Reason);
                    clock.Now = clock.Now.AddSeconds(Math.Pow(2, attempt));
                }
                AssertRawRetained(receipt);
                AssertNoStoragePublication(root);
                Assert.HasCount(0, ProcessingGraphOperationsTests.ReadProjectedSceneOutputs(root, receipt.Manifest.Descriptor.Capture.CaptureId));
                var liveId = await Operations(provider).ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                var live = await Operations(provider).ReadExecutionAsync(liveId!.Value, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, live!.Status);
                Assert.AreEqual("processing.storage-policy-mismatch-scene-unavailable", live.FailureReason);
                Assert.IsNotNull(await Ingress(provider).AcceptAsync(current, Submission(2), CancellationToken.None).ConfigureAwait(false));
                await ProcessNextAsync(provider, current).ConfigureAwait(false);
                await AssertOptionalFailureHealthAsync(provider, clock).ConfigureAwait(false);
            }
            using var reopened = ProcessingGraphOperationsTests.CreateProvider(root, timeProvider: clock);
            var restored = await InitializeAsync(reopened, file).ConfigureAwait(false);
            Assert.AreEqual(0L, (await reopened.GetRequiredService<ICaptureLaneStore>().ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(item => item.Lane == "standard").QuarantineCount);
            Assert.IsNotNull(await Ingress(reopened).AcceptAsync(restored, Submission(3), CancellationToken.None).ConfigureAwait(false));
            await AssertOptionalFailureHealthAsync(reopened, clock).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ConfiguredBasicStorageProfileChangeRefreshesWithoutFalseDegradedHealthOrError()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-basic-health");
        try
        {
            var file = Profile(root);
            using (var provider = Provider(root))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                var before = await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                var state = new CaptureProcessingState();
                var logger = new StorageHealthLogger();
                using var refresh = new CaptureProcessingStateRefreshService(provider.GetRequiredService<SqliteCaptureProcessingStore>(),
                    state, TimeProvider.System, Ingress(provider), Operations(provider), logger);
                await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureProcessingAvailability.Healthy, state.Snapshot.Availability, state.Snapshot.Reason);
                Assert.AreEqual(0, logger.Errors);
                Assert.AreEqual(0, logger.Recoveries, "Do not raise and then clear a false alarm.");
                var after = await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphRegistryMode.ConfiguredBasic, after.Mode);
                Assert.AreNotEqual(before.ActiveRevisionId, after.ActiveRevisionId);
                var selected = await Operations(provider).ReadRevisionSnapshotAsync(after.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
                CaptureStoragePolicy.EnsureMatchesProfile(selected.Pipeline, current.Pipeline);
            }
            using var reopened = Provider(root);
            await InitializeAsync(reopened, file).ConfigureAwait(false);
            var restoredState = new CaptureProcessingState();
            using var restoredRefresh = new CaptureProcessingStateRefreshService(reopened.GetRequiredService<SqliteCaptureProcessingStore>(),
                restoredState, TimeProvider.System, Ingress(reopened), Operations(reopened));
            await restoredRefresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureProcessingAvailability.Healthy, restoredState.Snapshot.Availability);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<CameraModuleConfig> ChangeRetentionOnlyAsync(ServiceProvider provider, CameraModuleConfig file)
    {
        var changed = file with
        {
            Pipeline = file.Pipeline with
            {
                Steps = [.. file.Pipeline.Steps.Select(step => step.Type == "Storage"
            ? step with { Options = JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
            { StorageRoot = StorageOptions(file).StorageRoot, RetentionDays = 31, UpdateLatestFrame = false }) } : step)]
            }
        };
        var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
        var state = await provider.GetRequiredService<SqliteCaptureScheduleStore>().GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var staged = await runtime.StageAsync(LocalCaptureProfileDefinition.CreateV2(changed, changed.Schedule!),
            "retention-only-stage", state.Version, "test", null, CancellationToken.None).ConfigureAwait(false);
        await runtime.ActivateAsync(staged.PendingRevision!.RevisionId, "retention-only-activate", staged.Version, "test", null, CancellationToken.None).ConfigureAwait(false);
        return runtime.Snapshot!.Configuration;
    }

    private static async Task AssertOptionalFailureHealthAsync(ServiceProvider provider, TimeProvider clock)
    {
        var state = new CaptureProcessingState();
        using var refresh = new CaptureProcessingStateRefreshService(provider.GetRequiredService<SqliteCaptureProcessingStore>(),
            state, clock, Ingress(provider), Operations(provider));
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureProcessingAvailability.Degraded, state.Snapshot.Availability, state.Snapshot.Reason);
        Assert.AreEqual(0L, state.Snapshot.TerminalCount);
        Assert.AreNotEqual(RawIngressAvailability.Unhealthy, provider.GetRequiredService<RawIngressState>().Snapshot.Availability);
    }
}
