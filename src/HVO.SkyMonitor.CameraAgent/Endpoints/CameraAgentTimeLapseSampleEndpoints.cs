using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// Serves the operator-configured time-lapse sample with byte ranges so a player can seek a large file. The sample is
/// not a product of any night; it is absent unless explicitly enabled.
/// </summary>
internal static class CameraAgentTimeLapseSampleEndpoints
{
    internal const string Path = "/api/v1/operations/time-lapse-sample";

    internal static IEndpointRouteBuilder MapCameraAgentTimeLapseSampleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(Path, [HttpMethods.Get, HttpMethods.Head], Read)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Time-Lapse Sample")
            .WithName("GetCameraAgentTimeLapseSample");
        return endpoints;
    }

    private static IResult Read(IOptions<CameraAgentHostOptions> options, HttpContext context)
    {
        var sample = options.Value.TimeLapseSample;
        CameraAgentStillProductEndpoints.SetPrivateMediaHeaders(context.Response);
        if (!sample.Enabled || sample.FilePath is null)
        {
            return Results.NotFound();
        }
        FileInfo file;
        try
        {
            file = new FileInfo(sample.FilePath);
            if (!file.Exists)
            {
                return Results.NotFound();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Results.Problem("The time-lapse sample is temporarily unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        context.Response.Headers.ContentDisposition = "inline; filename=\"time-lapse-sample\"";
        // The sample is mutable operator configuration, so its validator is its length and write time, not a checksum.
        var tag = new EntityTagHeaderValue(FormattableString.Invariant(
            $"\"{file.Length:X}-{file.LastWriteTimeUtc.Ticks:X}\""));
        return Results.File(file.FullName, sample.MediaType, lastModified: file.LastWriteTimeUtc, entityTag: tag,
            enableRangeProcessing: true);
    }
}
