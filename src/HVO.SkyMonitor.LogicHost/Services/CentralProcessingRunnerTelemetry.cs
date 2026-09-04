using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralProcessingRunnerTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.LogicHost.ProcessingRunner";
    public const string ActivitySourceName = "HVO.SkyMonitor.LogicHost.ProcessingRunner";
    private readonly Meter _meter = new(MeterName);
    private readonly ActivitySource _activitySource = new(ActivitySourceName);
    private readonly Counter<long> _registrations;
    private readonly Counter<long> _heartbeats;
    private readonly Counter<long> _claims;
    private readonly Counter<long> _renewals;
    private readonly Counter<long> _completions;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _bytes;
    private readonly Histogram<double> _duration;
    private readonly ConcurrentDictionary<string, long> _runners = new(StringComparer.Ordinal);

    public CentralProcessingRunnerTelemetry()
    {
        _registrations = _meter.CreateCounter<long>("skymonitor.central.runner.registrations", "{registration}");
        _heartbeats = _meter.CreateCounter<long>("skymonitor.central.runner.heartbeats", "{heartbeat}");
        _claims = _meter.CreateCounter<long>("skymonitor.central.runner.claims", "{claim}");
        _renewals = _meter.CreateCounter<long>("skymonitor.central.runner.lease.renewals", "{renewal}");
        _completions = _meter.CreateCounter<long>("skymonitor.central.runner.completions", "{completion}");
        _failures = _meter.CreateCounter<long>("skymonitor.central.runner.failures", "{failure}");
        _bytes = _meter.CreateCounter<long>("skymonitor.central.runner.bytes", "By");
        _duration = _meter.CreateHistogram<double>("skymonitor.central.runner.duration", "ms");
        _meter.CreateObservableGauge(
            "skymonitor.central.runner.registered",
            () => _runners.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("status", pair.Key))),
            "{runner}");
    }

    public Activity? Start(string name) => _activitySource.StartActivity(name, ActivityKind.Server);

    public void RecordRegistration(string outcome)
        => _registrations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordHeartbeat(string outcome)
        => _heartbeats.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordClaim(string outcome, string recipe, TimeSpan elapsed)
    {
        _claims.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("recipe", recipe));
        _duration.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", "claim"),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordRenewal(string outcome)
        => _renewals.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordCompletion(string outcome, string recipe, TimeSpan elapsed, long productBytes)
    {
        _completions.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("recipe", recipe));
        _duration.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", "complete"),
            new KeyValuePair<string, object?>("outcome", outcome));
        if (productBytes > 0)
        {
            _bytes.Add(productBytes, new KeyValuePair<string, object?>("direction", "product"));
        }
    }

    public void RecordFailure(string reason, string recipe)
        => _failures.Add(1,
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("recipe", recipe));

    public void RecordInputBytes(long bytes)
    {
        if (bytes > 0)
        {
            _bytes.Add(bytes, new KeyValuePair<string, object?>("direction", "input"));
        }
    }

    public void SetRegistered(string status, long count) => _runners[status] = count;

    public void Dispose()
    {
        _meter.Dispose();
        _activitySource.Dispose();
    }
}
