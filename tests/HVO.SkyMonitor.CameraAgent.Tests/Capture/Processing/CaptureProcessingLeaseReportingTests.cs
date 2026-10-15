using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// The lifecycle drain waits on the processing section's leased count, so the durable refresh must report standard
/// work a worker holds rather than zero. #1175 item 6.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CaptureProcessingLeaseReportingTests
{
    [TestMethod]
    public async Task RefreshReportsLeasedStandardProcessingUntilTheLeaseCompletes()
    {
        var root = EdgeUpgradeLaneTests.CreateRoot("processing-leased");
        try
        {
            using var provider = EdgeUpgradeLaneTests.CreateProvider(root, TimeProvider.System);
            var fixture = await EdgeUpgradeLaneTests.StartAsync(provider, root).ConfigureAwait(false);
            await fixture.AcceptAsync(0).ConfigureAwait(false);
            var state = new CaptureProcessingState();
            using var refresh = new CaptureProcessingStateRefreshService(fixture.Store, state, TimeProvider.System);

            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, state.Snapshot.PendingCount);
            Assert.AreEqual(0L, state.Snapshot.LeasedCount);

            var lease = await fixture.Ingress.ClaimAsync(fixture.Standard, "lease-reporting", fixture.Configuration,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1L, state.Snapshot.LeasedCount, "A leased standard capture is reported as leased processing.");
            Assert.AreEqual(1L, state.Snapshot.PendingCount, "Leased work stays pending until it completes.");

            var handled = await fixture.StandardHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, handled.Outcome, handled.Reason);
            await fixture.Ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, state.Snapshot.LeasedCount);
            Assert.AreEqual(0L, state.Snapshot.PendingCount);
        }
        finally
        {
            EdgeUpgradeLaneTests.Cleanup(root);
        }
    }
}
