using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentGalleryEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentGalleryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var gallery = endpoints.MapGroup("/api/v1/operations/gallery")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Gallery");

        gallery.MapGet("/", GetPageAsync)
            .WithName("GetCameraAgentGalleryPage")
            .Produces<CameraAgentGalleryPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        gallery.MapGet("/current", GetCurrentAsync)
            .WithName("GetCameraAgentCurrentImagePresentation")
            .Produces<CameraAgentCurrentImagePresentation>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        gallery.MapGet("/calendar", GetCalendarAsync)
            .WithName("GetCameraAgentGalleryCalendar")
            .Produces<CameraAgentGalleryCalendar>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        gallery.MapGet("/products", GetProductPageAsync)
            .WithName("GetCameraAgentProductPage")
            .Produces<CameraAgentProductPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        gallery.MapGet("/products/{artifactId:guid}", GetProductAsync)
            .WithName("GetCameraAgentProduct")
            .Produces<CameraAgentProductDetail>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        gallery.MapGet("/{captureId:guid}/thumbnail", RedirectToThumbnailAsync)
            .WithName("GetCameraAgentGalleryThumbnail")
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        gallery.MapGet("/{captureId:guid}", GetCaptureAsync)
            .WithName("GetCameraAgentGalleryCapture")
            .Produces<CameraAgentGalleryCapture>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        gallery.MapMethods("/{captureId:guid}/presentation.svg", [HttpMethods.Get, HttpMethods.Head], WritePresentationAsync)
            .WithName("GetCameraAgentGalleryPresentation")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        gallery.MapGet("/{captureId:guid}/presentation", GetPresentationAsync)
            .WithName("GetCameraAgentGalleryPresentationDescriptor")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        gallery.MapPost("/{captureId:guid}/materializations", SaveMaterializationAsync)
            .WithName("SaveCameraAgentGalleryMaterialization")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .AddEndpointFilter(ValidateAntiforgeryAsync)
            .Produces<CameraAgentPresentationMaterializationReceipt>(StatusCodes.Status201Created)
            .Produces<CameraAgentPresentationMaterializationReceipt>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetPageAsync(
        [AsParameters] CameraAgentGalleryEndpointQuery request,
        ICameraAgentGallery gallery,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await gallery.GetPageAsync(new CameraAgentGalleryQuery(
                request.PageSize,
                request.Cursor,
                request.FromUtc,
                request.ToUtc,
                request.MinimumSequence,
                request.MaximumSequence,
                request.RawState,
                request.EvidenceOrigin,
                request.ProcessingRole,
                request.Recipe,
                request.ProcessingStatus), cancellationToken).ConfigureAwait(false);
            return Results.Ok(page);
        }
        catch (CameraAgentGalleryQueryException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The gallery query is invalid.");
        }
    }

    // The calendar takes observing-day dates; other gallery filters apply
    // within each day and the range is bounded by the read model.
    private static async Task<IResult> GetCalendarAsync(
        [FromQuery(Name = "from")] DateOnly? fromDate,
        [FromQuery(Name = "to")] DateOnly? toDate,
        [AsParameters] CameraAgentGalleryEndpointQuery filters,
        ICameraAgentArchive archive,
        CancellationToken cancellationToken)
    {
        if (fromDate is null || toDate is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The calendar range requires from and to dates.");
        }
        try
        {
            var calendar = await archive.GetCalendarAsync(new CameraAgentGalleryCalendarQuery(
                fromDate.Value,
                toDate.Value,
                new CameraAgentGalleryQuery(
                    RawState: filters.RawState,
                    EvidenceOrigin: filters.EvidenceOrigin,
                    ProcessingRole: filters.ProcessingRole,
                    Recipe: filters.Recipe,
                    ProcessingStatus: filters.ProcessingStatus)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(calendar);
        }
        catch (CameraAgentGalleryQueryException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The calendar query is invalid.");
        }
    }

    private static async Task<IResult> GetProductPageAsync(
        [FromQuery(Name = "pageSize")] int? pageSize,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "role")] FrameArtifactRole? role,
        [FromQuery(Name = "kind")] string? kind,
        [FromQuery(Name = "recipe")] string? recipe,
        [FromQuery(Name = "availability")] string? availability,
        [FromQuery(Name = "from")] DateTimeOffset? fromUtc,
        [FromQuery(Name = "to")] DateTimeOffset? toUtc,
        ICameraAgentArchive archive,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await archive.GetProductPageAsync(
                new CameraAgentProductQuery(pageSize, cursor, role, kind, recipe, availability, fromUtc, toUtc), cancellationToken).ConfigureAwait(false);
            return Results.Ok(page);
        }
        catch (CameraAgentGalleryQueryException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The product query is invalid.");
        }
    }

    private static async Task<IResult> GetProductAsync(
        Guid artifactId,
        ICameraAgentArchive archive,
        CancellationToken cancellationToken)
    {
        var product = await archive.GetProductAsync(artifactId, cancellationToken).ConfigureAwait(false);
        return product is null ? Results.NotFound() : Results.Ok(product);
    }

    private static async Task<IResult> GetCurrentAsync(
        ICameraAgentCurrentImagePresentationService presentation,
        CancellationToken cancellationToken)
        => Results.Ok(await presentation.GetAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetCaptureAsync(
        Guid captureId,
        ICameraAgentGallery gallery,
        CancellationToken cancellationToken)
    {
        var capture = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
        return capture is null ? Results.NotFound() : Results.Ok(capture);
    }

    /// <summary>
    /// Resolves a capture's best displayable preview through the presentation projector and
    /// redirects to that artifact's preview, so a page that knows only capture ids (the calendar
    /// grid, #988) can show one image per capture without projecting on the client. The
    /// redirect target is the existing authorized artifact preview route.
    /// </summary>
    private static async Task<IResult> RedirectToThumbnailAsync(
        Guid captureId,
        HttpContext context,
        ICameraAgentGallery gallery,
        ICameraAgentCapturePresentationProjector projector,
        CancellationToken cancellationToken)
    {
        var capture = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
        if (capture is null)
        {
            return Results.NotFound();
        }
        var presentation = projector.Project(capture);
        var slot = presentation.SelectedStage is { } selected
            ? presentation.Stages.FirstOrDefault(item => item.Stage == selected && item.PreviewUrl is not null)
            : null;
        slot ??= presentation.Stages.FirstOrDefault(static item => item.PreviewUrl is not null);
        if (slot?.PreviewUrl is not { } url)
        {
            return Results.NotFound();
        }
        // The chosen artifact is stable for a capture once published; the redirect may be held
        // privately for a while so a calendar of thumbnails does not re-project every cell.
        context.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Redirect(url.OriginalString);
    }

    private static async Task WritePresentationAsync(
        Guid captureId,
        HttpContext context,
        ICameraAgentLayeredPresentationService presentations,
        CancellationToken cancellationToken)
    {
        var result = await presentations.GetAsync(captureId, cancellationToken).ConfigureAwait(false);
        if (result.Status != CameraAgentLayeredPresentationStatus.Found || result.Presentation is null)
        {
            var (status, title) = result.Status switch
            {
                CameraAgentLayeredPresentationStatus.Malformed =>
                    (StatusCodes.Status409Conflict, "The retained presentation is invalid."),
                CameraAgentLayeredPresentationStatus.TooLarge =>
                    (StatusCodes.Status413PayloadTooLarge, "The retained presentation exceeds its bounds."),
                _ => (StatusCodes.Status404NotFound, "Structured layers are unavailable for this capture.")
            };
            context.Response.StatusCode = status;
            context.Response.Headers.CacheControl = "private, no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.WriteAsJsonAsync(
                    new ProblemDetails { Status = status, Title = title }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var presentation = result.Presentation;
        var etag = $"\"{presentation.SvgChecksumSha256}\"";
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        context.Response.Headers.Vary = "Cookie";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        if (context.Request.Headers.IfNoneMatch.Any(value =>
                string.Equals(value?.Trim(), etag, StringComparison.Ordinal) || value?.Trim() == "*"))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "image/svg+xml; charset=utf-8";
        context.Response.ContentLength = presentation.Svg.Length;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(presentation.Svg, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> GetPresentationAsync(
        Guid captureId,
        ICameraAgentLayeredPresentationService presentations,
        CancellationToken cancellationToken)
    {
        var result = await presentations.GetAsync(captureId, cancellationToken).ConfigureAwait(false);
        if (result.Status != CameraAgentLayeredPresentationStatus.Found || result.Presentation is null)
        {
            return result.Status switch
            {
                CameraAgentLayeredPresentationStatus.Malformed => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "The retained presentation is invalid."),
                CameraAgentLayeredPresentationStatus.TooLarge => Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge, title: "The retained presentation exceeds its bounds."),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound, title: "Structured layers are unavailable for this capture.")
            };
        }
        var presentation = result.Presentation;
        return Results.Ok(new
        {
            presentation.CaptureId,
            presentation.BaseArtifactId,
            presentation.ManifestIdentitySha256,
            presentation.PresentationIdentitySha256,
            presentation.SvgChecksumSha256,
            presentation.WidthPixels,
            presentation.HeightPixels,
            presentation.Layers,
            SvgUrl = FormattableString.Invariant($"/api/v1/operations/gallery/{captureId:D}/presentation.svg"),
            BasePreviewUrl = FormattableString.Invariant(
                $"/api/v1/operations/artifacts/{presentation.BaseArtifactId:D}/preview")
        });
    }

    private static async Task<IResult> SaveMaterializationAsync(
        Guid captureId,
        [FromBody] CameraAgentPresentationMaterializationRequest request,
        ClaimsPrincipal user,
        ICameraAgentPresentationMaterializer materializer,
        CancellationToken cancellationToken)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(user);
        if (actor is not { Length: > 0 and <= 128 })
        {
            return Results.Forbid();
        }
        var result = await materializer.SaveAsync(
            captureId, request.EnabledLayerIdentitySha256 ?? [], actor, cancellationToken).ConfigureAwait(false);
        if (result.Status == CameraAgentPresentationMaterializationStatus.Saved && result.Receipt is { } receipt)
        {
            return receipt.Replayed
                ? Results.Ok(receipt)
                : Results.Created(
                    FormattableString.Invariant($"/api/v1/operations/artifacts/{receipt.ArtifactId:D}/content"), receipt);
        }
        return result.Status switch
        {
            CameraAgentPresentationMaterializationStatus.Invalid => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "The selected presentation stack is invalid."),
            CameraAgentPresentationMaterializationStatus.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "The retained presentation conflicts with the request."),
            _ => Results.Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Structured layers are unavailable for this capture.")
        };
    }

    private static async ValueTask<object?> ValidateAntiforgeryAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context.HttpContext).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The materialization request is invalid.");
        }
        return await next(context).ConfigureAwait(false);
    }

    private sealed record CameraAgentPresentationMaterializationRequest(
        IReadOnlyList<string>? EnabledLayerIdentitySha256);

    private sealed class CameraAgentGalleryEndpointQuery
    {
        public int? PageSize { get; init; }

        public string? Cursor { get; init; }

        public DateTimeOffset? FromUtc { get; init; }

        public DateTimeOffset? ToUtc { get; init; }

        public long? MinimumSequence { get; init; }

        public long? MaximumSequence { get; init; }

        public string? RawState { get; init; }

        public GalleryEvidenceOrigin? EvidenceOrigin { get; init; }

        public FrameArtifactRole? ProcessingRole { get; init; }

        public string? Recipe { get; init; }

        public string? ProcessingStatus { get; init; }
    }
}
