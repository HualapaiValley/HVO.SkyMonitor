using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralPresentationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.Presentation";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _cache;
    private readonly Counter<long> _generatedBytes;
    private readonly Counter<long> _materializations;
    private readonly Histogram<double> _duration;

    public CentralPresentationTelemetry()
    {
        _cache = _meter.CreateCounter<long>("skymonitor.central.presentation.cache", "{lookup}");
        _generatedBytes = _meter.CreateCounter<long>("skymonitor.central.presentation.generated_bytes", "By");
        _materializations = _meter.CreateCounter<long>("skymonitor.central.presentation.materializations", "{materialization}");
        _duration = _meter.CreateHistogram<double>("skymonitor.central.presentation.duration", "ms");
    }

    public void RecordCache(string tier, string outcome) =>
        _cache.Add(1, new TagList { { "tier", tier }, { "outcome", outcome } });

    public void RecordGeneration(string outcome, int bytes, TimeSpan elapsed)
    {
        var tags = new TagList { { "operation", "svg" }, { "outcome", outcome } };
        if (bytes > 0)
        {
            _generatedBytes.Add(bytes, tags);
        }
        _duration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void RecordMaterialization(string outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "operation", "materialization" }, { "outcome", outcome } };
        _materializations.Add(1, tags);
        _duration.Record(elapsed.TotalMilliseconds, tags);
    }

    public void Dispose() => _meter.Dispose();
}
