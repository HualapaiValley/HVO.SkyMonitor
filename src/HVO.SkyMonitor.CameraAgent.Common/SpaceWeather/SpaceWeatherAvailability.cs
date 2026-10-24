using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>Optional store availability; failure closes refresh without stopping the host.</summary>
public sealed class SpaceWeatherAvailability(ISwpcStore store, ILogger<SpaceWeatherAvailability> logger) : IDisposable
{
    /// <summary>The sole public unavailable reason, independent of filesystem details.</summary>
    public const string StoreUnavailableReasonCode = "space-weather.store-unavailable";
    private readonly SemaphoreSlim initialization = new(1, 1);
    private volatile bool available;

    /// <summary>A memory-only observation; initialization is explicit.</summary>
    public bool IsAvailable => available;

    /// <summary>Initializes or retries the store; caller cancellation propagates.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An optional store must fail closed without stopping the host; only the exception type is logged.")]
    public async ValueTask<bool> EnsureAvailableAsync(CancellationToken ct)
    {
        if (available) return true;
        await initialization.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!available)
            {
                await store.InitializeAsync(ct).ConfigureAwait(false);
                available = true;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { RecordFailure(exception); return false; }
        finally { initialization.Release(); }
    }

    internal void RecordFailure(Exception exception)
    {
        available = false;
        SpaceWeatherLog.Storage(logger, exception.GetType().Name);
    }

    public void Dispose() => initialization.Dispose();
}

/// <summary>Initializes even while disabled, ahead of the first automation sweep, and retries failures.</summary>
internal sealed class SpaceWeatherInitializationService(SpaceWeatherAvailability availability,
    SpaceWeatherRefreshJob job, TimeProvider clock) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await availability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false))
            await job.PublishStateAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (availability.IsAvailable) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                if (await availability.EnsureAvailableAsync(stoppingToken).ConfigureAwait(false))
                {
                    await job.PublishStateAsync(stoppingToken).ConfigureAwait(false);
                    return;
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
