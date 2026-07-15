using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public sealed class CaptureControlTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.CaptureControl";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.CaptureControl";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _meteringSamples;
    private readonly Counter<long> _meteringBytes;
    private readonly Histogram<double> _startJitter;
    private readonly Histogram<double> _cycleDuration;
    private readonly Histogram<double> _segmentDuration;
    private readonly Histogram<double> _meteringDuration;
    private readonly Histogram<double> _controlDuration;

    public CaptureControlTelemetry()
    {
        _cycles = _meter.CreateCounter<long>("camera_agent.capture_control.cycles", "{cycle}");
        _decisions = _meter.CreateCounter<long>("camera_agent.capture_control.decisions", "{decision}");
        _meteringSamples = _meter.CreateCounter<long>("camera_agent.capture_control.metering.samples", "{sample}");
        _meteringBytes = _meter.CreateCounter<long>("camera_agent.capture_control.metering.scanned", "By");
        _startJitter = _meter.CreateHistogram<double>("camera_agent.capture_control.start_jitter", "s");
        _cycleDuration = _meter.CreateHistogram<double>("camera_agent.capture_control.cycle.duration", "s");
        _segmentDuration = _meter.CreateHistogram<double>("camera_agent.capture_control.segment.duration", "s");
        _meteringDuration = _meter.CreateHistogram<double>("camera_agent.capture_control.metering.duration", "s");
        _controlDuration = _meter.CreateHistogram<double>("camera_agent.capture_control.decision.duration", "s");
    }

    internal void RecordMetering(CaptureMeteringEvidence evidence, TimeSpan duration)
    {
        var outcome = new KeyValuePair<string, object?>("outcome", evidence.Outcome.ToString());
        _meteringSamples.Add(evidence.ConsideredSampleCount, outcome);
        _meteringBytes.Add(evidence.ScannedBytes, outcome);
        _meteringDuration.Record(Math.Max(0, duration.TotalSeconds), outcome);
    }

    internal void RecordCycle(
        CaptureCycleEvidence evidence,
        CaptureAcquisitionTiming? acquisitionTiming,
        TimeSpan moduleDuration,
        TimeSpan meteringDuration,
        TimeSpan controlDecisionDuration,
        TimeSpan setpointDuration,
        TimeSpan cycleDuration,
        TimeSpan ingressDuration)
    {
        var cadence = new KeyValuePair<string, object?>("cadence", evidence.CadenceMode.ToString());
        var reason = new KeyValuePair<string, object?>("reason", evidence.Decision.Reason.ToString());
        var regime = new KeyValuePair<string, object?>("regime", evidence.SolarRegime?.ToString() ?? "none");
        _cycles.Add(1, cadence);
        _decisions.Add(1, reason, regime);
        _startJitter.Record(evidence.MonotonicStartJitter.TotalSeconds, cadence);
        _cycleDuration.Record(cycleDuration.TotalSeconds, cadence);
        var acquisitionDuration = TimeSpan.Zero;
        if (acquisitionTiming is not null)
        {
            var exposureDuration = ClampDuration(
                acquisitionTiming.ExposureEndedUtc - acquisitionTiming.ExposureStartedUtc,
                moduleDuration);
            var readoutDuration = ClampDuration(
                acquisitionTiming.ReadoutCompletedUtc - acquisitionTiming.ExposureEndedUtc,
                moduleDuration - exposureDuration);
            RecordSegment(
                exposureDuration,
                "exposure",
                cadence);
            RecordSegment(
                readoutDuration,
                "readout",
                cadence);
            acquisitionDuration = exposureDuration + readoutDuration;
        }
        RecordSegment(moduleDuration - acquisitionDuration, "module", cadence);
        if (evidence.Metering is not null)
        {
            RecordSegment(meteringDuration, "metering", cadence);
        }
        RecordSegment(controlDecisionDuration, "control", cadence);
        var setpointAttempted = evidence.Decision.SetpointAppliedUtc.HasValue ||
            evidence.Decision.Reason == CaptureControlDecisionReason.SetpointApplicationFailed;
        if (setpointAttempted)
        {
            RecordSegment(setpointDuration, "setpoint", cadence);
        }
        _segmentDuration.Record(ingressDuration.TotalSeconds, new("segment", "ingress"), cadence);
        var accounted = moduleDuration + ingressDuration +
            meteringDuration + controlDecisionDuration +
            (setpointAttempted ? setpointDuration : TimeSpan.Zero);
        RecordSegment(cycleDuration - accounted, "host-gap", cadence);
        _controlDuration.Record(
            controlDecisionDuration.TotalSeconds,
            reason,
            regime);
    }

    private void RecordSegment(
        TimeSpan duration,
        string segment,
        KeyValuePair<string, object?> cadence)
        => _segmentDuration.Record(
            Math.Max(0, duration.TotalSeconds),
            new("segment", segment),
            cadence);

    private static TimeSpan ClampDuration(TimeSpan value, TimeSpan maximum)
        => value <= TimeSpan.Zero
            ? TimeSpan.Zero
            : value >= maximum
                ? Max(maximum, TimeSpan.Zero)
                : value;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    public void Dispose() => _meter.Dispose();
}
