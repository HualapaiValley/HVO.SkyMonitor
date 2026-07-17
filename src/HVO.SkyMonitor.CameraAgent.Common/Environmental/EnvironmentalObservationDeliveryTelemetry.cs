using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalObservationDeliveryTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _settlements;
    private readonly Counter<long> _recoveryDrained;
    private readonly Histogram<long> _payloadBytes;
    private readonly Histogram<double> _enqueueDuration;
    private readonly Histogram<double> _claimDuration;
    private readonly Histogram<double> _sendDuration;
    private readonly Histogram<double> _acknowledgementDuration;

    public EnvironmentalObservationDeliveryTelemetry(
        EnvironmentalObservationDeliveryState state,
        TimeProvider timeProvider)
    {
        _enqueued = _meter.CreateCounter<long>("skymonitor.environment.edge.enqueue", "{observation}");
        _outcomes = _meter.CreateCounter<long>("skymonitor.environment.edge.delivery", "{observation}");
        _settlements = _meter.CreateCounter<long>("skymonitor.environment.edge.settlement", "{observation}");
        _recoveryDrained = _meter.CreateCounter<long>("skymonitor.environment.edge.recovery.drained", "{observation}");
        _payloadBytes = _meter.CreateHistogram<long>("skymonitor.environment.edge.payload.size", "By");
        _enqueueDuration = _meter.CreateHistogram<double>("skymonitor.environment.edge.enqueue.duration", "ms");
        _claimDuration = _meter.CreateHistogram<double>("skymonitor.environment.edge.claim.duration", "ms");
        _sendDuration = _meter.CreateHistogram<double>("skymonitor.environment.edge.send.duration", "ms");
        _acknowledgementDuration = _meter.CreateHistogram<double>("skymonitor.environment.edge.ack.duration", "ms");
        _meter.CreateObservableGauge(
            "skymonitor.environment.edge.pending",
            () => state.Snapshot.Outbox?.PendingCount ?? 0,
            "{observation}");
        _meter.CreateObservableGauge(
            "skymonitor.environment.edge.pending.bytes",
            () => state.Snapshot.Outbox?.PendingBytes ?? 0,
            "By");
        _meter.CreateObservableGauge(
            "skymonitor.environment.edge.oldest.age",
            () => state.Snapshot.Outbox?.OldestPendingUtc is { } oldest
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds)
                : 0,
            "s");
    }

    public void RecordEnqueue(
        EnvironmentalObservationEnqueueDisposition disposition,
        int payloadBytes,
        TimeSpan duration)
    {
        var tags = new TagList { { "schema", "v1" }, { "disposition", disposition.ToString() } };
        _enqueued.Add(1, tags);
        _payloadBytes.Record(payloadBytes, tags);
        _enqueueDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordClaim(TimeSpan duration)
        => _claimDuration.Record(duration.TotalMilliseconds);

    public void RecordSend(EnvironmentalObservationTransportResult result, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(result);
        var outcome = result.Disposition switch
        {
            EnvironmentalObservationTransportDisposition.Acknowledged
                when result.Acknowledgement?.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted => "accepted",
            EnvironmentalObservationTransportDisposition.Acknowledged => "duplicate",
            EnvironmentalObservationTransportDisposition.Retry => "retry",
            EnvironmentalObservationTransportDisposition.AuthenticationBlocked => "authentication-blocked",
            EnvironmentalObservationTransportDisposition.Quarantine => "quarantine",
            EnvironmentalObservationTransportDisposition.Terminal when result.Reason == "http-409" => "conflict",
            EnvironmentalObservationTransportDisposition.Terminal => "terminal",
            _ => "unknown"
        };
        var tags = new TagList { { "schema", "v1" }, { "outcome", outcome }, { "reason", NormalizeReason(result.Reason) } };
        _outcomes.Add(1, tags);
        _sendDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordAcknowledgement(TimeSpan duration, bool recovered)
    {
        _acknowledgementDuration.Record(duration.TotalMilliseconds);
        if (recovered)
        {
            _recoveryDrained.Add(1);
        }
    }

    public void RecordSettlement(string outcome, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _settlements.Add(1, new TagList
        {
            { "schema", "v1" },
            { "outcome", outcome },
            { "reason", NormalizeReason(reason) }
        });
    }

    public void Dispose() => _meter.Dispose();

    internal static string NormalizeReason(string reason)
    {
        if (reason.StartsWith("http-", StringComparison.Ordinal) &&
            int.TryParse(reason.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return status switch
            {
                409 => "http-conflict",
                >= 400 and < 500 => "http-client",
                >= 500 and < 600 => "http-server",
                _ => "http-other"
            };
        }
        return reason switch
        {
            "accepted" or "duplicate" or "credentials-unavailable" or "credentials-rejected" or
            "provisioning-device-mismatch" or "invalid-envelope" or "invalid-acknowledgement" or
            "transport-unavailable" or "request-timeout" or "maximum-attempts-exceeded" or
            "http-conflict" or "http-client" or "http-server" or "http-other" => reason,
            _ => "other"
        };
    }
}
