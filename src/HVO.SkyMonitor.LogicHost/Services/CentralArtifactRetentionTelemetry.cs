using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralArtifactRetentionTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.Retention";
    private const string ActivitySourceName = CentralIngestTelemetry.ActivitySourceName;
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> operations;
    private readonly Histogram<double> duration;
    private readonly Histogram<double> stageDuration;
    private readonly Counter<long> objectBytes;
    private readonly Counter<long> recovery;
    private readonly Counter<long> finalizationConflicts;
    private long pending;
    private long pendingBytes;
    private long pendingOldestAge;

    public CentralArtifactRetentionTelemetry()
    {
        operations = meter.CreateCounter<long>("skymonitor.central.retention.operations", "{operation}");
        duration = meter.CreateHistogram<double>("skymonitor.central.retention.duration", "ms");
        stageDuration = meter.CreateHistogram<double>("skymonitor.central.retention.stage.duration", "ms");
        objectBytes = meter.CreateCounter<long>("skymonitor.central.retention.object_bytes", "By");
        recovery = meter.CreateCounter<long>("skymonitor.central.retention.recovery", "{operation}");
        finalizationConflicts = meter.CreateCounter<long>(
            "skymonitor.central.retention.finalization_conflicts", "{conflict}");
        meter.CreateObservableGauge(
            "skymonitor.central.retention.pending", () => Interlocked.Read(ref pending), "{operation}");
        meter.CreateObservableGauge(
            "skymonitor.central.retention.pending_bytes", () => Interlocked.Read(ref pendingBytes), "By");
        meter.CreateObservableGauge(
            "skymonitor.central.retention.pending_oldest_age", () => Interlocked.Read(ref pendingOldestAge), "s");
    }

    public static Activity? Start(string name) => ActivitySource.StartActivity(name);

    public void RecordOperation(string outcome, string origin, long bytes, TimeSpan elapsed)
    {
        var tags = new TagList { { "outcome", outcome }, { "origin", origin } };
        operations.Add(1, tags);
        duration.Record(elapsed.TotalMilliseconds, tags);
        if (outcome is "deleted" or "missing")
        {
            objectBytes.Add(bytes, tags);
        }
    }

    public void RecordStage(string stage, string outcome, string origin, TimeSpan elapsed)
        => stageDuration.Record(elapsed.TotalMilliseconds,
            new TagList { { "stage", stage }, { "outcome", outcome }, { "origin", origin } });

    public void RecordRecovery(string outcome, int count, TimeSpan elapsed)
    {
        var tags = new TagList { { "outcome", outcome }, { "origin", "worker" }, { "stage", "reconcile" } };
        recovery.Add(count, tags);
        stageDuration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordFinalizationConflict(string origin)
        => finalizationConflicts.Add(1, new TagList { { "outcome", "conflict" }, { "origin", origin } });

    public void RecordBacklog(long count, long bytes, long oldestAgeSeconds)
    {
        Interlocked.Exchange(ref pending, count);
        Interlocked.Exchange(ref pendingBytes, bytes);
        Interlocked.Exchange(ref pendingOldestAge, oldestAgeSeconds);
    }

    public void Dispose() => meter.Dispose();
}
