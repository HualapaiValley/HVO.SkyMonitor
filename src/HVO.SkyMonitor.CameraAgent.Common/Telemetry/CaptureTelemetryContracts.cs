using System;
using System.Collections.Generic;
using System.Linq;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Telemetry;

public sealed record CaptureTelemetrySample(
    DateTimeOffset StartedUtc,
    TimeSpan Interval,
    TimeSpan Exposure,
    double Gain,
    double? TargetFps,
    CaptureMode Mode,
    bool RequiresImmediateUpload,
    bool FrameStored,
    TimeSpan ProcessingLatency,
    TimeSpan LoopDuration,
    IReadOnlyList<CaptureProcessingStepTelemetry> ProcessingSteps);

public sealed record CaptureTelemetryAggregate(
    double AverageIntervalMilliseconds,
    double AverageExposureMilliseconds,
    double AverageProcessingMilliseconds,
    double AverageLoopMilliseconds,
    double AverageGain,
    double CapturesPerMinute,
    double DutyCycle,
    int FramesStored,
    int ImmediateUploadCount)
{
    public static CaptureTelemetryAggregate FromSamples(IReadOnlyList<CaptureTelemetrySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return new CaptureTelemetryAggregate(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var avgInterval = samples.Average(static s => s.Interval.TotalMilliseconds);
        var avgExposure = samples.Average(static s => s.Exposure.TotalMilliseconds);
        var avgProcessing = samples.Average(static s => s.ProcessingLatency.TotalMilliseconds);
        var avgLoop = samples.Average(static s => s.LoopDuration.TotalMilliseconds);
        var avgGain = samples.Average(static s => s.Gain);
        var framesStored = samples.Count(static s => s.FrameStored);
        var immediateUploads = samples.Count(static s => s.RequiresImmediateUpload);

        var first = samples[0];
        var last = samples[^1];
        var windowMinutes = Math.Max((last.StartedUtc - first.StartedUtc).TotalMinutes, 0.0001);
        var capturesPerMinute = samples.Count / windowMinutes;
        var dutyCycle = avgInterval <= 0 ? 0 : avgLoop / avgInterval;

        return new CaptureTelemetryAggregate(
            avgInterval,
            avgExposure,
            avgProcessing,
            avgLoop,
            avgGain,
            capturesPerMinute,
            dutyCycle,
            framesStored,
            immediateUploads);
    }
}

public sealed record CaptureTelemetrySnapshot(
    IReadOnlyList<CaptureTelemetrySample> Samples,
    CaptureTelemetryAggregate Aggregate);
