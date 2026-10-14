using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingStateRefreshService(
    SqliteCaptureProcessingStore store,
    CaptureProcessingState state,
    TimeProvider timeProvider,
    IRawCaptureIngress? rawIngress = null,
    ProcessingGraphOperationsCoordinator? graphOperations = null,
    ILogger<CaptureProcessingStateRefreshService>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<CaptureProcessingStateRefreshService>.Instance;
    private string? _reportedSupersededRevision;

    internal async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        if (rawIngress is not null)
        {
            await rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        var durable = await store.ReadOperationalStateAsync(cancellationToken).ConfigureAwait(false);
        var inventory = await store.ReadAvailabilityInventoryAsync(cancellationToken).ConfigureAwait(false);
        state.SetDurable(
            durable.PendingCount,
            durable.RetryCount,
            durable.TerminalCount,
            durable.OldestPendingUtc);
        state.SetReplayDurable(
            durable.ReplayPendingCount,
            durable.ReplayRetryCount,
            durable.ReplayTerminalCount,
            durable.OldestReplayPendingUtc,
            durable.ReplayPendingBytes);
        state.SetProcessingEvidence(inventory.MissingCount, inventory.QuarantinedCount);
        if (graphOperations is not null)
        {
            ObserveActiveRevision(
                await graphOperations.FindSupersededActiveRevisionAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    // A superseded active revision is reported once per transition rather than per abandoned capture.
    private void ObserveActiveRevision(ProcessingActiveRevisionSupersession? supersession)
    {
        state.SetActiveRevisionSuperseded(supersession?.RevisionId);
        if (string.Equals(_reportedSupersededRevision, supersession?.RevisionId, StringComparison.Ordinal)) return;
        if (supersession is not null)
        {
            _logger.ProcessingActiveRevisionSuperseded(
                supersession.RevisionId, supersession.NodeId, supersession.StoredPlanSha256, supersession.CurrentPlanSha256);
        }
        else
        {
            _logger.ProcessingActiveRevisionSupersededCleared(_reportedSupersededRevision!);
        }
        _reportedSupersededRevision = supersession?.RevisionId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.SetRefreshFailure();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
