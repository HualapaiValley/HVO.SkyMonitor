using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using Microsoft.Data.Sqlite;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>Authenticated reads and bounded projections of immutable still products.</summary>
internal static class CameraAgentStillProductEndpoints
{
    private const string ChecksumHeader = "X-Content-SHA256";

    internal static IEndpointRouteBuilder MapCameraAgentStillProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/still-products")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Still Products");
        group.MapGet("/dates", (DateOnly from, DateOnly to, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () =>
            {
                try { return Results.Ok(await catalog.SummarizeDatesAsync(from, to, token).ConfigureAwait(false)); }
                catch (ArgumentOutOfRangeException)
                {
                    return Results.Problem(
                        FormattableString.Invariant(
                            $"The date range must be ordered and span at most {NightlyProductProjectionContract.MaximumSummarizedDates} days."),
                        statusCode: StatusCodes.Status400BadRequest);
                }
            })).WithName("SummarizeCameraAgentStillProductDates");
        group.MapGet("/dates/{date}", (DateOnly date, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => Results.Ok(new
            {
                products = await catalog.ListAsync(date, token).ConfigureAwait(false),
                windows = await catalog.ListWindowsAsync(date, token).ConfigureAwait(false)
            }))).WithName("ListCameraAgentStillProducts");
        group.MapGet("/days/{date}", (DateOnly date, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => Results.Ok(await catalog.GetDayAsync(date, token).ConfigureAwait(false))))
            .WithName("GetCameraAgentStillProductDay");
        group.MapGet("/{productId:guid}", (Guid productId, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => await catalog.GetAsync(productId, token).ConfigureAwait(false) is { } product
                ? Results.Ok(product) : Results.NotFound())).WithName("GetCameraAgentStillProduct");
        group.MapGet("/{productId:guid}/presentation", (Guid productId, INightlyProductCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => await catalog.GetPresentationAsync(productId, token).ConfigureAwait(false) is { } presentation
                ? Results.Ok(presentation) : Results.NotFound())).WithName("GetCameraAgentStillProductPresentation");
        group.MapMethods("/{productId:guid}/preview", [HttpMethods.Get, HttpMethods.Head],
            (Guid productId, INightlyProductCatalog catalog, HttpContext context, CancellationToken token) =>
                ReadAsync(async () =>
                {
                    SetPrivateMediaHeaders(context.Response);
                    return await catalog.OpenRenditionAsync(productId, token).ConfigureAwait(false) is { } rendition
                        ? Verified(context, rendition.Content, rendition.MediaType, rendition.ChecksumSha256,
                            FormattableString.Invariant($"{productId:D}.jpg"))
                        : Results.NotFound();
                })).WithName("GetCameraAgentStillProductPreview");
        group.MapMethods("/{productId:guid}/provenance", [HttpMethods.Get, HttpMethods.Head],
            (Guid productId, INightlyProductCatalog catalog, HttpContext context, CancellationToken token) =>
                ReadAsync(async () =>
                {
                    SetPrivateMediaHeaders(context.Response);
                    return await catalog.OpenProvenanceAsync(productId, token).ConfigureAwait(false) is { } provenance
                        ? Verified(context, provenance.Content, "application/json", provenance.ChecksumSha256,
                            FormattableString.Invariant($"{productId:D}.provenance.json"))
                        : Results.NotFound();
                })).WithName("GetCameraAgentStillProductProvenance");
        return endpoints;
    }

    /// <summary>
    /// Private media is revalidated on every use: the strong ETag is the published checksum, so an unchanged product
    /// answers a conditional request with 304 and a browser never shows bytes the store did not verify.
    /// </summary>
    internal static void SetPrivateMediaHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-cache";
        response.Headers.Vary = "Cookie";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private static IResult Verified(
        HttpContext context,
        ReadOnlyMemory<byte> content,
        string mediaType,
        string checksumSha256,
        string fileName)
    {
        var download = context.Request.Query.TryGetValue("download", out var value) && value is ["1"];
        context.Response.Headers[ChecksumHeader] = checksumSha256;
        context.Response.Headers.ContentDisposition = $"{(download ? "attachment" : "inline")}; filename=\"{fileName}\"";
        return Results.Bytes(content, mediaType, enableRangeProcessing: true,
            entityTag: new EntityTagHeaderValue($"\"{checksumSha256.ToUpperInvariant()}\""));
    }

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            return Results.Problem("The still product catalog is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
