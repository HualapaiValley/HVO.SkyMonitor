using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// Counters and spans for the local automation contract. Labels are bounded to the contract's own
/// enumerations; no definition identifier, actor, or task target ever becomes a metric label.
/// </summary>
public sealed class LocalAutomationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Automation";
    public const string ActivitySourceName = MeterName;
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _commands;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _runDuration;

    public LocalAutomationTelemetry()
    {
        _commands = _meter.CreateCounter<long>(
            "skymonitor.cameraagent.automation.commands", "{command}");
        _runs = _meter.CreateCounter<long>(
            "skymonitor.cameraagent.automation.runs", "{run}");
        _runDuration = _meter.CreateHistogram<double>(
            "skymonitor.cameraagent.automation.run_duration", "ms");
    }

    public void RecordCommand(string command, LocalAutomationCommandStatus status)
        => _commands.Add(1, new TagList
        {
            { "command", command },
            { "outcome", status.ToString() }
        });

    public void RecordRun(LocalAutomationTriggerKind trigger, LocalAutomationRunOutcome outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "trigger", trigger.ToString() },
            { "outcome", outcome.ToString() }
        };
        _runs.Add(1, tags);
        _runDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void Dispose()
    {
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }
}
