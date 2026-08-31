using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1.0/public/artifacts")]
internal sealed class PublicArtifactController(
    ApplicationDbContext dbContext,
    ICentralArtifactObjectReader objectReader) : ControllerBase
{
    [HttpGet("{publicId:guid}/content")]
    public async Task GetAsync(Guid publicId, CancellationToken cancellationToken)
    {
        var artifact = await EligibleArtifacts(publicId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        CentralArtifactObjectSnapshot snapshot;
        try
        {
            snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectStoreException
            or CentralArtifactStorageException
            or CentralArtifactIntegrityException)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (!await EligibleArtifacts(publicId).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var etag = $"\"{artifact.ChecksumSha256.ToUpperInvariant()}\"";
        Response.Headers.CacheControl = "public, no-cache, must-revalidate";
        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            Response.Headers.ETag = etag;
            return;
        }
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = artifact.MediaType;
        Response.ContentLength = artifact.ByteLength;
        Response.Headers.ETag = etag;
        Response.Headers.ContentDisposition = $"inline; filename=\"{publicId:D}\"";
        Response.Headers.XContentTypeOptions = "nosniff";
        await objectReader.CopyToAsync(snapshot, Response.Body, null, cancellationToken).ConfigureAwait(false);
    }

    private IQueryable<CentralArtifact> EligibleArtifacts(Guid publicId)
        => dbContext.PublicRecordPublicationDecisions.AsNoTracking()
            .Where(decision => decision.PublicId == publicId
                && decision.SubjectKind == PublicRecordSubjectKind.Artifact
                && decision.State == PublicationDecisionState.Released
                && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id)
                && dbContext.ObservatoryPublicationProfileVersions.Any(profile =>
                    profile.ObservatoryId == decision.AuthorityObservatoryId
                    && profile.SupersededAtUtc == null
                    && profile.ProfileVisibility == ObservatoryProfileVisibility.Public
                    && profile.Observatory!.IsActive))
            .Join(dbContext.CentralArtifacts.AsNoTracking().Where(artifact =>
                    artifact.ObjectState == CentralArtifactObjectState.Available
                    && artifact.ReconstructionState == CentralReconstructionState.Complete
                    && (artifact.Role == FrameArtifactRole.Preview
                        || artifact.Role == FrameArtifactRole.AnnotatedPreview)
                    && (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png"
                        || artifact.MediaType == "image/webp")),
                decision => decision.CentralArtifactId,
                artifact => artifact.Id,
                (_, artifact) => artifact);
}
