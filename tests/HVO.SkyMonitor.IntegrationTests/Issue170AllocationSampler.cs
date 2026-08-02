using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed class Issue170AllocationSampler : EventListener
{
    private const int IntervalMilliseconds = 100;
    private readonly ConcurrentQueue<Issue170AllocationSample> _rawSamples = [];
    private readonly object _gate = new();
    private long _sampledBytes;
    private long _started;
    private int _samples;
    private int _active;
    private int _invalid;

    internal Issue170AllocationSampler()
    {
        foreach (var eventSource in EventSource.GetSources())
        {
            EnableRuntimeCounters(eventSource);
        }
        Thread.Sleep(TimeSpan.FromMilliseconds(IntervalMilliseconds * 2));
    }

    internal void Start()
    {
        lock (_gate)
        {
            _rawSamples.Clear();
            _sampledBytes = 0;
            _samples = 0;
            _invalid = 0;
            _started = Stopwatch.GetTimestamp();
            Volatile.Write(ref _active, 1);
        }
    }

    internal Task<Issue170AllocationMeasurement> StopAsync()
    {
        lock (_gate)
        {
            Volatile.Write(ref _active, 0);
            var bytes = _sampledBytes;
            var samples = _samples;
            if (_invalid != 0 || bytes < 0 || samples == 0)
            {
                throw new InvalidDataException("System.Runtime did not provide valid sampled allocation-rate increments.");
            }
            var rawSamples = _rawSamples.OrderBy(item => item.ElapsedMilliseconds).ToArray();
            if (rawSamples.Length != samples || rawSamples.Sum(item => item.AllocatedBytes) != bytes)
            {
                throw new InvalidDataException("System.Runtime allocation-rate raw samples do not reproduce the aggregate.");
            }
            return Task.FromResult(new Issue170AllocationMeasurement(
                bytes, samples, IntervalMilliseconds, rawSamples));
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
        => EnableRuntimeCounters(eventSource);

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (Volatile.Read(ref _active) == 0
            || !string.Equals(eventData.EventName, "EventCounters", StringComparison.Ordinal)
            || eventData.Payload is not [IDictionary<string, object?> payload]
            || !payload.TryGetValue("Name", out var name)
            || !string.Equals(Convert.ToString(name, CultureInfo.InvariantCulture), "alloc-rate", StringComparison.Ordinal)
            || !payload.TryGetValue("Increment", out var increment))
        {
            return;
        }
        var value = Convert.ToDouble(increment, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value < 0 || value > long.MaxValue)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _active) != 0)
                {
                    _invalid = 1;
                }
            }
            return;
        }
        lock (_gate)
        {
            if (Volatile.Read(ref _active) == 0)
            {
                return;
            }
            var allocatedBytes = checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
            _sampledBytes += allocatedBytes;
            _rawSamples.Enqueue(new(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, allocatedBytes));
            _samples++;
        }
    }

    private void EnableRuntimeCounters(EventSource eventSource)
    {
        if (string.Equals(eventSource.Name, "System.Runtime", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All,
                new Dictionary<string, string?>
                {
                    ["EventCounterIntervalSec"] = (IntervalMilliseconds / 1000d)
                        .ToString(CultureInfo.InvariantCulture)
                });
        }
    }
}

internal sealed record Issue170AllocationMeasurement(
    long SampledBytes,
    int Samples,
    int IntervalMilliseconds,
    IReadOnlyList<Issue170AllocationSample> RawSamples);

internal sealed record Issue170AllocationSample(double ElapsedMilliseconds, long AllocatedBytes);
