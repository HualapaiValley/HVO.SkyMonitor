using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalAcquisitionTelemetry : IDisposable
{
    public const string InstrumentationName = "HVO.SkyMonitor.CameraAgent.EnvironmentalAcquisition";
    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);
    private readonly Meter _meter = new(InstrumentationName);
    private readonly Counter<long> _triggers;
    private readonly Counter<long> _acquisitions;
    private readonly Counter<long> _commits;
    private readonly Counter<long> _associations;
    private readonly Histogram<double> _acquisitionDuration;
    private readonly Histogram<double> _commitDuration;
    private readonly Histogram<long> _payloadSize;
    private readonly Histogram<double> _historyQueryDuration;
    private readonly TimeProvider _timeProvider;
    private long _inflight;
    private long _queueDepth;
    private long _journalRecords;
    private long _journalBytes;
    private long _sources;
    private long _journalOldestUnixMilliseconds;

    public EnvironmentalAcquisitionTelemetry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _triggers = _meter.CreateCounter<long>("skymonitor.environment.local.triggers", "{trigger}");
        _acquisitions = _meter.CreateCounter<long>("skymonitor.environment.local.acquisitions", "{attempt}");
        _commits = _meter.CreateCounter<long>("skymonitor.environment.local.journal.commits", "{observation}");
        _associations = _meter.CreateCounter<long>("skymonitor.environment.local.associations", "{association}");
        _acquisitionDuration = _meter.CreateHistogram<double>("skymonitor.environment.local.acquisition.duration", "ms");
        _commitDuration = _meter.CreateHistogram<double>("skymonitor.environment.local.journal.commit.duration", "ms");
        _payloadSize = _meter.CreateHistogram<long>("skymonitor.environment.local.payload.size", "By");
        _historyQueryDuration = _meter.CreateHistogram<double>("skymonitor.environment.local.history.query.duration", "ms");
        _meter.CreateObservableGauge(
            "skymonitor.environment.local.inflight", () => Interlocked.Read(ref _inflight), "{acquisition}");
        _meter.CreateObservableGauge(
            "skymonitor.environment.local.queue.depth", () => Interlocked.Read(ref _queueDepth), "{request}");
        _meter.CreateObservableGauge(
            "skymonitor.environment.local.journal.records", () => Interlocked.Read(ref _journalRecords), "{observation}");
        _meter.CreateObservableGauge("skymonitor.environment.local.journal.bytes", () => Interlocked.Read(ref _journalBytes), "By");
        _meter.CreateObservableGauge(
            "skymonitor.environment.local.journal.oldest.age",
            () => Interlocked.Read(ref _journalOldestUnixMilliseconds) is var oldest && oldest > 0
                ? Math.Max(0, (_timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(oldest)).TotalSeconds)
                : 0,
            "s");
        _meter.CreateObservableGauge(
            "skymonitor.environment.local.sources", () => Interlocked.Read(ref _sources), "{source}");
    }

    public void SetSourceCount(int count) => Interlocked.Exchange(ref _sources, count);
    public void SetQueueDepth(long count) => Interlocked.Exchange(ref _queueDepth, count);

    public void RecordTrigger(EnvironmentalAcquisitionTrigger trigger)
        => _triggers.Add(1, new TagList { { "trigger", trigger.ToString() } });

    public void AcquisitionStarted()
    {
        Interlocked.Increment(ref _inflight);
    }

    public void AcquisitionAborted() => Interlocked.Decrement(ref _inflight);

    public void AcquisitionCompleted(
        EnvironmentalSourceDescriptor source,
        EnvironmentalAcquisitionReceipt receipt,
        bool wasInFlight = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(receipt);
        if (wasInFlight)
        {
            Interlocked.Decrement(ref _inflight);
        }
        var tags = new TagList
        {
            { "trigger", receipt.Trigger.ToString() },
            { "observation_kind", source.Kind.ToString() },
            { "outcome", receipt.Disposition.ToString() },
            { "reason", NormalizeAcquisitionReason(receipt.Reason) }
        };
        _acquisitions.Add(1, tags);
        _acquisitionDuration.Record((receipt.CompletedUtc - receipt.StartedUtc).TotalMilliseconds, tags);
    }

    public void RecordCommit(
        string schema,
        string outcome,
        int payloadBytes,
        TimeSpan duration,
        LocalEnvironmentalObservationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var tags = new TagList { { "schema", schema }, { "outcome", outcome } };
        _commits.Add(1, tags);
        _commitDuration.Record(duration.TotalMilliseconds, tags);
        _payloadSize.Record(payloadBytes, tags);
        Interlocked.Exchange(ref _journalRecords, snapshot.StoredCount);
        Interlocked.Exchange(ref _journalBytes, snapshot.StoredBytes);
        Interlocked.Exchange(
            ref _journalOldestUnixMilliseconds,
            snapshot.OldestRecordedUtc?.ToUnixTimeMilliseconds() ?? 0);
    }

    public void RecordHistoryQuery(EnvironmentalObservationKind? kind, TimeSpan duration)
        => _historyQueryDuration.Record(
            duration.TotalMilliseconds,
            new TagList { { "observation_kind", kind?.ToString() ?? "All" } });

    public void RecordAssociation(LocalEnvironmentalAssociationStatus status)
        => _associations.Add(1, new TagList { { "association_status", status.ToString() } });

    internal static string NormalizeAcquisitionReason(string reason)
        => reason switch
        {
            "produced" or "duplicate" or "trigger-coalesced" or "source-timeout" or "source-failure" or
            "source-missing" or "virtual-source-failure" or "invalid-result" or "journal-unavailable" or
            "capacity-exhausted" => reason,
            _ => "other"
        };

    public void Dispose() => _meter.Dispose();
}
