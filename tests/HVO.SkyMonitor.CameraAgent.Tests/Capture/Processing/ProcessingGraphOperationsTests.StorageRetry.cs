using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StorageCreateReceiptReplaysAfterProfileSwitchAndRejectsConflictingOrNewRequests(bool restart)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-create-retry");
        try
        {
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            string revisionId;
            using (var provider = StoragePolicyRawIngressIntegrationTests.Provider(root))
            {
                await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await operations.EnsureConfiguredBasicAsync(profile, CancellationToken.None).ConfigureAwait(false);
                revisionId = (await operations.CreateRevisionAsync("retry", "1", profile.Pipeline, "receipt-key", "test", "same request", CancellationToken.None).ConfigureAwait(false)).RevisionId;
                await StoragePolicyRawIngressIntegrationTests.ChangeProfileAsync(provider, profile).ConfigureAwait(false);
                if (!restart) await AssertRetryAsync(provider).ConfigureAwait(false);
            }
            if (restart)
            {
                using var reopened = StoragePolicyRawIngressIntegrationTests.Provider(root);
                await StoragePolicyRawIngressIntegrationTests.InitializeAsync(reopened, profile).ConfigureAwait(false);
                await AssertRetryAsync(reopened).ConfigureAwait(false);
            }

            async Task AssertRetryAsync(ServiceProvider provider)
            {
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var before = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                var changedRequest = StoragePolicyRawIngressIntegrationTests.ChangedPipeline(profile);
                var replay = await operations.CreateRevisionAsync("retry", "1", profile.Pipeline, "receipt-key", "test", "same request", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(revisionId, replay.RevisionId, "A committed create receipt survives later Storage policy changes.");
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.CreateRevisionAsync("retry", "1", changedRequest, "receipt-key", "test", "same request", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.CreateRevisionAsync("retry", "1", profile.Pipeline, "receipt-key", "another-actor", "same request", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                    await operations.CreateRevisionAsync("retry", "1", profile.Pipeline, "receipt-key", "test", "changed reason", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                    await operations.CreateRevisionAsync("new-divergent", "1", profile.Pipeline, "new-key", "test", "same request", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                var after = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(before.ActiveRevisionId, after.ActiveRevisionId);
                Assert.AreEqual(before.StateVersion, after.StateVersion);
                Assert.AreEqual(before.Revisions.Count, after.Revisions.Count);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
