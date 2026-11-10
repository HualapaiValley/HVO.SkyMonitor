using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("storageRoot")]
    [DataRow("retentionDays")]
    [DataRow("queueForUpload")]
    [DataRow("updateLatestFrame")]
    [DataRow("policies")]
    public async Task StoragePolicyRejectsLocalDraftDivergenceWithoutChangingHostPlans(string member)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-policy-create");
        try
        {
            using var provider = CreateRemainingOptionsProvider(root);
            var profile = StoragePolicyProfile(root);
            await InitializeStoragePolicyFixtureAsync(provider, profile).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(profile, CancellationToken.None).ConfigureAwait(false);
            var changed = DivergentStoragePipeline(profile.Pipeline, root, member);
            var error = await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                await operations.CreateRevisionAsync("storage-policy", "2", changed, "create", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            StringAssert.Contains(error.Message, "capture profile", StringComparison.Ordinal);
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, registry.ActiveRevisionId);
            Assert.HasCount(1, registry.Revisions);
            AssertProfileStoragePlans(provider.GetRequiredService<ICameraAgentConfigurationAccessor>(), profile, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("storageRoot")]
    [DataRow("retentionDays")]
    [DataRow("queueForUpload")]
    public async Task StoragePolicyRejectsLegacyValidateActivateRollbackAndRecoveryAcrossRestart(string member)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-policy-legacy");
        try
        {
            var profile = StoragePolicyProfile(root);
            string sourceId;
            string legacyId;
            using (var provider = CreateRemainingOptionsProvider(root))
            {
                await InitializeStoragePolicyFixtureAsync(provider, profile).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(profile, CancellationToken.None).ConfigureAwait(false);
                sourceId = initial.ActiveRevisionId;
                // Model an immutable draft persisted by the pre-fix UI, using its real snapshot compiler.
                var compiler = typeof(ProcessingGraphOperationsCoordinator).GetMethod("CreateRevisionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var snapshot = (ProcessingGraphRevisionSnapshot)compiler.Invoke(operations,
                    [profile with { Pipeline = DivergentStoragePipeline(profile.Pipeline, root, member) },
                        "legacy-storage", "2", DateTimeOffset.UtcNow, ProcessingGraphRevisionLifecycle.Draft, null, false])!;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                legacyId = (await store.InsertNamedRevisionAsync(snapshot, "legacy-create", "old-host", null, CancellationToken.None).ConfigureAwait(false)).RevisionId;
                await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                    await operations.ValidateRevisionAsync(legacyId, "validate-rejected", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft,
                    (await store.ReadRevisionAsync(legacyId, CancellationToken.None).ConfigureAwait(false)).State.Lifecycle);
                // A revision already validated by an old host must still be rejected at selection boundaries.
                await store.ValidateRevisionAsync(legacyId, "legacy-validate", "old-host", null, CancellationToken.None).ConfigureAwait(false);
                var oldActive = await store.ActivateRevisionAsync(legacyId, initial.StateVersion, "legacy-activate", "old-host", null, CancellationToken.None).ConfigureAwait(false);
                var oldRollback = await store.RollbackRevisionAsync(sourceId, oldActive.StateVersion, "legacy-rollback", "old-host", null, CancellationToken.None).ConfigureAwait(false);
                await AssertStorageCommandsRejectedAndRawBindingPreservedAsync(operations, profile, legacyId, oldRollback.StateVersion, "before").ConfigureAwait(false);
                AssertProfileStoragePlans(provider.GetRequiredService<ICameraAgentConfigurationAccessor>(), profile, root);
            }
            using (var restarted = CreateRemainingOptionsProvider(root))
            {
                await InitializeStoragePolicyFixtureAsync(restarted, profile).ConfigureAwait(false);
                var operations = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var initial = await operations.EnsureConfiguredBasicAsync(profile, CancellationToken.None).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                    await operations.ValidateRevisionAsync(legacyId, "restart-validate-rejected", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await AssertStorageCommandsRejectedAndRawBindingPreservedAsync(operations, profile, legacyId, initial.StateVersion, "restart").ConfigureAwait(false);
                Assert.AreEqual(sourceId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
                var unchangedStorage = profile.Pipeline with
                {
                    Steps = [.. profile.Pipeline.Steps.Select(step => step.Type == "MeasuredAstrometry"
                        ? step with { Options = ChangeStorageMember(step.Options!.Value, "captureStride", "120") } : step)]
                };
                var safe = await operations.CreateRevisionAsync("safe-storage", "3", unchangedStorage, "safe-create", "test", null, CancellationToken.None).ConfigureAwait(false);
                await operations.ValidateRevisionAsync(safe.RevisionId, "safe-validate", "test", null, CancellationToken.None).ConfigureAwait(false);
                var active = await operations.ActivateRevisionAsync(safe.RevisionId, initial.StateVersion, "safe-activate", "test", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(safe.RevisionId, active.ActiveRevisionId);
                var selected = await operations.SelectLiveRevisionAsync(profile, false, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(root, selected.Pipeline.Steps.Single(step => step.Type == "Storage").Options!.Value.GetProperty("StorageRoot").GetString());
                AssertProfileStoragePlans(restarted.GetRequiredService<ICameraAgentConfigurationAccessor>(), profile, root);
                var rolledBack = await operations.RollbackRevisionAsync(sourceId, active.StateVersion, "safe-rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(sourceId, rolledBack.ActiveRevisionId);
            }
            using var reopened = CreateRemainingOptionsProvider(root);
            await InitializeStoragePolicyFixtureAsync(reopened, profile).ConfigureAwait(false);
            var restored = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await restored.EnsureConfiguredBasicAsync(profile, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(sourceId, (await restored.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            AssertProfileStoragePlans(reopened.GetRequiredService<ICameraAgentConfigurationAccessor>(), profile, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task StoragePolicyUsesConfiguredHostProfileInsteadOfLastCaptureConfiguration()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-profile-source");
        try
        {
            using var provider = CreateRemainingOptionsProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var stale = StoragePolicyProfile(root);
            var current = stale with
            {
                Pipeline = DivergentStoragePipeline(
                DivergentStoragePipeline(stale.Pipeline, root, "retentionDays"), root, "queueForUpload")
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(stale, CancellationToken.None).ConfigureAwait(false);
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(current);
            await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                await operations.CreateRevisionAsync("stale-policy", "1", stale.Pipeline, "stale-create", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var accepted = await operations.CreateRevisionAsync("current-policy", "1", current.Pipeline, "current-create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, accepted.Lifecycle);
            await operations.ValidateRevisionAsync(accepted.RevisionId, "current-validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(accepted.RevisionId, initial.StateVersion, "current-activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(accepted.RevisionId, active.ActiveRevisionId);
            var selected = await operations.SelectLiveRevisionAsync(current, false, CancellationToken.None).ConfigureAwait(false);
            var storage = selected.Pipeline.Steps.Single(step => step.Type == "Storage").Options!.Value;
            Assert.AreEqual(90, storage.GetProperty("RetentionDays").GetInt32());
            Assert.IsTrue(storage.GetProperty("QueueForUpload").GetBoolean());
            AssertProfileStoragePlans(provider.GetRequiredService<ICameraAgentConfigurationAccessor>(), current, root, 90, true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task InitializeStoragePolicyFixtureAsync(ServiceProvider provider, CameraModuleConfig profile)
    {
        await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(profile);
    }

    private static async Task AssertStorageCommandsRejectedAndRawBindingPreservedAsync(ProcessingGraphOperationsCoordinator operations,
        CameraModuleConfig profile, string revisionId, long stateVersion, string key)
    {
        await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
            await operations.ActivateRevisionAsync(revisionId, stateVersion, key + "-activate", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
            await operations.RollbackRevisionAsync(revisionId, stateVersion, key + "-rollback", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var binding = await operations.SelectLiveRevisionAsync(profile, false, CancellationToken.None, revisionId).ConfigureAwait(false);
        Assert.AreEqual(revisionId, binding.State.RevisionId, "Raw binding preserves the immutable graph even when standard processing must skip it.");
        await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
            await operations.EnsureLiveStoragePolicyAsync(profile with { Pipeline = binding.Pipeline }, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StoragePolicyUsesActiveScheduleProfileInsteadOfFilePolicy()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-schedule-source");
        try
        {
            using var provider = CreateRemainingOptionsProvider(root);
            var configured = StoragePolicyProfile(root);
            var file = configured with
            {
                Module = new CameraModuleDescriptor(typeof(RandomImageCameraModule).FullName!),
                DeploymentLocation = DeploymentLocationSnapshot.Create(
                    "storage-test", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC"),
                Rig = configured.Rig with
                {
                    ControlPolicy = new CameraControlPolicy
                    {
                        ExposureControl = AutomaticControlOwnership.Disabled,
                        GainControl = AutomaticControlOwnership.Disabled
                    }
                },
                Schedule = new("capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("always", TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(2))],
                    Enum.GetValues<DayOfWeek>().Select(day => new CaptureWeeklyScheduleWindow(
                        $"always-{day}", day,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue, DayOffset: 1),
                        "always")).ToArray())
            };
            var current = file with { Pipeline = DivergentStoragePipeline(file.Pipeline, root, "retentionDays") };
            await InitializeStoragePolicyFixtureAsync(provider, file).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await operations.EnsureConfiguredBasicAsync(file, CancellationToken.None).ConfigureAwait(false);
            var snapshot = await provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>()
                .InitializeAsync(current, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                await operations.CreateRevisionAsync("file-policy", "1", file.Pipeline, "file-create", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var accepted = await operations.CreateRevisionAsync("schedule-policy", "1", snapshot.Configuration.Pipeline,
                "schedule-create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, accepted.Lifecycle);
            AssertProfileStoragePlans(provider.GetRequiredService<ICameraAgentConfigurationAccessor>(), snapshot.Configuration, root, 90);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CameraModuleConfig StoragePolicyProfile(string root)
        => CreateConfiguration() with
        {
            Module = new CameraModuleDescriptor("RandomImage"),
            Pipeline = RemainingOptionsPipeline(root) with
            { Steps = [.. RemainingOptionsPipeline(root).Steps.Select(step => step.Type == "Storage" ? step with { DependsOn = ["$raw", "scene"] } : step)] }
        };

    private static CapturePipelineConfig DivergentStoragePipeline(CapturePipelineConfig source, string root, string member)
        => source with
        {
            Steps = [.. source.Steps.Select(step => step.Type == "Storage" ? step with
        { Options = ChangeStorageMember(step.Options!.Value, member, member switch
        {
            "storageRoot" => JsonSerializer.Serialize(Path.Combine(root, "edited")),
            "retentionDays" => "90", "queueForUpload" => "true", "updateLatestFrame" => "false",
            _ => "[{\"Role\":\"Raw\",\"QueueForUpload\":true}]"
        }) } : step)]
        };

    private static JsonElement ChangeStorageMember(JsonElement source, string member, string token)
    {
        var json = JsonNode.Parse(source.GetRawText())!.AsObject();
        var key = json.Select(item => item.Key).FirstOrDefault(name => name.Equals(member, StringComparison.OrdinalIgnoreCase)) ?? member;
        json[key] = JsonNode.Parse(token);
        return JsonSerializer.SerializeToElement(json);
    }

    private static void AssertProfileStoragePlans(ICameraAgentConfigurationAccessor accessor, CameraModuleConfig profile, string root,
        int retentionDays = 30, bool queueForUpload = false)
    {
        var host = new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            CentralIntegration = new() { Mode = CentralIntegrationMode.Enabled },
            CaptureDistribution = new() { UploadEnabled = false }
        };
        using var retention = new RetentionBackgroundService(accessor, Options.Create(host), TimeProvider.System,
            Mock.Of<IArtifactOutbox>(), Mock.Of<IStorageCapacityProvider>(), new StoragePressureState(), NullLogger<RetentionBackgroundService>.Instance);
        var planner = typeof(RetentionBackgroundService).GetMethod("BuildRetentionPlans", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plans = (List<StorageRetentionPlan>)planner.Invoke(retention, [profile])!;
        Assert.HasCount(1, plans);
        Assert.AreEqual(Path.GetFullPath(root), plans[0].StorageRoot);
        Assert.AreEqual(retentionDays, plans[0].RetentionDays);
        CollectionAssert.AreEqual(queueForUpload ? new[] { Path.GetFullPath(root) } : [],
            ArtifactOutboxDrainService.ResolveStorageRoots(profile, host).ToArray());
    }
}
