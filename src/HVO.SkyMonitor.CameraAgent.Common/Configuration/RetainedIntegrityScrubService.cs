using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

// Startup configuration performs the first full scrub. This independent timer ensures
// out-of-band changes are checked even when no lifecycle command/restart occurs.
internal sealed partial class RetainedIntegrityScrubService(
    ICameraAgentConfigurationLoader loader, ILogger<RetainedIntegrityScrubService> logger, TimeProvider timeProvider) : BackgroundService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An independent scrub failure must keep the host/recovery UI alive while protected verification remains refused.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { _ = await loader.LoadForRetainedIntegrityScrubAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { ScrubFailed(logger); }
        }
    }

    [LoggerMessage(7310, LogLevel.Warning, "Periodic retained integrity scrub failed; protected verification remains unqualified")]
    private static partial void ScrubFailed(ILogger logger);
}
