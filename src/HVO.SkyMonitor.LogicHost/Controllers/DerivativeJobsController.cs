using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using System.Security.Claims;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/derivative-jobs")]
[Authorize(AuthenticationSchemes = "Bearer", Policy = "DerivativeJobsRead")]
internal sealed class DerivativeJobsController(
    ApplicationDbContext dbContext,
    ICentralDerivativeJobOperationsService operations) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DerivativeJobItem>>> ListAsync(
        [FromQuery] string? agentId,
        [FromQuery] string? status,
        [FromQuery] string? targetRole,
        [FromQuery] Guid? sourceArtifactId,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 500)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 500" });
        }
        if (!TryParseEnum(status, out CentralDerivativeJobStatus? parsedStatus)
            || !TryParseEnum(targetRole, out FrameArtifactRole? parsedTargetRole))
        {
            return BadRequest(new ProblemDetails { Title = "status or targetRole is invalid" });
        }

        var query = dbContext.CentralDerivativeJobs.AsNoTracking()
            .Include(job => job.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(job => job.ResultArtifact)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            query = query.Where(job => job.SourceArtifact!.Frame!.AgentId == agentId);
        }
        if (parsedStatus is not null)
        {
            query = query.Where(job => job.Status == parsedStatus);
        }
        if (parsedTargetRole is not null)
        {
            query = query.Where(job => job.TargetRole == parsedTargetRole);
        }
        if (sourceArtifactId is not null)
        {
            query = query.Where(job => job.SourceArtifact!.ArtifactId == sourceArtifactId);
        }

        var jobs = await query.OrderByDescending(job => job.CreatedAtUtc)
            .ThenByDescending(job => job.Id)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(jobs.Select(Project).ToList());
    }

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await operations.CancelAsync(jobId, GetActor(), cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (CentralDerivativeJobStateException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message });
        }
    }

    [HttpPost("{jobId:guid}/requeue")]
    public async Task<IActionResult> RequeueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await operations.RequeueAsync(jobId, GetActor(), cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (CentralDerivativeJobStateException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message });
        }
    }

    [HttpPost("{jobId:guid}/reprocess")]
    public async Task<ActionResult<ReprocessResponse>> ReprocessAsync(
        Guid jobId,
        [FromBody] CentralDerivativeReprocessRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RecipeName)
            || request.RecipeName.Length > 128
            || request.RecipeName is not (BuiltInProcessingRecipes.EncodedPreview
                or BuiltInProcessingRecipes.Annotation
                or BuiltInProcessingRecipes.ImageQuality
                or BuiltInProcessingRecipes.RollingMean)
            || string.IsNullOrWhiteSpace(request.OutputVariant)
            || request.OutputVariant.Length > 128
            || request.Options.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new ProblemDetails { Title = "The reprocess request is invalid." });
        }
        try
        {
            var newJobId = await operations.ReprocessAsync(jobId, request, GetActor(), cancellationToken)
                .ConfigureAwait(false);
            return Ok(new ReprocessResponse(newJobId));
        }
        catch (CentralDerivativeJobStateException exception)
        {
            return Conflict(new ProblemDetails { Title = exception.Message });
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return BadRequest(new ProblemDetails { Title = "The recipe options are invalid." });
        }
    }

    private static DerivativeJobItem Project(CentralDerivativeJob job)
    {
        var source = job.SourceArtifact!;
        var frame = source.Frame!;
        return new DerivativeJobItem(
            job.Id, job.Status.ToString(), source.ArtifactId, frame.FrameId, frame.AgentId,
            frame.CapturedAtUtc, source.Role.ToString(), source.RecipeVersion,
            job.TargetRole.ToString(), job.TargetRecipeVersion, job.AttemptCount, job.MaxAttempts,
            job.AvailableAtUtc, job.LeaseOwner, job.LeaseAcquiredAtUtc, job.LeaseExpiresAtUtc,
            job.CreatedAtUtc, job.UpdatedAtUtc, job.LastFailedAtUtc, job.LastError,
            job.CompletedAtUtc, job.ResultArtifact?.ArtifactId, frame.RigProfileVersion);
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var candidate) || !Enum.IsDefined(candidate))
        {
            return false;
        }
        parsed = candidate;
        return true;
    }

    private string GetActor()
        => User.FindFirstValue("sub")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "unknown";

    internal sealed record DerivativeJobItem(
        Guid JobId,
        string Status,
        Guid SourceArtifactId,
        Guid FrameId,
        string AgentId,
        DateTimeOffset CapturedAtUtc,
        string SourceRole,
        string SourceRecipeVersion,
        string TargetRole,
        string TargetRecipeVersion,
        int AttemptCount,
        int MaxAttempts,
        DateTimeOffset? AvailableAtUtc,
        string? LeaseOwner,
        DateTimeOffset? LeaseAcquiredAtUtc,
        DateTimeOffset? LeaseExpiresAtUtc,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? LastFailedAtUtc,
        string? LastError,
        DateTimeOffset? CompletedAtUtc,
        Guid? ResultArtifactId,
        int? RigProfileVersion);

    internal sealed record ReprocessResponse(Guid JobId);
}
