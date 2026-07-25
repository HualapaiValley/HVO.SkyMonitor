using System.Diagnostics.Tracing;
using System.Globalization;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed class Issue170AllocationSampler : EventListener
{
    private const int IntervalMilliseconds = 100;
    private long _sampledBytes;
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
        Interlocked.Exchange(ref _sampledBytes, 0);
        Interlocked.Exchange(ref _samples, 0);
        Volatile.Write(ref _active, 1);
    }

    internal Task<Issue170AllocationMeasurement> StopAsync()
    {
        Volatile.Write(ref _active, 0);
        var bytes = Interlocked.Read(ref _sampledBytes);
        var samples = Volatile.Read(ref _samples);
        if (Volatile.Read(ref _invalid) != 0 || bytes < 0 || samples == 0)
        {
            throw new InvalidDataException("System.Runtime did not provide valid sampled allocation-rate increments.");
        }
        return Task.FromResult(new Issue170AllocationMeasurement(bytes, samples, IntervalMilliseconds));
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
            Volatile.Write(ref _invalid, 1);
            return;
        }
        Interlocked.Add(ref _sampledBytes, checked((long)Math.Round(value, MidpointRounding.AwayFromZero)));
        Interlocked.Increment(ref _samples);
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

internal sealed record Issue170AllocationMeasurement(long SampledBytes, int Samples, int IntervalMilliseconds);
