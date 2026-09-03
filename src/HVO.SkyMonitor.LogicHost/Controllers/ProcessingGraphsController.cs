using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/processing-graphs")]
internal sealed class ProcessingGraphsController(
    IProcessingGraphCatalogService catalog,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("revisions")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<IReadOnlyList<CentralProcessingGraphRevisionView>>> ListRevisionsAsync(
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 100)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 100." });
        }
        return Ok(await catalog.ListRevisionsAsync(take, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("revisions")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    [RequestSizeLimit(ProcessingGraphJson.MaximumDocumentBytes + 16 * 1024)]
    public async Task<ActionResult<CentralProcessingGraphRevisionView>> CreateRevisionAsync(
        JsonElement definition,
        CancellationToken cancellationToken)
    {
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(definition.GetRawText()));
        if (!parsed.IsValid)
        {
            return BadRequest(new ProblemDetails { Title = "The processing graph definition is invalid." });
        }
        return ToActionResult(await catalog.CreateRevisionAsync(
            parsed.Definition!,
            GetActor(),
            User.IsInRole(AuthorizationRoleNames.PlatformEditor),
            cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("revisions/{revisionId:guid}/publish")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    public async Task<ActionResult<CentralProcessingGraphRevisionView>> PublishRevisionAsync(
        Guid revisionId,
        CancellationToken cancellationToken)
        => ToActionResult(await catalog.PublishRevisionAsync(
            revisionId,
            GetActor(),
            User.IsInRole(AuthorizationRoleNames.PlatformEditor),
            cancellationToken).ConfigureAwait(false));

    [HttpPost("revisions/{revisionId:guid}/retire")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    public async Task<ActionResult<CentralProcessingGraphRevisionView>> RetireRevisionAsync(
        Guid revisionId,
        ProcessingGraphRetirementRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await catalog.RetireRevisionAsync(
            revisionId,
            GetActor(),
            request.ReasonCode,
            User.IsInRole(AuthorizationRoleNames.PlatformEditor),
            cancellationToken).ConfigureAwait(false));

    [HttpPost("assignments")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
    public async Task<ActionResult<CentralProcessingGraphAssignmentView>> AssignAsync(
        CentralProcessingGraphAssignmentRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await catalog.AssignAsync(
            request,
            GetActor(),
            User.IsInRole(AuthorizationRoleNames.PlatformEditor),
            CentralArtifactCredentialAccess.GetObservatoryScope(User),
            cancellationToken).ConfigureAwait(false));

    [HttpGet("assignments/effective")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<CentralProcessingGraphAssignmentView>> ResolveAsync(
        [FromQuery] string targetHost,
        [FromQuery] Guid observatoryId,
        [FromQuery] Guid? logicalCameraId = null,
        [FromQuery] DateTimeOffset? effectiveAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<CentralProcessingGraphTargetHost>(targetHost, ignoreCase: false, out var parsedTargetHost) ||
            !Enum.IsDefined(parsedTargetHost) ||
            !string.Equals(targetHost, parsedTargetHost.ToString(), StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails { Title = "targetHost must be a defined string value." });
        }
        var result = await catalog.ResolveForUserAsync(
            parsedTargetHost,
            observatoryId,
            logicalCameraId,
            effectiveAtUtc ?? timeProvider.GetUtcNow(),
            GetActor(),
            User.IsInRole(AuthorizationRoleNames.PlatformEditor),
            CentralArtifactCredentialAccess.GetObservatoryScope(User),
            cancellationToken).ConfigureAwait(false);
        return result is null ? NotFound() : Ok(result);
    }

    private string GetActor()
        => CentralArtifactCredentialAccess.GetOwnerId(User) ?? string.Empty;

    private ActionResult<T> ToActionResult<T>(CentralProcessingGraphMutationResult<T> result)
        => result.Outcome switch
        {
            CentralProcessingGraphMutationOutcome.Applied => Ok(result.Value),
            CentralProcessingGraphMutationOutcome.Unchanged => Ok(result.Value),
            CentralProcessingGraphMutationOutcome.Invalid =>
                BadRequest(new ProblemDetails { Title = "The processing graph request is invalid." }),
            CentralProcessingGraphMutationOutcome.Conflict =>
                Conflict(new ProblemDetails { Title = "The processing graph request conflicts with durable state." }),
            CentralProcessingGraphMutationOutcome.NotFoundOrDenied => NotFound(),
            _ => throw new InvalidOperationException("Unknown processing graph mutation outcome.")
        };

    internal sealed record ProcessingGraphRetirementRequest(
        [Required, StringLength(128, MinimumLength = 1)] string ReasonCode);
}
