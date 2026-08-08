using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Fleet.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Common.Fleet;

public sealed record FleetRuntimeSnapshot(
    FleetCaptureSummary Capture,
    IReadOnlyList<FleetTimingSummary> Timings);

public sealed class FleetRuntimeState(TimeProvider timeProvider)
{
    private const int Capacity = 120;
    private readonly object _gate = new();
    private readonly Dictionary<FleetTimingSegment, Queue<double>> _timings = [];
    private FleetAvailability _captureAvailability = FleetAvailability.Initializing;
    private string _captureReason = "initializing";
    private DateTimeOffset? _lastSucceededUtc;
    private DateTimeOffset? _lastFailedUtc;
    private DateTimeOffset? _lastRecoveredUtc;
    private bool _failed;

    public FleetRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new FleetRuntimeSnapshot(
                    new FleetCaptureSummary(
                        _captureAvailability,
                        _captureReason,
                        _lastSucceededUtc,
                        _lastFailedUtc,
                        _lastRecoveredUtc),
                    _timings.OrderBy(static pair => pair.Key)
                        .Select(static pair => Summarize(pair.Key, pair.Value))
                        .ToArray());
            }
        }
    }

    public void ModuleAvailable()
    {
        lock (_gate)
        {
            _captureAvailability = FleetAvailability.Available;
            _captureReason = "ready";
        }
    }

    public void CaptureFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _captureAvailability = FleetAvailability.Unavailable;
            _captureReason = Bound(reason);
            _lastFailedUtc = timeProvider.GetUtcNow();
            _failed = true;
        }
    }

    public void CaptureSucceeded(CaptureResult result, TimeSpan moduleDuration, TimeSpan ingressDuration)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_failed)
            {
                _lastRecoveredUtc = now;
            }
            _failed = false;
            _captureAvailability = FleetAvailability.Available;
            _captureReason = "capturing";
            _lastSucceededUtc = now;
            Add(FleetTimingSegment.ModuleRender, result.ProcessingLatency);
            if (result.AcquisitionTiming is { } timing)
            {
                Add(FleetTimingSegment.Readout, timing.ReadoutCompletedUtc - timing.ExposureEndedUtc);
            }
            else
            {
                Add(FleetTimingSegment.Readout, moduleDuration - result.ProcessingLatency);
            }
            Add(FleetTimingSegment.Ingress, ingressDuration);
        }
    }

    public void ProcessingCompleted(TimeSpan duration)
    {
        lock (_gate)
        {
            Add(FleetTimingSegment.Processing, duration);
        }
    }

    public void UploadCompleted(TimeSpan duration)
    {
        lock (_gate)
        {
            Add(FleetTimingSegment.Upload, duration);
        }
    }

    public void ResetTimings()
    {
        lock (_gate)
        {
            _timings.Clear();
        }
    }

    private void Add(FleetTimingSegment segment, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            return;
        }
        if (!_timings.TryGetValue(segment, out var values))
        {
            values = new Queue<double>(Capacity);
            _timings.Add(segment, values);
        }
        if (values.Count == Capacity)
        {
            values.Dequeue();
        }
        values.Enqueue(duration.TotalMilliseconds);
    }

    private static FleetTimingSummary Summarize(FleetTimingSegment segment, Queue<double> values)
    {
        var ordered = values.Order().ToArray();
        return new FleetTimingSummary(
            segment,
            ordered.Length,
            Percentile(ordered, 0.5),
            Percentile(ordered, 0.95),
            ordered[^1]);
    }

    private static double Percentile(double[] ordered, double percentile)
        => ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];

    private static string Bound(string reason) => reason.Length <= 128 ? reason : reason[..128];
}
