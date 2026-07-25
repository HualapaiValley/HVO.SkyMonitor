using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/deployment-location-proposals")]
internal sealed class DeploymentLocationProposalsController(
    IDeploymentLocationAuthorityService authorityService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<IReadOnlyList<DeploymentLocationProposalResponse>>> ListAsync(
        [FromQuery] DeploymentLocationResolutionStatus? status = null,
        [FromQuery] int take = 100,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetOwnerId(out var ownerUserId))
        {
            return Forbid();
        }
        if (take is < 1 or > 100)
        {
            return BadRequest(new ProblemDetails { Title = "take must be between 1 and 100." });
        }
        if (!TryParseCursor(cursor, out var parsedCursor))
        {
            return BadRequest(new ProblemDetails { Title = "cursor is invalid." });
        }
        var page = await authorityService.ListAsync(ownerUserId, status, take, parsedCursor, cancellationToken)
            .ConfigureAwait(false);
        if (page.NextCursor is not null)
        {
            Response.Headers["X-Next-Cursor"] = CreateCursor(page.NextCursor);
        }
        return Ok(page.Proposals.Select(ToResponse).ToArray());
    }

    [HttpGet("{deploymentLocationId:guid}")]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<DeploymentLocationProposalResponse>> GetAsync(
        Guid deploymentLocationId,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(out var ownerUserId))
        {
            return Forbid();
        }
        var proposal = await authorityService.GetAsync(deploymentLocationId, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        if (proposal is null)
        {
            return NotFound();
        }
        Response.Headers.ETag = DeploymentLocationEtag.Create(proposal.ConcurrencyToken);
        return Ok(ToResponse(proposal));
    }

    [HttpPost("{deploymentLocationId:guid}/resolution")]
    [Authorize(Policy = "OwnerLocationWrite")]
    public async Task<ActionResult<DeploymentLocationProposalResponse>> ResolveAsync(
        Guid deploymentLocationId,
        [FromBody] DeploymentLocationResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetOwnerId(out var ownerUserId))
        {
            return Forbid();
        }
        var ifMatchValues = Request.Headers.IfMatch;
        if (ifMatchValues.Count != 1)
        {
            return StatusCode(StatusCodes.Status428PreconditionRequired,
                new ProblemDetails { Title = "If-Match is required." });
        }
        if (!DeploymentLocationEtag.TryParse(ifMatchValues[0]!, out var expectedConcurrencyToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "If-Match must contain one current strong deployment-location ETag."
            });
        }
        if (request.Status == DeploymentLocationResolutionStatus.Pending)
        {
            return BadRequest(new ProblemDetails { Title = "Resolution must acknowledge or reject the deployment." });
        }

        DeploymentLocationResolutionResult result;
        try
        {
            result = await authorityService.ResolveAsync(
                deploymentLocationId,
                ownerUserId,
                request.Status,
                request.Reason,
                expectedConcurrencyToken,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return BadRequest(new ProblemDetails { Title = "The deployment-location resolution is invalid." });
        }
        if (result.Proposal is { } proposal)
        {
            Response.Headers.ETag = DeploymentLocationEtag.Create(proposal.ConcurrencyToken);
        }
        return result.Status switch
        {
            DeploymentLocationMutationStatus.Applied => Ok(ToResponse(result.Proposal!)),
            DeploymentLocationMutationStatus.NotFound => NotFound(),
            DeploymentLocationMutationStatus.PreconditionFailed =>
                StatusCode(StatusCodes.Status412PreconditionFailed,
                    new ProblemDetails { Title = "The deployment-location ETag is stale." }),
            DeploymentLocationMutationStatus.InvalidTransition =>
                Conflict(new ProblemDetails { Title = "The deployment location was already resolved." }),
            DeploymentLocationMutationStatus.StaleAuthority =>
                Conflict(new ProblemDetails { Title = "The Observatory location changed; resolve the current proposal instead." }),
            _ => throw new InvalidOperationException("Unknown deployment-location resolution result.")
        };
    }

    private bool TryGetOwnerId(out string ownerUserId)
    {
        ownerUserId = string.Empty;
        if (CentralArtifactCredentialAccess.GetSingleCredentialIdentity(User) is null
            || CentralArtifactCredentialAccess.IsSystem(User))
        {
            return false;
        }
        ownerUserId = CentralArtifactCredentialAccess.GetOwnerId(User) ?? string.Empty;
        return ownerUserId.Length > 0;
    }

    private static string CreateCursor(DeploymentLocationProposalCursor cursor)
        => WebEncoders.Base64UrlEncode(Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.ProposedAtUtc.UtcTicks:D19}.{cursor.Id:N}")));

    private static bool TryParseCursor(string? value, out DeploymentLocationProposalCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }
        try
        {
            var decoded = Encoding.ASCII.GetString(WebEncoders.Base64UrlDecode(value));
            if (decoded.Length != 52 || decoded[19] != '.'
                || !long.TryParse(decoded.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
                || !Guid.TryParseExact(decoded.AsSpan(20), "N", out var id)
                || utcTicks < DateTimeOffset.MinValue.UtcTicks
                || utcTicks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }
            cursor = new DeploymentLocationProposalCursor(
                new DateTimeOffset(utcTicks, TimeSpan.Zero),
                id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DeploymentLocationProposalResponse ToResponse(DeploymentLocationProposal proposal)
        => new(
            proposal.Id,
            proposal.RegistrationId,
            proposal.DeviceId,
            proposal.FriendlyName,
            proposal.ObservatoryId,
            proposal.ObservatoryName,
            proposal.ObservatoryLocation,
            proposal.DeploymentLocation,
            proposal.SourceKind,
            proposal.Status,
            proposal.ReasonCode,
            proposal.ProposedAtUtc,
            proposal.ResolvedAtUtc,
            DeploymentLocationEtag.Create(proposal.ConcurrencyToken));

    internal sealed record DeploymentLocationResolutionRequest(
        DeploymentLocationResolutionStatus Status,
        [Required, StringLength(128, MinimumLength = 1)] string Reason);

    internal sealed record DeploymentLocationProposalResponse(
        Guid Id,
        Guid RegistrationId,
        string DeviceId,
        string FriendlyName,
        Guid ObservatoryId,
        string ObservatoryName,
        ObservatoryLocationSnapshot ObservatoryLocation,
        DeploymentLocationSnapshot DeploymentLocation,
        DeploymentLocationSourceKind SourceKind,
        DeploymentLocationResolutionStatus Status,
        string? ReasonCode,
        DateTimeOffset ProposedAtUtc,
        DateTimeOffset? ResolvedAtUtc,
        string ETag);
}
