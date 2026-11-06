using System;
using System.Collections.Generic;

namespace HVO.SkyMonitor.CameraAgent.Common.Telemetry;

public interface ICaptureTelemetryProvider
{
    CaptureTelemetrySnapshot GetSnapshot();
    CaptureTelemetrySample? Latest { get; }
}

public interface ICaptureTelemetrySink
{
    void Report(CaptureTelemetrySample sample);
}

public sealed class CaptureTelemetrySink : ICaptureTelemetrySink, ICaptureTelemetryProvider
{
    private readonly CaptureTelemetrySample[] _buffer;
    private int _nextIndex;
    private int _count;
    private readonly object _gate = new();
    private CaptureTelemetrySample? _latest;

    public CaptureTelemetrySink(int capacity = 120)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        _buffer = new CaptureTelemetrySample[capacity];
    }

    public CaptureTelemetrySample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Attempts to read the latest sample; a successful read may return null before the first report.</summary>
    public bool TryGetLatest(TimeSpan timeout, out CaptureTelemetrySample? sample)
    {
        if (!Monitor.TryEnter(_gate, timeout))
        {
            sample = null;
            return false;
        }
        try
        {
            sample = _latest;
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }

    public void Report(CaptureTelemetrySample sample)
    {
        lock (_gate)
        {
            _buffer[_nextIndex] = sample;
            _latest = sample;
            _nextIndex = (_nextIndex + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }
    }

    public CaptureTelemetrySnapshot GetSnapshot()
    {
        CaptureTelemetrySample[] snapshot;
        lock (_gate)
        {
            snapshot = new CaptureTelemetrySample[_count];
            for (var i = 0; i < _count; i++)
            {
                var index = (_nextIndex - _count + i + _buffer.Length) % _buffer.Length;
                snapshot[i] = _buffer[index];
            }
        }

        var aggregate = CaptureTelemetryAggregate.FromSamples(snapshot);
        return new CaptureTelemetrySnapshot(snapshot, aggregate);
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _nextIndex = 0;
            _count = 0;
            _latest = null;
        }
    }
}
