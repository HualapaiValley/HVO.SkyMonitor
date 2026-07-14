using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    private static CameraModuleConfig CreateConfig(params string[] roots)
    {
        return new CameraModuleConfig(new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(new SensorProfile("Virtual", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            roots.Select(root => new CaptureProcessingStepConfig(
                "HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.NoOpFileStorageProcessingStep, HVO.SkyMonitor.CameraAgent.Common",
                Options: System.Text.Json.JsonSerializer.SerializeToElement(new { storageRoot = root, retentionDays = 7 }))).ToArray());
    }

    private static RetentionBackgroundService CreateService(IArtifactOutbox outbox)
        => new(
            new StubConfigurationAccessor(),
            Options.Create(new CameraAgentHostOptions()),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)),
            outbox,
            new FixedCapacityProvider(50),
            new StoragePressureState(),
            NullLogger<RetentionBackgroundService>.Instance);

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

    private sealed class StubConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => false;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<CameraModuleConfig>(new NotSupportedException());
    }
}
