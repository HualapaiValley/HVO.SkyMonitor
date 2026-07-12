using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class NoOpFileStorageProcessingStep(
    CaptureProcessingStepMetadata metadata,
    NoOpFileStorageProcessingStepOptions options,
    ILatestFrameAccessor latestFrameAccessor,
    IFrameStorageService frameStorageService,
    IArtifactOutbox artifactOutbox,
    ILogger<NoOpFileStorageProcessingStep> logger) : ConfigurableCaptureProcessingStep<NoOpFileStorageProcessingStepOptions>(metadata, options)
{
    private readonly ILatestFrameAccessor _latestFrameAccessor = latestFrameAccessor;
    private readonly IFrameStorageService _frameStorageService = frameStorageService;
    private readonly IArtifactOutbox _artifactOutbox = artifactOutbox;
    private readonly ILogger<NoOpFileStorageProcessingStep> _logger = logger;

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Config.AgentId);
        var artifacts = context.Artifacts;
        if (artifacts is null)
        {
            _logger.NoOpStorageSkipped(Name);
            return;
        }

        _logger.NoOpStoragePlanned(Name, artifacts.Raw.Frame.TimestampUtc, Options.StorageRoot, Options.RetentionDays);
        foreach (var artifact in artifacts.Artifacts.Values)
        {
            var stored = await _frameStorageService.SaveAsync(Options.StorageRoot, artifact, cancellationToken).ConfigureAwait(false);
            if (Options.QueueForUpload)
            {
                await _artifactOutbox.EnqueueAsync(Options.StorageRoot, new ArtifactUploadManifest(
                    "v1", context.Config.AgentId, artifact.ArtifactId, artifacts.Raw.ArtifactId, artifact.Role,
                    MediaTypeFor(artifact.Frame.PixelFormat), artifact.Frame.PixelData.Length,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artifact.Frame.PixelData.Span)),
                    artifact.Frame.TimestampUtc, artifact.RecipeVersion ?? "raw-v1", stored.RelativePath,
                    artifact.Frame.Metadata.Scene), cancellationToken).ConfigureAwait(false);
            }
        }

        if (Options.UpdateLatestFrame)
        {
            _latestFrameAccessor.Update(artifacts.Raw);
            if (artifacts.Artifacts.TryGetValue(FrameArtifactRole.Combined, out var combined))
            {
                _latestFrameAccessor.Update(combined);
            }
            if (artifacts.Artifacts.TryGetValue(FrameArtifactRole.AnnotatedPreview, out var annotated))
            {
                _latestFrameAccessor.Update(annotated);
            }
            else if (artifacts.Artifacts.TryGetValue(FrameArtifactRole.Preview, out var preview))
            {
                _latestFrameAccessor.Update(preview);
            }
        }

    }

    private static string MediaTypeFor(HVO.SkyMonitor.AgentCore.CameraPixelFormat pixelFormat) => pixelFormat switch
    {
        HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono8 => "application/x-skymonitor-mono8",
        HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16 => "application/x-skymonitor-mono16",
        HVO.SkyMonitor.AgentCore.CameraPixelFormat.Rgb24 => "application/x-skymonitor-rgb24",
        HVO.SkyMonitor.AgentCore.CameraPixelFormat.BayerRggb16 => "application/x-skymonitor-bayer-rggb16",
        _ => "application/octet-stream"
    };
}

public sealed class NoOpFileStorageProcessingStepOptions
{
    [Required(AllowEmptyStrings = false)]
    public string StorageRoot { get; init; } = "/tmp/camera";

    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 30;

    public bool UpdateLatestFrame { get; init; } = true;

    public bool QueueForUpload { get; init; } = true;
}
