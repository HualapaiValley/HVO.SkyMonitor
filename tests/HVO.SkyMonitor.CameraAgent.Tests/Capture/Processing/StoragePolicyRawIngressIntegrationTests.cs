using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    private const string LaneReason = "storage-policy-mismatch";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProfileChangeWithNamedGraphPreservesCapturesAcrossUpgradeRestartAndReconciliation(bool restartBeforeCapture)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-raw-change");
        try
        {
            var file = Profile(root);
            string namedId;
            CameraModuleConfig current;
            using (var provider = Provider(root))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                namedId = await ActivateNamedAsync(provider, file.Pipeline, "named-original").ConfigureAwait(false);
                current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                Assert.AreEqual(namedId, (await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
                if (!restartBeforeCapture)
                    await AcceptAndSkipAsync(provider, current, namedId, 1).ConfigureAwait(false);
            }
            // The persisted profile now differs from the already active Named revision, including on upgrade.
            using (var restarted = Provider(root))
            {
                current = await InitializeAsync(restarted, file).ConfigureAwait(false);
                Assert.AreEqual(namedId, (await Operations(restarted).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
                await AcceptAndSkipAsync(restarted, current, namedId, 2).ConfigureAwait(false);
                await AcceptAndSkipAsync(restarted, current, namedId, 3).ConfigureAwait(false);
                var reconciled = await ActivateNamedAsync(restarted, current.Pipeline, "named-reconciled").ConfigureAwait(false);
                var receipt = (await Ingress(restarted).AcceptAsync(current, Submission(4), CancellationToken.None).ConfigureAwait(false))!;
                Assert.IsNotNull(receipt);
                Assert.AreEqual(reconciled, receipt.Manifest.CaptureGraphRevisionId);
                var result = await ProcessNextAsync(restarted, current).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var rootB = Path.Combine(root, "storage-b");
                Assert.IsTrue(Directory.EnumerateFiles(rootB, "*.json", SearchOption.AllDirectories).Any(), "Matching profile publication resumes.");
                Assert.IsTrue((await restarted.GetRequiredService<IArtifactOutbox>().GetSnapshotAsync(rootB, CancellationToken.None).ConfigureAwait(false)).HeldCount > 0,
                    "Matching upload-enabled profile publishes real outbox work.");
                await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                    await Operations(restarted).RollbackRevisionAsync(namedId,
                        (await Operations(restarted).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).StateVersion,
                        "unsafe-rollback", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                AssertRawRetained(receipt);
            }
            using var reopened = Provider(root);
            var restored = await InitializeAsync(reopened, file).ConfigureAwait(false);
            Assert.AreEqual(90, StorageOptions(restored).RetentionDays);
            Assert.IsTrue(StorageOptions(restored).QueueForUpload);
            var next = (await Ingress(reopened).AcceptAsync(restored, Submission(5), CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessNextAsync(reopened, restored).ConfigureAwait(false)).Outcome);
            AssertRawRetained(next);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ProfileChangeAfterRawBindingSkipsUnsafeProcessingAndKeepsRaw()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-bound-change");
        try
        {
            using var provider = Provider(root);
            var file = Profile(root);
            await InitializeAsync(provider, file).ConfigureAwait(false);
            var named = await ActivateNamedAsync(provider, file.Pipeline, "named-bound").ConfigureAwait(false);
            var receipt = (await Ingress(provider).AcceptAsync(file, Submission(1), CancellationToken.None).ConfigureAwait(false))!;
            Assert.IsNotNull(receipt);
            var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
            var result = await ProcessNextAsync(provider, current).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, result.Outcome);
            Assert.AreEqual(LaneReason, result.Reason);
            Assert.AreEqual(named, receipt.Manifest.CaptureGraphRevisionId);
            AssertRawRetained(receipt);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "storage-a")), "No stale Storage publication is allowed.");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "storage-b")), "Mismatch skips processing rather than changing its immutable graph.");
            var execution = await Operations(provider).ReadExecutionAsync(
                (await Operations(provider).ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("processing." + LaneReason, execution!.FailureReason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("payload")]
    [DataRow("sidecar")]
    public async Task StorageMismatchDoesNotHideDamagedRawEvidence(string damagedPart)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-damaged-raw");
        try
        {
            using var provider = Provider(root);
            var file = Profile(root);
            await InitializeAsync(provider, file).ConfigureAwait(false);
            await ActivateNamedAsync(provider, file.Pipeline, "named-damaged").ConfigureAwait(false);
            var receipt = (await Ingress(provider).AcceptAsync(file, Submission(1), CancellationToken.None).ConfigureAwait(false))!;
            var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
            var path = damagedPart == "payload" ? receipt.StoredFrame.AbsolutePath : Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json");
            await File.WriteAllBytesAsync(path, new byte[] { 0 }).ConfigureAwait(false);
            var result = await ProcessNextAsync(provider, current).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, result.Outcome);
            Assert.AreEqual("evidence-unavailable", result.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task StartupBindsAllLegacyCapturesAndProcessesCompatibleWorkAfterFirstMismatch()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-startup-binding");
        try
        {
            var file = Profile(root);
            var retained = new List<RawCaptureReceipt>();
            using (var seed = Provider(root))
            {
                await InitializeAsync(seed, file).ConfigureAwait(false);
                var namedA = await ActivateNamedAsync(seed, file.Pipeline, "legacy-a").ConfigureAwait(false);
                retained.Add((await Ingress(seed).AcceptAsync(file, Submission(1), CancellationToken.None).ConfigureAwait(false))!);
                retained.Add((await Ingress(seed).AcceptAsync(file, Submission(2), CancellationToken.None).ConfigureAwait(false))!);
                var current = await ChangeProfileAsync(seed, file).ConfigureAwait(false);
                await ActivateNamedAsync(seed, current.Pipeline, "legacy-b").ConfigureAwait(false);
                retained.Add((await Ingress(seed).AcceptAsync(current, Submission(3), CancellationToken.None).ConfigureAwait(false))!);
                // Model the pre-fix active legacy selection without asking the corrected lifecycle to authorize it.
                var registry = await Operations(seed).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                await seed.GetRequiredService<SqliteCaptureProcessingStore>().ActivateRevisionAsync(
                    namedA, registry.StateVersion, "old-host-activation", "old-host", null, CancellationToken.None).ConfigureAwait(false);
            }
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM processing_executions WHERE execution_class = 'Live';";
                Assert.AreEqual(3, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var sidecars = retained.Select(receipt => File.ReadAllBytes(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json"))).ToArray();
            using (var restarted = Provider(root))
            {
                var current = await InitializeAsync(restarted, file).ConfigureAwait(false);
                var service = restarted.GetRequiredService<CaptureDistributionService>();
                await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (ReadTerminalLaneCount(root) != 3)
                        await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                    var executions = await Operations(restarted).ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, CancellationToken.None).ConfigureAwait(false);
                    Assert.HasCount(3, executions);
                    Assert.AreEqual(2, executions.Count(execution => execution.FailureReason == "processing." + LaneReason));
                    Assert.AreEqual(1, executions.Count(execution => execution.Status == ProcessingGraphExecutionStatus.Completed));
                    Assert.AreNotEqual(RawIngressAvailability.Unhealthy, restarted.GetRequiredService<RawIngressState>().Snapshot.Availability);
                    Assert.AreEqual(90, StorageOptions(current).RetentionDays);
                }
                finally { await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            for (var index = 0; index < retained.Count; index++)
            {
                AssertRawRetained(retained[index]);
                CollectionAssert.AreEqual(sidecars[index], await File.ReadAllBytesAsync(Path.ChangeExtension(retained[index].StoredFrame.AbsolutePath, ".json")).ConfigureAwait(false));
            }
            using var reopened = Provider(root);
            await InitializeAsync(reopened, file).ConfigureAwait(false);
            await Ingress(reopened).BindRecoveredLiveExecutionsAsync(file, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(3, await Operations(reopened).ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 10, CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static ServiceProvider Provider(string root) => ProcessingGraphOperationsTests.CreateProvider(root, new Dictionary<string, string?>
    {
        ["CameraAgent:CentralIntegration:Mode"] = "Enabled",
        ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
    });

    internal static CameraModuleConfig Profile(string root)
    {
        var source = ProcessingGraphOperationsTests.CreateProjectedSceneConfiguration();
        return source with
        {
            Module = new CameraModuleDescriptor(typeof(RandomImageCameraModule).FullName!),
            DeploymentLocation = DeploymentLocationSnapshot.Create("storage-test", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
            Rig = source.Rig with { ControlPolicy = new CameraControlPolicy { ExposureControl = AutomaticControlOwnership.Disabled, GainControl = AutomaticControlOwnership.Disabled } },
            Schedule = new("capture-schedule-v1", [new CaptureScheduleSetpointProfile("always", TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(2))],
                Enum.GetValues<DayOfWeek>().Select(day => new CaptureWeeklyScheduleWindow($"always-{day}", day,
                    new(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                    new(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue, DayOffset: 1), "always")).ToArray()),
            Pipeline = new([
                new("Preview", "preview", DependsOn: ["$raw"], Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
                new("Storage", "storage", DependsOn: ["preview"], Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                { StorageRoot = Path.Combine(root, "storage-a"), UpdateLatestFrame = false }))
            ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
    }

    internal static async Task<CameraModuleConfig> InitializeAsync(ServiceProvider provider, CameraModuleConfig file)
    {
        await Ingress(provider).InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await provider.GetRequiredService<CaptureAdmissionCoordinator>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = await provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>().InitializeAsync(file, CancellationToken.None).ConfigureAwait(false);
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(snapshot.Configuration);
        await Operations(provider).EnsureConfiguredBasicAsync(snapshot.Configuration, CancellationToken.None).ConfigureAwait(false);
        return snapshot.Configuration;
    }

    internal static async Task<string> SeedLegacyRevisionAsync(ServiceProvider provider, CameraModuleConfig profile, CapturePipelineConfig pipeline)
    {
        var compiler = typeof(ProcessingGraphOperationsCoordinator).GetMethod("CreateRevisionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = (ProcessingGraphRevisionSnapshot)compiler.Invoke(Operations(provider),
            [profile with { Pipeline = pipeline }, "legacy-storage", "1", DateTimeOffset.UtcNow, ProcessingGraphRevisionLifecycle.Draft, null, false])!;
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var created = await store.InsertNamedRevisionAsync(snapshot, "old-create", "old-host", null, CancellationToken.None).ConfigureAwait(false);
        await store.ValidateRevisionAsync(created.RevisionId, "old-validate", "old-host", null, CancellationToken.None).ConfigureAwait(false);
        var registry = await Operations(provider).GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var active = await store.ActivateRevisionAsync(created.RevisionId, registry.StateVersion, "old-activate", "old-host", null, CancellationToken.None).ConfigureAwait(false);
        await store.RollbackRevisionAsync(registry.ActiveRevisionId, active.StateVersion, "old-rollback", "old-host", null, CancellationToken.None).ConfigureAwait(false);
        return created.RevisionId;
    }

    internal static CapturePipelineConfig ChangedPipeline(CameraModuleConfig profile)
        => profile.Pipeline with
        {
            Steps = [.. profile.Pipeline.Steps.Select(step => step.Type == "Storage" ? step with
        { Options = JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
        { StorageRoot = Path.Combine(Path.GetDirectoryName(StorageOptions(profile).StorageRoot)!, "storage-b"), RetentionDays = 90, QueueForUpload = true, UpdateLatestFrame = false }) } : step)]
        };

    internal static async Task<CameraModuleConfig> ChangeProfileAsync(ServiceProvider provider, CameraModuleConfig profile)
    {
        var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
        var state = await provider.GetRequiredService<SqliteCaptureScheduleStore>().GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var staged = await runtime.StageAsync(LocalCaptureProfileDefinition.CreateV2(profile with { Pipeline = ChangedPipeline(profile) }, profile.Schedule!),
            "profile-stage", state.Version, "test", null, CancellationToken.None).ConfigureAwait(false);
        await runtime.ActivateAsync(staged.PendingRevision!.RevisionId, "profile-activate", staged.Version, "test", null, CancellationToken.None).ConfigureAwait(false);
        return runtime.Snapshot!.Configuration;
    }

    private static async Task<string> ActivateNamedAsync(ServiceProvider provider, CapturePipelineConfig pipeline, string name)
    {
        var operations = Operations(provider);
        var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var draft = await operations.CreateRevisionAsync(name, "1", pipeline, name + "-create", "test", null, CancellationToken.None).ConfigureAwait(false);
        await operations.ValidateRevisionAsync(draft.RevisionId, name + "-validate", "test", null, CancellationToken.None).ConfigureAwait(false);
        await operations.ActivateRevisionAsync(draft.RevisionId, registry.StateVersion, name + "-activate", "test", null, CancellationToken.None).ConfigureAwait(false);
        return draft.RevisionId;
    }

    private static async Task AcceptAndSkipAsync(ServiceProvider provider, CameraModuleConfig current, string namedId, int sequence)
    {
        var receipt = (await Ingress(provider).AcceptAsync(current, Submission(sequence), CancellationToken.None).ConfigureAwait(false))!;
        Assert.IsNotNull(receipt, "A Storage mismatch cannot refuse raw capture.");
        Assert.AreEqual(namedId, receipt.Manifest.CaptureGraphRevisionId);
        var result = await ProcessNextAsync(provider, current).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, result.Outcome);
        Assert.AreEqual(LaneReason, result.Reason);
        AssertRawRetained(receipt);
        Assert.AreNotEqual(RawIngressAvailability.Unhealthy, provider.GetRequiredService<RawIngressState>().Snapshot.Availability);
        Assert.IsFalse(Directory.Exists(StorageOptions(current).StorageRoot), "Mismatched work cannot publish under another policy.");
    }

    private static async Task<CaptureLaneHandlerResult> ProcessNextAsync(ServiceProvider provider, CameraModuleConfig current)
    {
        var store = provider.GetRequiredService<ICaptureLaneStore>();
        await store.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
        var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
        var lease = await store.ClaimAsync(lane, "storage-policy-test", current, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome == CaptureLaneHandlerOutcome.Completed) await store.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        else await store.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static FileStorageCaptureProcessingStepOptions StorageOptions(CameraModuleConfig config)
        => JsonSerializer.Deserialize<FileStorageCaptureProcessingStepOptions>(config.Pipeline.Steps.Single(step => step.Type == "Storage").Options!.Value)!;

    private static void AssertRawRetained(RawCaptureReceipt receipt)
    {
        Assert.IsTrue(File.Exists(receipt.StoredFrame.AbsolutePath));
        Assert.IsTrue(string.Equals(receipt.Manifest.Descriptor.Artifact.ChecksumSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt.StoredFrame.AbsolutePath))), StringComparison.OrdinalIgnoreCase));
    }

    private static long ReadTerminalLaneCount(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state IN ('completed', 'abandoned');";
        return (long)command.ExecuteScalar()!;
    }

    private static CaptureLoopSubmission Submission(int sequence)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-30 + sequence);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var frame = new CameraFrame(now, 2, 2, CameraPixelFormat.Mono16, new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }, new(TimeSpan.FromSeconds(1), 1, 10, "virtual-storage-test"));
        return new(new(now, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint),
            new(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false) { AcquisitionTiming = new(now, now.AddSeconds(1), now.AddSeconds(1.1)) },
            now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static ProcessingGraphOperationsCoordinator Operations(ServiceProvider provider) => provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
    private static IRawCaptureIngress Ingress(ServiceProvider provider) => provider.GetRequiredService<IRawCaptureIngress>();
}
