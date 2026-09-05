using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>The autoscaler's last observation, shared with the health check and the meter.</summary>
internal sealed record ElasticProviderSnapshot(
    DateTimeOffset SampledAtUtc,
    string Provider,
    int Starting,
    int Running,
    int Idle,
    int Backlog,
    long OldestBacklogAgeSeconds,
    int InstanceMinutesToday,
    string LastDecision,
    int OrphansCleaned);

/// <summary>Elastic provider signals (#430): instance lifecycle, placement decisions, cold starts, and accounting.</summary>
internal sealed class ElasticProviderTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.ElasticProviders";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _provisions;
    private readonly Counter<long> _retirements;
    private readonly Counter<long> _orphans;
    private readonly Counter<long> _rejectedPlacements;
    private readonly Histogram<double> _coldStart;
    private readonly ConcurrentDictionary<string, ElasticProviderSnapshot> _snapshots = new(StringComparer.Ordinal);

    public ElasticProviderTelemetry()
    {
        _provisions = _meter.CreateCounter<long>("skymonitor.central.elastic.provisions", "{instance}");
        _retirements = _meter.CreateCounter<long>("skymonitor.central.elastic.retirements", "{instance}");
        _orphans = _meter.CreateCounter<long>("skymonitor.central.elastic.orphans_cleaned", "{instance}");
        _rejectedPlacements = _meter.CreateCounter<long>("skymonitor.central.elastic.placements_rejected", "{decision}");
        _coldStart = _meter.CreateHistogram<double>("skymonitor.central.elastic.cold_start", "ms");
        _meter.CreateObservableGauge("skymonitor.central.elastic.instances", ObserveInstances, "{instance}");
        _meter.CreateObservableGauge("skymonitor.central.elastic.backlog", ObserveBacklog, "{job}");
        _meter.CreateObservableGauge("skymonitor.central.elastic.instance_minutes_today", ObserveMinutes, "min");
    }

    public void RecordProvision(string provider) => _provisions.Add(1, Tag(provider));

    public void RecordRetirement(string provider, string reason)
        => _retirements.Add(1, Tag(provider), new KeyValuePair<string, object?>("reason", reason));

    public void RecordOrphanCleaned(string provider) => _orphans.Add(1, Tag(provider));

    public void RecordRejectedPlacement(string provider, string reason)
        => _rejectedPlacements.Add(1, Tag(provider), new KeyValuePair<string, object?>("reason", reason));

    public void RecordColdStart(string provider, TimeSpan duration) => _coldStart.Record(duration.TotalMilliseconds, Tag(provider));

    public void UpdateSnapshot(ElasticProviderSnapshot snapshot) => _snapshots[snapshot.Provider] = snapshot;

    public ElasticProviderSnapshot? Snapshot(string provider) => _snapshots.TryGetValue(provider, out var snapshot) ? snapshot : null;

    private static KeyValuePair<string, object?> Tag(string provider) => new("provider", provider);

    private IEnumerable<Measurement<long>> ObserveInstances()
    {
        foreach (var snapshot in _snapshots.Values)
        {
            yield return new Measurement<long>(snapshot.Starting, Tag(snapshot.Provider), new KeyValuePair<string, object?>("state", "starting"));
            yield return new Measurement<long>(snapshot.Running, Tag(snapshot.Provider), new KeyValuePair<string, object?>("state", "running"));
            yield return new Measurement<long>(snapshot.Idle, Tag(snapshot.Provider), new KeyValuePair<string, object?>("state", "idle"));
        }
    }

    private IEnumerable<Measurement<long>> ObserveBacklog()
        => _snapshots.Values.Select(snapshot => new Measurement<long>(snapshot.Backlog, Tag(snapshot.Provider)));

    private IEnumerable<Measurement<long>> ObserveMinutes()
        => _snapshots.Values.Select(snapshot => new Measurement<long>(snapshot.InstanceMinutesToday, Tag(snapshot.Provider)));

    public void Dispose() => _meter.Dispose();
}
