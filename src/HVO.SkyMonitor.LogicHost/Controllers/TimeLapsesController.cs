using System.Text;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/time-lapses")]
[Authorize(Policy = "ArtifactRetrieval")]
internal sealed class TimeLapsesController(CentralTimeLapseCatalog catalog, CentralTimeLapseObjects objects) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> ListAsync([FromQuery] Guid? deviceId, [FromQuery] DateOnly? date,
        [FromQuery] DateTimeOffset? before, CancellationToken cancellationToken)
    {
        Private();
        return Ok(await catalog.ListAsync(User, deviceId, date, before, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{productId:guid}")]
    public async Task<IActionResult> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        Private();
        var product = await catalog.GetAsync(User, productId, cancellationToken).ConfigureAwait(false);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpGet("{productId:guid}/provenance")]
    public async Task<IActionResult> ProvenanceAsync(Guid productId, CancellationToken cancellationToken)
    {
        Private();
        var product = await catalog.GetAsync(User, productId, cancellationToken).ConfigureAwait(false);
        return product is null ? NotFound() : File(Encoding.UTF8.GetBytes(CentralTimeLapseJson.Serialize(product)),
            "application/json", $"time-lapse-{productId:D}.json");
    }

    [HttpGet("{productId:guid}/video")]
    [HttpHead("{productId:guid}/video")]
    public async Task<IActionResult> VideoAsync(Guid productId, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        Private();
        var product = await catalog.GetAsync(User, productId, cancellationToken).ConfigureAwait(false);
        if (product is null || product.IsGapFiller) return NotFound();
        try
        {
            var stream = await objects.OpenAsync(product, cancellationToken).ConfigureAwait(false);
            Response.Headers["X-Artifact-SHA256"] = product.Encoding.PayloadSha256;
            return new FileStreamResult(stream, "video/mp4")
            {
                EnableRangeProcessing = true, EntityTag = new EntityTagHeaderValue($"\"{product.Encoding.PayloadSha256}\""),
                FileDownloadName = download ? $"time-lapse-{productId:D}.mp4" : null
            };
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        {
            return StatusCode(StatusCodes.Status410Gone, new { reasonCode = "timelapse.video-missing" });
        }
        catch (Exception exception) when (exception is ObjectStoreException or InvalidDataException or IOException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { reasonCode = "timelapse.video-unavailable" });
        }
    }

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}
