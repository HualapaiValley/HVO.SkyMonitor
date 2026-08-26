using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/captures/{captureId:guid}")]
[Authorize(Policy = "ArtifactRetrieval")]
internal sealed class CentralPresentationController(
    ICentralLayeredPresentationService presentations,
    ICentralArtifactRetrievalService retrieval,
    ICentralArtifactObjectReader objectReader) : ControllerBase
{
    [HttpGet("presentation")]
    public async Task<IActionResult> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var result = await presentations.GetAsync(captureId, User, cancellationToken).ConfigureAwait(false);
        if (result.Status != CentralLayeredPresentationStatus.Found || result.Presentation is not { } presentation)
        {
            return Error(result.Status);
        }
        return Ok(new
        {
            presentation.CaptureId,
            presentation.BaseArtifactId,
            presentation.ManifestIdentitySha256,
            presentation.PresentationIdentitySha256,
            presentation.SvgChecksumSha256,
            presentation.WidthPixels,
            presentation.HeightPixels,
            presentation.Layers,
            presentation.BaseContentPath,
            presentation.SvgPath
        });
    }

    [HttpGet("presentation.svg")]
    [HttpHead("presentation.svg")]
    public async Task WriteSvgAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var result = await presentations.GetAsync(captureId, User, cancellationToken).ConfigureAwait(false);
        if (result.Status != CentralLayeredPresentationStatus.Found || result.Presentation is not { } presentation)
        {
            await WriteErrorAsync(result.Status, cancellationToken).ConfigureAwait(false);
            return;
        }
        var etag = $"\"{presentation.SvgChecksumSha256}\"";
        SetImmutableHeaders(etag);
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        if (Matches(etag))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "image/svg+xml; charset=utf-8";
        Response.ContentLength = presentation.Svg.Length;
        if (HttpMethods.IsGet(Request.Method))
        {
            await Response.Body.WriteAsync(presentation.Svg, cancellationToken).ConfigureAwait(false);
        }
    }

    [HttpGet("presentation/base")]
    [HttpHead("presentation/base")]
    public async Task WriteBaseAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var result = await presentations.GetAsync(captureId, User, cancellationToken).ConfigureAwait(false);
        if (result.Status != CentralLayeredPresentationStatus.Found || result.Presentation is not { } presentation)
        {
            await WriteErrorAsync(result.Status, cancellationToken).ConfigureAwait(false);
            return;
        }
        var lookup = await retrieval.FindAsync(
            presentation.DevicePublicId,
            presentation.BaseArtifactId,
            User,
            workerAccess: null,
            cancellationToken).ConfigureAwait(false);
        if (lookup.Status != CentralArtifactLookupStatus.Found || lookup.Artifact is not { } artifact ||
            artifact.MediaType is not ("image/jpeg" or "image/png"))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var etag = $"\"{artifact.ChecksumSha256.ToUpperInvariant()}\"";
        SetImmutableHeaders(etag);
        if (Matches(etag))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        try
        {
            var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = artifact.MediaType;
            Response.ContentLength = artifact.ByteLength;
            if (HttpMethods.IsGet(Request.Method))
            {
                await objectReader.CopyToAsync(snapshot, Response.Body, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (CentralArtifactIntegrityException)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactStorageException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private ObjectResult Error(CentralLayeredPresentationStatus status) => status switch
    {
        CentralLayeredPresentationStatus.Malformed => Problem(
            statusCode: StatusCodes.Status409Conflict, title: "The retained presentation is invalid."),
        CentralLayeredPresentationStatus.TooLarge => Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge, title: "The retained presentation exceeds its bounds."),
        CentralLayeredPresentationStatus.DependencyUnavailable => Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable, title: "Presentation storage is temporarily unavailable."),
        _ => Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Structured layers are unavailable for this capture.")
    };

    private async Task WriteErrorAsync(CentralLayeredPresentationStatus status, CancellationToken cancellationToken)
    {
        var response = Error(status) as ObjectResult;
        Response.StatusCode = response?.StatusCode ?? StatusCodes.Status404NotFound;
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (!HttpMethods.IsHead(Request.Method))
        {
            await Response.WriteAsJsonAsync(response?.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetImmutableHeaders(string etag)
    {
        Response.Headers.CacheControl = "private, no-cache, must-revalidate";
        Response.Headers.Vary = "Cookie";
        Response.Headers.ETag = etag;
        Response.Headers.XContentTypeOptions = "nosniff";
    }

    private bool Matches(string etag) => Request.Headers.IfNoneMatch.Any(value =>
        string.Equals(value?.Trim(), etag, StringComparison.Ordinal) || value?.Trim() == "*");
}
