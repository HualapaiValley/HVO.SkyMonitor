using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public enum CaptureAvailability
{
    Initializing,
    Available,
    Degraded,
    Unavailable
}

public enum CaptureTimingSegment
{
    ModuleRender,
    Readout,
    Ingress,
    Processing
}

public sealed record CaptureRuntimeSummary(
    CaptureAvailability Availability,
    string Reason,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc,
    DateTimeOffset? LastRecoveredUtc);

public sealed record CaptureTimingSummary(
    CaptureTimingSegment Segment,
    long SampleCount,
    double MedianMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds);

public sealed record CaptureRuntimeSnapshot(
    CaptureRuntimeSummary Capture,
    IReadOnlyList<CaptureTimingSummary> Timings);

public sealed class CaptureRuntimeState(TimeProvider timeProvider)
{
    private const int Capacity = 120;
    private readonly object _gate = new();
    private readonly Dictionary<CaptureTimingSegment, Queue<double>> _timings = [];
    private CaptureAvailability _captureAvailability = CaptureAvailability.Initializing;
    private string _captureReason = "initializing";
    private DateTimeOffset? _lastSucceededUtc;
    private DateTimeOffset? _lastFailedUtc;
    private DateTimeOffset? _lastRecoveredUtc;
    private bool _failed;
    private bool _cameraDisconnected;
    private bool _storageFailure;
    private bool _startupStorageFailure;

    public bool IsCameraDisconnected
    {
        get
        {
            lock (_gate)
            {
                return _cameraDisconnected;
            }
        }
    }

    public bool IsStorageFailure
    {
        get
        {
            lock (_gate)
            {
                return _storageFailure;
            }
        }
    }

    public bool IsStartupStorageFailure
    {
        get
        {
            lock (_gate)
            {
                return _startupStorageFailure;
            }
        }
    }

    public CaptureRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return Compose();
            }
        }
    }

    /// <summary>Attempts to capture runtime state without waiting beyond the lock-acquisition timeout.</summary>
    public bool TrySnapshot(TimeSpan timeout, [NotNullWhen(true)] out CaptureRuntimeSnapshot? snapshot)
    {
        if (!Monitor.TryEnter(_gate, timeout))
        {
            snapshot = null;
            return false;
        }
        try
        {
            snapshot = Compose();
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }

    private CaptureRuntimeSnapshot Compose()
    {
        return new CaptureRuntimeSnapshot(
            new CaptureRuntimeSummary(
                _captureAvailability,
                _captureReason,
                _lastSucceededUtc,
                _lastFailedUtc,
                _lastRecoveredUtc),
            _timings.OrderBy(static pair => pair.Key)
                .Select(static pair => Summarize(pair.Key, pair.Value))
                .ToArray());
    }

    public void ModuleAvailable()
    {
        lock (_gate)
        {
            _captureAvailability = CaptureAvailability.Available;
            _captureReason = "ready";
            _cameraDisconnected = false;
            _storageFailure = false;
            _startupStorageFailure = false;
        }
    }

    public void CaptureFailed(string reason)
        => CaptureFailed(reason, isCameraDisconnected: false, isStorageFailure: false, isStartupStorageFailure: false);

    public void CaptureFailed(string reason, bool isCameraDisconnected)
        => CaptureFailed(reason, isCameraDisconnected, isStorageFailure: false, isStartupStorageFailure: false);

    public void CaptureFailed(string reason, bool isCameraDisconnected, bool isStorageFailure)
        => CaptureFailed(reason, isCameraDisconnected, isStorageFailure, isStartupStorageFailure: false);

    public void CaptureFailed(string reason, bool isCameraDisconnected, bool isStorageFailure, bool isStartupStorageFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            _captureAvailability = CaptureAvailability.Unavailable;
            _captureReason = Bound(reason);
            _lastFailedUtc = timeProvider.GetUtcNow();
            _failed = true;
            _cameraDisconnected = isCameraDisconnected;
            _storageFailure = isStorageFailure;
            _startupStorageFailure = isStorageFailure && isStartupStorageFailure;
        }
    }

    internal static bool IsCameraDisconnectException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Data["IsCameraDisconnected"] is bool typedDisconnected)
        {
            return typedDisconnected;
        }

        var typeName = exception.GetType().Name;
        var message = exception.Message ?? string.Empty;
        if (string.Equals(typeName, "AsiException", StringComparison.Ordinal))
        {
            return message.Contains("CameraRemoved", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("CameraClosed", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("disconnect", StringComparison.OrdinalIgnoreCase);
        }

        return message.Contains("camera is not connected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("camera removed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("camera disconnected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("device disconnected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("device removed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("usb disconnect", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("CameraRemoved", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("CameraClosed", StringComparison.OrdinalIgnoreCase);
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
            _cameraDisconnected = false;
            _storageFailure = false;
            _startupStorageFailure = false;
            _captureAvailability = CaptureAvailability.Available;
            _captureReason = "capturing";
            _lastSucceededUtc = now;
            Add(CaptureTimingSegment.ModuleRender, result.ProcessingLatency);
            if (result.AcquisitionTiming is { } timing)
            {
                Add(CaptureTimingSegment.Readout, timing.ReadoutCompletedUtc - timing.ExposureEndedUtc);
            }
            else
            {
                Add(CaptureTimingSegment.Readout, moduleDuration - result.ProcessingLatency);
            }
            Add(CaptureTimingSegment.Ingress, ingressDuration);
        }
    }

    public void ProcessingCompleted(TimeSpan duration)
    {
        lock (_gate)
        {
            Add(CaptureTimingSegment.Processing, duration);
        }
    }

    public void ResetTimings()
    {
        lock (_gate)
        {
            _timings.Clear();
        }
    }

    private void Add(CaptureTimingSegment segment, TimeSpan duration)
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

    private static CaptureTimingSummary Summarize(CaptureTimingSegment segment, Queue<double> values)
    {
        var ordered = values.Order().ToArray();
        return new CaptureTimingSummary(
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
