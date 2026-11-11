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
    IHostApplicationLifetime? hostLifetime = null) : BackgroundService
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
        using var timer = new PeriodicTimer(TimeSpan.FromHours(23), timeProvider);
        var force = false;
        do
        {
            try
            {
                await loader.RequestRetainedIntegrityScrubAsync(force, stoppingToken).ConfigureAwait(false);
                if (initializer is not null && accessor is { IsConfigured: false } && locations is
                    { Active: not null, RecentRetainedEvidenceReady: true })
                    await initializer.StartAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { ScrubFailed(logger); }
            force = true;
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await loader.DrainRetainedIntegrityScrubAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(7310, LogLevel.Warning, "Periodic retained integrity scrub failed; protected verification remains unqualified")]
    private static partial void ScrubFailed(ILogger logger);
}
