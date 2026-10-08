using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CompactSceneUploadLaneTests
{
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task VirtualCaptureUploadsOneCanonicalSceneAcrossArrivalOrderAndOptionalStorage(
        bool rawFirst, bool withStorage)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-compact-scene-upload", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CameraAgent:RawIngressRoot"] = root,
                    ["CameraAgent:RawIngressReserveBytes"] = "0",
                    ["CameraAgent:AgentId"] = "scene-upload-agent",
                    ["CameraAgent:CentralIntegration:Mode"] = "Enabled",
                    ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true"
                }).Build());
            using var provider = services.BuildServiceProvider();
            var config = CreateConfiguration(root, withStorage);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var module = new VirtualSkyCameraModule(TimeProvider.System,
                provider.GetRequiredService<ICelestialCatalog>(), provider.GetRequiredService<IProjectedSceneStore>(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var request = new CaptureRequest(new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(5), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var submission = new CaptureLoopSubmission(request, capture, request.RequestedStartUtc,
                request.TargetInterval, TimeSpan.Zero);
            var accepted = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(accepted);
            var uploadLane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static value => value.Name == "upload");
            var uploadLease = await ingress.ClaimAsync(uploadLane, "scene-upload-test", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(uploadLease);
            var receipt = uploadLease.Context.RawCapture;
            Assert.IsTrue(receipt.Manifest.Scene?.RequiresProjectedScene);
            var rawBytes = CaptureContractJson.Serialize(receipt.Manifest);
            var upload = provider.GetRequiredService<UploadCaptureLaneHandler>();
            var context = uploadLease.Context;
            var outbox = provider.GetRequiredService<IArtifactOutbox>();
            if (rawFirst)
            {
                var pending = await upload.HandleAsync(uploadLease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Deferred, pending.Outcome, pending.Reason);
                Assert.AreEqual("outbox-scene-pending", pending.Reason);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Deferred,
                    await ingress.FailAsync(uploadLease, pending, CancellationToken.None).ConfigureAwait(false));
                Assert.HasCount(1, await ingress.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
                Assert.AreEqual(1L, (await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).PendingCount);
            }
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static value => value.Name == "standard");
            var lease = await ingress.ClaimAsync(lane, "scene-upload-test", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var processed = await provider.GetRequiredService<StandardCaptureLaneHandler>()
                .HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            await ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            if (rawFirst)
            {
                clock.Now = clock.Now.AddMinutes(1);
                uploadLease = await ingress.ClaimAsync(uploadLane, "scene-upload-retry", config, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(uploadLease);
                Assert.AreEqual(1, uploadLease.Attempt, "Waiting for a scene never consumes a failure attempt.");
                context = uploadLease.Context;
            }
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var sceneNode = await store.ReadNodeAsync(receipt.Manifest.Descriptor.Capture.CaptureId, "scene",
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(sceneNode);
            var output = sceneNode.Outputs.Single();
            var payloadPath = Path.Combine(root, output.PayloadRelativePath);
            var sidecarPath = Path.Combine(root, output.SidecarRelativePath);
            var payload = await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            var scene = ProjectedSceneJson.Parse(payload).Scene;
            Assert.IsNotNull(scene);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(receipt.Manifest.Descriptor), scene.Source.ArtifactIdentitySha256);
            foreach (var attempt in new[] { 1, 2 })
            {
                var uploaded = await upload.HandleAsync(context with { Attempt = attempt }, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, uploaded.Outcome, uploaded.Reason);
                Assert.AreEqual(2L, (await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).PendingCount);
            }
            await ingress.CompleteAsync(uploadLease, CancellationToken.None).ConfigureAwait(false);
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var manifest = await persistence.FindCommittedSceneUploadAsync(receipt.Manifest.Descriptor, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(manifest);
            var queued = await outbox.ReadAsync(root, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(queued);
            CollectionAssert.AreEqual(StructuredProcessingProductManifestJson.Serialize(manifest), queued.ManifestBytes.ToArray());
            CollectionAssert.AreEqual(sidecar, output.EvidenceJson);
            CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false));
            CollectionAssert.AreEqual(rawBytes, CaptureContractJson.Serialize(receipt.Manifest));
            Assert.HasCount(2, await outbox.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
            foreach (var path in new[] { payloadPath, sidecarPath })
            {
                var original = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                var tampered = original.ToArray();
                tampered[0] ^= 1;
                await File.WriteAllBytesAsync(path, tampered).ConfigureAwait(false);
                var rejected = await upload.HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, rejected.Outcome, rejected.Reason);
                await File.WriteAllBytesAsync(path, original).ConfigureAwait(false);
            }
            var foreign = receipt.Manifest.Descriptor with
            {
                Timing = receipt.Manifest.Descriptor.Timing with
                { RequestedStartUtc = receipt.Manifest.Descriptor.Timing.RequestedStartUtc.AddSeconds(1) }
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await persistence.FindCommittedSceneUploadAsync(foreign, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(2L, (await outbox.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).PendingCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public async Task StorageImageUploadIncludesSceneDespiteOptionalMetadataMask(
        bool rawImage, bool uploadImages)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-storage-scene-upload", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateStorageProvider(root);
            var config = CreateStorageConfiguration(root, rawImage, uploadImages, declareScene: true);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(provider.GetRequiredService<CaptureLanePolicy>().Definitions
                .Single(static lane => lane.Name == "upload").Enabled);
            var module = new VirtualSkyCameraModule(TimeProvider.System,
                provider.GetRequiredService<ICelestialCatalog>(), provider.GetRequiredService<IProjectedSceneStore>(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var request = new CaptureRequest(new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(5), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var submission = new CaptureLoopSubmission(request, capture, request.RequestedStartUtc,
                request.TargetInterval, TimeSpan.Zero);
            Assert.IsNotNull(await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false));
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static value => value.Name == "standard");
            var lease = await ingress.ClaimAsync(lane, "storage-scene-test", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            Assert.IsTrue(lease.Context.RawCapture.Manifest.Scene?.RequiresProjectedScene);
            var processed = await provider.GetRequiredService<StandardCaptureLaneHandler>()
                .HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            await ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            var outbox = provider.GetRequiredService<IArtifactOutbox>();
            var records = await outbox.ReadRecentDeliveryAsync(root, 25, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(uploadImages ? 2 : 0, records);
            var scene = await provider.GetRequiredService<CaptureProcessingPersistence>().FindCommittedSceneUploadAsync(
                lease.Context.RawCapture.Manifest.Descriptor, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(scene, "The canonical scene remains local even when uploads are disabled.");
            var queued = await outbox.ReadAsync(root, scene.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            if (uploadImages)
            {
                CollectionAssert.AreEquivalent(new FrameArtifactRole?[]
                    { FrameArtifactRole.Metadata, rawImage ? FrameArtifactRole.Raw : FrameArtifactRole.Preview },
                    records.Select(static item => item.Role).ToArray());
                Assert.IsNotNull(queued);
                CollectionAssert.AreEqual(StructuredProcessingProductManifestJson.Serialize(scene), queued.ManifestBytes.ToArray());
                var imagePath = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.json", SearchOption.AllDirectories)
                    .Single(path => Path.GetFileName(Path.GetDirectoryName(path)) == (rawImage ? "Raw" : "Preview"));
                var imageBytes = await File.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
                var imageManifest = CaptureContractJson.ParseManifest(imageBytes).Document!.Manifest;
                var imageRecord = await outbox.ReadAsync(root, imageManifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(imageRecord);
                CollectionAssert.AreEqual(imageBytes, imageRecord.ManifestBytes.ToArray());
            }
            else
            {
                Assert.IsNull(queued, "An optional metadata mask still suppresses independent scene publication.");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StorageImageUploadRequiresAnExplicitSceneDependencyBeforeCapture(bool rawImage)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-storage-scene-validation", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = CreateStorageProvider(root);
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var config = CreateStorageConfiguration(root, rawImage, uploadImages: true, declareScene: false);
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));
            StringAssert.Contains(exception.Message, "must explicitly depend on ProjectedScene", StringComparison.Ordinal);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "frames")), "Invalid new publication never reaches raw commit.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider CreateStorageProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:AgentId"] = "scene-upload-agent",
                ["CameraAgent:CentralIntegration:Mode"] = "Enabled",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateStorageConfiguration(
        string root, bool rawImage, bool uploadImages, bool declareScene)
    {
        var dependencies = new List<string> { rawImage ? "$raw" : "preview" };
        if (declareScene) dependencies.Add("scene");
        return CreateConfiguration(root, withStorage: false) with
        {
            Pipeline = new CapturePipelineConfig([
                new("ProjectedScene", "scene", DependsOn: ["$raw"]),
                new("Preview", "preview", DependsOn: ["$raw"]),
                new("Storage", "storage", Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                {
                    StorageRoot = root,
                    QueueForUpload = uploadImages,
                    UpdateLatestFrame = false,
                    Policies = declareScene ? [new() { Role = FrameArtifactRole.Metadata, QueueForUpload = false }] : []
                }), DependsOn: dependencies)
            ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CameraModuleConfig CreateConfiguration(string root, bool withStorage)
    {
        var steps = new List<CaptureProcessingStepConfig>
        { new("ProjectedScene", "scene", DependsOn: ["$raw"]) };
        if (withStorage) steps.Add(new("Storage", "archive", Options: JsonSerializer.SerializeToElement(
            new FileStorageCaptureProcessingStepOptions
            { StorageRoot = Path.Combine(root, "archive"), QueueForUpload = true, UpdateLatestFrame = false }),
            DependsOn: ["scene"]));
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            { seed = 63, maximumResults = 1, shotNoiseEnabled = false })),
            new CameraRigConfig(
                new SensorProfile("SceneUploadFixture", 64, 48, 5.86, SensorColorMode.Mono,
                    CameraPixelFormat.Mono16, SensorResponseMode.Monochrome, SensorRecipeVersion: "scene-upload-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 32, 24, 23,
                    CalibrationVersion: "scene-upload-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(steps, CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent), AgentId: "scene-upload-agent");
    }
}
