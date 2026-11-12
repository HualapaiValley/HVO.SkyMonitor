using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

// Retained-history recovery and consumers of deferred capture configuration must
// not hold the host's sequential StartAsync loop before ApplicationStarted.
internal sealed class AfterApplicationStartedService<T>(
    T service, IHostApplicationLifetime lifetime, CaptureLaneState state,
    ILogger<AfterApplicationStartedService<T>> logger) : BackgroundService where T : IHostedService
{
    private static readonly Action<ILogger, string, string, Exception?> InitializationFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(7312, "DeferredCaptureInitializationFailed"),
        "Deferred capture initialization {Service} failed ({ErrorType}); capture readiness is unhealthy");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Keep recovery available after initialization failure; readiness is unhealthy and diagnostics contain only the error type.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!lifetime.ApplicationStarted.IsCancellationRequested)
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
                await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            stoppingToken.ThrowIfCancellationRequested();
            await service.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            state.SetUnhealthy("startup-initialization-failed", typeof(T).Name);
            InitializationFailed(logger, typeof(T).Name, exception.GetType().Name, null);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await service.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
