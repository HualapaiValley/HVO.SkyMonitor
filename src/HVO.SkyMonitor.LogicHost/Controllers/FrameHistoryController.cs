using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.LogicHost.Controllers;

/// <summary>Provides bounded frame-oriented queries over normalized central ingest records.</summary>
[ApiController]
[Route("api/v1.0/frames")]
[Authorize(AuthenticationSchemes = "Bearer")]
internal sealed class FrameHistoryController(ApplicationDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CentralFrameHistoryItem>>> ListAsync(
        [FromQuery] string? agentId,
        [FromQuery] string? role,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(role, take);
        if (validation.Error is not null)
        {
            return BadRequest(validation.Error);
        }

        var frames = await ApplyFilters(dbContext.CentralFrames.AsNoTracking(), agentId, validation.Role)
            .Include(frame => frame.Artifacts)
            .OrderByDescending(frame => frame.CapturedAtUtc)
            .ThenByDescending(frame => frame.FrameId)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(frames.Select(Project).ToList());
    }

    [HttpGet("latest")]
    public async Task<ActionResult<CentralFrameHistoryItem>> LatestAsync(
        [FromQuery] string? agentId,
        [FromQuery] string? role,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(role, 1);
        if (validation.Error is not null)
        {
            return BadRequest(validation.Error);
        }

        var frame = await ApplyFilters(dbContext.CentralFrames.AsNoTracking(), agentId, validation.Role)
            .Include(item => item.Artifacts)
            .OrderByDescending(frame => frame.CapturedAtUtc)
            .ThenByDescending(frame => frame.FrameId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return frame is null ? NotFound() : Ok(Project(frame));
    }

    private static IQueryable<CentralFrame> ApplyFilters(
        IQueryable<CentralFrame> query,
        string? agentId,
        FrameArtifactRole? role)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            query = query.Where(frame => frame.AgentId == agentId);
        }
        if (role is not null)
        {
            query = query.Where(frame => frame.Artifacts.Any(artifact => artifact.Role == role));
        }
        return query;
    }

    private static CentralFrameHistoryItem Project(CentralFrame frame)
        => new(
            frame.FrameId,
            frame.AgentId,
            frame.CapturedAtUtc,
            frame.FirstReceivedAtUtc,
            frame.RigProfileVersion,
            frame.SceneProvenanceJson,
            frame.Artifacts.OrderBy(artifact => artifact.Role).ThenBy(artifact => artifact.RecipeVersion)
                .Select(artifact => new CentralFrameArtifactItem(
                    artifact.ArtifactId, artifact.Role, artifact.RecipeVersion, artifact.ManifestSchemaVersion,
                    artifact.MediaType, artifact.ByteLength, artifact.ChecksumSha256,
                    artifact.StorageReference, artifact.ReceivedAtUtc))
                .ToList());

    private static (FrameArtifactRole? Role, ProblemDetails? Error) Validate(string? role, int take)
    {
        if (take is < 1 or > 500)
        {
            return (null, new ProblemDetails { Title = "take must be between 1 and 500" });
        }
        if (!string.IsNullOrWhiteSpace(role))
        {
            if (!Enum.TryParse<FrameArtifactRole>(role, ignoreCase: true, out var parsedRole)
                || !Enum.IsDefined(parsedRole))
            {
                return (null, new ProblemDetails { Title = "role is invalid" });
            }
            return (parsedRole, null);
        }
        return (null, null);
    }

    internal sealed record CentralFrameHistoryItem(
        Guid FrameId,
        string AgentId,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset FirstReceivedAtUtc,
        int? RigProfileVersion,
        string? SceneProvenanceJson,
        IReadOnlyList<CentralFrameArtifactItem> Artifacts);

    internal sealed record CentralFrameArtifactItem(
        Guid ArtifactId,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] FrameArtifactRole Role,
        string RecipeVersion,
        string ManifestSchemaVersion,
        string MediaType,
        long ByteLength,
        string ChecksumSha256,
        string StorageReference,
        DateTimeOffset ReceivedAtUtc);
}
