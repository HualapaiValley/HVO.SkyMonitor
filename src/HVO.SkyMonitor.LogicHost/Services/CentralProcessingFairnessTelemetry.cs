using HVO.SkyMonitor.LogicHost.Data;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralObservatoryQueueMeasurement(
    Guid ObservatoryId,
    long Pending,
    long Leased,
    long Waiting,
    long OldestPendingAgeSeconds,
    int Entitlement);

/// <summary>Per-observatory scheduling signals for fair scheduling and entitlements (#429).</summary>
internal sealed class CentralProcessingFairnessTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.ProcessingFairness";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _throttled;
    private readonly ConcurrentDictionary<Guid, CentralObservatoryQueueMeasurement> _queues = new();
    private readonly ConcurrentDictionary<(Guid ObservatoryId, string ResourceClass, string Outcome), (long Attempts, long InputBytes, long OutputBytes)> _usage = new();

    public CentralProcessingFairnessTelemetry()
    {
        _throttled = _meter.CreateCounter<long>("skymonitor.central.fairness.throttled", "{claim}");
        // Completions and usage bytes are cumulative totals read from the persisted CentralProcessingUsageRollups (a
        // durable global fact, identical on every replica), so nothing is lost when a process exits between scrapes.
        _meter.CreateObservableCounter("skymonitor.central.fairness.completions", ObserveCompletions, "{attempt}");
        _meter.CreateObservableCounter("skymonitor.central.fairness.usage.bytes", ObserveUsageBytes, "By");
        _meter.CreateObservableGauge("skymonitor.central.fairness.queue", ObserveQueue, "{job}");
        _meter.CreateObservableGauge("skymonitor.central.fairness.queue.oldest_age", ObserveOldestAge, "s");
        _meter.CreateObservableGauge("skymonitor.central.fairness.active", ObserveActive, "{job}");
        _meter.CreateObservableGauge("skymonitor.central.fairness.entitlement", ObserveEntitlement, "{job}");
        _meter.CreateObservableGauge("skymonitor.central.fairness.saturation", ObserveSaturation, "1");
    }

    public void RecordThrottled(Guid observatoryId, string reason)
        => _throttled.Add(1,
            new KeyValuePair<string, object?>("observatory", observatoryId.ToString("D")),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Replaces the cumulative usage totals with the persisted rollup rows.</summary>
    public void ReplaceUsageTotals(IEnumerable<CentralProcessingUsageRollup> rollups)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        var replacement = new Dictionary<(Guid, string, string), (long, long, long)>();
        foreach (var rollup in rollups)
        {
            replacement[(rollup.ObservatoryId, rollup.ResourceClass, rollup.Outcome.ToLowerInvariant())] = (rollup.Attempts, rollup.InputBytes, rollup.OutputBytes);
        }
        foreach (var key in _usage.Keys.Where(key => !replacement.ContainsKey(key)).ToArray())
        {
            _usage.TryRemove(key, out _);
        }
        foreach (var pair in replacement)
        {
            _usage[pair.Key] = pair.Value;
        }
    }

    private IEnumerable<Measurement<long>> ObserveCompletions()
        => _usage.Select(pair => new Measurement<long>(
            pair.Value.Attempts,
            new KeyValuePair<string, object?>("observatory", pair.Key.ObservatoryId.ToString("D")),
            new KeyValuePair<string, object?>("class", pair.Key.ResourceClass),
            new KeyValuePair<string, object?>("outcome", pair.Key.Outcome)));

    private IEnumerable<Measurement<long>> ObserveUsageBytes()
    {
        foreach (var group in _usage.GroupBy(pair => pair.Key.ObservatoryId))
        {
            var observatory = new KeyValuePair<string, object?>("observatory", group.Key.ToString("D"));
            yield return new Measurement<long>(group.Sum(pair => pair.Value.InputBytes), observatory, new KeyValuePair<string, object?>("direction", "input"));
            yield return new Measurement<long>(group.Sum(pair => pair.Value.OutputBytes), observatory, new KeyValuePair<string, object?>("direction", "output"));
        }
    }

    public void UpdateQueueSnapshot(IReadOnlyCollection<CentralObservatoryQueueMeasurement> measurements)
    {
        _queues.Clear();
        foreach (var measurement in measurements)
        {
            _queues[measurement.ObservatoryId] = measurement;
        }
    }

    public IReadOnlyCollection<CentralObservatoryQueueMeasurement> Snapshot => _queues.Values.ToArray();

    private IEnumerable<Measurement<long>> ObserveQueue()
    {
        foreach (var pair in _queues)
        {
            var observatory = new KeyValuePair<string, object?>("observatory", pair.Key.ToString("D"));
            yield return new Measurement<long>(pair.Value.Pending, observatory, new KeyValuePair<string, object?>("status", "pending"));
            yield return new Measurement<long>(pair.Value.Leased, observatory, new KeyValuePair<string, object?>("status", "leased"));
            yield return new Measurement<long>(pair.Value.Waiting, observatory, new KeyValuePair<string, object?>("status", "waiting"));
        }
    }

    private IEnumerable<Measurement<long>> ObserveOldestAge()
        => _queues.Select(pair => new Measurement<long>(
            pair.Value.OldestPendingAgeSeconds, new KeyValuePair<string, object?>("observatory", pair.Key.ToString("D"))));

    private IEnumerable<Measurement<long>> ObserveActive()
        => _queues.Select(pair => new Measurement<long>(
            pair.Value.Leased, new KeyValuePair<string, object?>("observatory", pair.Key.ToString("D"))));

    private IEnumerable<Measurement<long>> ObserveEntitlement()
        => _queues.Select(pair => new Measurement<long>(
            pair.Value.Entitlement, new KeyValuePair<string, object?>("observatory", pair.Key.ToString("D"))));

    private IEnumerable<Measurement<double>> ObserveSaturation()
        => _queues.Select(pair => new Measurement<double>(
            pair.Value.Entitlement <= 0 ? 0 : Math.Min(1.0, pair.Value.Leased / (double)pair.Value.Entitlement),
            new KeyValuePair<string, object?>("observatory", pair.Key.ToString("D"))));

    public void Dispose() => _meter.Dispose();
}
