using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Fleet;

public sealed class FleetHeartbeatTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.FleetStatus";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.FleetStatus";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _reports;
    private readonly Histogram<long> _payloadSize;
    private readonly Histogram<double> _operationDuration;
    private readonly Counter<long> _retries;

    public FleetHeartbeatTelemetry()
    {
        _reports = _meter.CreateCounter<long>("hvo.fleet.edge.reports", "{report}");
        _payloadSize = _meter.CreateHistogram<long>("hvo.fleet.edge.payload.size", "By");
        _operationDuration = _meter.CreateHistogram<double>("hvo.fleet.edge.operation.duration", "ms");
        _retries = _meter.CreateCounter<long>("hvo.fleet.edge.retries", "{retry}");
    }

    public void RecordQueued(int payloadBytes, bool transition, TimeSpan duration)
    {
        var tags = new TagList { { "schema", "v1" }, { "kind", transition ? "transition" : "checkpoint" } };
        _reports.Add(1, tags);
        _payloadSize.Record(payloadBytes, tags);
        _operationDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "enqueue"));
    }

    public void RecordDelivery(FleetDeliveryDisposition disposition, TimeSpan duration)
    {
        _operationDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("operation", "send"));
        if (disposition == FleetDeliveryDisposition.Retry)
        {
            _retries.Add(1, new KeyValuePair<string, object?>("reason", "delivery"));
        }
    }

    public void Dispose() => _meter.Dispose();
}
