using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class RetentionBackgroundServiceTests
{
    [TestMethod]
    public async Task ApplyRetentionAsync_RemovesOnlyExpiredArtifactDates()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-retention", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frames", "2020", "01", "01", "Raw"));
            Directory.CreateDirectory(Path.Combine(root, "frames", "2026", "07", "11", "Raw"));
            await File.WriteAllTextAsync(Path.Combine(root, "frames", "2020", "01", "01", "Raw", "expired.bin"), "expired").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "frames", "2026", "07", "11", "Raw", "current.bin"), "current").ConfigureAwait(false);
            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
            var service = new RetentionBackgroundService(
                new StubConfigurationAccessor(),
                Options.Create(new CameraAgentHostOptions()), timeProvider, new FileSystemArtifactOutbox(),
                new FixedCapacityProvider(50), new StoragePressureState(),
                NullLogger<RetentionBackgroundService>.Instance);

            await service.ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(Path.Combine(root, "frames", "2020", "01", "01", "Raw", "expired.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "frames", "2026", "07", "11", "Raw", "current.bin")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_UnderPressureShortensEligibleHistoryButPreservesCurrentDay()
    {
        var root = CreateRoot();
        try
        {
            var eligible = Path.Combine(root, "frames", "2026", "07", "09", "Raw", "eligible.bin");
            var current = Path.Combine(root, "frames", "2026", "07", "11", "Raw", "current.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(eligible)!);
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            await File.WriteAllTextAsync(eligible, "old").ConfigureAwait(false);
            await File.WriteAllTextAsync(current, "current").ConfigureAwait(false);
            var state = new StoragePressureState();
            var service = new RetentionBackgroundService(
                new StubConfigurationAccessor(), Options.Create(new CameraAgentHostOptions()),
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)),
                new FileSystemArtifactOutbox(), new FixedCapacityProvider(5), state,
                NullLogger<RetentionBackgroundService>.Instance);

            await service.ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(eligible));
            Assert.IsTrue(File.Exists(current));
            Assert.IsTrue(state.Get(root)!.IsUnderPressure);
            Assert.AreEqual(1, state.Get(root)!.EffectiveRetentionDays);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_FirstRootProbeFailureStillEvaluatesSecondRoot()
    {
        var failedRoot = CreateRoot();
        var healthyRoot = CreateRoot();
        try
        {
            var expired = Path.Combine(healthyRoot, "frames", "2020", "01", "01", "Raw", "expired.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(expired)!);
            await File.WriteAllTextAsync(expired, "expired").ConfigureAwait(false);
            var service = new RetentionBackgroundService(
                new StubConfigurationAccessor(), Options.Create(new CameraAgentHostOptions()),
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)),
                new FileSystemArtifactOutbox(),
                new DelegateCapacityProvider(root => root == Path.GetFullPath(failedRoot)
                    ? throw new IOException("probe failed")
                    : new StorageCapacity(1000, 500)),
                new StoragePressureState(), NullLogger<RetentionBackgroundService>.Instance);

            await Assert.ThrowsExactlyAsync<IOException>(
                () => service.ApplyRetentionAsync(CreateConfig(failedRoot, healthyRoot), CancellationToken.None)).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(expired));
        }
        finally
        {
            DeleteRoot(failedRoot);
            DeleteRoot(healthyRoot);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_ExpiredPendingArtifactPreservesPayloadMetadataAndIndexEntry()
    {
        var root = CreateRoot();
        try
        {
            var pending = await CreateStoredArtifactAsync(root, "pending", Guid.NewGuid()).ConfigureAwait(false);
            var eligible = await CreateStoredArtifactAsync(root, "eligible", Guid.NewGuid()).ConfigureAwait(false);
            var outbox = new FileSystemArtifactOutbox();
            await outbox.EnqueueAsync(root, CreateManifest(pending.ArtifactId, pending.RelativePath), CancellationToken.None)
                .ConfigureAwait(false);
            var service = CreateService(outbox);

            await service.ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(pending.PayloadPath));
            Assert.IsTrue(File.Exists(pending.MetadataPath));
            Assert.IsFalse(File.Exists(eligible.PayloadPath));
            Assert.IsFalse(File.Exists(eligible.MetadataPath));
            var indexLines = await File.ReadAllLinesAsync(pending.IndexPath).ConfigureAwait(false);
            Assert.HasCount(1, indexLines);
            StringAssert.Contains(indexLines[0], pending.ArtifactId.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_CommittedRawIngressHoldPreservesPayloadSidecarAndIndex()
    {
        var root = CreateRoot();
        try
        {
            var held = await CreateStoredArtifactAsync(root, "held-ingress", Guid.NewGuid()).ConfigureAwait(false);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var service = new RetentionBackgroundService(
                new StubConfigurationAccessor(),
                options,
                new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)),
                new FileSystemArtifactOutbox(),
                new FixedCapacityProvider(50),
                new StoragePressureState(),
                NullLogger<RetentionBackgroundService>.Instance,
                new FixedRawIngressHolds([
                    new RawIngressRetentionHold(
                        held.ArtifactId,
                        held.RelativePath,
                        Path.GetRelativePath(root, held.MetadataPath))
                ]));

            await service.ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(held.PayloadPath));
            Assert.IsTrue(File.Exists(held.MetadataPath));
            Assert.HasCount(1, await File.ReadAllLinesAsync(held.IndexPath).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_AfterAcknowledgementDeletesExpiredArtifact()
    {
        var root = CreateRoot();
        try
        {
            var artifact = await CreateStoredArtifactAsync(root, "pending", Guid.NewGuid()).ConfigureAwait(false);
            var outbox = new FileSystemArtifactOutbox();
            var manifest = CreateManifest(artifact.ArtifactId, artifact.RelativePath);
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
            await outbox.AcknowledgeAsync(root, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);

            await CreateService(new FileSystemArtifactOutbox())
                .ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(artifact.PayloadPath));
            Assert.IsFalse(File.Exists(artifact.MetadataPath));
            Assert.IsFalse(File.Exists(artifact.IndexPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_V2SidecarIsHeldUntilV1OutboxAcknowledgement()
    {
        var root = CreateRoot();
        try
        {
            var payload = new byte[8];
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;
            var requested = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var descriptor = template with
            {
                Timing = new(
                    requested,
                    requested.AddSeconds(1),
                    requested.AddSeconds(2),
                    requested.AddSeconds(3),
                    requested.AddSeconds(4)),
                Artifact = template.Artifact with { CreatedUtc = requested.AddSeconds(4) }
            };
            var frame = new CameraFrame(
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Layout.Width,
                descriptor.Layout.Height,
                descriptor.Layout.PixelFormat,
                payload,
                new FrameMetadata(
                    descriptor.Controls.EffectiveExposure,
                    descriptor.Controls.EffectiveGain,
                    descriptor.Controls.EffectiveTemperatureC!.Value,
                    descriptor.Artifact.SourceId,
                    Offset: descriptor.Controls.EffectiveOffset),
                descriptor.Layout.StrideBytes);
            var artifact = new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame);
            var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var stored = await storage.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
            var outbox = new FileSystemArtifactOutbox();
            var hold = CreateManifest(descriptor.Artifact.ArtifactId, stored.RelativePath);
            await outbox.EnqueueAsync(root, hold, CancellationToken.None).ConfigureAwait(false);

            await CreateService(outbox).ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(stored.AbsolutePath));
            Assert.IsTrue(File.Exists(Path.ChangeExtension(stored.AbsolutePath, ".json")));

            await outbox.AcknowledgeAsync(root, hold.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            await CreateService(outbox).ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(stored.AbsolutePath));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(stored.AbsolutePath, ".json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_StructuredSidecarIsHeldUntilSqliteOutboxAcknowledgement()
    {
        var root = CreateRoot();
        try
        {
            var source = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, 2, 2, 2, new byte[4]).Descriptor;
            var layer = PresentationLayerPayloadJson.Create(new string('A', 64), 2, 2);
            var payload = PresentationLayerPayloadJson.Serialize(layer);
            var sourceIds = new[] { source.Artifact.ArtifactId };
            var recipe = RecipeIdentityDescriptor.Create(
                "presentation-layer", "1.0.0", "retention-v1", JsonSerializer.SerializeToElement(new { }));
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Metadata,
                "scene-layer",
                ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
                sourceIds);
            var artifact = new ArtifactDescriptor(
                ProcessingIdentity.CreateArtifactId(outputIdentity),
                FrameArtifactRole.Metadata,
                "presentation-layer-step",
                "scene-layer",
                source.Timing.ReadoutCompletedUtc,
                sourceIds,
                recipe,
                PresentationLayerPayloadJson.MediaType,
                ProcessingIdentity.ComputePayloadSha256(payload));
            var relativePayload = "derived/2020/01/01/Metadata/expired.json";
            var relativeSidecar = "derived/2020/01/01/Metadata/expired.manifest.json";
            var manifest = new StructuredProcessingProductManifestV1(
                StructuredProcessingProductManifestV1.CurrentSchemaVersion,
                new(
                    source,
                    artifact,
                    outputIdentity,
                    [new("presentation-layer", "retention-v1")],
                    new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                    TimeSpan.FromSeconds(1).Ticks,
                    payload.LongLength,
                    ProcessingProductKind.Metadata,
                    PresentationLayerPayloadV1.CurrentSchemaVersion,
                    layer.ContentIdentitySha256),
                relativePayload,
                ProducerStepId: "presentation-layer-step");
            var payloadPath = Path.Combine(root, relativePayload.Replace('/', Path.DirectorySeparatorChar));
            var sidecarPath = Path.Combine(root, relativeSidecar.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
            var durableSidecar = new DurableTypedMetadataProductManifestV3(
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion,
                source.Capture,
                artifact,
                outputIdentity,
                manifest.Descriptor.Algorithms,
                manifest.Descriptor.Compatibility,
                manifest.Descriptor.TotalIntegrationTicks,
                payload.LongLength,
                relativePayload,
                JsonSerializer.SerializeToElement<object?>(null),
                ProcessingProductKind.Metadata,
                PresentationLayerPayloadV1.CurrentSchemaVersion,
                layer.ContentIdentitySha256);
            await File.WriteAllBytesAsync(
                    sidecarPath, DurableProcessingProductManifestJson.Serialize(durableSidecar))
                .ConfigureAwait(false);
            File.SetLastWriteTimeUtc(payloadPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(sidecarPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            using var outbox = new SqliteArtifactOutbox();
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);

            await CreateService(outbox).ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(payloadPath));
            Assert.IsTrue(File.Exists(sidecarPath));
            var lease = await outbox.ClaimAsync(
                root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await outbox.AcknowledgeAsync(
                root,
                lease,
                new(
                    ArtifactUploadAcknowledgement.CurrentSchemaVersion,
                    manifest.IdempotencyKey,
                    artifact.ArtifactId,
                    artifact.ChecksumSha256,
                    payload.LongLength,
                    DateTimeOffset.UtcNow,
                    manifest.SchemaVersion),
                CancellationToken.None).ConfigureAwait(false);

            await CreateService(outbox).ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(payloadPath));
            Assert.IsFalse(File.Exists(sidecarPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_MalformedOutboxManifestFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var artifact = await CreateStoredArtifactAsync(root, "expired", Guid.NewGuid()).ConfigureAwait(false);
            var outboxDirectory = Path.Combine(root, "outbox");
            Directory.CreateDirectory(outboxDirectory);
            await File.WriteAllTextAsync(Path.Combine(outboxDirectory, "invalid.json"), "{").ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateService(new FileSystemArtifactOutbox())
                .ApplyRetentionAsync(CreateConfig(root), CancellationToken.None)).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(artifact.PayloadPath));
            Assert.IsTrue(File.Exists(artifact.MetadataPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_MissingPendingPayloadFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var artifact = await CreateStoredArtifactAsync(root, "expired", Guid.NewGuid()).ConfigureAwait(false);
            var outbox = new FileSystemArtifactOutbox();
            await outbox.EnqueueAsync(
                root,
                CreateManifest(Guid.NewGuid(), Path.Combine("frames", "2020", "01", "01", "Raw", "missing.bin")),
                CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CreateService(outbox)
                .ApplyRetentionAsync(CreateConfig(root), CancellationToken.None)).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(artifact.PayloadPath));
            Assert.IsTrue(File.Exists(artifact.MetadataPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_CanceledBeforeSweepLeavesArtifactsUntouched()
    {
        var root = CreateRoot();
        try
        {
            var artifact = await CreateStoredArtifactAsync(root, "expired", Guid.NewGuid()).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => CreateService(new FileSystemArtifactOutbox())
                .ApplyRetentionAsync(CreateConfig(root), cancellation.Token)).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(artifact.PayloadPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_ProcessingHoldPreservesDerivedMetadataEvidence()
    {
        var root = CreateRoot();
        try
        {
            var artifactId = Guid.NewGuid();
            var (payload, sidecar) = await CreateDerivedMetadataEvidenceAsync(root).ConfigureAwait(false);
            var holds = new FixedProcessingHolds([
                new ProcessingRetentionHold(
                    artifactId,
                    Path.GetRelativePath(root, payload),
                    Path.GetRelativePath(root, sidecar))
            ]);

            await CreateService(new FileSystemArtifactOutbox(), root, holds)
                .ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(payload));
            Assert.IsTrue(File.Exists(sidecar));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_UnheldExpiredDerivedMetadataEvidenceIsDeleted()
    {
        var root = CreateRoot();
        try
        {
            var (payload, sidecar) = await CreateDerivedMetadataEvidenceAsync(root).ConfigureAwait(false);

            await CreateService(new FileSystemArtifactOutbox(), root, new FixedProcessingHolds([]))
                .ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(payload));
            Assert.IsFalse(File.Exists(sidecar));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(payload)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_MetadataPolicyExtendsDerivedEvidenceRetention()
    {
        var root = CreateRoot();
        try
        {
            var createdUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var directory = Path.Combine(root, "derived", "2026", "07", "01", "Metadata");
            Directory.CreateDirectory(directory);
            var payloadPath = Path.Combine(directory, "assessment.json");
            var sidecarPath = Path.Combine(directory, "assessment.manifest.json");
            var payload = "{}"u8.ToArray();
            await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
            var sourceId = Guid.Parse("80000000-0000-0000-0000-000000000001");
            var recipe = RecipeIdentityDescriptor.Create(
                "cloud-assessment",
                "1.0.0",
                "test-v1",
                JsonSerializer.SerializeToElement(new { }));
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256;
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Metadata,
                "cloud-assessment-v1",
                recipeIdentity,
                [sourceId]);
            var artifact = new ArtifactDescriptor(
                ProcessingIdentity.CreateArtifactId(outputIdentity),
                FrameArtifactRole.Metadata,
                "cloud",
                "cloud-assessment-v1",
                createdUtc,
                [sourceId],
                recipe,
                "application/json",
                ProcessingIdentity.ComputePayloadSha256(payload));
            using var nullDocument = JsonDocument.Parse("null");
            var manifest = new DurableProcessingProductManifestV1(
                DurableProcessingProductManifestV1.CurrentSchemaVersion,
                new CaptureIdentityDescriptor(
                    "agent", "rig", 1, Guid.Parse("80000000-0000-0000-0000-000000000002")),
                artifact,
                outputIdentity,
                [new ProcessingAlgorithmIdentity("cloud", "v1")],
                new ProcessingCompatibilityIdentity("rig", "orientation", "cal", "mask", "sensor", "setpoint", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                payload.Length,
                Path.GetRelativePath(root, payloadPath),
                nullDocument.RootElement.Clone());
            await File.WriteAllBytesAsync(
                sidecarPath,
                DurableProcessingProductManifestJson.Serialize(manifest)).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(payloadPath, createdUtc.UtcDateTime);
            File.SetLastWriteTimeUtc(sidecarPath, createdUtc.UtcDateTime);

            await CreateService(new FileSystemArtifactOutbox(), root)
                .ApplyRetentionAsync(CreateConfigWithMetadataPolicy(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(payloadPath));
            Assert.IsTrue(File.Exists(sidecarPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_DurableProductsUseRawIngressPlanWhenStorageRootDiffers()
    {
        var rawRoot = CreateRoot();
        var storageRoot = CreateRoot();
        try
        {
            var expired = Path.Combine(rawRoot, "derived", "2020", "01", "01", "AnnotatedPreview", "expired.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(expired)!);
            await File.WriteAllBytesAsync(expired, [0xFF, 0xD8, 0xFF, 0xD9]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(expired, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var config = CreateConfig() with
            {
                Pipeline = new CapturePipelineConfig(
                    [
                        new CaptureProcessingStepConfig(
                            "JpegEncoding",
                            "final-jpeg",
                            DependsOn: ["$raw"],
                            Publication: new CaptureProcessingPublicationPolicy(
                                CaptureProcessingPersistenceMode.DurableLocal)),
                        new CaptureProcessingStepConfig("Telemetry", "telemetry", DependsOn: ["final-jpeg"]),
                        new CaptureProcessingStepConfig(
                            NoOpFileStorageProcessingStep.StableAlias,
                            "storage",
                            Options: JsonSerializer.SerializeToElement(new NoOpFileStorageProcessingStepOptions
                            {
                                StorageRoot = storageRoot,
                                RetentionDays = 1
                            }),
                            DependsOn: ["telemetry"])
                    ],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var service = CreateService(new FileSystemArtifactOutbox(), rawRoot);

            await service.ApplyRetentionAsync(config, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(expired));
        }
        finally
        {
            DeleteRoot(rawRoot);
            DeleteRoot(storageRoot);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplyRetentionAsync_ExplicitRawRootPlanWinsRegardlessOfStorageOrder(bool rawStorageFirst)
    {
        var rawRoot = CreateRoot();
        var archiveRoot = CreateRoot();
        try
        {
            var retained = Path.Combine(rawRoot, "derived", "2026", "07", "01", "AnnotatedPreview", "retained.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
            await File.WriteAllBytesAsync(retained, [0xFF, 0xD8, 0xFF, 0xD9]).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(retained, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
            CaptureProcessingStepConfig Storage(string id, string root, int days) => new(
                NoOpFileStorageProcessingStep.StableAlias,
                id,
                Options: JsonSerializer.SerializeToElement(new NoOpFileStorageProcessingStepOptions
                {
                    StorageRoot = root,
                    RetentionDays = days
                }),
                DependsOn: ["final-jpeg"]);
            var rawStorage = Storage("raw-storage", rawRoot, 30);
            var archiveStorage = Storage("archive-storage", archiveRoot, 1);
            var config = CreateConfig() with
            {
                Pipeline = new CapturePipelineConfig(
                    [
                        new CaptureProcessingStepConfig(
                            "JpegEncoding", "final-jpeg", DependsOn: ["$raw"],
                            Publication: new CaptureProcessingPublicationPolicy(
                                CaptureProcessingPersistenceMode.DurableLocal)),
                        .. (rawStorageFirst
                            ? new[] { rawStorage, archiveStorage }
                            : new[] { archiveStorage, rawStorage })
                    ],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };

            await CreateService(new FileSystemArtifactOutbox(), rawRoot)
                .ApplyRetentionAsync(config, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(retained));
        }
        finally
        {
            DeleteRoot(rawRoot);
            DeleteRoot(archiveRoot);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_DurableOutputWithoutStoragePolicyFailsClosed()
    {
        var rawRoot = CreateRoot();
        try
        {
            var config = CreateConfig() with
            {
                Pipeline = new CapturePipelineConfig(
                    [new CaptureProcessingStepConfig(
                        "JpegEncoding", "final-jpeg", DependsOn: ["$raw"],
                        Publication: new CaptureProcessingPublicationPolicy(
                            CaptureProcessingPersistenceMode.DurableLocal))],
                    CapturePipelineSchemaVersions.ExplicitV2,
                    CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CreateService(new FileSystemArtifactOutbox(), rawRoot)
                    .ApplyRetentionAsync(config, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            DeleteRoot(rawRoot);
        }
    }

    [TestMethod]
    public async Task ApplyRetentionAsync_RecasedStepIdsMergeToMaximumWithoutChangingSemanticSourceId()
    {
        var root = CreateRoot();
        try
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, 2, 2, 2, payload);
            var reconstruction = FrameReconstructor.TryReconstruct(manifest.Descriptor, payload, out var frame);
            Assert.IsTrue(reconstruction.IsValid);
            var artifact = new FrameArtifact(
                manifest.Descriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                frame!);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var stored = await storage.SaveAsync(
                root,
                artifact,
                manifest.Descriptor,
                "custom-producer-node",
                CancellationToken.None).ConfigureAwait(false);
            var sidecarPath = Path.ChangeExtension(stored.AbsolutePath, ".json");
            var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            Assert.AreEqual("custom-producer-node", parsed.Document!.Manifest!.ProducerStepId);
            Assert.AreEqual(manifest.Descriptor.Artifact.SourceId, parsed.Document.Manifest.Descriptor.Artifact.SourceId);
            var evaluatedUtc = manifest.Descriptor.Artifact.CreatedUtc.AddDays(10);
            var config = CreateConfig() with
            {
                Pipeline = new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        NoOpFileStorageProcessingStep.StableAlias,
                        Options: JsonSerializer.SerializeToElement(new NoOpFileStorageProcessingStepOptions
                        {
                            StorageRoot = root,
                            RetentionDays = 1,
                            Policies =
                            [
                                new ArtifactStoragePolicyOptions
                                {
                                    StepId = "custom-producer-node",
                                    RetentionDays = 1
                                }
                            ]
                        })),
                    new CaptureProcessingStepConfig(
                        NoOpFileStorageProcessingStep.StableAlias,
                        Options: JsonSerializer.SerializeToElement(new NoOpFileStorageProcessingStepOptions
                        {
                            StorageRoot = root,
                            RetentionDays = 1,
                            Policies =
                            [
                                new ArtifactStoragePolicyOptions
                                {
                                    StepId = "CUSTOM-PRODUCER-NODE",
                                    RetentionDays = 30
                                }
                            ]
                        }))
                ])
            };
            var service = new RetentionBackgroundService(
                new StubConfigurationAccessor(),
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }),
                new FixedTimeProvider(evaluatedUtc),
                new FileSystemArtifactOutbox(),
                new FixedCapacityProvider(50),
                new StoragePressureState(),
                NullLogger<RetentionBackgroundService>.Instance);

            await service.ApplyRetentionAsync(config, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(stored.AbsolutePath));
            Assert.IsTrue(File.Exists(sidecarPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static CameraModuleConfig CreateConfig(params string[] roots)
    {
        return new CameraModuleConfig(new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(new SensorProfile("Virtual", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(roots.Select(root => new CaptureProcessingStepConfig(
                NoOpFileStorageProcessingStep.StableAlias,
                Options: System.Text.Json.JsonSerializer.SerializeToElement(new { storageRoot = root, retentionDays = 7 }))).ToArray()));
    }

    private static CameraModuleConfig CreateConfigWithMetadataPolicy(string root)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("Virtual", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig([new CaptureProcessingStepConfig(
                NoOpFileStorageProcessingStep.StableAlias,
                Options: JsonSerializer.SerializeToElement(new
                {
                    storageRoot = root,
                    retentionDays = 7,
                    policies = new[]
                    {
                        new
                        {
                            role = FrameArtifactRole.Metadata,
                            variant = "cloud-assessment-v1",
                            retentionDays = 30
                        }
                    }
                }))]));

    private static RetentionBackgroundService CreateService(
        IArtifactOutbox outbox,
        string? root = null,
        IProcessingRetentionHolds? processingHolds = null)
        => new(
            new StubConfigurationAccessor(),
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = root ?? "data" }),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)),
            outbox,
            new FixedCapacityProvider(50),
            new StoragePressureState(),
            NullLogger<RetentionBackgroundService>.Instance,
            processingHolds: processingHolds);

    private static async Task<(string Payload, string Sidecar)> CreateDerivedMetadataEvidenceAsync(string root)
    {
        var directory = Path.Combine(root, "derived", "2020", "01", "01", "Metadata");
        Directory.CreateDirectory(directory);
        var payload = Path.Combine(directory, "assessment.json");
        var sidecar = Path.Combine(directory, "assessment.manifest.json");
        await File.WriteAllTextAsync(payload, "{}").ConfigureAwait(false);
        await File.WriteAllTextAsync(sidecar, "{}").ConfigureAwait(false);
        var expired = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(payload, expired);
        File.SetLastWriteTimeUtc(sidecar, expired);
        return (payload, sidecar);
    }

    private static async Task<StoredArtifact> CreateStoredArtifactAsync(string root, string stem, Guid artifactId)
    {
        var directory = Path.Combine(root, "frames", "2020", "01", "01", "Raw");
        Directory.CreateDirectory(directory);
        var payloadPath = Path.Combine(directory, string.Concat(stem, ".bin"));
        var metadataPath = Path.ChangeExtension(payloadPath, ".json");
        await File.WriteAllTextAsync(payloadPath, stem).ConfigureAwait(false);
        await File.WriteAllTextAsync(metadataPath, "{}").ConfigureAwait(false);
        var indexDirectory = Path.Combine(root, "index");
        Directory.CreateDirectory(indexDirectory);
        var indexPath = Path.Combine(indexDirectory, "frames_2020-01-01.jsonl");
        await File.AppendAllTextAsync(indexPath, $"{{\"artifactId\":\"{artifactId}\"}}{Environment.NewLine}")
            .ConfigureAwait(false);
        return new StoredArtifact(
            artifactId, payloadPath, metadataPath, indexPath, Path.GetRelativePath(root, payloadPath));
    }

    private static ArtifactUploadManifest CreateManifest(Guid artifactId, string relativePath)
        => new(
            "v1", "agent-a", artifactId, Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 4, new string('A', 64), DateTimeOffset.UnixEpoch,
            "raw-v1", relativePath);

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "skymonitor-retention", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record StoredArtifact(
        Guid ArtifactId,
        string PayloadPath,
        string MetadataPath,
        string IndexPath,
        string RelativePath);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FixedCapacityProvider(double availablePercent) : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot)
            => new(1000, (long)(10 * availablePercent));
    }

    private sealed class DelegateCapacityProvider(Func<string, StorageCapacity> getCapacity) : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => getCapacity(Path.GetFullPath(storageRoot));
    }

    private sealed class FixedRawIngressHolds(IReadOnlyList<RawIngressRetentionHold> holds) : IRawIngressRetentionHolds
    {
        public ValueTask<IReadOnlyList<RawIngressRetentionHold>> GetRetentionHoldsAsync(
            string storageRoot,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(holds);
    }

    private sealed class FixedProcessingHolds(IReadOnlyList<ProcessingRetentionHold> holds) : IProcessingRetentionHolds
    {
        public ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
            string storageRoot,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(holds);
    }

    private sealed class StubConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => false;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<CameraModuleConfig>(new NotSupportedException());
    }
}
