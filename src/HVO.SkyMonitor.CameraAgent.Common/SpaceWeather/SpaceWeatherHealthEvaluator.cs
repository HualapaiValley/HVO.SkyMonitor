using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>Optional dependency health, capped at Degraded.</summary>
public sealed record SpaceWeatherHealthSnapshot(bool Degraded, string Description, IReadOnlyDictionary<string, object> Data);

/// <summary>
/// Evaluates retained facts without provider requests. First success is overdue only when a configured
/// product has no success and its earliest retained attempt is at least its fixed outdated threshold old.
/// With no retained attempt there is no overdue claim; retention bounds this observation to the journal.
/// </summary>
public sealed class SpaceWeatherHealthEvaluator(ISwpcStore store, SpaceWeatherAvailability availability,
    IOptions<CameraAgentHostOptions> options, TimeProvider clock)
{
    /// <summary>Returns bounded reason/count data; disabled is healthy unless storage is unavailable.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Health failures disclose no exception content and never exceed Degraded.")]
    public async ValueTask<SpaceWeatherHealthSnapshot> EvaluateAsync(CancellationToken ct)
    {
        try
        {
            if (!await availability.EnsureAvailableAsync(ct).ConfigureAwait(false)) return Snapshot("space-weather.store-unavailable");
            var now = clock.GetUtcNow().ToUniversalTime();
            var health = await store.ReadHealthAsync(now, ct).ConfigureAwait(false);
            if (!options.Value.SpaceWeather.Enabled) return Snapshot(null, disabled: true);
            var configured = options.Value.SpaceWeather.Products;
            if (health.ProviderHalt is not null) return Snapshot("space-weather.provider-halted");
            if (configured.Any(p => health.Products[p].ProductHalt is not null)) return Snapshot("space-weather.product-halted");
            if (configured.Contains(SwpcProduct.Alerts) && health.Products[SwpcProduct.Alerts].Freshness == SwpcFreshness.Outdated)
                return Snapshot("space-weather.alerts-outdated");
            if (configured.Any(p => health.Products[p].Checkpoint.ConsecutiveRejected >= SwpcSource.RejectedAttentionCount))
                return Snapshot("space-weather.format-drift");
            if (configured.Contains(SwpcProduct.Alerts) && health.Products[SwpcProduct.Alerts].Checkpoint.RecordDriftStampUtc is not null)
                return Snapshot("space-weather.record-drift");
            foreach (var product in configured)
                if (health.Products[product].LastSuccessUtc is null &&
                    await FirstSuccessOverdueAsync(product, now, health.AttemptCount, ct).ConfigureAwait(false))
                    return Snapshot("space-weather.first-success-overdue");
            if (health.RawBodyBytes > 2L * 256 * 1024 * 1024) return Snapshot("space-weather.store-oversized");
            return Snapshot(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { availability.RecordFailure(exception); return Snapshot("space-weather.store-unavailable"); }
    }

    private async ValueTask<bool> FirstSuccessOverdueAsync(SwpcProduct product, DateTimeOffset now, long bound, CancellationToken ct)
    {
        long? cursor = null;
        DateTimeOffset? oldest = null;
        for (long read = 0; read <= bound;)
        {
            var page = await store.ReadDiagnosticsAsync(new(now, product, cursor, 200), ct).ConfigureAwait(false);
            foreach (var attempt in page.Attempts)
                if (oldest is null || attempt.StartedUtc < oldest) oldest = attempt.StartedUtc;
            read += page.Attempts.Length;
            if (page.NextBeforeSequence is null) return oldest is { } start && now - start >= SwpcSource.OutdatedAfter(product);
            if (page.Attempts.IsEmpty || cursor is { } before && page.NextBeforeSequence >= before)
                throw new SwpcStorageException("Space-weather journal paging is inconsistent.");
            cursor = page.NextBeforeSequence;
        }
        throw new SwpcStorageException("Space-weather journal paging exceeded its retained bound.");
    }

    private static SpaceWeatherHealthSnapshot Snapshot(string? reason, bool disabled = false)
        => new(reason is not null, reason ?? (disabled ? "disabled" : "space-weather.ready"),
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Reason"] = reason ?? (disabled ? "space-weather.disabled" : "space-weather.ready"),
                ["Availability"] = reason == "space-weather.store-unavailable" ? "Unavailable" : disabled ? "Disabled" : "Available"
            });
}
