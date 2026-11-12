using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

// Recent startup admission is bounded. The same host-owned coordinator performs
// initial and periodic full qualification after Kestrel has started.
internal sealed partial class RetainedIntegrityScrubService(
    ICameraAgentConfigurationLoader loader, ILogger<RetainedIntegrityScrubService> logger, TimeProvider timeProvider,
    CameraAgentConfigurationInitializer? initializer = null,
    ICameraAgentConfigurationAccessor? accessor = null,
    DeploymentLocation.IDeploymentLocationStore? locations = null,
    IHostApplicationLifetime? hostLifetime = null, CaptureLaneState? captureState = null) : BackgroundService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An independent scrub failure must keep the host/recovery UI alive while protected verification remains refused.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (hostLifetime is not null && !hostLifetime.ApplicationStarted.IsCancellationRequested)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = hostLifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        var force = false;
        do
        {
            try
            {
                await loader.RequestRetainedIntegrityScrubAsync(force, stoppingToken).ConfigureAwait(false);
                await InitializeCaptureAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { ScrubFailed(logger); }
            // Incremental restarts preserve the previous full-scrub deadline.
            // Persistent faults retain the daily retry rather than a polling loop.
            var delay = locations is { RequiresRetainedIntegrityScrub: false, RetainedIntegrityRefreshUtc: { } refresh }
                ? refresh - timeProvider.GetUtcNow() : TimeSpan.FromHours(23);
            delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 30, TimeSpan.FromHours(23).TotalSeconds));
            await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            force = true;
        } while (!stoppingToken.IsCancellationRequested);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Deferred capture initialization exposes unhealthy readiness and sanitized failure events while retaining recovery access.")]
    private async Task InitializeCaptureAsync(CancellationToken stoppingToken)
    {
        if (initializer is null || accessor is not { IsConfigured: false } || locations is not
            { Active: not null, RecentRetainedEvidenceReady: true }) return;
        foreach (var seconds in new[] { 0, 1, 2, 4, 8 })
        {
            if (seconds != 0)
                await Task.Delay(TimeSpan.FromSeconds(seconds), timeProvider, stoppingToken).ConfigureAwait(false);
            try
            {
                await initializer.StartAsync(stoppingToken).ConfigureAwait(false);
                if (accessor.IsConfigured)
                    DeferredCaptureInitialization.Recover(captureState, typeof(CameraAgentConfigurationInitializer));
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                DeferredCaptureInitialization.Fail(logger, captureState, typeof(CameraAgentConfigurationInitializer), exception);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await loader.DrainRetainedIntegrityScrubAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(7310, LogLevel.Warning, "Retained integrity maintenance failed; protected verification readiness must be checked")]
    private static partial void ScrubFailed(ILogger logger);
}
