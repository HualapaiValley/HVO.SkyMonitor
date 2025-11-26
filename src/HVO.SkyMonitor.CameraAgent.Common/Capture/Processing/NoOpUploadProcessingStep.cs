using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class NoOpUploadProcessingStep(
    CaptureProcessingStepMetadata metadata,
    NoOpUploadProcessingStepOptions options,
    ILogger<NoOpUploadProcessingStep> logger) : ConfigurableCaptureProcessingStep<NoOpUploadProcessingStepOptions>(metadata, options)
{
    private readonly ILogger<NoOpUploadProcessingStep> _logger = logger;

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Options.Enabled)
        {
            return ValueTask.CompletedTask;
        }

        var submission = context.Submission;
        var result = submission.Result;
        var requiresUpload = result.RequiresImmediateUpload || Options.UploadAllFrames;
        if (!requiresUpload || result.Frame is null)
        {
            _logger.NoOpUploadSkipped(Name, result.RequiresImmediateUpload, Options.UploadAllFrames);
            return ValueTask.CompletedTask;
        }

        _logger.NoOpUploadPlanned(Name, submission.CaptureStartedUtc, Options.Endpoint, Options.BatchSize, Options.WarmupSeconds);

        return ValueTask.CompletedTask;
    }
}

public sealed class NoOpUploadProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    public bool UploadAllFrames { get; init; }

    [Range(1, 50)]
    public int BatchSize { get; init; } = 1;

    [Range(0, 600)]
    public int WarmupSeconds { get; init; } = 5;

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string Endpoint { get; init; } = "https://localhost/upload";
}
