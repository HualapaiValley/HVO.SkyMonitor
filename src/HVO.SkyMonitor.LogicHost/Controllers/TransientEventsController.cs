using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/transient-events")]
[Authorize(Policy = "TransientEventsRead")]
internal sealed class TransientEventsController(
    ICentralTransientEventReadService eventReadService,
    ICentralTransientReviewService reviewService,
    ICentralTransientDerivativeRetrievalService derivativeRetrievalService,
    ICentralTransientNotificationRetryService notificationRetryService,
    ICentralTransientReprocessingService reprocessingService,
    ICentralTransientPayloadReleaseService payloadReleaseService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CentralTransientEventPage>> ListAsync(
        [FromQuery] int take = 100,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 100)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 100" });
        }
        try
        {
            return Ok(await eventReadService.ListAsync(User, take, cursor, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "The transient event cursor is invalid." });
        }
    }

    [HttpGet("{centralTransientEventId:guid}")]
    public async Task<ActionResult<CentralTransientEventDetail>> GetAsync(
        Guid centralTransientEventId,
        CancellationToken cancellationToken)
    {
        var result = await eventReadService.GetAsync(User, centralTransientEventId, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            return NotFound();
        }
        Response.Headers.ETag = result.Summary.ETag;
        return Ok(result);
    }

    [HttpGet("{centralTransientEventId:guid}/derivatives/{derivativeId:guid}/content")]
    public async Task GetDerivativeContentAsync(
        Guid centralTransientEventId,
        Guid derivativeId,
        CancellationToken cancellationToken)
    {
        var result = await derivativeRetrievalService.GetAsync(
            User, centralTransientEventId, derivativeId, cancellationToken).ConfigureAwait(false);
        await using var contentLease = result;
        if (result.Status == CentralTransientDerivativeLookupStatus.NotFound)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (result.Status == CentralTransientDerivativeLookupStatus.Gone)
        {
            await WriteProblemAsync(StatusCodes.Status410Gone, "Derivative evidence payload was released.",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        if (result.Status == CentralTransientDerivativeLookupStatus.IntegrityFailure)
        {
            await WriteProblemAsync(StatusCodes.Status409Conflict,
                "Derivative content failed integrity verification.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (result.Status != CentralTransientDerivativeLookupStatus.Found)
        {
            await WriteProblemAsync(StatusCodes.Status503ServiceUnavailable,
                "Derivative content is temporarily unavailable.", cancellationToken).ConfigureAwait(false);
            return;
        }
        var etag = $"\"{result.ChecksumSha256!.ToUpperInvariant()}\"";
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.ETag = etag;
        Response.Headers[ArtifactRetrievalController.ChecksumHeader] = result.ChecksumSha256;
        Response.Headers.XContentTypeOptions = "nosniff";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        if (!CentralArtifactByteRange.TryParse(Request.Headers.Range.ToString(), result.ByteLength, out var range))
        {
            Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            Response.Headers.ContentRange = $"bytes */{result.ByteLength}";
            return;
        }
        Response.StatusCode = range is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
        Response.ContentType = result.MediaType!;
        Response.ContentLength = range?.Length ?? result.ByteLength;
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{result.ArtifactId:D}.bin\"";
        if (range is not null)
        {
            Response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{result.ByteLength}";
        }
        try
        {
            await derivativeRetrievalService.CopyToAsync(result, Response.Body, range, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is Minio.Exceptions.MinioException or
            CentralArtifactStorageException or IOException)
        {
            if (Response.HasStarted)
            {
                HttpContext.Abort();
                return;
            }
            Response.Headers.Clear();
            Response.ContentLength = null;
            await WriteProblemAsync(StatusCodes.Status503ServiceUnavailable,
                "Derivative content is temporarily unavailable.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task WriteProblemAsync(int status, string title, CancellationToken cancellationToken)
    {
        Response.StatusCode = status;
        Response.Headers.CacheControl = "private, no-store";
        return Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title }, cancellationToken);
    }

    [HttpPost("{centralTransientEventId:guid}/notifications/{notificationId:guid}/retry")]
    [Authorize(Policy = "TransientAdmin")]
    public async Task<ActionResult<CentralTransientNotificationRetryResponse>> RetryNotificationAsync(
        Guid centralTransientEventId,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var idempotencyKeys = Request.Headers["Idempotency-Key"];
        var ifMatchValues = Request.Headers.IfMatch;
        if (idempotencyKeys.Count != 1 || string.IsNullOrWhiteSpace(idempotencyKeys[0]) || ifMatchValues.Count != 1)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                new ProblemDetails { Title = "Idempotency-Key and If-Match are required." });
        }
        if (!CentralTransientEventEtag.TryParse(ifMatchValues[0]!, out var expectedRowVersion))
        {
            return BadRequest(new ProblemDetails { Title = "If-Match must contain one current strong event ETag." });
        }
        var result = await notificationRetryService.RetryAsync(
            User,
            centralTransientEventId,
            notificationId,
            expectedRowVersion,
            idempotencyKeys[0]!,
            cancellationToken).ConfigureAwait(false);
        if (result.Response is not null)
        {
            Response.Headers.ETag = result.Response.ETag;
        }
        return result.Status switch
        {
            CentralTransientNotificationRetryStatus.Scheduled => Accepted(result.Response),
            CentralTransientNotificationRetryStatus.NotFound => NotFound(),
            CentralTransientNotificationRetryStatus.Invalid =>
                BadRequest(new ProblemDetails { Title = "The notification retry request is invalid." }),
            CentralTransientNotificationRetryStatus.Ineligible =>
                Conflict(new ProblemDetails { Title = "Only failed notification dispatches can be retried." }),
            CentralTransientNotificationRetryStatus.PreconditionFailed =>
                StatusCode(StatusCodes.Status412PreconditionFailed,
                    new ProblemDetails { Title = "The event ETag is stale." }),
            CentralTransientNotificationRetryStatus.IdempotencyConflict =>
                Conflict(new ProblemDetails { Title = "Idempotency-Key was already used for a different retry." }),
            _ => throw new InvalidOperationException("Unknown transient notification retry result.")
        };
    }

    [HttpPost("{centralTransientEventId:guid}/payload-release")]
    [Authorize(Policy = "TransientAdmin")]
    public async Task<ActionResult<CentralTransientPayloadReleaseResponse>> ReleasePayloadsAsync(
        Guid centralTransientEventId,
        CancellationToken cancellationToken)
    {
        var idempotencyKeys = Request.Headers["Idempotency-Key"];
        var ifMatchValues = Request.Headers.IfMatch;
        if (idempotencyKeys.Count != 1 || string.IsNullOrWhiteSpace(idempotencyKeys[0]) || ifMatchValues.Count != 1)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                new ProblemDetails { Title = "Idempotency-Key and If-Match are required." });
        }
        if (!CentralTransientEventEtag.TryParse(ifMatchValues[0]!, out var expectedRowVersion))
        {
            return BadRequest(new ProblemDetails { Title = "If-Match must contain one current strong event ETag." });
        }
        var result = await payloadReleaseService.ReleaseAsync(
            User,
            centralTransientEventId,
            expectedRowVersion,
            idempotencyKeys[0]!,
            cancellationToken).ConfigureAwait(false);
        if (result.Response is not null)
        {
            Response.Headers.ETag = result.Response.ETag;
        }
        return result.Status switch
        {
            CentralTransientPayloadReleaseStatus.Released => Ok(result.Response),
            CentralTransientPayloadReleaseStatus.Accepted => Accepted(result.Response),
            CentralTransientPayloadReleaseStatus.NotFound => NotFound(),
            CentralTransientPayloadReleaseStatus.Invalid =>
                BadRequest(new ProblemDetails { Title = "The payload release request is invalid." }),
            CentralTransientPayloadReleaseStatus.Disabled =>
                Conflict(new ProblemDetails { Title = "Transient payload release is disabled." }),
            CentralTransientPayloadReleaseStatus.Ineligible =>
                Conflict(new ProblemDetails { Title = "The event is not eligible for payload release." }),
            CentralTransientPayloadReleaseStatus.PreconditionFailed =>
                StatusCode(StatusCodes.Status412PreconditionFailed,
                    new ProblemDetails { Title = "The event ETag is stale." }),
            CentralTransientPayloadReleaseStatus.IdempotencyConflict =>
                Conflict(new ProblemDetails { Title = "Idempotency-Key was already used for a different release." }),
            CentralTransientPayloadReleaseStatus.Failed =>
                Conflict(new ProblemDetails { Title = "Transient payload release failed safely." }),
            _ => throw new InvalidOperationException("Unknown transient payload release result.")
        };
    }

    [HttpPost("{centralTransientEventId:guid}/reprocessing-jobs")]
    [Authorize(Policy = "TransientAdmin")]
    public async Task<ActionResult<CentralTransientReprocessingResponse>> ReprocessAsync(
        Guid centralTransientEventId,
        [FromBody] CentralTransientReprocessingRequest request,
        CancellationToken cancellationToken)
    {
        var idempotencyKeys = Request.Headers["Idempotency-Key"];
        var ifMatchValues = Request.Headers.IfMatch;
        if (idempotencyKeys.Count != 1 || string.IsNullOrWhiteSpace(idempotencyKeys[0]) || ifMatchValues.Count != 1)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                new ProblemDetails { Title = "Idempotency-Key and If-Match are required." });
        }
        if (!CentralTransientEventEtag.TryParse(ifMatchValues[0]!, out var expectedRowVersion))
        {
            return BadRequest(new ProblemDetails { Title = "If-Match must contain one current strong event ETag." });
        }
        CentralTransientReprocessingResult result;
        try
        {
            result = await reprocessingService.ScheduleAsync(
                User,
                centralTransientEventId,
                expectedRowVersion,
                idempotencyKeys[0]!,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "The reprocessing request is invalid." });
        }
        if (result.Response is not null)
        {
            Response.Headers.ETag = result.Response.ETag;
        }
        return result.Status switch
        {
            CentralTransientReprocessingStatus.Scheduled => Accepted(result.Response),
            CentralTransientReprocessingStatus.NotFound => NotFound(),
            CentralTransientReprocessingStatus.Invalid =>
                BadRequest(new ProblemDetails { Title = "The reprocessing request is invalid." }),
            CentralTransientReprocessingStatus.Ineligible =>
                Conflict(new ProblemDetails { Title = "The event is not eligible for reprocessing." }),
            CentralTransientReprocessingStatus.PreconditionFailed =>
                StatusCode(StatusCodes.Status412PreconditionFailed,
                    new ProblemDetails { Title = "The event ETag is stale." }),
            CentralTransientReprocessingStatus.IdempotencyConflict =>
                Conflict(new ProblemDetails { Title = "Idempotency-Key was already used for different reprocessing." }),
            _ => throw new InvalidOperationException("Unknown transient reprocessing result.")
        };
    }

    [HttpPost("{centralTransientEventId:guid}/reviews")]
    [Authorize(Policy = "TransientReview")]
    public async Task<ActionResult<CentralTransientReviewMutationResponse>> ReviewAsync(
        Guid centralTransientEventId,
        [FromBody] CentralTransientReviewRequest request,
        CancellationToken cancellationToken)
    {
        var idempotencyKeys = Request.Headers["Idempotency-Key"];
        var ifMatchValues = Request.Headers.IfMatch;
        if (idempotencyKeys.Count != 1 || string.IsNullOrWhiteSpace(idempotencyKeys[0]) || ifMatchValues.Count != 1)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                new ProblemDetails { Title = "Idempotency-Key and If-Match are required." });
        }
        if (!CentralTransientEventEtag.TryParse(ifMatchValues[0]!, out var expectedRowVersion))
        {
            return BadRequest(new ProblemDetails { Title = "If-Match must contain one current strong event ETag." });
        }

        CentralTransientReviewMutationResult result;
        try
        {
            result = await reviewService.ReviewAsync(
                User,
                centralTransientEventId,
                expectedRowVersion,
                idempotencyKeys[0]!,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "The review request is invalid." });
        }

        if (result.Response is not null)
        {
            Response.Headers.ETag = result.Response.ETag;
        }
        return result.Status switch
        {
            CentralTransientReviewMutationStatus.Applied => Ok(result.Response),
            CentralTransientReviewMutationStatus.NotFound => NotFound(),
            CentralTransientReviewMutationStatus.Invalid =>
                BadRequest(new ProblemDetails { Title = "The review request is invalid." }),
            CentralTransientReviewMutationStatus.PreconditionFailed =>
                StatusCode(StatusCodes.Status412PreconditionFailed,
                    new ProblemDetails { Title = "The event ETag is stale." }),
            CentralTransientReviewMutationStatus.IdempotencyConflict =>
                Conflict(new ProblemDetails { Title = "Idempotency-Key was already used for a different review." }),
            _ => throw new InvalidOperationException("Unknown transient review result.")
        };
    }
}
