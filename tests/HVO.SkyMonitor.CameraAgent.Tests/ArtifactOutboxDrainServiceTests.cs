using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ArtifactOutboxDrainServiceTests
{
    [TestMethod]
    public void ShouldRemoveUploadedArtifact_OnlyRemovesCopiesOutsideDurableIngressRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "raw-ingress");

        Assert.IsFalse(ArtifactOutboxDrainService.ShouldRemoveUploadedArtifact(root, root));
        Assert.IsFalse(ArtifactOutboxDrainService.ShouldRemoveUploadedArtifact(root, root));
        Assert.IsTrue(ArtifactOutboxDrainService.ShouldRemoveUploadedArtifact(
            Path.Combine(root, "archive"), root));
    }

    [TestMethod]
    public void ResolveStorageRoot_WhenOptionalStorageIsNotConfigured_ReturnsNull()
    {
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            CapturePipelineConfig.Empty,
            AgentId: "agent");

        Assert.IsNull(ArtifactOutboxDrainService.ResolveStorageRoot(config));
    }

    [TestMethod]
    public void ResolveStorageRoots_WhenUploadLaneIsEnabled_IncludesRawIngressWithoutStorageStep()
    {
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            CapturePipelineConfig.Empty,
            AgentId: "agent");
        var root = Path.Combine(Path.GetTempPath(), "raw-ingress");
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = true }
        };

        var roots = ArtifactOutboxDrainService.ResolveStorageRoots(config, options);

        Assert.HasCount(1, roots);
        Assert.AreEqual(Path.GetFullPath(root), roots[0]);
    }

    [TestMethod]
    public void ResolveStorageRoots_WhenOnlyArtifactPolicyUploads_IncludesStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "policy-storage");
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            new CapturePipelineConfig([new CaptureProcessingStepConfig(
                NoOpFileStorageProcessingStep.StableAlias,
                Options: JsonSerializer.SerializeToElement(new NoOpFileStorageProcessingStepOptions
                {
                    StorageRoot = root,
                    QueueForUpload = false,
                    Policies = [new ArtifactStoragePolicyOptions { Role = FrameArtifactRole.Preview, QueueForUpload = true }]
                }))]),
            AgentId: "agent");

        var roots = ArtifactOutboxDrainService.ResolveStorageRoots(config, new CameraAgentHostOptions());

        Assert.HasCount(1, roots);
        Assert.AreEqual(Path.GetFullPath(root), roots[0]);
    }

    [TestMethod]
    public void StorageResolver_AssignsStableAliasesWithoutExposingRoots()
    {
        var rawRoot = Path.Combine(Path.GetTempPath(), "private-raw");
        var archiveRoot = Path.Combine(Path.GetTempPath(), "private-archive");
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            new CapturePipelineConfig([new CaptureProcessingStepConfig(
                NoOpFileStorageProcessingStep.StableAlias,
                Options: JsonSerializer.SerializeToElement(new NoOpFileStorageProcessingStepOptions
                {
                    StorageRoot = archiveRoot,
                    QueueForUpload = false
                }))]),
            AgentId: "agent");
        var options = new CameraAgentHostOptions
        {
            RawIngressRoot = rawRoot,
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            CaptureDistribution = new CaptureDistributionOptions { UploadEnabled = false }
        };

        var locations = CameraAgentStorageResolver.Resolve(config, options);

        Assert.HasCount(2, locations);
        Assert.AreEqual("raw-ingress", locations[0].Alias);
        Assert.AreEqual("storage-1", locations[1].Alias);
        Assert.AreEqual(Path.GetFullPath(rawRoot), locations[0].Root);
        Assert.AreEqual(Path.GetFullPath(archiveRoot), locations[1].Root);
        Assert.IsFalse(locations.Any(location => location.Alias.Contains("private", StringComparison.OrdinalIgnoreCase)));

        var defaulted = config with
        {
            Pipeline = new CapturePipelineConfig(
                [new CaptureProcessingStepConfig(NoOpFileStorageProcessingStep.StableAlias)])
        };
        var defaultRoots = ArtifactOutboxDrainService.ResolveLocalStorageRoots(defaulted, options);
        Assert.HasCount(2, defaultRoots);
        Assert.AreEqual(Path.GetFullPath("/tmp/camera"), defaultRoots[1]);
        var disabled = defaulted with
        {
            Pipeline = new CapturePipelineConfig([new CaptureProcessingStepConfig(
                NoOpFileStorageProcessingStep.StableAlias,
                Enabled: false)])
        };
        Assert.HasCount(1, ArtifactOutboxDrainService.ResolveLocalStorageRoots(disabled, options));
    }

    [TestMethod]
    public void UploadIdentity_SameRoleWithDistinctVariantRecipeLabels_DoesNotCollide()
    {
        var frameId = Guid.NewGuid();
        var first = TestManifest(FrameArtifactRole.Preview) with { FrameId = frameId, RecipeVersion = "preview-v1:display" };
        var second = TestManifest(FrameArtifactRole.Preview) with { FrameId = frameId, RecipeVersion = "preview-v1:local" };

        Assert.AreNotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [TestMethod]
    [DataRow(1, 10)]
    [DataRow(2, 20)]
    [DataRow(3, 40)]
    [DataRow(4, 60)]
    [DataRow(30, 60)]
    public void CalculateRetryDelay_UsesBoundedExponentialBackoff(int attempt, int expectedSeconds)
    {
        var delay = ArtifactOutboxDrainService.CalculateRetryDelay(
            attempt,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(60));

        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    private static ArtifactUploadManifest TestManifest(FrameArtifactRole role)
        => new(
            ArtifactUploadManifest.CurrentSchemaVersion,
            "agent",
            Guid.NewGuid(),
            Guid.NewGuid(),
            role,
            "application/octet-stream",
            1,
            new string('A', 64),
            DateTimeOffset.UnixEpoch,
            "recipe-v1",
            "frames/2026-07-14/raw.bin");
}
