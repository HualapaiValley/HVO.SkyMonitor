using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly Histogram<long> _allocationJobs;
    private readonly Histogram<long> _allocationSlots;
    private readonly Histogram<long> _allocationEdgeVisits;
    private readonly Histogram<double> _allocationDuration;
    private readonly ConcurrentDictionary<string, ElasticProviderSnapshot> _snapshots = new(StringComparer.Ordinal);

    public ElasticProviderTelemetry()
    {
        _provisions = _meter.CreateCounter<long>("skymonitor.central.elastic.provisions", "{instance}");
        _retirements = _meter.CreateCounter<long>("skymonitor.central.elastic.retirements", "{instance}");
        _orphans = _meter.CreateCounter<long>("skymonitor.central.elastic.orphans_cleaned", "{instance}");
        _rejectedPlacements = _meter.CreateCounter<long>("skymonitor.central.elastic.placements_rejected", "{decision}");
        _coldStart = _meter.CreateHistogram<double>("skymonitor.central.elastic.cold_start", "ms");
        _allocationJobs = _meter.CreateHistogram<long>("skymonitor.central.elastic.allocation.jobs", "{job}");
        _allocationSlots = _meter.CreateHistogram<long>("skymonitor.central.elastic.allocation.slots", "{slot}");
        _allocationEdgeVisits = _meter.CreateHistogram<long>("skymonitor.central.elastic.allocation.edge_visits", "{visit}");
        _allocationDuration = _meter.CreateHistogram<double>("skymonitor.central.elastic.allocation.duration", "ms");
        _meter.CreateObservableGauge("skymonitor.central.elastic.instances", ObserveInstances, "{instance}");
        _meter.CreateObservableGauge("skymonitor.central.elastic.backlog", ObserveBacklog, "{job}");
        _meter.CreateObservableGauge("skymonitor.central.elastic.instance_minutes_today", ObserveMinutes, "min");
    }

    public void RecordProvision(string provider, string reason)
        => _provisions.Add(1, Tag(provider), new KeyValuePair<string, object?>("reason", reason));

    public void RecordRetirement(string provider, string reason)
        => _retirements.Add(1, Tag(provider), new KeyValuePair<string, object?>("reason", reason));

    public void RecordOrphanCleaned(string provider) => _orphans.Add(1, Tag(provider));

    public void RecordRejectedPlacement(string provider, string reason)
        => _rejectedPlacements.Add(1, Tag(provider), new KeyValuePair<string, object?>("reason", reason));

    public void RecordColdStart(string provider, TimeSpan duration) => _coldStart.Record(duration.TotalMilliseconds, Tag(provider));

    public void RecordAllocation(string provider, string phase, ElasticFleetAllocator.Result result)
    {
        var tags = new TagList { Tag(provider), new("phase", phase) };
        _allocationJobs.Record(result.MatchedJobIds.Count + result.UnmatchedJobIds.Count, tags);
        _allocationSlots.Record(result.AvailableSlots, tags);
        _allocationEdgeVisits.Record(result.CompatibilityChecks, tags);
        _allocationDuration.Record(result.Elapsed.TotalMilliseconds, tags);
    }

    public void UpdateSnapshot(ElasticProviderSnapshot snapshot)
    {
        _snapshots[snapshot.Provider] = snapshot;
        _failures[snapshot.Provider] = (0, null, null);
    }

    private readonly ConcurrentDictionary<string, (int Consecutive, DateTimeOffset? LastFailureUtc, string? Message)> _failures = new(StringComparer.Ordinal);

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public void MarkStarted(DateTimeOffset now) => StartedAtUtc ??= now;

    public void RecordSampleFailure(string provider, DateTimeOffset now, string message)
        => _failures.AddOrUpdate(provider, (1, now, message), (_, current) => (current.Consecutive + 1, now, message));

    public (int Consecutive, DateTimeOffset? LastFailureUtc, string? Message) SampleFailures(string provider)
        => _failures.TryGetValue(provider, out var failures) ? failures : (0, null, null);

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
