using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class FileSystemFrameStorageServiceTests
{
    [TestMethod]
    public async Task SaveAsync_CanonicalReplayPreservesExactPayloadAndSidecar()
    {
        using var root = new TemporaryRoot();
        var (artifact, descriptor) = CreateArtifact([1, 2, 3, 4]);
        using var service = CreateService();

        var first = await service.SaveAsync(
            root.Path, artifact, descriptor, "raw-capture", CancellationToken.None).ConfigureAwait(false);
        var expectedSidecar = await File.ReadAllBytesAsync(Path.ChangeExtension(first.AbsolutePath, ".json"))
            .ConfigureAwait(false);
        var second = await service.SaveAsync(
            root.Path, artifact, descriptor, "RAW-CAPTURE", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(first, second);
        CollectionAssert.AreEqual(
            artifact.Frame.PixelData.ToArray(),
            await File.ReadAllBytesAsync(first.AbsolutePath).ConfigureAwait(false));
        CollectionAssert.AreEqual(
            expectedSidecar,
            await File.ReadAllBytesAsync(Path.ChangeExtension(first.AbsolutePath, ".json")).ConfigureAwait(false));
        var parsed = CaptureContractJson.ParseManifest(expectedSidecar);
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        Assert.AreEqual(
            CaptureContractJson.ComputeDescriptorSha256(descriptor),
            CaptureContractJson.ComputeDescriptorSha256(parsed.Document!.Manifest.Descriptor));
        Assert.AreEqual("raw-capture", parsed.Document.Manifest.ProducerStepId);
    }

    [TestMethod]
    public async Task SaveAsync_ConflictingPayloadFailsClosedWithoutRewritingSidecar()
    {
        using var root = new TemporaryRoot();
        var (artifact, descriptor) = CreateArtifact([1, 2, 3, 4]);
        using var service = CreateService();
        var stored = await service.SaveAsync(root.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
        var sidecarPath = Path.ChangeExtension(stored.AbsolutePath, ".json");
        var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
        await File.WriteAllBytesAsync(stored.AbsolutePath, [9, 9, 9, 9]).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await service.SaveAsync(root.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SaveAsync_MismatchedDescriptorRejectsBeforePublishingAnyFiles()
    {
        using var root = new TemporaryRoot();
        var (artifact, descriptor) = CreateArtifact([1, 2, 3, 4]);
        var mismatched = descriptor with
        {
            Artifact = descriptor.Artifact with { ArtifactId = Guid.NewGuid() }
        };
        using var service = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await service.SaveAsync(root.Path, artifact, mismatched, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsFalse(Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories).Any());
        Assert.IsFalse(Directory.EnumerateDirectories(root.Path, "*", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("length")]
    [DataRow("content")]
    public async Task SaveAsync_ConflictingSidecarRejectsBeforePublishingPayloadOrMutatingEvidence(string disagreement)
    {
        using var seedRoot = new TemporaryRoot();
        using var root = new TemporaryRoot();
        var (artifact, descriptor) = CreateArtifact([1, 2, 3, 4]);
        StoredFrameReference seed;
        using (var seedService = CreateService())
        {
            seed = await seedService.SaveAsync(
                seedRoot.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
        }
        var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
            Path.ChangeExtension(seed.AbsolutePath, ".json")).ConfigureAwait(false));
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        var manifest = parsed.Document!.Manifest;
        manifest = disagreement switch
        {
            "path" => manifest with { RelativeArtifactPath = "frames/different.bin" },
            "length" => manifest with
            {
                Descriptor = manifest.Descriptor with
                {
                    Layout = manifest.Descriptor.Layout with
                    {
                        Width = manifest.Descriptor.Layout.Width + 1,
                        StrideBytes = manifest.Descriptor.Layout.StrideBytes + 1,
                        ByteLength = manifest.Descriptor.Layout.ByteLength + 1
                    }
                }
            },
            "content" => manifest with
            {
                Descriptor = manifest.Descriptor with
                {
                    Artifact = manifest.Descriptor.Artifact with { SourceId = "different-source" }
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(disagreement))
        };
        var sidecar = CaptureContractJson.Serialize(manifest);
        var payloadPath = Path.Combine(root.Path, seed.RelativePath);
        var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
        await File.WriteAllBytesAsync(sidecarPath, sidecar).ConfigureAwait(false);
        using var service = CreateService();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await service.SaveAsync(root.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsFalse(File.Exists(payloadPath));
        CollectionAssert.AreEqual(sidecar, await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false));
        var remainingFiles = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories).ToArray();
        Assert.HasCount(1, remainingFiles);
        Assert.AreEqual(sidecarPath, remainingFiles[0]);
    }

    [TestMethod]
    public async Task SaveAsync_MatchingPayloadWithoutSidecarConvergesAfterRestart()
    {
        using var root = new TemporaryRoot();
        var (artifact, descriptor) = CreateArtifact([1, 2, 3, 4]);
        StoredFrameReference stored;
        using (var first = CreateService())
        {
            stored = await first.SaveAsync(root.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
        }
        File.Delete(Path.ChangeExtension(stored.AbsolutePath, ".json"));

        using var restarted = CreateService();
        var recovered = await restarted.SaveAsync(
            root.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(stored, recovered);
        Assert.IsTrue(CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
            Path.ChangeExtension(stored.AbsolutePath, ".json")).ConfigureAwait(false)).IsValid);
    }

    [TestMethod]
    public async Task List_RequiresCanonicalVersionedSidecarAndExactPayloadBinding()
    {
        using var root = new TemporaryRoot();
        var (artifact, descriptor) = CreateArtifact([1, 2, 3, 4]);
        using var service = CreateService();
        var stored = await service.SaveAsync(root.Path, artifact, descriptor, CancellationToken.None).ConfigureAwait(false);
        var date = DateOnly.FromDateTime(descriptor.Timing.ExposureStartedUtc.UtcDateTime);

        Assert.HasCount(1, service.List(root.Path, date, FrameArtifactRole.Raw, 10));
        await File.WriteAllTextAsync(Path.ChangeExtension(stored.AbsolutePath, ".json"), "{}").ConfigureAwait(false);
        Assert.IsEmpty(service.List(root.Path, date, FrameArtifactRole.Raw, 10));
    }

    [TestMethod]
    public async Task RemoveBatchAsync_RemovesPayloadSidecarsAndIndexEntriesAcrossRestart()
    {
        using var root = new TemporaryRoot();
        var firstEvidence = CreateArtifact([1, 2, 3, 4], Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var secondEvidence = CreateArtifact([5, 6, 7, 8], Guid.Parse("10000000-0000-0000-0000-000000000002"));
        using var service = CreateService();
        var first = await service.SaveAsync(
            root.Path, firstEvidence.Artifact, firstEvidence.Descriptor, CancellationToken.None).ConfigureAwait(false);
        var second = await service.SaveAsync(
            root.Path, secondEvidence.Artifact, secondEvidence.Descriptor, CancellationToken.None).ConfigureAwait(false);

        await service.RemoveBatchAsync(
            root.Path,
            [
                new(first, firstEvidence.Descriptor.Artifact.ArtifactId),
                new(second, secondEvidence.Descriptor.Artifact.ArtifactId)
            ],
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(File.Exists(first.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(first.AbsolutePath, ".json")));
        using var restarted = CreateService();
        Assert.IsEmpty(restarted.List(
            root.Path,
            DateOnly.FromDateTime(firstEvidence.Descriptor.Timing.ExposureStartedUtc.UtcDateTime),
            FrameArtifactRole.Raw,
            10));
    }

    private static (FrameArtifact Artifact, ReconstructionDescriptor Descriptor) CreateArtifact(
        byte[] payload,
        Guid? artifactId = null)
    {
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8, payload.Length, 1, payload.Length, payload);
        var descriptor = artifactId.HasValue
            ? manifest.Descriptor with
            {
                Artifact = manifest.Descriptor.Artifact with { ArtifactId = artifactId.Value }
            }
            : manifest.Descriptor;
        var reconstruction = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);
        Assert.IsTrue(reconstruction.IsValid, reconstruction.ReasonCode);
        return (new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame!), descriptor);
    }

    private static FileSystemFrameStorageService CreateService()
        => new(NullLogger<FileSystemFrameStorageService>.Instance);

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "skymonitor-frame-storage", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
