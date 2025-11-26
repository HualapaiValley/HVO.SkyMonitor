using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public sealed class CaptureProcessingContext
{
    private CaptureLoopSubmission _submission;
    private readonly List<CaptureProcessingStepTelemetry> _stepTelemetry = new();

    public CaptureProcessingContext(CameraModuleConfig config, CaptureLoopSubmission submission)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _submission = submission ?? throw new ArgumentNullException(nameof(submission));
    }

    public CameraModuleConfig Config { get; }

    public CaptureLoopSubmission Submission => _submission;

    public CameraFrame? Frame => _submission.Result.Frame;

    public IReadOnlyList<CaptureProcessingStepTelemetry> StepTelemetry => _stepTelemetry;

    public void ReplaceFrame(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var result = _submission.Result with { Frame = frame };
        _submission = _submission with { Result = result };
    }

    public void UpdateResult(CaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _submission = _submission with { Result = result };
    }

    internal void AddStepTelemetry(CaptureProcessingStepTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _stepTelemetry.Add(telemetry);
    }
}

public interface ICaptureProcessingStep
{
    string Name { get; }

    int Order { get; }

    ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken);
}
