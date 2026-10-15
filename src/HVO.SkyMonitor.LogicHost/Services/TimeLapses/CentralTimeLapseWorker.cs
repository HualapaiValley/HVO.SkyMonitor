using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

/// <summary>One optional central lane. Failures stay in video state and never stop ingest or the existing workers.</summary>
internal sealed partial class CentralTimeLapseWorker(IServiceScopeFactory scopes, IOptions<CentralTimeLapseOptions> options,
    TimeProvider clock, ILogger<CentralTimeLapseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var nextScan = DateTimeOffset.MinValue;
        var targetIndex = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                options.Value.Validate();
                if (clock.GetUtcNow() >= nextScan && options.Value.Targets.Count > 0)
                {
                    nextScan = clock.GetUtcNow().AddMinutes(1);
                    var target = options.Value.Targets[targetIndex++ % options.Value.Targets.Count];
                    try
                    {
                        await using var scanScope = scopes.CreateAsyncScope();
                        await scanScope.ServiceProvider.GetRequiredService<CentralTimeLapseScheduler>().ScanAsync(target, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        Log.DiscoveryFailed(logger, target.DevicePublicId, exception.GetType().Name);
                    }
                }
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<CentralTimeLapseStore>();
                if (await store.ClaimAsync(stoppingToken).ConfigureAwait(false) is { } lease)
                    await RunLeasedAsync(scope.ServiceProvider, store, lease, stoppingToken).ConfigureAwait(false);
                else await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                Log.WorkerFailed(logger, exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The optional video lane contains job failures, reaps execution, and persists the failure without stopping ingest.")]
    private async Task RunLeasedAsync(IServiceProvider services, CentralTimeLapseStore store, CentralTimeLapseLease lease, CancellationToken stop)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cancellation.CancelAfter(TimeSpan.FromHours(1));
        var execution = services.GetRequiredService<CentralTimeLapseGenerator>().RunAsync(lease, cancellation.Token);
        try
        {
            while (!execution.IsCompleted)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(20), clock, cancellation.Token);
                if (await Task.WhenAny(execution, delay).ConfigureAwait(false) == execution) break;
                await delay.ConfigureAwait(false);
                await using var renewal = scopes.CreateAsyncScope();
                using var renewalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                renewalTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                if (!await renewal.ServiceProvider.GetRequiredService<CentralTimeLapseStore>().RenewAsync(lease, renewalTimeout.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("The central video lease is no longer current.");
            }
            await execution.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            // Reap any child process and stop publication before this worker can claim another job.
            try { await execution.ConfigureAwait(false); }
            catch (Exception) when (execution.IsFaulted || execution.IsCanceled) { }
            try
            {
                // A failed attempt owns only its own and older prefixes. Committed gap/hour products survive.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await services.GetRequiredService<CentralTimeLapseObjects>().RemoveUnpublishedAsync(lease,
                    await store.RetainedChunkKeysAsync(lease, cleanup.Token).ConfigureAwait(false), cleanup.Token,
                    includeCurrentAttempt: true).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                // An unavailable store retains bounded attempt prefixes for recovery; never obscure the original failure.
                Log.WorkerFailed(logger, cleanupFailure.GetType().Name);
            }
            stop.ThrowIfCancellationRequested();
            var reason = exception is TimeLapseEncodingException media ? media.ReasonCode :
                exception is OperationCanceledException ? "timelapse.job-timeout" : "timelapse.generation-failed";
            var state = exception is TimeLapseEncodingException ? CentralTimeLapseState.Unavailable : CentralTimeLapseState.Failed;
            await store.CompleteAsync(lease, state, reason, null, stop).ConfigureAwait(false);
            Log.JobFailed(logger, lease.JobId, reason);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(11310, LogLevel.Warning, "Central time-lapse discovery failed for {DeviceId}: {FailureType}")]
        public static partial void DiscoveryFailed(ILogger logger, Guid deviceId, string failureType);
        [LoggerMessage(11311, LogLevel.Warning, "Central time-lapse worker unavailable: {FailureType}")]
        public static partial void WorkerFailed(ILogger logger, string failureType);
        [LoggerMessage(11312, LogLevel.Warning, "Central time-lapse job {JobId} ended: {ReasonCode}")]
        public static partial void JobFailed(ILogger logger, Guid jobId, string reasonCode);
    }
}

internal static class CentralTimeLapseServices
{
    internal static IServiceCollection AddCentralTimeLapses(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CentralTimeLapseOptions>(configuration.GetSection("CentralTimeLapses"));
        services.TryAddSingleton<IPlanetEphemeris, AstronomyEnginePlanetEphemeris>();
        services.AddSingleton(provider => new FFmpegTimeLapseEncoder(provider.GetRequiredService<IOptions<CentralTimeLapseOptions>>().Value.Encoder));
        services.AddScoped<CentralTimeLapseStore>();
        services.AddScoped<CentralTimeLapseSources>();
        services.AddScoped<CentralTimeLapseObjects>();
        services.AddScoped<CentralTimeLapseScheduler>();
        services.AddScoped<CentralTimeLapseGenerator>();
        services.AddScoped<CentralTimeLapseCatalog>();
        services.AddHostedService<CentralTimeLapseWorker>();
        return services;
    }
}
