using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class DeploymentLocationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.DeploymentLocation";
    public const string ActivitySourceName = "HVO.SkyMonitor.LogicHost.DeploymentLocation";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> operations;
    private readonly Histogram<double> duration;
    private readonly Counter<long> backfills;
    private readonly Counter<long> reconciliationItems;
    private readonly Histogram<double> reconciliationDuration;
    private long pendingCount;
    private double oldestPendingAgeSeconds;

    public DeploymentLocationTelemetry()
    {
        operations = meter.CreateCounter<long>("skymonitor.deployment_location.operations", "{operation}");
        duration = meter.CreateHistogram<double>("skymonitor.deployment_location.duration", "ms");
        backfills = meter.CreateCounter<long>("skymonitor.deployment_location.backfill", "{entity}");
        reconciliationItems = meter.CreateCounter<long>(
            "skymonitor.deployment_location.reconciliation.items", "{capture}");
        reconciliationDuration = meter.CreateHistogram<double>(
            "skymonitor.deployment_location.reconciliation.duration", "ms");
        meter.CreateObservableGauge(
            "skymonitor.deployment_location.pending",
            () => Interlocked.Read(ref pendingCount),
            "{location}");
        meter.CreateObservableGauge(
            "skymonitor.deployment_location.oldest_age",
            () => Volatile.Read(ref oldestPendingAgeSeconds),
            "s");
    }

    public void RecordOperation(string operation, string outcome, string reason, string entity, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "outcome", outcome },
            { "reason", reason },
            { "entity", entity }
        };
        operations.Add(1, tags);
        duration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordPendingSnapshot(long count, double oldestAgeSeconds)
    {
        Interlocked.Exchange(ref pendingCount, count);
        Volatile.Write(ref oldestPendingAgeSeconds, Math.Max(0, oldestAgeSeconds));
    }

    public void RecordBackfill(int count)
        => backfills.Add(count, new KeyValuePair<string, object?>("entity", "observatory"));

    public void RecordReconciliation(string phase, string outcome, long itemCount, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "phase", phase },
            { "outcome", outcome }
        };
        reconciliationItems.Add(itemCount, tags);
        reconciliationDuration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void Dispose() => meter.Dispose();
}
