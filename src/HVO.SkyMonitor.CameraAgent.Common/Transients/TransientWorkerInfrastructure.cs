using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

internal sealed class TransientWorkerWakeup
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    internal void Signal() => _channel.Writer.TryWrite(true);

    internal async ValueTask WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumDelay);
        try
        {
            _ = await _channel.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

public enum TransientWorkerAvailability
{
    Disabled,
    Starting,
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record TransientWorkerSnapshot(
    TransientWorkerAvailability Availability,
    string Reason,
    long PendingFrames,
    long PendingCandidates,
    DateTimeOffset UpdatedUtc);

public sealed class TransientWorkerState(TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private TransientWorkerSnapshot _snapshot = new(
        TransientWorkerAvailability.Starting,
        "starting",
        0,
        0,
        timeProvider.GetUtcNow());

    public TransientWorkerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    internal void Set(TransientWorkerAvailability availability, string reason, long frames, long candidates)
    {
        lock (_gate)
        {
            _snapshot = new TransientWorkerSnapshot(
                availability,
                reason,
                frames,
                candidates,
                timeProvider.GetUtcNow());
        }
    }
}

public sealed class TransientWorkerTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Transients";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.Transients";
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _outcomes;
    private readonly Histogram<double> _duration;
    private readonly UpDownCounter<long> _backlog;
    private readonly Counter<long> _evidenceLoads;
    private readonly Counter<long> _evidenceBytes;
    private readonly Counter<long> _evidenceFiles;

    public TransientWorkerTelemetry()
    {
        _outcomes = _meter.CreateCounter<long>("hvo.transient.worker.outcomes");
        _duration = _meter.CreateHistogram<double>("hvo.transient.worker.duration", "ms");
        _backlog = _meter.CreateUpDownCounter<long>("hvo.transient.worker.backlog");
        _evidenceLoads = _meter.CreateCounter<long>("hvo.transient.worker.evidence.loads");
        _evidenceBytes = _meter.CreateCounter<long>("hvo.transient.worker.evidence.bytes", "By");
        _evidenceFiles = _meter.CreateCounter<long>("hvo.transient.worker.evidence.files");
    }

    internal void Record(string stage, string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "stage", stage }, { "outcome", outcome } };
        _outcomes.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
    }

    internal void RecordBacklog(long delta, string kind)
        => _backlog.Add(delta, new KeyValuePair<string, object?>("kind", kind));

    internal void RecordEvidenceRead(long payloadBytes, long sidecarBytes)
    {
        _evidenceLoads.Add(1);
        _evidenceBytes.Add(checked(payloadBytes + sidecarBytes));
        _evidenceFiles.Add(2);
    }

    public void Dispose() => _meter.Dispose();
}
