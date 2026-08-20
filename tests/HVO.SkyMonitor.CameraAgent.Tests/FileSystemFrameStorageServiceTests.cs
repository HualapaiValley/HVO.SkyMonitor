using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class FileSystemFrameStorageServiceTests
{
    [TestMethod]
    public async Task SaveAsync_EquivalentVersionedOutput_IsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (artifact, descriptor) = CreateVersionedArtifact();
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            var first = await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
            var second = await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(first.AbsolutePath, second.AbsolutePath);
            Assert.AreEqual(1, Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count());
            Assert.AreEqual(1, Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Count());
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
    public async Task SaveAsync_LegacySidecarWithoutProducer_ConvergesOnProducerAwareReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (artifact, descriptor) = CreateVersionedArtifact();
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var stored = await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);

            await service.SaveAsync(root, artifact, descriptor, "Final-Jpeg", CancellationToken.None).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(Path.ChangeExtension(stored.AbsolutePath, ".json")).ConfigureAwait(false));

            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            Assert.AreEqual("Final-Jpeg", parsed.Document!.Manifest!.ProducerStepId);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), parsed.Document.Manifest.IdempotencyKey);
            Assert.AreEqual(1, Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count());
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
    public async Task SaveAsync_ExistingNonNullProducerConflict_FailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (artifact, descriptor) = CreateVersionedArtifact();
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            await service.SaveAsync(root, artifact, descriptor, "final-jpeg", CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await service.SaveAsync(
                    root, artifact, descriptor, "thumbnail-large", CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
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
    public async Task SaveAsync_ExistingCorruptUnversionedPayload_FailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var frame = new CameraFrame(
                DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono8,
                new byte[] { 1, 2, 3, 4 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var artifact = new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame);
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var stored = await service.SaveAsync(root, artifact, CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllBytesAsync(stored.AbsolutePath, new byte[] { 4, 3, 2, 1 }).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await service.SaveAsync(root, artifact, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
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
    public async Task RemoveThenResave_RestoresIndexEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var frame = new CameraFrame(
                DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono8,
                new byte[] { 1, 2, 3, 4 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var artifact = new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame);
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var stored = await service.SaveAsync(root, artifact, CancellationToken.None).ConfigureAwait(false);
            await service.RemoveAsync(root, stored, artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);

            await service.SaveAsync(root, artifact, CancellationToken.None).ConfigureAwait(false);
            var listed = service.List(root, DateOnly.FromDateTime(frame.TimestampUtc.UtcDateTime), FrameArtifactRole.Raw, 10);

            Assert.HasCount(1, listed);
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
    public async Task SaveAsync_MatchingPayloadWithoutSidecar_CompletesPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (artifact, descriptor) = CreateVersionedArtifact();
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var first = await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
            File.Delete(Path.ChangeExtension(first.AbsolutePath, ".json"));

            var recovered = await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(Path.ChangeExtension(recovered.AbsolutePath, ".json")).ConfigureAwait(false));

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), parsed.Document!.Manifest!.IdempotencyKey);
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
    public async Task SaveAsync_WithReconstructionDescriptor_WritesReadableV2Sidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = new byte[8];
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var descriptor = manifest.Descriptor;
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
            var artifact = new FrameArtifact(
                descriptor.Artifact.ArtifactId,
                descriptor.Artifact.Role,
                frame,
                descriptor.Artifact.SourceArtifactIds,
                recipeVersion: null);
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            var stored = await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
            var sidecarJson = await File.ReadAllBytesAsync(Path.ChangeExtension(stored.AbsolutePath, ".json")).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(sidecarJson);
            var listed = service.List(
                root,
                DateOnly.FromDateTime(descriptor.Timing.ExposureStartedUtc.UtcDateTime),
                FrameArtifactRole.Raw,
                10);

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(CaptureManifestCompleteness.Complete, parsed.Document!.Completeness);
            Assert.AreEqual(stored.RelativePath, parsed.Document.Manifest!.RelativeArtifactPath);
            Assert.AreEqual(
                CaptureContractJson.ComputeDescriptorSha256(descriptor),
                CaptureContractJson.ComputeDescriptorSha256(parsed.Document.Manifest.Descriptor));
            Assert.HasCount(1, listed);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static (FrameArtifact Artifact, ReconstructionDescriptor Descriptor) CreateVersionedArtifact()
    {
        var payload = new byte[8];
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload).Descriptor;
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
        return (new FrameArtifact(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            frame,
            descriptor.Artifact.SourceArtifactIds), descriptor);
    }

    [TestMethod]
    public async Task SaveAsync_WithMismatchedReconstructionDescriptor_WritesNoPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = new byte[8];
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var descriptor = manifest.Descriptor;
            var frame = new CameraFrame(
                descriptor.Timing.ExposureStartedUtc,
                2,
                2,
                CameraPixelFormat.Mono16,
                payload,
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0),
                4);
            var artifact = new FrameArtifact(
                descriptor.Artifact.ArtifactId,
                FrameArtifactRole.Raw,
                frame,
                descriptor.Artifact.SourceArtifactIds,
                descriptor.Artifact.Recipe.ImplementationVersion);
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await service.SaveAsync(root, artifact, descriptor, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsFalse(Directory.Exists(root) && Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Any());
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
    public async Task List_WithMalformedV2Sidecar_SkipsItAndContinues()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = new byte[8];
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var descriptor = manifest.Descriptor;
            var frame = CreateMatchingFrame(descriptor, payload);
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var versioned = await service.SaveAsync(
                root,
                CreateMatchingArtifact(descriptor, frame),
                descriptor,
                CancellationToken.None).ConfigureAwait(false);
            var legacy = await service.SaveAsync(
                root,
                new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame),
                CancellationToken.None).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.ChangeExtension(versioned.AbsolutePath, ".json"),
                "{\"schemaVersion\":2}").ConfigureAwait(false);

            var listed = service.List(
                root,
                DateOnly.FromDateTime(frame.TimestampUtc.UtcDateTime),
                FrameArtifactRole.Raw,
                10);

            Assert.HasCount(1, listed);
            Assert.AreEqual(legacy.AbsolutePath, listed[0].AbsolutePath);
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
    public async Task List_WithV2PathOrLengthDisagreement_SkipsArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = new byte[8];
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var descriptor = manifest.Descriptor;
            var frame = CreateMatchingFrame(descriptor, payload);
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var stored = await service.SaveAsync(
                root,
                CreateMatchingArtifact(descriptor, frame),
                descriptor,
                CancellationToken.None).ConfigureAwait(false);
            var sidecarPath = Path.ChangeExtension(stored.AbsolutePath, ".json");
            var originalSidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            var sidecar = JsonNode.Parse(originalSidecar)!.AsObject();
            sidecar["relativeArtifactPath"] = "frames/different.bin";
            await File.WriteAllTextAsync(sidecarPath, sidecar.ToJsonString()).ConfigureAwait(false);

            Assert.IsEmpty(service.List(
                root, DateOnly.FromDateTime(frame.TimestampUtc.UtcDateTime), FrameArtifactRole.Raw, 10));

            await File.WriteAllBytesAsync(sidecarPath, originalSidecar).ConfigureAwait(false);
            await File.WriteAllBytesAsync(stored.AbsolutePath, [1]).ConfigureAwait(false);

            Assert.IsEmpty(service.List(
                root, DateOnly.FromDateTime(frame.TimestampUtc.UtcDateTime), FrameArtifactRole.Raw, 10));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CameraFrame CreateMatchingFrame(ReconstructionDescriptor descriptor, byte[] payload)
        => new(
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

    private static FrameArtifact CreateMatchingArtifact(ReconstructionDescriptor descriptor, CameraFrame frame)
        => new(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            frame,
            descriptor.Artifact.SourceArtifactIds,
            descriptor.Artifact.Role == FrameArtifactRole.Raw
                ? null
                : descriptor.Artifact.Recipe.ImplementationVersion);

    [TestMethod]
    public async Task SaveAsync_WritesRawPayloadAndMetadataWithoutTemporaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 1, CameraPixelFormat.Mono16, new byte[] { 1, 2, 3, 4 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));

            var stored = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(frame.PixelData.ToArray(), await File.ReadAllBytesAsync(stored.AbsolutePath).ConfigureAwait(false));
            Assert.IsTrue(File.Exists(Path.ChangeExtension(stored.AbsolutePath, ".json")));
            Assert.AreEqual(FrameArtifactRole.Raw, stored.Role);
            Assert.AreEqual(0, Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Count());
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
    public async Task List_FiltersByRoleAndReturnsPersistedArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Preview, frame), CancellationToken.None).ConfigureAwait(false);

            var artifacts = service.List(root, DateOnly.FromDateTime(DateTime.UnixEpoch), FrameArtifactRole.Preview, 10);
            var adjacentDate = service.List(root, DateOnly.FromDateTime(DateTime.UnixEpoch).AddDays(1), null, 10);

            Assert.AreEqual(1, artifacts.Count);
            Assert.AreEqual(FrameArtifactRole.Preview, artifacts[0].Role);
            Assert.IsEmpty(adjacentDate);
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
    public async Task SaveAsync_WithSameTimestampAndRole_UsesDistinctArtifactPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var first = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var second = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            Assert.AreNotEqual(first.AbsolutePath, second.AbsolutePath);
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
    public async Task SaveAsync_UsesTheRequestedStorageRoot()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));

            var local = await service.SaveAsync(localRoot, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var archive = await service.SaveAsync(archiveRoot, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(local.AbsolutePath.StartsWith(Path.GetFullPath(localRoot), StringComparison.Ordinal));
            Assert.IsTrue(archive.AbsolutePath.StartsWith(Path.GetFullPath(archiveRoot), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(localRoot))
            {
                Directory.Delete(localRoot, recursive: true);
            }
            if (Directory.Exists(archiveRoot))
            {
                Directory.Delete(archiveRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SaveAsync_NormalizesUnknownTemperatureForJsonMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN));

            var stored = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            using var metadata = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(stored.AbsolutePath, ".json")).ConfigureAwait(false));
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null, metadata.RootElement.GetProperty("metadata").GetProperty("temperatureC").ValueKind);
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
    public async Task SaveAsync_PersistsSceneProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var scene = new SceneProvenance(
                "scene-id", "rig-v1", "HYG", "4.2", new string('A', 64),
                "EquidistantFisheye", "projection-v1", "scene-v1", "sensor-v1");
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0, Scene: scene));

            var stored = await service.SaveAsync(root,
                new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            using var metadata = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.ChangeExtension(stored.AbsolutePath, ".json")).ConfigureAwait(false));
            Assert.AreEqual("scene-id",
                metadata.RootElement.GetProperty("metadata").GetProperty("scene").GetProperty("sceneId").GetString());
            using var indexEntry = System.Text.Json.JsonDocument.Parse(
                (await File.ReadAllLinesAsync(Path.Combine(root, "index", "frames_1970-01-01.jsonl")).ConfigureAwait(false))[0]);
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null, indexEntry.RootElement.GetProperty("metadata").ValueKind);
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
    public async Task RemoveAsync_RemovesAcknowledgedPayloadMetadataAndIndexEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var removedArtifactId = Guid.NewGuid();
            var retainedArtifactId = Guid.NewGuid();
            var removed = await service.SaveAsync(root, new FrameArtifact(removedArtifactId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var retained = await service.SaveAsync(root, new FrameArtifact(retainedArtifactId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            await service.RemoveAsync(root, removed, removedArtifactId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(removed.AbsolutePath));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(removed.AbsolutePath, ".json")));
            Assert.IsTrue(File.Exists(retained.AbsolutePath));
            var indexLines = await File.ReadAllLinesAsync(Path.Combine(root, "index", "frames_1970-01-01.jsonl")).ConfigureAwait(false);
            Assert.AreEqual(1, indexLines.Length);
            StringAssert.Contains(indexLines[0], retainedArtifactId.ToString(), StringComparison.Ordinal);
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
    public async Task RemoveBatchAsync_RemovesMultipleArtifactsAndRetainsOtherIndexEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var retainedId = Guid.NewGuid();
            var first = await service.SaveAsync(root, new FrameArtifact(firstId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var second = await service.SaveAsync(root, new FrameArtifact(secondId, FrameArtifactRole.Preview, frame), CancellationToken.None).ConfigureAwait(false);
            var retained = await service.SaveAsync(root, new FrameArtifact(retainedId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            await service.RemoveBatchAsync(root,
                [new StoredFrameRemoval(first, firstId), new StoredFrameRemoval(second, secondId)],
                CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(first.AbsolutePath));
            Assert.IsFalse(File.Exists(second.AbsolutePath));
            Assert.IsTrue(File.Exists(retained.AbsolutePath));
            var indexLines = await File.ReadAllLinesAsync(Path.Combine(root, "index", "frames_1970-01-01.jsonl")).ConfigureAwait(false);
            Assert.HasCount(1, indexLines);
            StringAssert.Contains(indexLines[0], retainedId.ToString(), StringComparison.Ordinal);
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
    public async Task List_AfterServiceRecreation_UsesCommittedMetadataAndCaptureTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifactId = Guid.NewGuid();
            var timestamp = new DateTimeOffset(2026, 7, 12, 23, 30, 0, TimeSpan.FromHours(2));
            var writer = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(timestamp, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var stored = await writer.SaveAsync(root,
                new FrameArtifact(artifactId, FrameArtifactRole.Preview, frame), CancellationToken.None).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(stored.AbsolutePath, DateTime.UtcNow.AddDays(1));

            var reader = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var artifacts = reader.List(root, new DateOnly(2026, 7, 12), FrameArtifactRole.Preview, 10);

            Assert.AreEqual(1, artifacts.Count);
            StringAssert.Contains(artifacts[0].RelativePath, artifactId.ToString("N"), StringComparison.Ordinal);
            Assert.AreEqual(timestamp.ToUniversalTime(), artifacts[0].TimestampUtc);
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
    public async Task List_SkipsMalformedIndexAndIncompleteArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var incomplete = await service.SaveAsync(root,
                new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            File.Delete(Path.ChangeExtension(incomplete.AbsolutePath, ".json"));
            await File.AppendAllTextAsync(Path.Combine(root, "index", "frames_1970-01-01.jsonl"), "{truncated")
                .ConfigureAwait(false);
            var retainedId = Guid.NewGuid();
            await service.SaveAsync(root,
                new FrameArtifact(retainedId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            var artifacts = service.List(root, DateOnly.FromDateTime(DateTime.UnixEpoch), null, 10);

            Assert.AreEqual(1, artifacts.Count);
            StringAssert.Contains(artifacts[0].RelativePath, retainedId.ToString("N"), StringComparison.Ordinal);
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
    public async Task List_DiscoversLegacyOffsetPathFromAdjacentDateIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var artifactId = Guid.NewGuid();
            var timestamp = new DateTimeOffset(2026, 7, 13, 0, 30, 0, TimeSpan.FromHours(2));
            var directory = Path.Combine(root, "frames", "2026", "07", "13", "Raw");
            Directory.CreateDirectory(directory);
            var stem = $"2026-07-13_00-30-00.000Z-{artifactId:N}";
            var payloadPath = Path.Combine(directory, stem + ".bin");
            await File.WriteAllBytesAsync(payloadPath, new byte[] { 1 }).ConfigureAwait(false);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                artifactId,
                role = "Raw",
                timestampUtc = timestamp,
                width = 1,
                height = 1,
                pixelFormat = "Mono8",
                metadata = new { exposure = TimeSpan.FromSeconds(1), gain = 1, temperatureC = 0 }
            });
            await File.WriteAllTextAsync(Path.ChangeExtension(payloadPath, ".json"), metadata).ConfigureAwait(false);
            var indexDirectory = Path.Combine(root, "index");
            Directory.CreateDirectory(indexDirectory);
            await File.WriteAllTextAsync(Path.Combine(indexDirectory, "frames_2026-07-13.jsonl"), metadata + "\n")
                .ConfigureAwait(false);

            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var artifacts = service.List(root, new DateOnly(2026, 7, 12), FrameArtifactRole.Raw, 10);

            Assert.AreEqual(1, artifacts.Count);
            StringAssert.Contains(artifacts[0].RelativePath, artifactId.ToString("N"), StringComparison.Ordinal);
            Assert.AreEqual(payloadPath, artifacts[0].AbsolutePath);
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
    public async Task List_WithEqualTimestamps_AppliesStableArtifactIdOrderBeforeLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var laterId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var earlierId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            await service.SaveAsync(root,
                new FrameArtifact(laterId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            await service.SaveAsync(root,
                new FrameArtifact(earlierId, FrameArtifactRole.Preview, frame), CancellationToken.None).ConfigureAwait(false);

            var artifacts = service.List(root, DateOnly.FromDateTime(DateTime.UnixEpoch), null, 1);

            Assert.AreEqual(1, artifacts.Count);
            StringAssert.Contains(artifacts[0].RelativePath, earlierId.ToString("N"), StringComparison.Ordinal);
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
    public async Task List_InvalidDuplicateDoesNotHideLaterValidArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var artifactId = Guid.NewGuid();
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            await service.SaveAsync(root,
                new FrameArtifact(artifactId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var indexPath = Path.Combine(root, "index", "frames_1970-01-01.jsonl");
            var validLine = await File.ReadAllTextAsync(indexPath).ConfigureAwait(false);
            var invalid = System.Text.Json.Nodes.JsonNode.Parse(validLine)!;
            invalid["width"] = 2;
            await File.WriteAllTextAsync(indexPath, invalid.ToJsonString() + "\n" + validLine).ConfigureAwait(false);

            var artifacts = service.List(root, DateOnly.FromDateTime(DateTime.UnixEpoch), null, 10);

            Assert.AreEqual(1, artifacts.Count);
            StringAssert.Contains(artifacts[0].RelativePath, artifactId.ToString("N"), StringComparison.Ordinal);
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
    public void List_AcceptsDateOnlyBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        using var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);

        Assert.IsEmpty(service.List(root, DateOnly.MinValue, null, 1));
        Assert.IsEmpty(service.List(root, DateOnly.MaxValue, null, 1));
    }
}
