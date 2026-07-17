using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class EnvironmentalObservationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.EnvironmentalObservations";
    public const string ActivitySourceName = "HVO.SkyMonitor.LogicHost.EnvironmentalObservations";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _ingest;
    private readonly Histogram<double> _ingestDuration;
    private readonly Histogram<long> _payloadSize;
    private readonly Histogram<double> _clockOffset;
    private readonly Counter<long> _ingestFailures;
    private readonly Counter<long> _validation;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _correlation;
    private readonly Histogram<double> _correlationDuration;
    private readonly Counter<long> _retention;
    private readonly Histogram<double> _retentionDuration;

    public EnvironmentalObservationTelemetry()
    {
        _ingest = _meter.CreateCounter<long>("skymonitor.environment.ingest", "{observation}");
        _ingestDuration = _meter.CreateHistogram<double>("skymonitor.environment.ingest.duration", "ms");
        _payloadSize = _meter.CreateHistogram<long>("skymonitor.environment.payload.size", "By");
        _clockOffset = _meter.CreateHistogram<double>("skymonitor.environment.clock.offset", "s");
        _ingestFailures = _meter.CreateCounter<long>("skymonitor.environment.ingest.failures", "{failure}");
        _validation = _meter.CreateCounter<long>("skymonitor.environment.validation", "{observation}");
        _conflicts = _meter.CreateCounter<long>("skymonitor.environment.conflicts", "{observation}");
        _correlation = _meter.CreateCounter<long>("skymonitor.environment.correlation", "{query}");
        _correlationDuration = _meter.CreateHistogram<double>("skymonitor.environment.correlation.duration", "ms");
        _retention = _meter.CreateCounter<long>("skymonitor.environment.retention", "{row}");
        _retentionDuration = _meter.CreateHistogram<double>("skymonitor.environment.retention.duration", "ms");
    }

    public void RecordIngest(
        EnvironmentalObservationIngestDisposition disposition,
        EnvironmentalObservationKind kind,
        EnvironmentalObservationSourceKind sourceKind,
        int payloadBytes,
        double clockOffsetSeconds,
        EnvironmentalClockDiagnostic diagnostic,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "schema", "v1" },
            { "disposition", disposition.ToString() },
            { "observation_kind", kind.ToString() },
            { "source_kind", sourceKind.ToString() }
        };
        _ingest.Add(1, tags);
        _ingestDuration.Record(duration.TotalMilliseconds, tags);
        _payloadSize.Record(payloadBytes, tags);
        _clockOffset.Record(clockOffsetSeconds, new KeyValuePair<string, object?>("diagnostic", diagnostic.ToString()));
    }

    public void RecordCorrelation(
        EnvironmentalObservationKind kind,
        EnvironmentalObservationMatchStatus status,
        bool overlap,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "observation_kind", kind.ToString() },
            { "outcome", status.ToString() },
            { "overlap", overlap ? "true" : "false" }
        };
        _correlation.Add(1, tags);
        _correlationDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordIngestFailure()
        => _ingestFailures.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

    public void RecordValidation(string diagnostic)
        => _validation.Add(1, new KeyValuePair<string, object?>("diagnostic", diagnostic));

    public void RecordConflict(string scope)
        => _conflicts.Add(1, new KeyValuePair<string, object?>("scope", scope));

    public void RecordRetention(int rows, TimeSpan duration, string outcome)
    {
        var tags = new TagList { { "outcome", outcome } };
        _retention.Add(rows, tags);
        _retentionDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void Dispose() => _meter.Dispose();
}
