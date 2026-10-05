using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using Microsoft.Data.Sqlite;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>Authenticated, checksum-verified local video reads with range/attachment support.</summary>
internal static class CameraAgentTimeLapseEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static IEndpointRouteBuilder MapCameraAgentTimeLapseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/time-lapses")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1).WithTags("CameraAgent Time-Lapses");
        group.MapGet("/days/{date}", (DateOnly date, ICameraAgentTimeLapseCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => Results.Ok(await catalog.GetDayAsync(date, token).ConfigureAwait(false))))
            .WithName("GetCameraAgentTimeLapseDay");
        group.MapGet("/{productId:guid}", (Guid productId, ICameraAgentTimeLapseCatalog catalog, CancellationToken token) =>
            ReadAsync(async () => await catalog.GetAsync(productId, token).ConfigureAwait(false) is { } product
                ? Results.Ok(product) : Results.NotFound())).WithName("GetCameraAgentTimeLapse");
        group.MapMethods("/{productId:guid}/video", [HttpMethods.Get, HttpMethods.Head],
            (Guid productId, ICameraAgentTimeLapseCatalog catalog, HttpContext context, CancellationToken token) => ReadAsync(async () =>
            {
                CameraAgentStillProductEndpoints.SetPrivateMediaHeaders(context.Response);
                if (await catalog.GetAsync(productId, token).ConfigureAwait(false) is not { IsGapFiller: false } product) return Results.NotFound();
                var stream = await catalog.OpenVideoAsync(productId, token).ConfigureAwait(false);
                if (stream is null) return Results.Problem("This generated video's file is no longer retained.", statusCode: StatusCodes.Status410Gone);
                context.Response.Headers["X-Content-SHA256"] = product.Encoding.PayloadSha256;
                var download = context.Request.Query.TryGetValue("download", out var value) && value is ["1"];
                context.Response.Headers.ContentDisposition = FormattableString.Invariant($"{(download ? "attachment" : "inline")}; filename=\"{productId:D}.mp4\"");
                return Results.Stream(stream, "video/mp4", enableRangeProcessing: true,
                    entityTag: new EntityTagHeaderValue($"\"{product.Encoding.PayloadSha256}\""));
            })).WithName("GetCameraAgentTimeLapseVideo");
        group.MapMethods("/{productId:guid}/provenance", [HttpMethods.Get, HttpMethods.Head],
            (Guid productId, ICameraAgentTimeLapseCatalog catalog, HttpContext context, CancellationToken token) => ReadAsync(async () =>
            {
                CameraAgentStillProductEndpoints.SetPrivateMediaHeaders(context.Response);
                if (await catalog.GetAsync(productId, token).ConfigureAwait(false) is not { } product) return Results.NotFound();
                return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(product, Json),
                    "application/json", FormattableString.Invariant($"{productId:D}.provenance.json"));
            })).WithName("GetCameraAgentTimeLapseProvenance");
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException)
        {
            return Results.Problem("The time-lapse catalog or verified media is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
