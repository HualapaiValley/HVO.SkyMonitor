using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/observatories/{observatoryId:guid}/cameras/{cameraId:guid}")]
[Authorize(Policy = "ArtifactRetrieval")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
internal sealed class LogicalCameraArchiveController(ILogicalCameraArchiveReadService archive) : ControllerBase
{
    [HttpGet("captures")]
    public async Task<IActionResult> ListAsync(Guid observatoryId, Guid cameraId,
        [FromQuery] CameraArchiveFilter filter, [FromQuery] int take = 25, [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await archive.ListCapturesAsync(User, observatoryId, cameraId, filter, take, cursor,
                cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { Error = "Invalid archive filter, page size or continuation." });
        }
    }

    [HttpGet("current")]
    public async Task<IActionResult> CurrentAsync(Guid observatoryId, Guid cameraId,
        [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await archive.SelectCurrentAsync(User, observatoryId, cameraId, cursor, cancellationToken)
                .ConfigureAwait(false);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { Error = "Invalid current-selection continuation." });
        }
    }

    [HttpGet("captures/{captureId:guid}")]
    public async Task<IActionResult> DetailAsync(Guid observatoryId, Guid cameraId, Guid captureId,
        CancellationToken cancellationToken = default)
    {
        var result = await archive.GetCaptureAsync(User, observatoryId, cameraId, captureId, cancellationToken)
            .ConfigureAwait(false);
        return result is null ? NotFound() : Ok(result);
    }
}
