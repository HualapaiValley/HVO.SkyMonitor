using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
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
    private const int MaximumBaseBytes = 32 * 1024 * 1024;
    private const long MaximumPixels = 32L * 1024 * 1024;

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
            artifact.MediaType is not ("image/jpeg" or "image/png" or CentralPresentationBaseDecoder.PackedMediaType))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        try
        {
            var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            if (artifact.MediaType == CentralPresentationBaseDecoder.PackedMediaType)
            {
                if (artifact.ByteLength is < 1 or > MaximumBaseBytes or > int.MaxValue)
                {
                    Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                var packedEtag = CreatePackedRepresentationEtag(artifact);
                SetImmutableHeaders(packedEtag);
                if (Matches(packedEtag))
                {
                    Response.StatusCode = StatusCodes.Status304NotModified;
                    return;
                }
                Response.StatusCode = StatusCodes.Status200OK;
                Response.ContentType = JpegImageCodec.MediaType;
                if (HttpMethods.IsHead(Request.Method))
                {
                    return;
                }
                using var packed = new MemoryStream(checked((int)artifact.ByteLength));
                await objectReader.CopyToAsync(snapshot, packed, null, cancellationToken).ConfigureAwait(false);
                var image = CentralPresentationBaseDecoder.Decode(
                    artifact, packed.ToArray(), MaximumPixels, cancellationToken);
                var jpeg = JpegImageCodec.EncodeToJpeg(image.Layout, image.PixelData,
                    cancellationToken: cancellationToken);
                Response.ContentLength = jpeg.LongLength;
                await Response.Body.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
                return;
            }
            var etag = $"\"{artifact.ChecksumSha256.ToUpperInvariant()}\"";
            SetImmutableHeaders(etag);
            if (Matches(etag))
            {
                Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = artifact.MediaType;
            Response.ContentLength = artifact.ByteLength;
            if (HttpMethods.IsGet(Request.Method))
            {
                await objectReader.CopyToAsync(snapshot, Response.Body, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is CentralArtifactIntegrityException or
            ArgumentException or InvalidDataException or InvalidOperationException)
        {
            await HandleStreamingFailureAsync(StatusCodes.Status409Conflict).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CentralArtifactMissingException or CentralArtifactStorageException)
        {
            await HandleStreamingFailureAsync(StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
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
        Response.Headers.Vary = "Authorization, X-API-Key, Cookie";
        Response.Headers.ETag = etag;
        Response.Headers.XContentTypeOptions = "nosniff";
    }

    private static string CreatePackedRepresentationEtag(CentralArtifact artifact)
    {
        var layout = artifact.Layout
            ?? throw new InvalidDataException("The retained packed presentation base has no layout.");
        var representationIdentity = string.Join('\n',
            "packed-jpeg-v1",
            artifact.ChecksumSha256.ToUpperInvariant(),
            PresentationProcessingProducts.ComputeLayoutIdentity(
                CentralReconstructionDescriptorFactory.CreateLayout(layout)),
            CentralPresentationBaseDecoder.PackedDecoderVersion,
            JpegImageCodec.AlgorithmVersion,
            JpegImageCodec.DefaultQuality.ToString(CultureInfo.InvariantCulture),
            JpegImageCodec.MediaType);
        return $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(representationIdentity)))}\"";
    }

    private Task HandleStreamingFailureAsync(int statusCode)
    {
        if (Response.HasStarted)
        {
            HttpContext.Abort();
            return Task.CompletedTask;
        }
        Response.Headers.Clear();
        Response.ContentLength = null;
        Response.StatusCode = statusCode;
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Vary = "Authorization, X-API-Key, Cookie";
        return Task.CompletedTask;
    }

    private bool Matches(string etag) => Request.Headers.IfNoneMatch
        .SelectMany(value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
        .Select(static value => value.Trim())
        .Any(value => value == "*" || string.Equals(
            value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..].Trim() : value,
            etag,
            StringComparison.Ordinal));
}
