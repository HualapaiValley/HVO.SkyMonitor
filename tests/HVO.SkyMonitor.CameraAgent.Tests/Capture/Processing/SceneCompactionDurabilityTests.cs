using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task SceneArchiveDestinationsQueueTheSameCommittedPayloadWithoutCopyingGeometry()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var descriptor = fixture.Manifest.Descriptor;
            var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(
                new VisibleSceneRequest(descriptor.Timing.ExposureStartedUtc, new ObserverLocation(0, 0, 0),
                    new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
                    new CatalogQuery(6.5, 10),
                    new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('A', 64), "test", "1"),
                    projectionVersion: "projection-v1")).ConfigureAwait(false);
            var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
                ProjectedSceneImageTransformV1.Identity(2, 2),
                new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                    CaptureContractJson.ComputeDescriptorSha256(descriptor)), "calibration-v1", "projection-v1");
            var product = CreateProjectedSceneProduct(fixture.Item, scene, "projected-scene-v1") with
            {
                MediaType = StructuredProcessingProductContracts.ProjectedSceneMediaType
            };
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var producer = new FixedMetadataProducingStep(product);
            var graph = new CaptureProcessingGraph([
                new CaptureProcessingGraphNode("scene", producer, [], true, BuiltInProcessingRecipes.ProjectedScene,
                    FrameArtifactRole.Metadata, product.Variant, new string('F', 64))
            ]);
            var produced = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence,
                telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, produced.Outcome, produced.Reason);
            var node = await store.ReadNodeAsync(descriptor.Capture.CaptureId, "scene", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            var committed = node.Outputs.Single();
            var before = await File.ReadAllBytesAsync(Path.Combine(root, committed.PayloadRelativePath)).ConfigureAwait(false);
            var beforeSidecar = await File.ReadAllBytesAsync(Path.Combine(root, committed.SidecarRelativePath)).ConfigureAwait(false);
            var queued = new List<(string Root, StructuredProcessingProductManifestV1 Manifest)>();
            var outbox = new Mock<IArtifactOutbox>(MockBehavior.Strict);
            outbox.Setup(value => value.EnqueueAsync(It.IsAny<string>(), It.IsAny<StructuredProcessingProductManifestV1>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, StructuredProcessingProductManifestV1, CancellationToken>((path, manifest, _) => queued.Add((path, manifest)))
                .Returns(ValueTask.CompletedTask);
            var context = new CaptureProcessingContext(fixture.Item.Config, fixture.Item.Submission, fixture.Item.RawCapture);
            context.BeginNode("scene", []);
            context.RegisterProcessingProduct(product);
            context.BeginNode("archive", ["scene"]);
            foreach (var destination in new[] { "archive-a", "archive-b" })
            {
                var archive = Path.Combine(root, destination);
                var step = new FileStorageCaptureProcessingStep(
                    new CaptureProcessingStepMetadata("archive", "Storage", 100),
                    new FileStorageCaptureProcessingStepOptions { StorageRoot = archive, QueueForUpload = true, UpdateLatestFrame = false },
                    Mock.Of<ILatestFrameAccessor>(), Mock.Of<IFrameStorageService>(), outbox.Object,
                    Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }),
                    NullLogger<FileStorageCaptureProcessingStep>.Instance, persistence);
                await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(Directory.Exists(Path.Combine(archive, "derived")));
            }
            Assert.HasCount(2, queued);
            foreach (var entry in queued)
            {
                Assert.AreEqual(root, entry.Root);
                Assert.AreEqual(committed.PayloadRelativePath, entry.Manifest.RelativeArtifactPath);
                Assert.AreEqual(
                    CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(committed.Artifact)).GetRawText(),
                    CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(entry.Manifest.Descriptor.Artifact)).GetRawText());
                Assert.AreEqual(product.ContentIdentitySha256, entry.Manifest.Descriptor.ContentIdentitySha256);
            }
            CollectionAssert.AreEqual(before,
                await File.ReadAllBytesAsync(Path.Combine(root, committed.PayloadRelativePath)).ConfigureAwait(false));
            CollectionAssert.AreEqual(beforeSidecar,
                await File.ReadAllBytesAsync(Path.Combine(root, committed.SidecarRelativePath)).ConfigureAwait(false));
            CollectionAssert.AreEqual(beforeSidecar, committed.EvidenceJson);
            Assert.AreEqual(product.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(before));
            outbox.Verify(value => value.EnqueueAsync(root, It.IsAny<StructuredProcessingProductManifestV1>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            outbox.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [TestMethod]
    [TestCategory("Unit")]
    public async Task SceneRetentionUsesHistoricalArchiveRootsAfterCanonicalConsumersExpire()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root, includeSceneEvidence: true).ConfigureAwait(false);
            var descriptor = fixture.Manifest.Descriptor;
            var manifest = fixture.Manifest with
            {
                Scene = fixture.Manifest.Scene! with
                {
                    SceneId = new string('D', 64),
                    SceneUtc = descriptor.Timing.ExposureStartedUtc,
                    ProjectedSceneSchemaVersion = SceneProvenance.RetainedProjectedSceneSchemaVersion,
                    ProjectedSceneStageSchemaVersion = "projected-scene-stage-v1",
                    ProjectedSceneStageKey = new string('A', 64),
                    RigProfileHashSha256 = new string('B', 64)
                }
            };
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            var archiveRoot = Path.Combine(root, "old-archive");
            var otherArchiveRoot = Path.Combine(root, "second-archive");
            var pipeline = new CapturePipelineConfig([
                new CaptureProcessingStepConfig("Storage", "archive", Options:
                    System.Text.Json.JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = archiveRoot, RetentionDays = 365 })),
                new CaptureProcessingStepConfig("Storage", "second-archive", Options:
                    System.Text.Json.JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = otherArchiveRoot, RetentionDays = 365 }))]);
            var now = DateTimeOffset.UtcNow;
            var revision = new ProcessingGraphRevisionSnapshot(
                new ProcessingGraphRevisionState(new string('C', 64), "old", "1", ProcessingGraphRevisionLifecycle.Validated,
                    new string('D', 64), new string('E', 64), new string('F', 64), now, now, null, null),
                pipeline, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(pipeline, WebEnumJsonOptions), "{}"u8.ToArray(), "{}"u8.ToArray(), []);
            var journal = new HVO.SkyMonitor.CameraAgent.Common.RawIngress.SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await store.UpsertConfiguredBasicRevisionAsync(revision, CancellationToken.None).ConfigureAwait(false);
            await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId,
                descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            var entry = new HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressJournalEntry(
                descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence, descriptor.Capture.CaptureId,
                descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeManifestSha256(manifest), descriptor.Artifact.ChecksumSha256,
                descriptor.Layout.ByteLength, manifest.RelativeArtifactPath, "raw.json", CaptureContractJson.Serialize(manifest),
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc);
            await journal.CommitAsync(entry, null, null, [], new ProcessingLiveExecutionSeed(Guid.NewGuid(),
                descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, revision, now, now, now.AddHours(1),
                now.AddHours(2), "{}"u8.ToArray()), CancellationToken.None).ConfigureAwait(false);
            var typed = CreateTypedMetadataProduct();
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata,
                descriptor.Artifact.Variant, typed.Recipe.IdentitySha256, [descriptor.Artifact.ArtifactId]);
            var sceneId = ProcessingIdentity.CreateArtifactId(outputIdentity);
            var sceneManifest = new DurableTypedMetadataProductManifestV3(
                DurableTypedMetadataProductManifestV3.CurrentSchemaVersion, descriptor.Capture,
                descriptor.Artifact with
                {
                    ArtifactId = sceneId,
                    Role = FrameArtifactRole.Metadata,
                    SourceArtifactIds = [descriptor.Artifact.ArtifactId],
                    Recipe = CreateTypedMetadataProduct().Recipe.Descriptor,
                    MediaType = "application/json"
                }, outputIdentity, [],
                CreateTypedMetadataProduct().Compatibility, 0, 2, "scene.json", null,
                ProcessingProductKind.Metadata, SceneProvenance.RetainedProjectedSceneSchemaVersion, new string('2', 64));
            var candidate = new DurableProcessingEvidence(outputIdentity, sceneId, descriptor.Capture.CaptureId,
                FrameArtifactRole.Metadata, "scene.json", "scene.manifest.json",
                DurableProcessingProductManifestJson.Serialize(sceneManifest), 1, "Available", null);
            Assert.IsTrue(await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                candidate, CancellationToken.None).ConfigureAwait(false), "The canonical raw is a consumer.");
            File.Delete(Path.Combine(root, "raw.bin"));
            File.Delete(Path.Combine(root, "raw.json"));
            var date = descriptor.Timing.ExposureStartedUtc;
            var relative = $"frames/{date:yyyy/MM/dd}/Raw/archived.bin";
            var archivePath = Path.Combine(archiveRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            await File.WriteAllBytesAsync(archivePath, new byte[8]).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.ChangeExtension(archivePath, ".json"),
                CaptureContractJson.Serialize(manifest with { RelativeArtifactPath = relative })).ConfigureAwait(false);
            var otherArchivePath = Path.Combine(otherArchiveRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(otherArchivePath)!);
            File.Copy(archivePath, otherArchivePath);
            File.Copy(Path.ChangeExtension(archivePath, ".json"), Path.ChangeExtension(otherArchivePath, ".json"));
            // There are deliberately no processing_outputs or execution-output associations for the archive.
            // A new active configuration removes this Storage destination, but its completed history remains.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE processing_executions SET status = 'Completed';";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await store.UpsertConfiguredBasicRevisionAsync(revision with
            {
                State = revision.State with { RevisionId = new string('3', 64), Revision = "2" },
                Pipeline = CapturePipelineConfig.Empty,
                PipelineJson = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(CapturePipelineConfig.Empty, WebEnumJsonOptions)
            }, CancellationToken.None).ConfigureAwait(false);
            var inventory = await store.ReadSceneConsumersAsync(candidate.CaptureId, sceneId, _ => { }, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(inventory);
            CollectionAssert.AreEquivalent(new[] { archiveRoot, otherArchiveRoot }, inventory.ArchiveRoots.ToArray());
            Assert.IsTrue(await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                candidate, CancellationToken.None).ConfigureAwait(false));
            var secondSidecar = Path.Combine(Path.GetDirectoryName(archivePath)!, "second.json");
            File.Copy(Path.ChangeExtension(archivePath, ".json"), secondSidecar);
            var otherSecondSidecar = Path.Combine(Path.GetDirectoryName(otherArchivePath)!, "second.json");
            File.Copy(Path.ChangeExtension(otherArchivePath, ".json"), otherSecondSidecar);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await new ProjectedSceneRetentionGuard(root, store, maximumSidecars: 1).HasRetainedConsumerAsync(
                    candidate, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            File.Delete(secondSidecar);
            File.Delete(otherSecondSidecar);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await new ProjectedSceneRetentionGuard(root, store, maximumBytes: 1).HasRetainedConsumerAsync(
                    candidate, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var bounded = new ProjectedSceneRetentionGuard(root, store, maximumCandidates: 1);
            Assert.IsTrue(await bounded.HasRetainedConsumerAsync(candidate, CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await bounded.HasRetainedConsumerAsync(candidate, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(archivePath), "Budget exhaustion never authorizes consumer deletion.");
            File.Delete(archivePath);
            Assert.IsTrue(await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                candidate, CancellationToken.None).ConfigureAwait(false), "Every archive copy must expire first.");
            File.Delete(otherArchivePath);
            Assert.IsFalse(await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                candidate, CancellationToken.None).ConfigureAwait(false), "A stale sidecar alone does not retain the scene.");
            // An archived replay created later is discovered on the next pass without a current-policy lookup.
            await File.WriteAllBytesAsync(archivePath, new byte[8]).ConfigureAwait(false);
            Assert.IsTrue(await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                candidate, CancellationToken.None).ConfigureAwait(false));
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(candidate,
                    new CancellationToken(canceled: true)).ConfigureAwait(false)).ConfigureAwait(false);
            File.Delete(archivePath);
            Directory.CreateDirectory(archivePath);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                    candidate, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Directory.Delete(archivePath);
            if (!OperatingSystem.IsWindows())
            {
                var foreign = Path.Combine(root, "foreign.bin");
                await File.WriteAllBytesAsync(foreign, new byte[8]).ConfigureAwait(false);
                File.CreateSymbolicLink(archivePath, foreign);
                await Assert.ThrowsAsync<IOException>(async () =>
                    await new ProjectedSceneRetentionGuard(root, store).HasRetainedConsumerAsync(
                        candidate, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                File.Delete(archivePath);
            }
            await File.WriteAllBytesAsync(archivePath, new byte[8]).ConfigureAwait(false);
            var guard = new ProjectedSceneRetentionGuard(root, store);
            await guard.SaveCursorAsync(1, outputIdentity, CancellationToken.None).ConfigureAwait(false);
            var cursor = await new ProjectedSceneRetentionGuard(root, store).ReadCursorAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, cursor.Timestamp);
            Assert.AreEqual(outputIdentity, cursor.Identity);
            Assert.IsTrue(await guard.HasRetainedConsumerAsync(candidate, CancellationToken.None).ConfigureAwait(false),
                "A cursor never supplies consumer-absence evidence.");
            guard.CompletePass();
            Assert.IsNull((await guard.ReadCursorAsync(CancellationToken.None).ConfigureAwait(false)).Timestamp);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [TestMethod]
    [TestCategory("Unit")]
    public async Task SceneExpirationCursorNeverDeletesAnUnscannedPrefixAndEventuallyReleasesConsumers()
    {
        var root = CreateTestRoot();
        try
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                DerivedProductLifecycle = new DerivedProductLifecycleOptions { ReconciliationBatchSize = 16 }
            });
            using var store = new SqliteCaptureProcessingStore(options);
            using var telemetry = new CaptureProcessingTelemetry();
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(options, store, storage, telemetry);
            var archiveRoot = Path.Combine(root, "archive");
            var pipeline = new CapturePipelineConfig([
                new CaptureProcessingStepConfig("Storage", "archive", Options:
                    System.Text.Json.JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = archiveRoot, RetentionDays = 365 }))]);
            var now = DateTimeOffset.UtcNow;
            var revision = new ProcessingGraphRevisionSnapshot(
                new ProcessingGraphRevisionState(new string('C', 64), "archive", "1", ProcessingGraphRevisionLifecycle.Validated,
                    new string('D', 64), new string('E', 64), new string('F', 64), now, now, null, null),
                pipeline, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(pipeline, WebEnumJsonOptions),
                "{}"u8.ToArray(), "{}"u8.ToArray(), []);
            var journal = new HVO.SkyMonitor.CameraAgent.Common.RawIngress.SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await store.UpsertConfiguredBasicRevisionAsync(revision, CancellationToken.None).ConfigureAwait(false);
            var payloads = new List<string>();
            var archives = new List<string>();
            for (var sequence = 1; sequence <= 17; sequence++)
            {
                var fixture = await CreateFixtureAsync(root, sequence, $"raw-{sequence}", new byte[8]).ConfigureAwait(false);
                var descriptor = fixture.Manifest.Descriptor;
                var manifest = fixture.Manifest with
                {
                    Scene = new SceneProvenance(new string('D', 64), "rig", "catalog", "1",
                    new string('A', 64), "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1",
                    RigProfileHashSha256: new string('B', 64), SceneUtc: descriptor.Timing.ExposureStartedUtc,
                    ProjectedSceneStageSchemaVersion: "projected-scene-stage-v1", ProjectedSceneStageKey: new string('C', 64),
                    ProjectedSceneSchemaVersion: SceneProvenance.RetainedProjectedSceneSchemaVersion)
                };
                var receipt = fixture.Item.RawCapture! with
                { Manifest = manifest, CommittedManifestSha256 = CaptureContractJson.ComputeManifestSha256(manifest) };
                fixture = fixture with { Manifest = manifest, Item = fixture.Item with { RawCapture = receipt } };
                await File.WriteAllBytesAsync(Path.ChangeExtension(Path.Combine(root, manifest.RelativeArtifactPath), ".json"),
                    CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
                await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId,
                    descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                var entry = new HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressJournalEntry(
                    descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence, descriptor.Capture.CaptureId,
                    descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
                    CaptureContractJson.ComputeManifestSha256(manifest), descriptor.Artifact.ChecksumSha256,
                    descriptor.Layout.ByteLength, manifest.RelativeArtifactPath, Path.ChangeExtension(manifest.RelativeArtifactPath, ".json"),
                    CaptureContractJson.Serialize(manifest), descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc);
                await journal.CommitAsync(entry, null, null, [], new ProcessingLiveExecutionSeed(Guid.NewGuid(),
                    descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, revision, now, now, now.AddHours(1),
                    now.AddHours(2), "{}"u8.ToArray()), CancellationToken.None).ConfigureAwait(false);
                var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(
                    new VisibleSceneRequest(descriptor.Timing.ExposureStartedUtc, new ObserverLocation(0, 0, 0),
                        new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2), new CatalogQuery(6.5, 10),
                        new CatalogMetadata("catalog", "1", new Uri("https://example.invalid"), new string('A', 64), "test", "1"),
                        projectionVersion: "projection-v1")).ConfigureAwait(false);
                var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
                    ProjectedSceneImageTransformV1.Identity(2, 2), new ProjectedSceneSource(descriptor.Capture.CaptureId,
                        descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor)), "calibration-v1", "projection-v1");
                var product = CreateProjectedSceneProduct(fixture.Item, scene, "projected-scene-v1");
                var graph = new CaptureProcessingGraph([
                    new CaptureProcessingGraphNode("scene", new FixedMetadataProducingStep(product), [], true,
                        BuiltInProcessingRecipes.ProjectedScene, FrameArtifactRole.Metadata, product.Variant, new string('F', 64))]);
                var produced = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence, telemetry,
                    1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, produced.Outcome, produced.Reason);
                var node = await store.ReadNodeAsync(descriptor.Capture.CaptureId, "scene", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                payloads.Add(Path.Combine(root, node.Outputs.Single().PayloadRelativePath));
                var relative = $"frames/{descriptor.Timing.ExposureStartedUtc:yyyy/MM/dd}/Raw/{sequence}.bin";
                var archive = Path.Combine(archiveRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                await File.WriteAllBytesAsync(archive, new byte[8]).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.ChangeExtension(archive, ".json"),
                    CaptureContractJson.Serialize(manifest with { RelativeArtifactPath = relative })).ConfigureAwait(false);
                archives.Add(archive);
                // The raw policy has expired these canonical files; longer-lived archive copies remain.
                File.Delete(Path.Combine(root, manifest.RelativeArtifactPath));
                File.Delete(Path.ChangeExtension(Path.Combine(root, manifest.RelativeArtifactPath), ".json"));
            }
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE processing_executions SET status = 'Completed';";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
            var noExternalHolds = new HashSet<string>(StringComparer.Ordinal);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await persistence.ExpireOutputsAsync(root, cutoff, noExternalHolds, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsTrue(payloads.All(File.Exists));
            Assert.IsNotNull((await new ProjectedSceneRetentionGuard(root, store).ReadCursorAsync(CancellationToken.None).ConfigureAwait(false)).Timestamp);
            Assert.AreEqual(0, await persistence.ExpireOutputsAsync(root, cutoff, noExternalHolds, CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(payloads.All(File.Exists), "Resuming after the cursor cannot delete its unscanned prefix.");
            foreach (var path in archives) File.Delete(path);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await persistence.ExpireOutputsAsync(root, cutoff, noExternalHolds, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(1, payloads.Count(File.Exists));
            Assert.AreEqual(2, await persistence.ExpireOutputsAsync(root, cutoff, noExternalHolds, CancellationToken.None).ConfigureAwait(false));
            Assert.IsFalse(payloads.Any(File.Exists));
            Assert.IsNull((await new ProjectedSceneRetentionGuard(root, store).ReadCursorAsync(CancellationToken.None).ConfigureAwait(false)).Timestamp);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
