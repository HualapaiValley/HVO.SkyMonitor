using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class FleetStatusTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.FleetStatus";
    public const string ActivitySourceName = "HVO.SkyMonitor.LogicHost.FleetStatus";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _ingest;
    private readonly Histogram<double> _ingestDuration;
    private readonly Histogram<long> _payloadSize;
    private readonly Histogram<double> _clockOffset;
    private readonly Counter<long> _retainedRowsDeleted;

    public FleetStatusTelemetry()
    {
        _ingest = _meter.CreateCounter<long>("hvo.fleet.central.ingest", "{report}");
        _ingestDuration = _meter.CreateHistogram<double>("hvo.fleet.central.ingest.duration", "ms");
        _payloadSize = _meter.CreateHistogram<long>("hvo.fleet.central.payload.size", "By");
        _clockOffset = _meter.CreateHistogram<double>("hvo.fleet.central.clock.offset", "s");
        _retainedRowsDeleted = _meter.CreateCounter<long>("hvo.fleet.central.retention", "{row}");
    }

    public void RecordIngest(
        FleetHeartbeatDisposition disposition,
        int payloadBytes,
        double offsetSeconds,
        FleetClockDiagnostic diagnostic,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "schema", "v1" },
            { "disposition", disposition.ToString() }
        };
        _ingest.Add(1, tags);
        _ingestDuration.Record(duration.TotalMilliseconds, tags);
        _payloadSize.Record(payloadBytes, tags);
        _clockOffset.Record(offsetSeconds, new KeyValuePair<string, object?>("diagnostic", diagnostic.ToString()));
    }

    public void RecordRetention(int rows) => _retainedRowsDeleted.Add(rows);

    public void Dispose() => _meter.Dispose();
}
