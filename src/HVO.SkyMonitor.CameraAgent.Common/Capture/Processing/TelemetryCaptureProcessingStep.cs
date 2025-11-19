using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class TelemetryCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    TelemetryProcessingStepOptions options,
    ICaptureTelemetrySink telemetrySink,
    ILogger<TelemetryCaptureProcessingStep> logger) : ConfigurableCaptureProcessingStep<TelemetryProcessingStepOptions>(metadata, options)
{
    private readonly ICaptureTelemetrySink _telemetrySink = telemetrySink;
    private readonly ILogger<TelemetryCaptureProcessingStep> _logger = logger;

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Options.Enabled)
        {
            return ValueTask.CompletedTask;
        }

        var result = context.Submission.Result;
        var exposureMs = result.NextSetpoint.Exposure.TotalMilliseconds;
        var gain = result.NextSetpoint.Gain;

        _logger.CaptureCycleCompleted(
            context.Submission.LoopDuration.TotalMilliseconds,
            context.Submission.EffectiveInterval.TotalMilliseconds,
            exposureMs,
            gain,
            result.Mode,
            result.RequiresImmediateUpload);

        var sample = new CaptureTelemetrySample(
            StartedUtc: context.Submission.CaptureStartedUtc,
            Interval: context.Submission.EffectiveInterval,
            Exposure: result.NextSetpoint.Exposure,
            Gain: gain,
            TargetFps: result.NextSetpoint.TargetFps,
            Mode: result.Mode,
            RequiresImmediateUpload: result.RequiresImmediateUpload,
            FrameStored: result.Frame is not null,
            ProcessingLatency: result.ProcessingLatency,
            LoopDuration: context.Submission.LoopDuration,
            ProcessingSteps: context.StepTelemetry.Count == 0
                ? Array.Empty<CaptureProcessingStepTelemetry>()
                : context.StepTelemetry.ToArray());

        _telemetrySink.Report(sample);
        return ValueTask.CompletedTask;
    }
}

public sealed class TelemetryProcessingStepOptions
{
    public bool Enabled { get; init; } = true;
}
