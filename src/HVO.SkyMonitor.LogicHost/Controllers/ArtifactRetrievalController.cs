using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/devices/{devicePublicId:guid}/artifacts/{artifactId:guid}")]
[Authorize(Policy = "ArtifactRetrieval")]
internal sealed partial class ArtifactRetrievalController(
    ICentralArtifactRetrievalService retrievalService,
    ICentralArtifactObjectReader objectReader,
    CentralArtifactRetrievalTelemetry telemetry,
    ILogger<ArtifactRetrievalController> logger) : ControllerBase
{
    internal const string JobIdHeader = "X-HVO-Job-Id";
    internal const string LeaseTokenHeader = "X-HVO-Lease-Token";
    internal const string ChecksumHeader = "X-Artifact-SHA256";
    internal const string RunnerIdHeader = "X-HVO-Runner-Id";
    private const string DownloadAuthorizationCookie = "HVO.RawDownloadAuthorization";

    [HttpGet]
    public async Task<ActionResult<CentralArtifactMetadata>> GetMetadataAsync(
        Guid devicePublicId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        SetPrivateCacheHeaders();
        var lookup = await FindAsync(devicePublicId, artifactId, cancellationToken).ConfigureAwait(false);
        if (lookup.Status != CentralArtifactLookupStatus.Found)
        {
            return LookupError(lookup.Status);
        }
        return Ok(Project(lookup.Artifact!));
    }

    [HttpGet("content")]
    public Task GetContentAsync(Guid devicePublicId, Guid artifactId, CancellationToken cancellationToken)
        => WriteContentAsync(devicePublicId, artifactId, headOnly: false, cancellationToken);

    [HttpHead("content")]
    public Task HeadContentAsync(Guid devicePublicId, Guid artifactId, CancellationToken cancellationToken)
        => WriteContentAsync(devicePublicId, artifactId, headOnly: true, cancellationToken);

    [HttpPost("download-authorizations")]
    public async Task<ActionResult<DownloadAuthorizationResponse>> IssueDownloadAuthorizationAsync(
        Guid devicePublicId,
        Guid artifactId,
        [FromBody] DownloadAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lookup = await FindAsync(devicePublicId, artifactId, cancellationToken).ConfigureAwait(false);
        if (lookup.Status != CentralArtifactLookupStatus.Found || lookup.Artifact!.Role != FrameArtifactRole.Raw)
        {
            return NotFound();
        }
        if (!CentralArtifactByteRange.TryParse(request.Range, lookup.Artifact.ByteLength, out var range))
        {
            return BadRequest(new ProblemDetails { Title = "The requested byte range is invalid." });
        }
        var grant = await retrievalService.IssueDownloadAuthorizationAsync(
            lookup.Artifact, User, range, cancellationToken).ConfigureAwait(false);
        if (grant is null)
        {
            return NotFound();
        }
        var contentUri = $"/api/v1.0/devices/{devicePublicId:D}/artifacts/{artifactId:D}/content";
        Response.Cookies.Append(
            DownloadAuthorizationCookie,
            $"{grant.AuthorizationId:D}.{grant.Token}",
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = contentUri,
                Expires = grant.ExpiresAtUtc,
                IsEssential = true
            });
        return Ok(new DownloadAuthorizationResponse(grant.AuthorizationId, grant.ExpiresAtUtc, contentUri));
    }

    private async Task WriteContentAsync(
        Guid devicePublicId,
        Guid artifactId,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        SetPrivateCacheHeaders();
        var lookup = await FindAsync(devicePublicId, artifactId, cancellationToken).ConfigureAwait(false);
        if (lookup.Status != CentralArtifactLookupStatus.Found)
        {
            await WriteLookupErrorAsync(lookup.Status, cancellationToken).ConfigureAwait(false);
            return;
        }
        var artifact = lookup.Artifact!;
        var applicationETag = $"\"{artifact.ChecksumSha256.ToUpperInvariant()}\"";
        string? requestedRange = headOnly ? null : Request.Headers.Range.ToString();
        if (!string.IsNullOrWhiteSpace(requestedRange)
            && (!requestedRange.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                || requestedRange.Contains(',', StringComparison.Ordinal)))
        {
            requestedRange = null;
        }
        if (Request.Headers.IfRange.Count > 0
            && !string.Equals(Request.Headers.IfRange.ToString(), applicationETag, StringComparison.Ordinal))
        {
            requestedRange = null;
        }
        var workerAccess = ReadWorkerAccess();
        if (workerAccess is not null
            && !await retrievalService.ReauthorizeWorkerAsync(artifact, User, workerAccess, cancellationToken).ConfigureAwait(false))
        {
            await WriteProblemAsync(StatusCodes.Status404NotFound, "Artifact was not found.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (!CentralArtifactByteRange.TryParse(requestedRange, artifact.ByteLength, out var range))
        {
            Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            Response.Headers.ContentRange = $"bytes */{artifact.ByteLength}";
            return;
        }
        if (!headOnly && artifact.Role == FrameArtifactRole.Raw
            && await retrievalService.RequiresDownloadAuthorizationAsync(
                artifact, User, cancellationToken).ConfigureAwait(false)
            && (!TryReadDownloadAuthorization(out var authorizationId, out var downloadToken)
                || !await retrievalService.ValidateDownloadAuthorizationAsync(
                    artifact,
                    User,
                    range,
                    authorizationId,
                    downloadToken,
                    cancellationToken).ConfigureAwait(false)))
        {
            await WriteProblemAsync(StatusCodes.Status404NotFound, "Artifact was not found.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        CentralArtifactObjectSnapshot snapshot;
        try
        {
            using var activity = CentralArtifactRetrievalTelemetry.StartActivity("central-artifact.verify");
            snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
        catch (CentralArtifactMissingException)
        {
            await retrievalService.MarkUnavailableAsync(
                artifact, "object.missing", quarantine: false, CancellationToken.None).ConfigureAwait(false);
            Log.Terminal(logger, devicePublicId, artifactId, "verify", "missing");
            await WriteProblemAsync(StatusCodes.Status503ServiceUnavailable, "Artifact content is temporarily unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            if (exception.StorageETag is not null)
            {
                try
                {
                    if (!await objectReader.IsCurrentGenerationAsync(
                            artifact, exception.StorageETag, CancellationToken.None).ConfigureAwait(false))
                    {
                        Log.Terminal(logger, devicePublicId, artifactId, "verify", "generation-changed");
                        await WriteProblemAsync(
                            StatusCodes.Status503ServiceUnavailable,
                            "Artifact content changed during verification.",
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
                catch (CentralArtifactStorageException)
                {
                    await WriteProblemAsync(
                        StatusCodes.Status503ServiceUnavailable,
                        "Artifact storage is temporarily unavailable.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            await retrievalService.MarkUnavailableAsync(
                artifact, exception.ReasonCode, quarantine: true, CancellationToken.None).ConfigureAwait(false);
            Log.Terminal(logger, devicePublicId, artifactId, "verify", "integrity-failed");
            await WriteProblemAsync(StatusCodes.Status409Conflict, "Artifact content failed integrity verification.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Terminal(logger, devicePublicId, artifactId, "verify", "cancelled");
            return;
        }
        catch (ObjectStoreException)
        {
            Log.Terminal(logger, devicePublicId, artifactId, "verify", "storage-unavailable");
            await WriteProblemAsync(StatusCodes.Status503ServiceUnavailable, "Artifact storage is temporarily unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (CentralArtifactStorageException)
        {
            Log.Terminal(logger, devicePublicId, artifactId, "verify", "storage-unavailable");
            await WriteProblemAsync(StatusCodes.Status503ServiceUnavailable, "Artifact storage is temporarily unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (MatchesIfNoneMatch(applicationETag))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            Response.Headers.ETag = applicationETag;
            return;
        }
        SetContentHeaders(artifact, range);
        if (headOnly)
        {
            return;
        }
        try
        {
            using var activity = CentralArtifactRetrievalTelemetry.StartActivity("central-artifact.stream");
            using var streamLease = telemetry.TrackStream();
            await objectReader.CopyToAsync(snapshot, Response.Body, range, cancellationToken).ConfigureAwait(false);
            Log.Terminal(logger, devicePublicId, artifactId, range is null ? "full" : "range", "completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client cancellation is not an object consistency failure.
            Log.Terminal(logger, devicePublicId, artifactId, range is null ? "full" : "range", "cancelled");
        }
        catch (ObjectStoreException)
        {
            Log.Terminal(logger, devicePublicId, artifactId, range is null ? "full" : "range", "storage-unavailable");
            await HandleStreamingFailureAsync().ConfigureAwait(false);
        }
        catch (CentralArtifactStorageException)
        {
            Log.Terminal(logger, devicePublicId, artifactId, range is null ? "full" : "range", "storage-unavailable");
            await HandleStreamingFailureAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            Log.Terminal(logger, devicePublicId, artifactId, range is null ? "full" : "range", "storage-unavailable");
            await HandleStreamingFailureAsync().ConfigureAwait(false);
        }
    }

    private Task<CentralArtifactLookup> FindAsync(Guid devicePublicId, Guid artifactId, CancellationToken cancellationToken)
        => retrievalService.FindAsync(devicePublicId, artifactId, User, ReadWorkerAccess(), cancellationToken);

    private CentralArtifactWorkerAccess? ReadWorkerAccess()
    {
        if (!Guid.TryParse(Request.Headers[JobIdHeader], out var jobId)
            || !Guid.TryParse(Request.Headers[LeaseTokenHeader], out var leaseToken))
        {
            return null;
        }
        var runnerId = Request.Headers[RunnerIdHeader].ToString();
        if (!string.IsNullOrWhiteSpace(runnerId))
        {
            return runnerId.Length > 128 ? null : new CentralArtifactWorkerAccess(jobId, runnerId, leaseToken, runnerId);
        }
        var workerId = CentralArtifactCredentialAccess.GetSubject(User);
        return string.IsNullOrWhiteSpace(workerId) || workerId.Length > 256
            ? null
            : new CentralArtifactWorkerAccess(jobId, workerId, leaseToken);
    }

    private bool TryReadDownloadAuthorization(out Guid authorizationId, out string token)
    {
        authorizationId = default;
        token = string.Empty;
        if (!Request.Cookies.TryGetValue(DownloadAuthorizationCookie, out var value)) return false;
        var separator = value.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || !Guid.TryParse(value.AsSpan(0, separator), out authorizationId)) return false;
        token = value[(separator + 1)..];
        return token.Length != 0;
    }

    private void SetContentHeaders(CentralArtifact artifact, CentralArtifactByteRange? range)
    {
        Response.StatusCode = range is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
        Response.ContentType = artifact.MediaType;
        Response.ContentLength = range?.Length ?? artifact.ByteLength;
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.ETag = $"\"{artifact.ChecksumSha256.ToUpperInvariant()}\"";
        Response.Headers[ChecksumHeader] = artifact.ChecksumSha256;
        Response.Headers.ContentDisposition = $"attachment; filename=\"{artifact.ArtifactId:D}.bin\"";
        Response.Headers.XContentTypeOptions = "nosniff";
        SetPrivateCacheHeaders();
        if (range is not null)
        {
            Response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{artifact.ByteLength}";
        }
    }

    private static CentralArtifactMetadata Project(CentralArtifact artifact)
    {
        var frame = artifact.Frame!;
        return new CentralArtifactMetadata(
            frame.DevicePublicId,
            artifact.ArtifactId,
            frame.FrameId,
            frame.AgentId,
            frame.RigId,
            frame.CaptureSequence,
            artifact.Role.ToString(),
            artifact.Variant,
            artifact.RecipeVersion,
            artifact.MediaType,
            artifact.ByteLength,
            artifact.ChecksumSha256,
            $"\"{artifact.ChecksumSha256.ToUpperInvariant()}\"",
            $"/api/v1.0/devices/{frame.DevicePublicId:D}/artifacts/{artifact.ArtifactId:D}/content",
            frame.CapturedAtUtc,
            artifact.ReceivedAtUtc);
    }

    private ActionResult LookupError(CentralArtifactLookupStatus status)
        => status switch
        {
            CentralArtifactLookupStatus.NotFound => NotFound(),
            CentralArtifactLookupStatus.Gone => StatusCode(StatusCodes.Status410Gone,
                new ProblemDetails { Status = StatusCodes.Status410Gone, Title = "Artifact content has expired." }),
            CentralArtifactLookupStatus.Pending => StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Title = "Artifact content is pending." }),
            _ => Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Artifact content is unavailable." })
        };

    private async Task WriteLookupErrorAsync(CentralArtifactLookupStatus status, CancellationToken cancellationToken)
    {
        var code = status switch
        {
            CentralArtifactLookupStatus.NotFound => StatusCodes.Status404NotFound,
            CentralArtifactLookupStatus.Gone => StatusCodes.Status410Gone,
            CentralArtifactLookupStatus.Pending => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status409Conflict
        };
        await WriteProblemAsync(code, code == StatusCodes.Status404NotFound
            ? "Artifact was not found."
            : "Artifact content is unavailable.", cancellationToken).ConfigureAwait(false);
    }

    private Task WriteProblemAsync(int status, string title, CancellationToken cancellationToken)
    {
        Response.StatusCode = status;
        SetPrivateCacheHeaders();
        return Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title }, cancellationToken);
    }

    private async Task HandleStreamingFailureAsync()
    {
        if (Response.HasStarted)
        {
            HttpContext.Abort();
            return;
        }
        Response.Headers.Clear();
        Response.ContentLength = null;
        await WriteProblemAsync(
            StatusCodes.Status503ServiceUnavailable,
            "Artifact storage is temporarily unavailable.",
            CancellationToken.None).ConfigureAwait(false);
    }

    private void SetPrivateCacheHeaders()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Vary = "Authorization, X-API-Key, Cookie";
    }

    private bool MatchesIfNoneMatch(string applicationETag)
        => Request.Headers.IfNoneMatch
            .SelectMany(value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Select(static value => value.Trim())
            .Any(value => value == "*"
                || string.Equals(
                    value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..].Trim() : value,
                    applicationETag,
                    StringComparison.Ordinal));

    internal sealed record CentralArtifactMetadata(
        Guid DevicePublicId,
        Guid ArtifactId,
        Guid FrameId,
        string AgentId,
        string? RigId,
        long? CaptureSequence,
        string Role,
        string? Variant,
        string RecipeVersion,
        string ContentType,
        long ByteLength,
        string ChecksumSha256,
        string ETag,
        string ContentUri,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset ReceivedAtUtc);

    internal sealed record DownloadAuthorizationRequest(string? Range);

    internal sealed record DownloadAuthorizationResponse(
        Guid AuthorizationId,
        DateTimeOffset ExpiresAtUtc,
        string ContentUri);

    private static partial class Log
    {
        [LoggerMessage(2101, LogLevel.Information,
            "Central artifact retrieval: DevicePublicId={DevicePublicId}, ArtifactId={ArtifactId}, Operation={Operation}, Outcome={Outcome}")]
        public static partial void Terminal(
            ILogger logger,
            Guid devicePublicId,
            Guid artifactId,
            string operation,
            string outcome);
    }
}
