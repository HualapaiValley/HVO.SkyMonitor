using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Integration")]
public sealed class CaptureProcessingStateRefreshServiceTests
{
    [TestMethod]
    public async Task RunOnceAsync_ClearsStaleAvailabilityEvidenceFromDurableInventory()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-processing-refresh", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 30);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var store = new SqliteCaptureProcessingStore(options);
            var state = new CaptureProcessingState();
            state.SetProcessingEvidence(2, 3);
            using var service = new CaptureProcessingStateRefreshService(store, state, TimeProvider.System);

            await service.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, state.Snapshot.MissingProductCount);
            Assert.AreEqual(0, state.Snapshot.ProcessingQuarantineCount);
            Assert.AreEqual(CaptureProcessingAvailability.Healthy, state.Snapshot.Availability);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
