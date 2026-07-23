using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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
        gallery.MapGet("/{captureId:guid}", GetCaptureAsync)
            .WithName("GetCameraAgentGalleryCapture")
            .Produces<CameraAgentGalleryCapture>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

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

    private static async Task<IResult> GetCaptureAsync(
        Guid captureId,
        ICameraAgentGallery gallery,
        CancellationToken cancellationToken)
    {
        var capture = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
        return capture is null ? Results.NotFound() : Results.Ok(capture);
    }

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
