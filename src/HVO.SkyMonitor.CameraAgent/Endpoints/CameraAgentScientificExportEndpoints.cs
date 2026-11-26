using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentScientificExportEndpoints
{
    internal static void MapScientificExports(this RouteGroupBuilder gallery)
    {
        gallery.MapMethods("/{captureId:guid}/artifacts/{artifactId:guid}/export/{format}", [HttpMethods.Get, HttpMethods.Head], DownloadAsync)
            .WithName("DownloadCameraAgentScientificExport")
            .Produces(StatusCodes.Status200OK, contentType: "image/fits", additionalContentTypes: ["application/fits", "application/xisf"])
            .Produces(StatusCodes.Status206PartialContent)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status416RangeNotSatisfiable)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task DownloadAsync(Guid captureId, Guid artifactId, string format, HttpContext context,
        [FromServices] ICameraAgentScientificExports exports, [FromServices] CameraAgentOperatorTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        ScientificExportFormat? encoding = format switch
        {
            "fits" => ScientificExportFormat.Fits,
            "fits.fz" => ScientificExportFormat.FitsTileGzip,
            "xisf" => ScientificExportFormat.Xisf,
            _ => null
        };
        ApplyPrivateHeaders(context.Response.Headers);
        if (encoding is null)
        {
            telemetry.RecordScientificExportFailure("unknown", "scientific-export.unknown-format", StatusCodes.Status400BadRequest);
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "scientific-export.unknown-format").ConfigureAwait(false);
            return;
        }

        var reuseRecent = HttpMethods.IsHead(context.Request.Method) || context.Request.Headers.ContainsKey(HeaderNames.Range) ||
            context.Request.Headers.ContainsKey(HeaderNames.IfNoneMatch);
        var result = await exports.PrepareAsync(captureId, artifactId, encoding.Value, cancellationToken, reuseRecent).ConfigureAwait(false);
        if (result.Download is not { } download)
        {
            var status = result.Status switch
            {
                CameraAgentArtifactReadStatus.NotFound => StatusCodes.Status404NotFound,
                CameraAgentArtifactReadStatus.Gone => StatusCodes.Status410Gone,
                CameraAgentArtifactReadStatus.Conflict => StatusCodes.Status409Conflict,
                CameraAgentArtifactReadStatus.TooLarge => StatusCodes.Status413PayloadTooLarge,
                CameraAgentArtifactReadStatus.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
                _ => StatusCodes.Status503ServiceUnavailable
            };
            var reason = result.ReasonCode ?? "scientific-export.source-unavailable";
            if (reason is "scientific-export.capacity" or "scientific-export.astrometry-unavailable")
                context.Response.Headers.RetryAfter = "1";
            telemetry.RecordScientificExportFailure(format, reason, status);
            await WriteProblemAsync(context, status, reason).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var lease = download.ConfigureAwait(false);
            var receipt = download.Receipt;
            var source = receipt.Source;
            var filename = FormattableString.Invariant($"capture-{source.CaptureSequence}-{source.ArtifactId:N}{receipt.Encoding.Extension}");
            context.Response.Headers["X-Artifact-SHA256"] = receipt.Sha256;
            await Results.File(download.Content, receipt.Encoding.MediaType, filename,
                enableRangeProcessing: true, entityTag: new EntityTagHeaderValue($"\"{receipt.Sha256}\""))
                .ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                telemetry.RecordScientificExportTransferFailure(format, context.Response.StatusCode, exception);
                context.Abort();
                return;
            }

            const string reason = "scientific-export.io-unavailable";
            telemetry.RecordScientificExportTransferFailure(format, StatusCodes.Status503ServiceUnavailable, exception);
            context.Response.Clear();
            ApplyPrivateHeaders(context.Response.Headers);
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, reason).ConfigureAwait(false);
            return;
        }

        if (context.Response.StatusCode == StatusCodes.Status416RangeNotSatisfiable)
            telemetry.RecordScientificExportFailure(format, "scientific-export.range-unsatisfiable", context.Response.StatusCode);
        if (context.Response.StatusCode == StatusCodes.Status412PreconditionFailed)
            telemetry.RecordScientificExportFailure(format, "scientific-export.precondition-failed", context.Response.StatusCode);
    }

    private static void ApplyPrivateHeaders(IHeaderDictionary headers)
    {
        headers.CacheControl = "private, no-store";
        headers.Vary = "Cookie";
        headers.XContentTypeOptions = "nosniff";
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string? reason)
    {
        context.Response.StatusCode = status;
        if (HttpMethods.IsHead(context.Request.Method))
            return Task.CompletedTask;

        return Results.Problem(statusCode: status, title: "Scientific export unavailable",
            extensions: new Dictionary<string, object?> { ["reasonCode"] = reason }).ExecuteAsync(context);
    }
}
