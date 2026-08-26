using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class TelemetryCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    TelemetryProcessingStepOptions options,
    ICaptureTelemetrySink telemetrySink,
    CaptureTelemetryMetricsRecorder metricsRecorder,
    ILogger<TelemetryCaptureProcessingStep> logger) : ConfigurableCaptureProcessingStep<TelemetryProcessingStepOptions>(metadata, options), ICaptureProcessingOutcomeConsumer,
    ILegacyCaptureProcessingPlanContract
{
    internal const string LegacyPlanContract = "telemetry-plan-v1";
    public string LegacyPlanContractId => LegacyPlanContract;
    private readonly ICaptureTelemetrySink _telemetrySink = telemetrySink;
    private readonly CaptureTelemetryMetricsRecorder _metricsRecorder = metricsRecorder;
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

        var processingSteps = context.Config.Pipeline is { SchemaVersion: CapturePipelineSchemaVersions.ExplicitV2 }
            ? context.GetDependencyStepTelemetry()
            : context.StepTelemetry;
        var input = CaptureContractJson.SerializeToElement(new
        {
            schemaVersion = "capture-processing-telemetry-input-v1",
            steps = processingSteps.Select(static step => new
            {
                step.Name,
                durationTicks = step.Duration.Ticks,
                step.Succeeded,
                step.ErrorMessage
            })
        });
        context.RecordCanonicalInput(
            "dependency-telemetry",
            "capture-processing-telemetry-input-v1",
            CaptureContractJson.ComputeCanonicalJsonSha256(input));
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
            ProcessingSteps: processingSteps.Count == 0
                ? Array.Empty<CaptureProcessingStepTelemetry>()
                : processingSteps.ToArray(),
            TemperatureC: result.Frame?.Metadata.TemperatureC);

        _telemetrySink.Report(sample);
        _metricsRecorder.Record(sample);
        return ValueTask.CompletedTask;
    }
}

public sealed class TelemetryProcessingStepOptions
{
    public bool Enabled { get; init; } = true;
}
