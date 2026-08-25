using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class DerivedProductReconciliationService(
    IOptions<CameraAgentHostOptions> options,
    SqliteCaptureProcessingStore store,
    CaptureProcessingTelemetry telemetry,
    CaptureProcessingState state,
    Capture.Distribution.CaptureDistributionService distribution,
    ILogger<DerivedProductReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconciler = new DerivedProductReconciler(
            options.Value.RawIngressRoot, store, options.Value.DerivedProductLifecycle, TimeProvider.System,
            distribution.NotifyCommittedCapture);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var summary = await reconciler.RunAsync(stoppingToken).ConfigureAwait(false);
                telemetry.RecordReconciliation(summary);
                var inventory = await store.ReadAvailabilityInventoryAsync(stoppingToken).ConfigureAwait(false);
                state.SetProcessingEvidence(inventory.MissingCount, inventory.QuarantinedCount);
                state.SetReconciliationFailure(false);
                logger.DerivedProductReconciliationCompleted(
                    summary.Inspected, summary.Available, summary.Recoverable, summary.Cleaned,
                    summary.Missing, summary.Quarantined, summary.QuarantineBytes);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.SetReconciliationFailure(true);
                logger.DerivedProductReconciliationFailed(exception);
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
