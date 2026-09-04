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
    private readonly Counter<long> _completions;
    private readonly Counter<long> _usageBytes;
    private readonly ConcurrentDictionary<Guid, CentralObservatoryQueueMeasurement> _queues = new();

    public CentralProcessingFairnessTelemetry()
    {
        _throttled = _meter.CreateCounter<long>("skymonitor.central.fairness.throttled", "{claim}");
        _completions = _meter.CreateCounter<long>("skymonitor.central.fairness.completions", "{attempt}");
        _usageBytes = _meter.CreateCounter<long>("skymonitor.central.fairness.usage.bytes", "By");
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

    public void RecordCompletion(Guid observatoryId, string resourceClass, string outcome, long inputBytes, long outputBytes)
    {
        var observatory = new KeyValuePair<string, object?>("observatory", observatoryId.ToString("D"));
        _completions.Add(1, observatory,
            new KeyValuePair<string, object?>("class", resourceClass),
            new KeyValuePair<string, object?>("outcome", outcome));
        if (inputBytes > 0)
        {
            _usageBytes.Add(inputBytes, observatory, new KeyValuePair<string, object?>("direction", "input"));
        }
        if (outputBytes > 0)
        {
            _usageBytes.Add(outputBytes, observatory, new KeyValuePair<string, object?>("direction", "output"));
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
