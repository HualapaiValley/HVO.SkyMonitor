using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

/// <summary>One recoverable video worker. Failure is contained here and never terminates capture or automation.</summary>
internal sealed partial class TimeLapseWorker(IOptions<CameraAgentHostOptions> options, SqliteTimeLapseStore store,
    TimeLapseGenerator generator, TimeProvider clock, ILogger<TimeLapseWorker> logger) : BackgroundService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional background video failures must never terminate acquisition; errors are logged and retried under the worker lease.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.TimeLapses.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lease = await store.AcquireWorkerAsync(stoppingToken).ConfigureAwait(false);
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await store.ClaimAsync(stoppingToken).ConfigureAwait(false) is { } job)
                        await ExecuteJobAsync(job, stoppingToken).ConfigureAwait(false);
                    else await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                WorkerUnavailable(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The job boundary records every producer failure as a sanitized terminal state; host cancellation still propagates.")]
    internal async ValueTask ExecuteJobAsync(CameraAgentTimeLapseJob job, CancellationToken token)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(TimeSpan.FromHours(1));
            await generator.RunAsync(job, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var reason = exception is TimeLapseEncodingException media ? media.ReasonCode
                : exception is OperationCanceledException ? "timelapse.timeout" : "timelapse.generation-failed";
            var unavailable = reason.StartsWith("timelapse.encoder-", StringComparison.Ordinal);
            await store.CompleteAsync(job.JobId, unavailable ? CameraAgentTimeLapseState.Unavailable : CameraAgentTimeLapseState.Failed,
                reason, null, token).ConfigureAwait(false);
            JobFailed(logger, job.JobId, reason, exception);
        }
    }

    [LoggerMessage(11300, LogLevel.Warning, "Time-lapse worker is unavailable; acquisition continues.")]
    private static partial void WorkerUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(11301, LogLevel.Warning, "Time-lapse job {JobId} failed with {ReasonCode}; acquisition continues.")]
    private static partial void JobFailed(ILogger logger, Guid jobId, string reasonCode, Exception exception);
}
