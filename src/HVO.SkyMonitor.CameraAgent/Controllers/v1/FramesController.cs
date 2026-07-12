using Asp.Versioning;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Imaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Controllers.v1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/frames")]
public sealed class FramesController : ControllerBase
{
    private readonly ILatestFrameAccessor _latestFrameAccessor;
    private readonly ILogger<FramesController> _logger;

    public FramesController(ILatestFrameAccessor latestFrameAccessor, ILogger<FramesController> logger)
    {
        _latestFrameAccessor = latestFrameAccessor;
        _logger = logger;
    }

    [HttpGet("latest")]
    [Produces("image/jpeg")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public IActionResult GetLatest()
        => GetFrame(FrameArtifactRole.Preview);

    [HttpGet("raw")]
    [Produces("image/jpeg")]
    public IActionResult GetRaw()
        => GetFrame(FrameArtifactRole.Raw);

    [HttpGet("processed")]
    [Produces("image/jpeg")]
    public IActionResult GetProcessed()
        => GetFrame(FrameArtifactRole.Combined);

    private IActionResult GetFrame(FrameArtifactRole role)
    {
        if (!_latestFrameAccessor.TryGetSnapshot(role, out var snapshot))
        {
            return NotFound();
        }

        if (snapshot.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16))
        {
            _logger.LogWarning("Frame role {Role} pixel format {PixelFormat} is not supported for preview.", role, snapshot.PixelFormat);
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var encodeResult = snapshot.PixelFormat == CameraPixelFormat.Mono8
            ? SkiaPreviewEncoder.EncodeMono8ToJpeg(snapshot.Width, snapshot.Height, snapshot.PixelData)
            : SkiaPreviewEncoder.EncodeMono16ToJpeg(snapshot.Width, snapshot.Height, snapshot.PixelData);
        if (encodeResult.IsFailure)
        {
            _logger.LogError(encodeResult.Error, "Failed to encode {Role} frame to JPEG.", role);
            return Problem("Unable to encode preview image.", statusCode: StatusCodes.Status500InternalServerError);
        }

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
        return File(encodeResult.Value, "image/jpeg");
    }
}
