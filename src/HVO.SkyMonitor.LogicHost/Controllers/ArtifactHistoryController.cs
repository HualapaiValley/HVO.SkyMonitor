using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.AgentCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>Provides bounded central history queries over durably ingested artifact metadata.</summary>
[ApiController]
[Route("api/v1.0/artifacts")]
[Authorize(Policy = "ArtifactRetrieval")]
internal sealed class ArtifactHistoryController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtifactHistoryItem>>> ListAsync(
        [FromQuery] string? agentId,
        [FromQuery] string? role,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(User);
        if (string.IsNullOrWhiteSpace(ownerId) || !CentralArtifactCredentialAccess.HasOwnerCredential(User))
        {
            return Forbid();
        }
        if (take is < 1 or > 500)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 500" });
        }

        FrameArtifactRole? parsedRole = null;
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<FrameArtifactRole>(role, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return BadRequest(new ProblemDetails { Title = "role is invalid" });
            }
            parsedRole = parsed;
        }

        var centralQuery = dbContext.CentralArtifacts.AsNoTracking().Where(artifact =>
            dbContext.DeviceRegistrations.Any(registration => registration.Id == artifact.Frame!.RegistrationId
                && registration.OwnerUserId == ownerId));
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            centralQuery = centralQuery.Where(artifact => artifact.Frame!.AgentId == agentId);
        }
        if (parsedRole is not null)
        {
            centralQuery = centralQuery.Where(artifact => artifact.Role == parsedRole);
        }
        var central = await centralQuery.Include(artifact => artifact.Frame)
            .OrderByDescending(artifact => artifact.Frame!.CapturedAtUtc)
            .ThenByDescending(artifact => artifact.ArtifactId).Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var legacyQuery = dbContext.DeviceImageUploads.AsNoTracking().Where(upload =>
            dbContext.DeviceRegistrations.Any(registration => registration.Id == upload.RegistrationId
                && registration.OwnerUserId == ownerId)
            && (upload.IdempotencyKey == null
                || !dbContext.CentralArtifacts.Any(artifact => artifact.IdempotencyKey == upload.IdempotencyKey)));
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            legacyQuery = legacyQuery.Where(upload => upload.AgentId == agentId);
        }
        if (parsedRole is not null)
        {
            var roleName = parsedRole.Value.ToString();
            legacyQuery = legacyQuery.Where(upload => upload.ArtifactRole == roleName);
        }
        var legacy = await legacyQuery.OrderByDescending(upload => upload.CapturedAtUtc)
            .ThenByDescending(upload => upload.ArtifactId).Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var results = central.Select(artifact => new ArtifactHistoryItem(
                artifact.ArtifactId, artifact.Role.ToString(), artifact.Frame!.CapturedAtUtc, artifact.ReceivedAtUtc,
                artifact.MediaType, artifact.ByteLength, artifact.ChecksumSha256,
                artifact.DevicePublicId,
                artifact.DevicePublicId.HasValue
                    && artifact.ObjectState == CentralArtifactObjectState.Available
                    && artifact.ReconstructionState == CentralReconstructionState.Complete
                        ? $"/api/v1.0/devices/{artifact.DevicePublicId:D}/artifacts/{artifact.ArtifactId:D}/content"
                        : null,
                artifact.Frame.RigProfileVersion))
            .Concat(legacy.Select(upload => new ArtifactHistoryItem(
                upload.ArtifactId, upload.ArtifactRole, upload.CapturedAtUtc, upload.ReceivedAtUtc,
                upload.ContentType, upload.ByteLength, upload.ChecksumSha256,
                upload.DevicePublicId, null, upload.RigProfileVersion)))
            .OrderByDescending(item => item.CapturedAtUtc)
            .ThenByDescending(item => item.ArtifactId)
            .Take(take)
            .ToList();
        return Ok(results);
    }

    internal sealed record ArtifactHistoryItem(
        Guid? ArtifactId,
        string? Role,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset ReceivedAtUtc,
        string ContentType,
        long? ByteLength,
        string? ChecksumSha256,
        Guid? DevicePublicId,
        string? ContentUri,
        int? RigProfileVersion);
}
