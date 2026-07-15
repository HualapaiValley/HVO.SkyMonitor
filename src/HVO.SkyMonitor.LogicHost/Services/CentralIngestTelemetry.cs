using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralIngestTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.Ingest";
    public const string ActivitySourceName = "HVO.SkyMonitor.LogicHost";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _requests;
    private readonly Counter<long> _bytes;
    private readonly Counter<long> _validation;
    private readonly Counter<long> _checksums;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _quarantines;
    private readonly Counter<long> _reconciled;
    private readonly Counter<long> _stagingCleanup;
    private readonly Counter<long> _stagingCleanupBytes;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _objectWriteDuration;
    private readonly Histogram<double> _sqlCommitDuration;
    private readonly Histogram<double> _reconciliationDuration;
    private long _pendingObjects;
    private long _pendingObjectBytes;
    private long _pendingObjectOldestAgeSeconds;
    private long _pendingReferences;
    private long _pendingReferenceBytes;
    private long _pendingReferenceOldestAgeSeconds;
    private long _quarantined;
    private long _quarantinedBytes;
    private long _quarantinedOldestAgeSeconds;

    public CentralIngestTelemetry()
    {
        _requests = _meter.CreateCounter<long>("skymonitor.central.ingest.requests", "{request}");
        _bytes = _meter.CreateCounter<long>("skymonitor.central.ingest.payload", "By");
        _validation = _meter.CreateCounter<long>("skymonitor.central.ingest.validation", "{validation}");
        _checksums = _meter.CreateCounter<long>("skymonitor.central.ingest.checksums", "{checksum}");
        _duplicates = _meter.CreateCounter<long>("skymonitor.central.ingest.duplicates", "{artifact}");
        _quarantines = _meter.CreateCounter<long>("skymonitor.central.ingest.quarantines", "{artifact}");
        _reconciled = _meter.CreateCounter<long>("skymonitor.central.ingest.reconciled", "{artifact}");
        _stagingCleanup = _meter.CreateCounter<long>("skymonitor.central.ingest.staging_cleanup", "{object}");
        _stagingCleanupBytes = _meter.CreateCounter<long>("skymonitor.central.ingest.staging_cleanup_bytes", "By");
        _duration = _meter.CreateHistogram<double>("skymonitor.central.ingest.duration", "ms");
        _objectWriteDuration = _meter.CreateHistogram<double>("skymonitor.central.ingest.object_write.duration", "ms");
        _sqlCommitDuration = _meter.CreateHistogram<double>("skymonitor.central.ingest.sql_commit.duration", "ms");
        _reconciliationDuration = _meter.CreateHistogram<double>("skymonitor.central.ingest.reconciliation.duration", "ms");
        _meter.CreateObservableGauge("skymonitor.central.ingest.pending_objects", () => Interlocked.Read(ref _pendingObjects), "{artifact}");
        _meter.CreateObservableGauge("skymonitor.central.ingest.pending_object_bytes", () => Interlocked.Read(ref _pendingObjectBytes), "By");
        _meter.CreateObservableGauge("skymonitor.central.ingest.pending_object_oldest_age", () => Interlocked.Read(ref _pendingObjectOldestAgeSeconds), "s");
        _meter.CreateObservableGauge("skymonitor.central.ingest.pending_references", () => Interlocked.Read(ref _pendingReferences), "{artifact}");
        _meter.CreateObservableGauge("skymonitor.central.ingest.pending_reference_bytes", () => Interlocked.Read(ref _pendingReferenceBytes), "By");
        _meter.CreateObservableGauge("skymonitor.central.ingest.pending_reference_oldest_age", () => Interlocked.Read(ref _pendingReferenceOldestAgeSeconds), "s");
        _meter.CreateObservableGauge("skymonitor.central.ingest.quarantined", () => Interlocked.Read(ref _quarantined), "{artifact}");
        _meter.CreateObservableGauge("skymonitor.central.ingest.quarantined_bytes", () => Interlocked.Read(ref _quarantinedBytes), "By");
        _meter.CreateObservableGauge("skymonitor.central.ingest.quarantined_oldest_age", () => Interlocked.Read(ref _quarantinedOldestAgeSeconds), "s");
    }

    public static Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    public void RecordRequest(string schema, string outcome, long byteLength, TimeSpan elapsed)
    {
        var tags = new TagList { { "schema", schema }, { "outcome", outcome } };
        _requests.Add(1, tags);
        _bytes.Add(byteLength, tags);
        _duration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordDuplicate(string schema)
        => _duplicates.Add(1, new TagList { { "schema", schema } });

    public void RecordValidation(string schema, string outcome)
        => _validation.Add(1, new TagList { { "schema", schema }, { "outcome", outcome } });

    public void RecordChecksum(string operation, string outcome)
        => _checksums.Add(1, new TagList { { "operation", operation }, { "outcome", outcome } });

    public void RecordQuarantine(string reason)
        => _quarantines.Add(1, new TagList { { "reason", reason } });

    public void RecordReconciled(string outcome)
        => _reconciled.Add(1, new TagList { { "outcome", outcome } });

    public void RecordObjectWrite(string operation, string outcome, TimeSpan elapsed)
        => _objectWriteDuration.Record(
            elapsed.TotalMilliseconds,
            new TagList { { "operation", operation }, { "outcome", outcome } });

    public void RecordSqlCommit(string outcome, TimeSpan elapsed)
        => _sqlCommitDuration.Record(elapsed.TotalMilliseconds, new TagList { { "outcome", outcome } });

    public void RecordReconciliationDuration(string outcome, TimeSpan elapsed)
        => _reconciliationDuration.Record(elapsed.TotalMilliseconds, new TagList { { "outcome", outcome } });

    public void RecordStagingCleanup(string outcome, long objects, long bytes = 0)
    {
        var tags = new TagList { { "outcome", outcome } };
        _stagingCleanup.Add(objects, tags);
        if (bytes > 0)
        {
            _stagingCleanupBytes.Add(bytes, tags);
        }
    }

    public void RecordBacklog(
        long pendingObjects,
        long pendingObjectBytes,
        long pendingObjectOldestAgeSeconds,
        long pendingReferences,
        long pendingReferenceBytes,
        long pendingReferenceOldestAgeSeconds,
        long quarantined,
        long quarantinedBytes,
        long quarantinedOldestAgeSeconds)
    {
        Interlocked.Exchange(ref _pendingObjects, pendingObjects);
        Interlocked.Exchange(ref _pendingObjectBytes, pendingObjectBytes);
        Interlocked.Exchange(ref _pendingObjectOldestAgeSeconds, pendingObjectOldestAgeSeconds);
        Interlocked.Exchange(ref _pendingReferences, pendingReferences);
        Interlocked.Exchange(ref _pendingReferenceBytes, pendingReferenceBytes);
        Interlocked.Exchange(ref _pendingReferenceOldestAgeSeconds, pendingReferenceOldestAgeSeconds);
        Interlocked.Exchange(ref _quarantined, quarantined);
        Interlocked.Exchange(ref _quarantinedBytes, quarantinedBytes);
        Interlocked.Exchange(ref _quarantinedOldestAgeSeconds, quarantinedOldestAgeSeconds);
    }

    public void Dispose() => _meter.Dispose();

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
