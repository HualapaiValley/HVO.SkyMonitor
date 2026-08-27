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
            var ingress = new InitializingIngress(journal);
            using var store = new SqliteCaptureProcessingStore(options);
            var state = new CaptureProcessingState();
            state.SetProcessingEvidence(2, 3);
            using var service = new CaptureProcessingStateRefreshService(
                store, state, TimeProvider.System, ingress);

            await service.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, ingress.InitializeCount);
            Assert.AreEqual(0, state.Snapshot.MissingProductCount);
            Assert.AreEqual(0, state.Snapshot.ProcessingQuarantineCount);
            Assert.AreEqual(CaptureProcessingAvailability.Healthy, state.Snapshot.Availability);

            var failedState = new CaptureProcessingState();
            var failingIngress = new FailingIngress();
            using var failedService = new CaptureProcessingStateRefreshService(
                store, failedState, TimeProvider.System, failingIngress);
            await failedService.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await failingIngress.Attempted.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            for (var attempt = 0; attempt < 50 && !failedState.Snapshot.DurableStateUnavailable; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
            }
            Assert.IsTrue(failedState.Snapshot.DurableStateUnavailable);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await failedService.StopAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class InitializingIngress(SqliteRawCaptureJournal journal) : IRawCaptureIngress
    {
        internal int InitializeCount { get; private set; }

        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            HVO.SkyMonitor.AgentCore.CameraModuleConfig configuration,
            HVO.SkyMonitor.AgentCore.CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FailingIngress : IRawCaptureIngress
    {
        private readonly TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Attempted => _attempted.Task;

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            _attempted.TrySetResult();
            return ValueTask.FromException(new InvalidDataException("invalid raw evidence"));
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            HVO.SkyMonitor.AgentCore.CameraModuleConfig configuration,
            HVO.SkyMonitor.AgentCore.CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
