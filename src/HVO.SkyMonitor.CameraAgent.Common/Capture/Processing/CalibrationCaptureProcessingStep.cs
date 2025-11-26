using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CalibrationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    CalibrationProcessingStepOptions options,
    ICaptureCalibrationProcessor calibrationProcessor,
    ILogger<CalibrationCaptureProcessingStep> logger) : ConfigurableCaptureProcessingStep<CalibrationProcessingStepOptions>(metadata, options)
{
    private readonly ICaptureCalibrationProcessor _calibrationProcessor = calibrationProcessor;
    private readonly ILogger<CalibrationCaptureProcessingStep> _logger = logger;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Calibration failures should not halt downstream processing steps.")]
    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Options.Enabled)
        {
            return;
        }

        var frame = context.Frame;
        if (frame is null)
        {
            return;
        }

        try
        {
            _logger.CalibrationApplying(Name, Options.Strategy, Options.CalibrationPasses, Options.MaxCalibrationSeconds);

            var calibrated = await _calibrationProcessor
                .ApplyCalibrationAsync(context.Config, frame, cancellationToken)
                .ConfigureAwait(false);

            if (!ReferenceEquals(calibrated, frame))
            {
                context.ReplaceFrame(calibrated);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.CaptureProcessingStepFailed(Name, context.Submission.CaptureStartedUtc, ex);
        }
    }
}

public sealed class CalibrationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 10)]
    public int CalibrationPasses { get; init; } = 1;

    [Range(1, 600)]
    public int MaxCalibrationSeconds { get; init; } = 30;

    [Required(AllowEmptyStrings = false)]
    public string Strategy { get; init; } = "None";
}
