using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    [TestMethod]
    public async Task BackgroundRetentionAndOutboxUseActivatedProfileInsteadOfStaleAccessor()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-background-profile-source");
        try
        {
            var retained = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var capacity = new Mock<IStorageCapacityProvider>();
            capacity.Setup(value => value.GetCapacity(It.IsAny<string>())).Returns((string path) =>
            {
                if (path.EndsWith("storage-b", StringComparison.Ordinal)) retained.TrySetResult(path);
                return new StorageCapacity(long.MaxValue, long.MaxValue);
            });
            var outbox = new Mock<IArtifactOutbox>();
            outbox.Setup(value => value.InitializeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string path, CancellationToken _) =>
                {
                    if (path.EndsWith("storage-b", StringComparison.Ordinal)) drained.TrySetResult(path);
                    return ValueTask.CompletedTask;
                });
            outbox.Setup(value => value.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 0, 0, 0));
            using var provider = ProcessingGraphOperationsTests.CreateProvider(root, new Dictionary<string, string?>
            {
                ["CameraAgent:CentralIntegration:Mode"] = "Enabled",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }, configureServices: services =>
            {
                services.AddSingleton(capacity.Object);
                services.AddSingleton(outbox.Object);
            });
            var file = Profile(root);
            await InitializeAsync(provider, file).ConfigureAwait(false);
            var accessor = provider.GetRequiredService<ICameraAgentConfigurationAccessor>();
            var fileConfiguration = await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            await ChangeProfileAsync(provider, file).ConfigureAwait(false);
            // Deliberately retain the file-era accessor. Both running workers must choose the schedule snapshot.
            Assert.AreSame(fileConfiguration, await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false));
            using var retention = ActivatorUtilities.CreateInstance<RetentionBackgroundService>(provider);
            using var drain = ActivatorUtilities.CreateInstance<ArtifactOutboxDrainService>(provider);
            await retention.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await drain.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Assert.AreEqual(Path.Combine(root, "storage-b"), await retained.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                Assert.AreEqual(Path.Combine(root, "storage-b"), await drained.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                capacity.Verify(value => value.GetCapacity(Path.Combine(root, "storage-a")), Times.Never);
                outbox.Verify(value => value.InitializeAsync(Path.Combine(root, "storage-a"), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await retention.StopAsync(stop.Token).ConfigureAwait(false);
                await drain.StopAsync(stop.Token).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
