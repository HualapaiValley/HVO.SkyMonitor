using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/processing-graph-executions")]
internal sealed class ProcessingGraphExecutionsController(
    ICentralProcessingGraphExecutionService executionService,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    public async Task<ActionResult<CentralProcessingGraphExecutionDetail>> ScheduleAsync(
        ProcessingGraphReplayApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCaller(out var actorId, out var observatoryScope, mutation: true))
        {
            return Forbid();
        }
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var keys) || keys.Count != 1 ||
            string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 256)
        {
            return BadRequest(new ProblemDetails { Title = "Exactly one bounded Idempotency-Key is required." });
        }
        var result = await executionService.ScheduleReplayAsync(
            request.RevisionId,
            request.SourceArtifactIds,
            actorId,
            observatoryScope,
            keys[0]!,
            request.ReasonCode,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == CentralProcessingGraphScheduleOutcome.Conflict)
        {
            return Conflict(new ProblemDetails { Title = "Idempotency-Key was used for a different replay." });
        }
        if (result.Execution is null)
        {
            return result.ReasonCode == "sources-not-found-or-denied"
                ? NotFound()
                : BadRequest(new ProblemDetails { Title = "The processing graph replay request is invalid." });
        }
        var detail = await executionService.GetAsync(
            result.Execution.Id, actorId, observatoryScope, cancellationToken).ConfigureAwait(false);
        return result.Outcome == CentralProcessingGraphScheduleOutcome.Created
            ? CreatedAtAction(nameof(GetAsync), new { executionId = result.Execution.Id }, detail)
            : Ok(detail);
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<IReadOnlyList<CentralProcessingGraphExecutionSummary>>> ListAsync(
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCaller(out var actorId, out var observatoryScope))
        {
            return Forbid();
        }
        if (take is < 1 or > 100)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 100." });
        }
        return Ok(await executionService.ListAsync(actorId, observatoryScope, take, cancellationToken)
            .ConfigureAwait(false));
    }

    [HttpGet("{executionId:guid}")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<CentralProcessingGraphExecutionDetail>> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCaller(out var actorId, out var observatoryScope))
        {
            return Forbid();
        }
        var result = await executionService.GetAsync(
            executionId, actorId, observatoryScope, cancellationToken).ConfigureAwait(false);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{executionId:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    public async Task<IActionResult> CancelAsync(Guid executionId, CancellationToken cancellationToken)
    {
        if (!TryGetCaller(out var actorId, out var observatoryScope, mutation: true))
        {
            return Forbid();
        }
        var result = await executionService.CancelAsync(
            executionId, actorId, observatoryScope, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            CentralProcessingGraphCancellationOutcome.Applied => Accepted(),
            CentralProcessingGraphCancellationOutcome.Unchanged => NoContent(),
            CentralProcessingGraphCancellationOutcome.NotFoundOrDenied => NotFound(),
            _ => throw new InvalidOperationException("The graph cancellation outcome is unsupported.")
        };
    }

    /// <summary>
    /// Mutation actions are already authorized by <see cref="AuthorizationPolicyNames.ApiKeyReadWrite"/>, which
    /// accepts a bearer carrying only <c>api.owner.write</c>; the caller check honors that same write credential
    /// instead of demanding the read scopes the read-only actions require.
    /// </summary>
    private bool TryGetCaller(out string actorId, out Guid? observatoryScope, bool mutation = false)
    {
        actorId = CentralArtifactCredentialAccess.GetOwnerId(User) ?? string.Empty;
        observatoryScope = CentralArtifactCredentialAccess.GetObservatoryScope(User);
        return actorId.Length > 0 && (mutation
            ? CentralArtifactCredentialAccess.HasOwnerWriteCredential(User)
            : CentralArtifactCredentialAccess.HasOwnerCredential(User));
    }

    internal sealed record ProcessingGraphReplayApiRequest(
        Guid RevisionId,
        [property: Required, MinLength(1), MaxLength(ProcessingGraphCompiler.MaximumSources)]
        IReadOnlyList<Guid> SourceArtifactIds,
        [property: Required, StringLength(128, MinimumLength = 1)] string ReasonCode);
}
