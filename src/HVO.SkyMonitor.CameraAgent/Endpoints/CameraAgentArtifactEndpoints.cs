using System.Globalization;
using System.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentArtifactEndpoints
{
    private const string ChecksumHeader = "X-Artifact-SHA256";

    internal static IEndpointRouteBuilder MapCameraAgentArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var artifacts = endpoints.MapGroup("/api/v1/operations/artifacts")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Artifacts");

        artifacts.MapMethods("/{artifactId:guid}/content", [HttpMethods.Get, HttpMethods.Head], WriteContentAsync)
            .WithName("GetCameraAgentArtifactContent")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status206PartialContent)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .Produces(StatusCodes.Status416RangeNotSatisfiable)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        artifacts.MapMethods("/{artifactId:guid}/preview", [HttpMethods.Get, HttpMethods.Head], WritePreviewAsync)
            .WithName("GetCameraAgentArtifactPreview")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task WriteContentAsync(
        Guid artifactId,
        HttpContext context,
        ICameraAgentArtifactService artifacts,
        CancellationToken cancellationToken)
    {
        var opened = await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
        await WriteOpenedContentAsync(context, opened, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteOpenedContentAsync(
        HttpContext context,
        CameraAgentArtifactContentResult opened,
        CancellationToken cancellationToken)
    {
        if (opened.Status != CameraAgentArtifactReadStatus.Found || opened.Content is null)
        {
            await WriteFailureAsync(context, opened.Status, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var content = opened.Content;
        var etag = CreateETag(content.ChecksumSha256);
        SetPrivateHeaders(context.Response, immutable: false);
        context.Response.Headers.ETag = etag;
        context.Response.Headers[ChecksumHeader] = content.ChecksumSha256;
        if (MatchesIfNoneMatch(context.Request, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        ByteRange? range = null;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var requestedRange = context.Request.Headers.Range.ToString();
            if (context.Request.Headers.IfRange.Count > 0 &&
                !string.Equals(context.Request.Headers.IfRange.ToString(), etag, StringComparison.Ordinal))
            {
                requestedRange = string.Empty;
            }
            if (!ByteRange.TryParse(requestedRange, content.ByteLength, out range))
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers.ContentRange = $"bytes */{content.ByteLength}";
                return;
            }
        }

        context.Response.StatusCode = range is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
        context.Response.ContentType = content.MediaType;
        context.Response.ContentLength = range?.Length ?? content.ByteLength;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{content.FileName}\"";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (range is not null)
        {
            context.Response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{content.ByteLength}";
        }
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        try
        {
            if (range is not null)
            {
                content.Position = range.Start;
            }
            await CopyAsync(
                content,
                context.Response.Body,
                range?.Length ?? content.ByteLength,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            context.Abort();
        }
    }

    private static async Task WritePreviewAsync(
        Guid artifactId,
        HttpContext context,
        ICameraAgentArtifactService artifacts,
        CameraAgentOperatorTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        using var activity = telemetry.StartArtifactComparison();
        var outcome = "failed";
        long outputBytes = 0;
        try
        {
            var preview = await artifacts.GetPreviewAsync(artifactId, cancellationToken).ConfigureAwait(false);
            if (preview.Status != CameraAgentArtifactReadStatus.Found || preview.ChecksumSha256 is null)
            {
                outcome = preview.Status switch
                {
                    CameraAgentArtifactReadStatus.NotFound => "not_found",
                    CameraAgentArtifactReadStatus.TooLarge => "too_large",
                    CameraAgentArtifactReadStatus.UnsupportedMediaType => "unsupported_media_type",
                    _ => "failed"
                };
                await WriteFailureAsync(context, preview.Status, cancellationToken).ConfigureAwait(false);
                return;
            }

            var etag = CreateETag(preview.ChecksumSha256);
            SetPrivateHeaders(context.Response, immutable: false);
            context.Response.Headers.ETag = etag;
            context.Response.Headers[ChecksumHeader] = preview.ChecksumSha256;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (MatchesIfNoneMatch(context.Request, etag))
            {
                outcome = "not_modified";
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
            outcome = "found";
            outputBytes = preview.Content.Length;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "image/jpeg";
            context.Response.ContentLength = preview.Content.Length;
            context.Response.Headers.ContentDisposition = $"inline; filename=\"{artifactId:D}.jpg\"";
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await context.Response.Body.WriteAsync(preview.Content, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            timer.Stop();
            activity?.SetTag("outcome", outcome);
            telemetry.RecordComparison(timer.Elapsed, outputBytes, outcome);
        }
    }

    private static async Task WriteFailureAsync(
        HttpContext context,
        CameraAgentArtifactReadStatus status,
        CancellationToken cancellationToken)
    {
        var (statusCode, title) = status switch
        {
            CameraAgentArtifactReadStatus.NotFound => (StatusCodes.Status404NotFound, "Artifact was not found."),
            CameraAgentArtifactReadStatus.Conflict => (StatusCodes.Status409Conflict, "Artifact content is unavailable."),
            CameraAgentArtifactReadStatus.Gone => (StatusCodes.Status410Gone, "Artifact content has expired."),
            CameraAgentArtifactReadStatus.TooLarge => (StatusCodes.Status413PayloadTooLarge, "Artifact preview exceeds the configured limit."),
            CameraAgentArtifactReadStatus.UnsupportedMediaType => (StatusCodes.Status415UnsupportedMediaType, "Artifact preview is not supported."),
            _ => (StatusCodes.Status503ServiceUnavailable, "Artifact storage is temporarily unavailable.")
        };
        context.Response.StatusCode = statusCode;
        SetPrivateHeaders(context.Response, immutable: false);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails { Status = statusCode, Title = title },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void SetPrivateHeaders(HttpResponse response, bool immutable)
    {
        response.Headers.CacheControl = immutable
            ? "private, max-age=31536000, immutable"
            : "private, no-cache";
        response.Headers.Vary = "Cookie";
    }

    private static string CreateETag(string checksumSha256) => $"\"{checksumSha256.ToUpperInvariant()}\"";

    private static bool MatchesIfNoneMatch(HttpRequest request, string etag)
        => request.Headers.IfNoneMatch
            .SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Select(static value => value.Trim())
            .Any(value => value == "*" || string.Equals(
                value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..].Trim() : value,
                etag,
                StringComparison.Ordinal));

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Artifact content ended before its validated length.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private sealed record ByteRange(long Start, long Length)
    {
        internal long End => Start + Length - 1;

        internal static bool TryParse(string? value, long totalLength, out ByteRange? range)
        {
            range = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
            if (totalLength <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var specification = value[6..].Trim();
            if (specification.Length == 0 || specification.Contains(',', StringComparison.Ordinal))
            {
                return false;
            }
            var separator = specification.IndexOf('-', StringComparison.Ordinal);
            if (separator < 0 || specification[(separator + 1)..].Contains('-', StringComparison.Ordinal))
            {
                return false;
            }
            var startText = specification[..separator].Trim();
            var endText = specification[(separator + 1)..].Trim();
            if (startText.Length == 0)
            {
                if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                {
                    return false;
                }
                var length = Math.Min(suffix, totalLength);
                range = new ByteRange(totalLength - length, length);
                return true;
            }
            if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                start < 0 || start >= totalLength)
            {
                return false;
            }
            var end = totalLength - 1;
            if (endText.Length > 0 &&
                (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start))
            {
                return false;
            }
            end = Math.Min(end, totalLength - 1);
            range = new ByteRange(start, end - start + 1);
            return true;
        }
    }
}
