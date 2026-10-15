using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>
/// Attaches before the isolated host starts and enables only instruments published afterward. Each raw-ingress and
/// lane telemetry instance owns a Meter, so the already-running shared fixture's instruments remain excluded.
/// </summary>
internal sealed class ExecutionEvidenceEdgeMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<IngressCommitSample> _commits = new();
    private bool _armed;
    private long _failures;
    private long _cancellations;
    private long _quarantines;
    private long _abandoned;
    private long _coalescedWakeups;
    private ICaptureTelemetryProvider? _captureTelemetry;
    private readonly ConcurrentDictionary<DateTimeOffset, byte> _failedCaptures = [];
    private readonly Timer _captureSampler;

    internal ExecutionEvidenceEdgeMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Volatile.Read(ref _armed) && instrument.Meter.Name is RawIngressTelemetry.MeterName or CaptureLaneTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "camera_agent.ingress.commit.duration") { return; }
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome" && tag.Value is "committed")
                {
                    _commits.Enqueue(new(DateTimeOffset.UtcNow, value * 1000));
                    return;
                }
            }
        });
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            switch (instrument.Name)
            {
                case "camera_agent.ingress.failures": Interlocked.Add(ref _failures, value); break;
                case "camera_agent.ingress.cancellations": Interlocked.Add(ref _cancellations, value); break;
                case "camera_agent.lanes.quarantined": Interlocked.Add(ref _quarantines, value); break;
                case "camera_agent.lanes.abandoned": Interlocked.Add(ref _abandoned, value); break;
                case "camera_agent.ingress.wakeups":
                    foreach (var tag in tags)
                    {
                        if (tag.Key == "result" && tag.Value is "dropped")
                        {
                            // A wakeup is only an accelerator; durable work survives this deliberate coalescing.
                            Interlocked.Add(ref _coalescedWakeups, value);
                        }
                    }
                    break;
            }
        });
        _listener.Start();
        Volatile.Write(ref _armed, true);
        _captureSampler = new Timer(_ =>
        {
            var telemetry = Volatile.Read(ref _captureTelemetry);
            if (telemetry is null) { return; }
            foreach (var sample in telemetry.GetSnapshot().Samples.Where(sample => !sample.FrameStored))
            {
                _failedCaptures.TryAdd(sample.StartedUtc, 0);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    internal IReadOnlyList<IngressCommitSample> Commits => _commits.ToArray();
    internal void Attach(ICaptureTelemetryProvider provider) => Volatile.Write(ref _captureTelemetry, provider);
    internal int FailedCaptureSamples => _failedCaptures.Count;
    internal EdgeFailureCounters Snapshot => new(Interlocked.Read(ref _failures), Interlocked.Read(ref _cancellations),
        Interlocked.Read(ref _quarantines), Interlocked.Read(ref _abandoned), Interlocked.Read(ref _coalescedWakeups));
    public void Dispose()
    {
        _captureSampler.Dispose();
        _listener.Dispose();
    }
}

internal sealed record IngressCommitSample(DateTimeOffset CompletedAtUtc, double Milliseconds);
internal sealed record EdgeFailureCounters(long IngressFailures, long IngressCancellations, long LaneQuarantines,
    long LaneAbandonedForPressure, long CoalescedWakeups);
