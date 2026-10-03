using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>Authenticated reads of immutable still products; broader archive page adoption is separate.</summary>
internal static class CameraAgentStillProductEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentStillProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/still-products")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Still Products");
        group.MapGet("/dates/{date}", (DateOnly date, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => Results.Ok(new
            {
                products = await catalog.ListAsync(date, token).ConfigureAwait(false),
                windows = await catalog.ListWindowsAsync(date, token).ConfigureAwait(false)
            }))).WithName("ListCameraAgentStillProducts");
        group.MapGet("/{productId:guid}", (Guid productId, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => await catalog.GetAsync(productId, token).ConfigureAwait(false) is { } product
                ? Results.Ok(product) : Results.NotFound())).WithName("GetCameraAgentStillProduct");
        group.MapGet("/{productId:guid}/preview", (Guid productId, INightlyProductCatalog catalog, HttpContext context, CancellationToken token) =>
            ReadAsync(async () =>
            {
                var product = await catalog.OpenRenditionAsync(productId, token).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "private, no-store";
                return product is null ? Results.NotFound() : Results.File(product.Content.ToArray(), product.MediaType);
            })).WithName("GetCameraAgentStillProductPreview");
        group.MapGet("/{productId:guid}/provenance", (Guid productId, INightlyProductCatalog catalog, HttpContext context, CancellationToken token) =>
            ReadAsync(async () =>
            {
                var product = await catalog.OpenProvenanceAsync(productId, token).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "private, no-store";
                return product is null ? Results.NotFound() : Results.File(product.Content.ToArray(), "application/json");
            })).WithName("GetCameraAgentStillProductProvenance");
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            return Results.Problem("The still product catalog is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
