using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class RunManifestEndpointTests
{
    [TestMethod]
    public async Task NondurableCaptureReportsItsFullEphemeralQueueAndTheHostStillShutsDown()
    {
        using var fixture = new CameraAgentIntegrationFixture();
        await InitializeRetainedCaptureFixtureAsync(fixture, nondurableCapture: true).ConfigureAwait(false);
        var storageRoot = fixture.StorageRoot;
        CaptureQueueProbe queue;
        CameraCaptureService acquisition;
        TimeSpan shutdownTimeout;
        using (var scope = fixture.CreateCameraAgentScope())
        {
            queue = scope.ServiceProvider.GetRequiredService<CaptureQueueProbe>();
            acquisition = scope.ServiceProvider.GetServices<IHostedService>().OfType<CameraCaptureService>().Single();
            shutdownTimeout = scope.ServiceProvider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
            var lost = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                PrepareRetainedCapturesAsync(fixture, scope.ServiceProvider)).ConfigureAwait(false);
            Assert.IsNull(lost.InnerException, "The probe, not the readiness deadline, must identify the lost durable path.");
            Assert.AreEqual(
                "The retained manifest fixture lost its durable capture path while completing the reference capture. " +
                "The ephemeral queue is full and has no consumer: 4 nondurable captures are queued, " +
                "1 capture writer is blocked and 0 captures were committed durably.",
                lost.Message);
        }

        // The shipped bounded channel accepts four captures and then holds acquisition at the fifth.
        Assert.AreEqual((0, 4, 1, 0), queue.Snapshot().Counts);
        Assert.IsNotNull(acquisition.ExecuteTask);
        Assert.IsFalse(acquisition.ExecuteTask.IsCompleted, "Acquisition must still be waiting on the full ephemeral queue.");

        var shutdown = Stopwatch.StartNew();
        fixture.Dispose();
        shutdown.Stop();
        Assert.IsLessThan(shutdownTimeout, shutdown.Elapsed, "Shutdown must not wait out the host timeout.");
        Assert.AreEqual(TaskStatus.RanToCompletion, acquisition.ExecuteTask.Status);
        Assert.AreEqual((0, 4, 0, 1), queue.Snapshot().Counts);
        Assert.IsFalse(Directory.Exists(storageRoot));
    }

    // Counts what the capture loop hands to the shipped distributor. Its channel and wake-up behavior are unchanged.
    private sealed class CaptureQueueProbe(CaptureDistributionService distributor) : ICaptureDistributor
    {
        private int _durableCaptures;
        private int _queuedNondurable;
        private int _blockedWriters;
        private int _cancelledWriters;

        public void NotifyCommittedCapture()
        {
            Interlocked.Increment(ref _durableCaptures);
            distributor.NotifyCommittedCapture();
        }

        public async ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            // The bounded channel completes a write at once unless it is full.
            var write = distributor.ProcessEphemeralAsync(configuration, submission, cancellationToken).AsTask();
            if (!write.IsCompleted)
            {
                Interlocked.Increment(ref _blockedWriters);
                try
                {
                    await write.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelledWriters);
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _blockedWriters);
                }
            }
            else
            {
                await write.ConfigureAwait(false);
            }
            Interlocked.Increment(ref _queuedNondurable);
        }

        internal CaptureQueueSnapshot Snapshot() => new(
            Volatile.Read(ref _durableCaptures),
            Volatile.Read(ref _queuedNondurable),
            Volatile.Read(ref _blockedWriters),
            Volatile.Read(ref _cancelledWriters));
    }

    private sealed record CaptureQueueSnapshot(int DurableCaptures, int QueuedNondurable, int BlockedWriters, int CancelledWriters)
    {
        // This host has no distribution consumer, so a writer waiting on the full queue never resumes.
        internal bool DurablePathLost => BlockedWriters > 0;

        // A failed comparison prints every count, including the cancelled writers the description omits.
        internal (int Durable, int Queued, int Blocked, int Cancelled) Counts =>
            (DurableCaptures, QueuedNondurable, BlockedWriters, CancelledWriters);

        public override string ToString() => DurablePathLost
            ? $"The ephemeral queue is full and has no consumer: {QueuedNondurable} nondurable captures are queued, " +
              $"{BlockedWriters} capture writer is blocked and {DurableCaptures} captures were committed durably."
            : $"The durable capture path is intact: {DurableCaptures} captures were committed durably, " +
              $"{QueuedNondurable} nondurable captures are queued and no capture writer is blocked.";
    }

    // Returns no receipt, as nondurable ingress does, so the host passes each real frame to the ephemeral queue.
    private sealed class NondurableCaptureIngress(IRawCaptureIngress durable) : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => durable.InitializeAsync(cancellationToken);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }
}
