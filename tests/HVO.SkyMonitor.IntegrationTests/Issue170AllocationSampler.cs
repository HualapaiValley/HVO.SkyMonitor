namespace HVO.SkyMonitor.IntegrationTests;

internal sealed class Issue170AllocationSampler : IDisposable
{
    private long? _startBytes;
    private bool _stopped;

    internal void Start()
    {
        if (_startBytes.HasValue)
        {
            throw new InvalidOperationException("Allocation measurement has already started.");
        }
        _startBytes = GC.GetTotalAllocatedBytes(precise: true);
    }

    internal Task<Issue170AllocationMeasurement> StopAsync()
    {
        if (_stopped || !_startBytes.HasValue)
        {
            throw new InvalidOperationException("Allocation measurement is not active.");
        }
        _stopped = true;
        var endBytes = GC.GetTotalAllocatedBytes(precise: true);
        if (endBytes < _startBytes.Value)
        {
            throw new InvalidDataException("The runtime allocation counter was not monotonic.");
        }
        return Task.FromResult(new Issue170AllocationMeasurement(
            _startBytes.Value, endBytes, endBytes - _startBytes.Value));
    }

    public void Dispose()
    {
    }
}

internal sealed record Issue170AllocationMeasurement(long StartBytes, long EndBytes, long DeltaBytes);
