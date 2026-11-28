using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    [TestMethod]
    public async Task BackgroundRetentionUsesActivatedProfileInsteadOfStaleAccessor()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-background-profile-source");
        try
        {
            var retained = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var capacity = new Mock<IStorageCapacityProvider>();
            capacity.Setup(value => value.GetCapacity(It.IsAny<string>())).Returns((string path) =>
            {
                if (path.EndsWith("storage-b", StringComparison.Ordinal)) retained.TrySetResult(path);
                return new StorageCapacity(long.MaxValue, long.MaxValue);
            });
            using var provider = ProcessingGraphOperationsTests.CreateProvider(root, configureServices: services =>
            {
                services.AddSingleton(capacity.Object);
            });
            var file = Profile(root);
            await InitializeAsync(provider, file).ConfigureAwait(false);
            var accessor = provider.GetRequiredService<ICameraAgentConfigurationAccessor>();
            var fileConfiguration = await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            await ChangeProfileAsync(provider, file).ConfigureAwait(false);
            // Deliberately retain the file-era accessor. Retention must choose the schedule snapshot.
            Assert.AreSame(fileConfiguration, await accessor.WaitForConfigurationAsync(CancellationToken.None).ConfigureAwait(false));
            using var retention = ActivatorUtilities.CreateInstance<RetentionBackgroundService>(provider);
            await retention.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Assert.AreEqual(Path.Combine(root, "storage-b"), await retained.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false));
                capacity.Verify(value => value.GetCapacity(Path.Combine(root, "storage-a")), Times.Never);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await retention.StopAsync(stop.Token).ConfigureAwait(false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
