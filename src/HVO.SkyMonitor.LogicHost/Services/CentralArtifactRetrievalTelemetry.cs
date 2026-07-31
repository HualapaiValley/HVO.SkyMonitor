using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralArtifactRetrievalTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.Retrieval";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _authorization;
    private readonly Counter<long> _verification;
    private readonly Counter<long> _reads;
    private readonly Counter<long> _bytes;
    private readonly Counter<long> _retention;
    private readonly Histogram<double> _duration;
    private long _activeStreams;

    public CentralArtifactRetrievalTelemetry()
    {
        _authorization = _meter.CreateCounter<long>("skymonitor.central.retrieval.authorization", "{decision}");
        _verification = _meter.CreateCounter<long>("skymonitor.central.retrieval.verification", "{verification}");
        _reads = _meter.CreateCounter<long>("skymonitor.central.retrieval.reads", "{read}");
        _bytes = _meter.CreateCounter<long>("skymonitor.central.retrieval.bytes", "By");
        _retention = _meter.CreateCounter<long>("skymonitor.central.retrieval.retention", "{decision}");
        _duration = _meter.CreateHistogram<double>("skymonitor.central.retrieval.duration", "ms");
        _meter.CreateObservableGauge("skymonitor.central.retrieval.active_streams", () =>
            Interlocked.Read(ref _activeStreams), "{stream}");
    }

    public static Activity? StartActivity(string name) => ActivitySource.StartActivity(name);

    public void RecordAuthorization(string callerKind, string outcome)
        => _authorization.Add(1, new TagList { { "caller.kind", callerKind }, { "outcome", outcome } });

    public void RecordVerification(string outcome)
        => _verification.Add(1, new TagList { { "outcome", outcome } });

    public void RecordObjectRead(string operation, string outcome, long bytes, TimeSpan elapsed)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        _reads.Add(1, tags);
        if (bytes > 0)
        {
            _bytes.Add(bytes, tags);
        }
        _duration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordRetention(string outcome)
        => _retention.Add(1, new TagList { { "outcome", outcome } });

    internal long ActiveStreams => Interlocked.Read(ref _activeStreams);

    public IDisposable TrackStream()
    {
        Interlocked.Increment(ref _activeStreams);
        return new StreamLease(() => Interlocked.Decrement(ref _activeStreams));
    }

    public void Dispose() => _meter.Dispose();

    private sealed class StreamLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    private static readonly ActivitySource ActivitySource = new(CentralIngestTelemetry.ActivitySourceName);
}
