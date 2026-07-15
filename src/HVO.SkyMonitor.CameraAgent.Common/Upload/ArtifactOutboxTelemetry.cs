using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

public sealed class ArtifactOutboxTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Outbox";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.Outbox";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _payloadBytes;
    private readonly Histogram<double> _sendDuration;
    private readonly Histogram<double> _claimDuration;
    private readonly Histogram<double> _settlementDuration;

    public ArtifactOutboxTelemetry(ArtifactOutboxState state, TimeProvider timeProvider)
    {
        _attempts = _meter.CreateCounter<long>("hvo.cameraagent.outbox.attempts", "attempt");
        _outcomes = _meter.CreateCounter<long>("hvo.cameraagent.outbox.outcomes", "record");
        _payloadBytes = _meter.CreateCounter<long>("hvo.cameraagent.outbox.payload", "By");
        _sendDuration = _meter.CreateHistogram<double>("hvo.cameraagent.outbox.send.duration", "ms");
        _claimDuration = _meter.CreateHistogram<double>("hvo.cameraagent.outbox.claim.duration", "ms");
        _settlementDuration = _meter.CreateHistogram<double>("hvo.cameraagent.outbox.settlement.duration", "ms");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.outbox.pending",
            () => state.Snapshot.PendingCount,
            "record");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.outbox.pending.bytes",
            () => state.Snapshot.PendingBytes,
            "By");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.outbox.oldest.age",
            () => state.Snapshot.OldestPendingUtc is { } oldest
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds)
                : 0,
            "s");
        _meter.CreateObservableGauge(
            "hvo.cameraagent.outbox.quarantined",
            () => state.Snapshot.QuarantineCount,
            "record");
    }

    public void RecordUpload(ArtifactUploadResult result, long payloadBytes, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(result);
        var outcome = result.Disposition switch
        {
            ArtifactUploadDisposition.Acknowledged => "acknowledged",
            ArtifactUploadDisposition.Retry => "retry",
            ArtifactUploadDisposition.Quarantine => "quarantine",
            _ => "unknown"
        };
        _attempts.Add(1);
        _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _payloadBytes.Add(payloadBytes, new KeyValuePair<string, object?>("outcome", outcome));
        _sendDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordClaim(TimeSpan duration)
        => _claimDuration.Record(duration.TotalMilliseconds);

    public void RecordSettlement(ArtifactUploadDisposition disposition, TimeSpan duration)
        => _settlementDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", disposition switch
            {
                ArtifactUploadDisposition.Acknowledged => "acknowledged",
                ArtifactUploadDisposition.Retry => "retry",
                ArtifactUploadDisposition.Quarantine => "quarantine",
                _ => "unknown"
            }));

    public void Dispose()
        => _meter.Dispose();
}
