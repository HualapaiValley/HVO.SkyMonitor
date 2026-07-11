using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>Provides bounded central history queries over durably ingested artifact metadata.</summary>
[ApiController]
[Route("api/v1.0/artifacts")]
[Authorize(AuthenticationSchemes = "Bearer")]
internal sealed class ArtifactHistoryController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ArtifactHistoryItem>>> ListAsync(
        [FromQuery] string? agentId,
        [FromQuery] string? role,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 500" });
        }

        var query = dbContext.DeviceImageUploads.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            query = query.Where(upload => upload.IdempotencyKey != null && upload.StorageReference.Contains($"/{agentId}/"));
        }
        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(upload => upload.ArtifactRole == role);
        }

        var results = await query.OrderByDescending(upload => upload.CapturedAtUtc).Take(take)
            .Select(upload => new ArtifactHistoryItem(
                upload.ArtifactId, upload.ArtifactRole, upload.CapturedAtUtc, upload.ReceivedAtUtc,
                upload.ContentType, upload.ByteLength, upload.ChecksumSha256, upload.StorageReference, upload.RigProfileVersion))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
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
        string StorageReference,
        int? RigProfileVersion);
}
