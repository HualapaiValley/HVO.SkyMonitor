using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentCenteredViewEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentCenteredViewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var centered = endpoints.MapGroup("/api/v1/operations/gallery/centered")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Gallery")
            .AddEndpointFilter(HandleReopenFailureAsync);
        centered.MapGet("/{baseId}/{revisionId}", GetCenteredRevisionAsync)
            .WithName("GetCameraAgentCenteredViewRevision")
            .Produces<CenteredView>()
            .Produces(StatusCodes.Status404NotFound);
        centered.MapMethods("/{baseId}/{revisionId}/preview", [HttpMethods.Get, HttpMethods.Head], WriteCenteredPreviewAsync)
            .WithName("GetCameraAgentCenteredViewPreview")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);
        centered.MapMethods("/{baseId}/{revisionId}/presentation.svg", [HttpMethods.Get, HttpMethods.Head], WriteCenteredPresentationAsync)
            .WithName("GetCameraAgentCenteredViewPresentation")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var exactBase = endpoints.MapGroup("/api/v1/operations/gallery/{referenceCaptureId:guid}/views/{baseId}")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Gallery")
            .AddEndpointFilter(HandleReopenFailureAsync);
        exactBase.MapMethods("/preview", [HttpMethods.Get, HttpMethods.Head], WriteBoundBasePreviewAsync);

        var exact = endpoints.MapGroup("/api/v1/operations/gallery/{referenceCaptureId:guid}/views/{baseId}/{revisionId}")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Gallery")
            .AddEndpointFilter(HandleReopenFailureAsync);
        exact.MapGet("", async (Guid referenceCaptureId, string baseId, string revisionId, ICenteredViewReadIndex index, CancellationToken token) =>
            await ReadBoundViewAsync(referenceCaptureId, baseId, revisionId, index, token).ConfigureAwait(false) is { } view
                ? Results.Ok(view) : Results.NotFound());
        exact.MapMethods("/preview", [HttpMethods.Get, HttpMethods.Head], WriteBoundPreviewAsync);
        exact.MapMethods("/overlay.svg", [HttpMethods.Get, HttpMethods.Head], WriteBoundPresentationAsync);
        return endpoints;
    }

    private static async ValueTask<object?> HandleReopenFailureAsync(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (CenteredViewReopenException exception)
        {
            if (context.HttpContext.Request.RouteValues.TryGetValue("referenceCaptureId", out var reference) &&
                (!Guid.TryParse(reference?.ToString(), out var captureId) || captureId != exception.ReferenceCaptureId))
                return Results.NotFound();

            if (exception.PolicyExpired)
                return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "Centered view expired by storage policy.");

            return Results.StatusCode(exception.Status switch
            {
                CameraAgentArtifactReadStatus.Conflict => StatusCodes.Status409Conflict,
                CameraAgentArtifactReadStatus.Gone => StatusCodes.Status410Gone,
                _ => StatusCodes.Status503ServiceUnavailable
            });
        }
    }

    private static async Task WriteBoundBasePreviewAsync(Guid referenceCaptureId, string baseId,
        HttpContext context, ICenteredViewReadIndex index, ICenteredViewPresentationReader reader, CancellationToken cancellationToken)
    {
        if (!CenteredViewLinks.IsIdentity(baseId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var viewBase = await index.ReadBaseAsync(baseId, cancellationToken).ConfigureAwait(false);
        if (viewBase is null || viewBase.Definition.Reference.Capture.CaptureId != referenceCaptureId)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var content = await reader.OpenBasePreviewAsync(baseId, cancellationToken).ConfigureAwait(false);
        await CameraAgentArtifactEndpoints.WriteOpenedContentAsync(context, content, cancellationToken, allowInline: true)
            .ConfigureAwait(false);
    }

    private static async ValueTask<CenteredView?> ReadBoundViewAsync(Guid referenceCaptureId, string baseId,
        string revisionId, ICenteredViewReadIndex index, CancellationToken token)
    {
        if (!CenteredViewLinks.IsIdentity(baseId) || !CenteredViewLinks.IsIdentity(revisionId))
            return null;
        var view = await index.ReadRevisionAsync(baseId, revisionId, token).ConfigureAwait(false);
        return view?.Base.Definition.Reference.Capture.CaptureId == referenceCaptureId ? view : null;
    }

    private static async Task WriteBoundPreviewAsync(Guid referenceCaptureId, string baseId, string revisionId,
        HttpContext context, ICenteredViewReadIndex index, ICenteredViewPresentationReader reader, CancellationToken cancellationToken)
    {
        if (await ReadBoundViewAsync(referenceCaptureId, baseId, revisionId, index, cancellationToken).ConfigureAwait(false) is null)
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        else
            await WriteCenteredPreviewAsync(baseId, revisionId, context, reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBoundPresentationAsync(Guid referenceCaptureId, string baseId, string revisionId,
        HttpContext context, ICenteredViewReadIndex index, ICenteredViewPresentationReader reader, CancellationToken cancellationToken)
    {
        if (await ReadBoundViewAsync(referenceCaptureId, baseId, revisionId, index, cancellationToken).ConfigureAwait(false) is null)
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        else
            await WriteCenteredPresentationAsync(baseId, revisionId, context, reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> GetCenteredRevisionAsync(string baseId, string revisionId,
        ICenteredViewReadIndex index, CancellationToken cancellationToken)
    {
        if (!CenteredViewLinks.IsIdentity(baseId) || !CenteredViewLinks.IsIdentity(revisionId))
            return Results.NotFound();
        return await index.ReadRevisionAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false) is { } view
            ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task WriteCenteredPreviewAsync(string baseId, string revisionId,
        HttpContext context, ICenteredViewPresentationReader reader, CancellationToken cancellationToken)
    {
        var content = await reader.OpenPreviewAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        await CameraAgentArtifactEndpoints.WriteOpenedContentAsync(context, content, cancellationToken, allowInline: true)
            .ConfigureAwait(false);
    }

    private static async Task WriteCenteredPresentationAsync(string baseId, string revisionId,
        HttpContext context, ICenteredViewPresentationReader reader, CancellationToken cancellationToken)
    {
        var result = await reader.ReadAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        await CameraAgentGalleryEndpoints.WritePresentationResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }
}
