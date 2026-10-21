using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// The one shared read of the space-weather state for every operator surface. A projection, or a failure, is reused
/// for <see cref="SpaceWeatherUiBounds.ProjectionTtl"/>; concurrent callers share one in-flight read, which runs under
/// its own bounded timeout rather than any caller's token, so a caller that stops waiting never cancels it for the
/// others. It never writes to the store and never changes the store's availability.
/// </summary>
internal sealed class SpaceWeatherUiProjectionSource(
    SpaceWeatherAvailability availability,
    ISwpcStore store,
    SpaceWeatherOperations operations,
    IDeploymentLocationStore locations,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider clock,
    ILogger<SpaceWeatherUiProjectionSource> logger,
    ISolarEventCalculator? solarEvents = null)
{
    private readonly Lock gate = new();
    private readonly ISolarEventCalculator sun = solarEvents ?? new AstronomyEngineSolarEventCalculator();
    private readonly SpaceWeatherDarknessCache darkness = new();
    private SpaceWeatherUiProjection? cached;
    private DateTimeOffset cachedAt;
    private TaskCompletionSource<SpaceWeatherUiProjection>? inflight;
    private long generation;

    /// <summary>The darkness cache, exposed for its bound.</summary>
    internal SpaceWeatherDarknessCache Darkness => darkness;

    /// <summary>
    /// Returns the cached projection while it is fresh, otherwise joins or starts the shared read. Only
    /// <paramref name="cancellationToken"/> cancellation escapes; every read failure is a projection state. A caller
    /// that is already cancelled never starts a read.
    /// </summary>
    internal Task<SpaceWeatherUiProjection> GetAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<SpaceWeatherUiProjection>(cancellationToken);
        }
        TaskCompletionSource<SpaceWeatherUiProjection>? started = null;
        Task<SpaceWeatherUiProjection> shared;
        long readGeneration;
        lock (gate)
        {
            if (cached is { } hit && clock.GetUtcNow() - cachedAt < SpaceWeatherUiBounds.ProjectionTtl)
            {
                return Task.FromResult(hit);
            }
            if (inflight is null)
            {
                inflight = started = new TaskCompletionSource<SpaceWeatherUiProjection>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            shared = inflight.Task;
            readGeneration = generation;
        }
        if (started is not null)
        {
            _ = CompleteAsync(started, readGeneration);
        }
        return shared.WaitAsync(cancellationToken);
    }

    /// <summary>Drops the cached projection; a read already in flight completes for its waiters but is not cached.</summary>
    internal void Invalidate()
    {
        lock (gate)
        {
            cached = null;
            inflight = null;
            generation++;
        }
    }

    private async Task CompleteAsync(TaskCompletionSource<SpaceWeatherUiProjection> completion, long readGeneration)
    {
        var projection = await ReadAsync().ConfigureAwait(false);
        lock (gate)
        {
            if (readGeneration == generation)
            {
                cached = projection;
                cachedAt = clock.GetUtcNow();
            }
            if (ReferenceEquals(inflight, completion))
            {
                inflight = null;
            }
        }
        completion.TrySetResult(projection);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A read failure becomes a cached projection state; only the exception type is logged.")]
    private async Task<SpaceWeatherUiProjection> ReadAsync()
    {
        var location = locations.Active;
        var enabled = options.Value.SpaceWeather.Enabled;
        var observed = clock.GetUtcNow();
        try
        {
            using var timeout = new CancellationTokenSource(SpaceWeatherUiBounds.ReadTimeout, clock);
            if (!await availability.EnsureAvailableAsync(timeout.Token).ConfigureAwait(false))
            {
                return SpaceWeatherUiProjectionBuilder.Unavailable(
                    SpaceWeatherUiState.StoreUnavailable, enabled, location, observed);
            }
            var (now, view, status) = await ReadPairAsync(timeout.Token).ConfigureAwait(false);
            var inconsistent = false;
            if (status.StoreAvailable && status.Revision != view.Revision)
            {
                (now, view, status) = await ReadPairAsync(timeout.Token).ConfigureAwait(false);
                if (status.StoreAvailable && status.Revision != view.Revision)
                {
                    inconsistent = true;
                    SpaceWeatherUiLog.Inconsistent(logger);
                }
            }
            if (!status.StoreAvailable)
            {
                return SpaceWeatherUiProjectionBuilder.Unavailable(
                    SpaceWeatherUiState.StoreUnavailable, enabled, location, now, status);
            }
            return SpaceWeatherUiProjectionBuilder.Build(status, view, location, now, inconsistent, darkness, sun);
        }
        catch (Exception exception) when (exception is SwpcStorageException or InvalidDataException)
        {
            SpaceWeatherUiLog.ReadFailed(logger, exception.GetType().Name);
            return SpaceWeatherUiProjectionBuilder.Unavailable(SpaceWeatherUiState.StoreUnavailable, enabled, location, observed);
        }
        catch (Exception exception)
        {
            SpaceWeatherUiLog.ReadFailed(logger, exception.GetType().Name);
            return SpaceWeatherUiProjectionBuilder.Unavailable(SpaceWeatherUiState.ReadFailed, enabled, location, observed);
        }
    }

    private async Task<(DateTimeOffset Now, SwpcStoreView View, SpaceWeatherOperations.Status Status)> ReadPairAsync(
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var view = await store.ReadAsync(now, cancellationToken).ConfigureAwait(false);
        var status = await operations.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return (now, view, status);
    }
}
