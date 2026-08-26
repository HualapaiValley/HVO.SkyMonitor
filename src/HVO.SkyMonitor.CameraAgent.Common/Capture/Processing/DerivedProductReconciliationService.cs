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
    internal async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        using var activity = CaptureProcessingTelemetry.ActivitySource.StartActivity(
            "processing-artifact.reconcile");
        try
        {
            var reconciler = new DerivedProductReconciler(
                options.Value.RawIngressRoot, store, options.Value.DerivedProductLifecycle, TimeProvider.System,
                distribution.NotifyCommittedCapture);
            var summary = await reconciler.RunAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordReconciliation(summary);
            var inventory = await store.ReadAvailabilityInventoryAsync(cancellationToken).ConfigureAwait(false);
            state.SetProcessingEvidence(inventory.MissingCount, inventory.QuarantinedCount);
            state.SetReconciliationFailure(false);
            logger.DerivedProductReconciliationCompleted(
                summary.Inspected, summary.Available, summary.Recoverable, summary.Cleaned,
                summary.Missing, summary.Quarantined, summary.QuarantineBytes);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetTag("error.type", exception.GetType().Name);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "reconciliation-failed");
            state.SetReconciliationFailure(true);
            logger.DerivedProductReconciliationFailed(exception);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
