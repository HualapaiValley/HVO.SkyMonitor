using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class NoOpFileStorageProcessingStep(
    CaptureProcessingStepMetadata metadata,
    NoOpFileStorageProcessingStepOptions options,
    ILatestFrameAccessor latestFrameAccessor,
    ILogger<NoOpFileStorageProcessingStep> logger) : ConfigurableCaptureProcessingStep<NoOpFileStorageProcessingStepOptions>(metadata, options)
{
    private readonly ILatestFrameAccessor _latestFrameAccessor = latestFrameAccessor;
    private readonly ILogger<NoOpFileStorageProcessingStep> _logger = logger;

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var frame = context.Frame;
        if (frame is null)
        {
            _logger.NoOpStorageSkipped(Name);
            return ValueTask.CompletedTask;
        }

        _logger.NoOpStoragePlanned(Name, frame.TimestampUtc, Options.StorageRoot, Options.RetentionDays);

        if (Options.UpdateLatestFrame)
        {
            _latestFrameAccessor.Update(frame);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class NoOpFileStorageProcessingStepOptions
{
    [Required(AllowEmptyStrings = false)]
    public string StorageRoot { get; init; } = "/tmp/camera";

    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;

    public bool UpdateLatestFrame { get; init; } = true;
}
