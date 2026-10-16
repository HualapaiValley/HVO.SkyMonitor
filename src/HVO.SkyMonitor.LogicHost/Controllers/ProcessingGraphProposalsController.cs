using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
[Route("api/v1.0/observatories/{observatoryId:guid}/cameras/{logicalCameraId:guid}/installations/{installationId:guid}/processing-graph-proposals")]
internal sealed class ProcessingGraphProposalsController(IProcessingGraphProposalReadService readService) : ControllerBase
{
    [HttpGet("{proposalId:guid}")]
    public async Task<ActionResult<ProcessingGraphProposalReadResult>> GetAsync(
        Guid observatoryId,
        Guid logicalCameraId,
        Guid installationId,
        Guid proposalId,
        [FromQuery] int take = ProcessingGraphProposalReadService.MaximumFacts,
        CancellationToken cancellationToken = default)
    {
        if (!CentralArtifactCredentialAccess.HasOwnerCredential(User) ||
            string.IsNullOrWhiteSpace(CentralArtifactCredentialAccess.GetOwnerId(User)))
        {
            return Forbid();
        }
        if (take is < 1 or > ProcessingGraphProposalReadService.MaximumFacts)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 50." });
        }
        var result = await readService.GetAsync(
            User, observatoryId, logicalCameraId, installationId, proposalId, take, cancellationToken)
            .ConfigureAwait(false);
        Response.Headers.CacheControl = "private, no-store";
        return result is null ? NotFound() : Ok(result);
    }
}
