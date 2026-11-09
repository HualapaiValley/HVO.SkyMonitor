using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StorageSkippedSceneSurvivesReconciliationRestartAndExplicitReplay(bool restartBeforeProcessing)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-scene-replay");
        try
        {
            var file = SceneProfile(root);
            RawCaptureReceipt receipt;
            string original;
            var stageKey = new string('a', 64);
            using (var provider = Provider(root))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                original = await ActivateNamedAsync(provider, file.Pipeline, "scene-original").ConfigureAwait(false);
                receipt = await AcceptSceneAsync(provider, file, stageKey).ConfigureAwait(false);
                var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                if (!restartBeforeProcessing)
                    await AssertSceneSkipAsync(provider, current, receipt, stageKey, root).ConfigureAwait(false);
            }
            using (var restarted = Provider(root))
            {
                var current = await InitializeAsync(restarted, file).ConfigureAwait(false);
                if (restartBeforeProcessing)
                    await AssertSceneSkipAsync(restarted, current, receipt, stageKey, root).ConfigureAwait(false);
                await ActivateNamedAsync(restarted, current.Pipeline, "scene-reconciled").ConfigureAwait(false);
            }
            // No transient stage or running live execution survives this restart. Replay must use committed evidence.
            using var reopened = Provider(root);
            await InitializeAsync(reopened, file).ConfigureAwait(false);
            var before = File.ReadAllBytes(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json"));
            await AssertSceneReplayAsync(reopened, receipt, root).ConfigureAwait(false);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")));
            Assert.AreEqual(original, receipt.Manifest.CaptureGraphRevisionId);
            AssertRawRetained(receipt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task StorageSkippedSceneCommitCanOverlapReconciliationWithoutExecutingStorage()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-scene-concurrent");
        try
        {
            using var barrier = new SceneCommitBarrier();
            using (var provider = ProcessingGraphOperationsTests.CreateProvider(root,
                configureServices: services => services.AddSingleton<ICaptureProcessingFaultInjector>(barrier)))
            {
                var file = SceneProfile(root);
                await InitializeAsync(provider, file).ConfigureAwait(false);
                await ActivateNamedAsync(provider, file.Pipeline, "scene-before").ConfigureAwait(false);
                var stageKey = new string('b', 64);
                var receipt = await AcceptSceneAsync(provider, file, stageKey).ConfigureAwait(false);
                var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                var processing = Task.Run(() => ProcessNextAsync(provider, current));
                try
                {
                    await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await ActivateNamedAsync(provider, current.Pipeline, "scene-during").ConfigureAwait(false);
                }
                finally { barrier.Release.Set(); }
                var result = await processing.ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, result.Outcome);
                Assert.AreEqual(LaneReason, result.Reason);
                Assert.HasCount(1, ProcessingGraphOperationsTests.ReadProjectedSceneOutputs(root, receipt.Manifest.Descriptor.Capture.CaptureId));
                AssertNoStoragePublication(root);
                await AssertSceneReplayAsync(provider, receipt, root).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StorageSkippedSceneMissingStageRemainsRetryableAndRecoversAfterRestart(bool sceneRequired)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-scene-retry");
        try
        {
            var original = SceneProfile(root);
            var file = original with
            {
                Pipeline = original.Pipeline with
                {
                    Steps = [.. original.Pipeline.Steps.Select(step =>
                step.Type == "ProjectedScene" ? step with { Required = sceneRequired } : step)]
                }
            };
            var stageKey = new string('c', 64);
            RawCaptureReceipt receipt;
            using (var provider = Provider(root))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                await ActivateNamedAsync(provider, file.Pipeline, "scene-missing").ConfigureAwait(false);
                receipt = await AcceptSceneAsync(provider, file, stageKey, stage: false).ConfigureAwait(false);
                var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                var result = await ProcessNextAsync(provider, current).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, result.Outcome, "Do not settle an unrecoverable skip before scene evidence commits.");
                AssertNoStoragePublication(root);
            }
            using var restarted = Provider(root);
            var restored = await InitializeAsync(restarted, file).ConfigureAwait(false);
            await StageSceneAsync(restarted, stageKey).ConfigureAwait(false);
            // Settle the durable retry's ordinary backoff before attempting it again.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await AssertSceneSkipAsync(restarted, restored, receipt, stageKey, root).ConfigureAwait(false);
            await ActivateNamedAsync(restarted, restored.Pipeline, "scene-after-retry").ConfigureAwait(false);
            await AssertSceneReplayAsync(restarted, receipt, root).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task StoppedProfilePolicyChangeExposesMatchingConfiguredBasicAndCompletesPlannedWorkflow()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-stopped-workflow");
        try
        {
            var file = Profile(root);
            using var provider = Provider(root);
            await InitializeAsync(provider, file).ConfigureAwait(false);
            var original = await ActivateNamedAsync(provider, file.Pipeline, "policy-original").ConfigureAwait(false);
            var before = await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            var changed = file with
            {
                Pipeline = file.Pipeline with
                {
                    Steps = [.. file.Pipeline.Steps.Select(step => step.Type == "Storage"
                ? step with { Options = JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                { StorageRoot = StorageOptions(file).StorageRoot, UpdateLatestFrame = false, Policies = [new() { Role = FrameArtifactRole.Preview, RetentionDays = 90 }] }) } : step)]
                }
            };
            var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
            var state = await provider.GetRequiredService<SqliteCaptureScheduleStore>().GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            var staged = await runtime.StageAsync(LocalCaptureProfileDefinition.CreateV2(changed, changed.Schedule!),
                "policy-stage", state.Version, "test", null, CancellationToken.None).ConfigureAwait(false);
            await runtime.ActivateAsync(staged.PendingRevision!.RevisionId, "policy-activate", staged.Version, "test", null, CancellationToken.None).ConfigureAwait(false);
            // No capture, distribution startup, or explicit internal refresh between profile activation and reading the UI source.
            var registry = await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(original, registry.ActiveRevisionId, "A registry read cannot select a different scientific graph.");
            Assert.AreNotEqual(before.ConfiguredBasicRevisionId, registry.ConfiguredBasicRevisionId);
            var source = await Operations(provider).ReadRevisionSnapshotAsync(registry.ConfiguredBasicRevisionId, CancellationToken.None).ConfigureAwait(false);
            CaptureStoragePolicy.EnsureMatchesProfile(source.Pipeline, changed.Pipeline);
            var reconciled = await ActivateNamedAsync(provider, source.Pipeline, "policy-reconciled").ConfigureAwait(false);
            var receipt = (await Ingress(provider).AcceptAsync(runtime.Snapshot!.Configuration, Submission(1), CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(reconciled, receipt.Manifest.CaptureGraphRevisionId);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessNextAsync(provider, runtime.Snapshot.Configuration).ConfigureAwait(false)).Outcome);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StorageMismatchDegradesHealthOncePerTransitionAndClearsAfterReconciliation(bool restart)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-health");
        try
        {
            var file = Profile(root);
            string original;
            using (var provider = Provider(root))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                original = await ActivateNamedAsync(provider, file.Pipeline, "health-original").ConfigureAwait(false);
                var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                if (!restart) await AssertHealthTransitionAsync(provider, current, original).ConfigureAwait(false);
            }
            if (restart)
            {
                using var reopened = Provider(root);
                var current = await InitializeAsync(reopened, file).ConfigureAwait(false);
                await AssertHealthTransitionAsync(reopened, current, original).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AssertHealthTransitionAsync(ServiceProvider provider, CameraModuleConfig current, string original)
    {
        var state = new CaptureProcessingState();
        var logger = new StorageHealthLogger();
        using var refresh = new CaptureProcessingStateRefreshService(provider.GetRequiredService<SqliteCaptureProcessingStore>(),
            state, TimeProvider.System, Ingress(provider), Operations(provider), logger);
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureProcessingAvailability.Degraded, state.Snapshot.Availability);
        StringAssert.Contains(state.Snapshot.Reason, "active-storage-policy-mismatch", StringComparison.Ordinal);
        var health = await new CaptureProcessingHealthCheck(state).CheckHealthAsync(new()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Degraded, health.Status);
        Assert.AreEqual(original, health.Data["StoragePolicyMismatchActiveRevisionId"]);
        StringAssert.Contains(health.Description!, "Storage", StringComparison.Ordinal);
        Assert.AreEqual(1, logger.Errors);
        await ActivateNamedAsync(provider, current.Pipeline, "health-reconciled").ConfigureAwait(false);
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureProcessingAvailability.Healthy, state.Snapshot.Availability, state.Snapshot.Reason);
        Assert.AreEqual(1, logger.Errors);
        Assert.AreEqual(1, logger.Recoveries);
    }

    private static CameraModuleConfig SceneProfile(string root)
    {
        var file = Profile(root);
        return file with
        {
            Pipeline = file.Pipeline with
            {
                Steps = [
            new("ProjectedScene", "projected-scene", DependsOn: ["$raw"]),
            .. file.Pipeline.Steps.Select(step => step.Type == "Storage" ? step with { DependsOn = ["preview", "projected-scene"] } : step)
        ]
            }
        };
    }

    private static async Task<RawCaptureReceipt> AcceptSceneAsync(
        ServiceProvider provider, CameraModuleConfig file, string stageKey, bool stage = true)
    {
        if (stage) await StageSceneAsync(provider, stageKey).ConfigureAwait(false);
        var submission = ProcessingGraphOperationsTests.WithSceneProvenance(Submission(1), new string('d', 64), stageKey);
        return (await Ingress(provider).AcceptAsync(file, submission, CancellationToken.None).ConfigureAwait(false))!;
    }

    private static async Task StageSceneAsync(ServiceProvider provider, string stageKey)
        => await provider.GetRequiredService<IProjectedSceneStagingStore>().StageAsync(stageKey, new string('d', 64),
            await ProcessingGraphOperationsTests.CreateProjectedSceneAsync().ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);

    private static async Task AssertSceneSkipAsync(ServiceProvider provider, CameraModuleConfig current,
        RawCaptureReceipt receipt, string stageKey, string root)
    {
        var result = await ProcessNextAsync(provider, current).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, result.Outcome, result.Reason);
        Assert.AreEqual(LaneReason, result.Reason);
        Assert.HasCount(1, ProcessingGraphOperationsTests.ReadProjectedSceneOutputs(root, receipt.Manifest.Descriptor.Capture.CaptureId),
            "A terminal Storage skip must retain the capture-time scene product required by explicit replay.");
        var liveId = await Operations(provider).ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
        var live = await Operations(provider).ReadExecutionDetailAsync(liveId!.Value, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Expired, live!.Execution.Status);
        Assert.AreEqual("processing." + LaneReason, live.Execution.FailureReason);
        Assert.IsTrue(live.Nodes.Where(node => node.NodeId != "projected-scene").All(node => node.Outputs.Count == 0),
            "Only capture-time scene evidence may commit on a policy mismatch.");
        Assert.IsNull(await provider.GetRequiredService<IProjectedSceneStagingReader>().ReadAsync(stageKey, CancellationToken.None).ConfigureAwait(false));
        AssertNoStoragePublication(root);
        AssertRawRetained(receipt);
    }

    private static void AssertNoStoragePublication(string root)
    {
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "storage-a")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "storage-b")));
    }

    private static async Task AssertSceneReplayAsync(ServiceProvider provider,
        RawCaptureReceipt receipt, string root)
    {
        var outputs = ProcessingGraphOperationsTests.ReadProjectedSceneOutputs(root, receipt.Manifest.Descriptor.Capture.CaptureId);
        Assert.HasCount(1, outputs);
        var payloadBefore = File.ReadAllBytes(Path.Combine(root, outputs[0].PayloadRelativePath));
        var registry = await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var replay = await Operations(provider).SubmitReplayAsync(new(receipt.Manifest.Descriptor.Capture.CaptureId,
            registry.ActiveRevisionId, receipt.Manifest.Descriptor.Artifact.ArtifactId, Reason: "recover Storage skip"),
            "scene-replay", "test", CancellationToken.None).ConfigureAwait(false);
        var result = await ProcessingGraphOperationsTests.RunReplayToCompletionAsync(provider, replay.Execution.ExecutionId).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, result.Execution.Status, result.Execution.FailureReason);
        Assert.HasCount(1, ProcessingGraphOperationsTests.ReadProjectedSceneOutputs(root, receipt.Manifest.Descriptor.Capture.CaptureId));
        CollectionAssert.AreEqual(payloadBefore, File.ReadAllBytes(Path.Combine(root, outputs[0].PayloadRelativePath)));
        AssertNoStoragePublication(root);
    }

    private sealed class SceneCommitBarrier : ICaptureProcessingFaultInjector, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.BeforeNodeExecution || nodeId != "projected-scene") return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Scene commit barrier timed out.");
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class StorageHealthLogger : ILogger<CaptureProcessingStateRefreshService>
    {
        internal int Errors { get; private set; }
        internal int Recoveries { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors++;
            if (logLevel == LogLevel.Information) Recoveries++;
        }
    }
}
