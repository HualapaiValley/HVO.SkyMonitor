using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

public sealed class DeploymentLocationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.DeploymentLocation";
    public const string ActivitySourceName = MeterName;
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _duration;

    public DeploymentLocationTelemetry()
    {
        _operations = _meter.CreateCounter<long>(
            "skymonitor.cameraagent.deployment_location.operations", "{operation}");
        _duration = _meter.CreateHistogram<double>(
            "skymonitor.cameraagent.deployment_location.duration", "ms");
    }

    internal void Record(string operation, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "outcome", outcome }
        };
        _operations.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
    }

    public void Dispose()
    {
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }
}
