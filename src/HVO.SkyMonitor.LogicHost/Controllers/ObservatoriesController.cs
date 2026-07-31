using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/observatories")]
internal sealed class ObservatoriesController(IObservatoryService observatoryService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicyNames.ApiKeyRead)]
    public async Task<ActionResult<IReadOnlyList<ObservatoryResponse>>> GetAsync(CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var items = await observatoryService.GetObservatoriesAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (GetApiKeyObservatoryId() is { } scopeId)
        {
            items = items.Where(item => item.Id == scopeId).ToList();
        }
        var response = items.Select(ToResponse).ToList();
        return Ok(response);
    }

    [HttpPost]
    [Authorize(Policy = "OwnerLocationWrite")]
    public async Task<ActionResult<ObservatoryResponse>> UpsertAsync(
        ObservatoryRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }
        string? expectedRepresentationSha256 = null;
        if (request.Id.HasValue)
        {
            if (Request.Headers.IfMatch.Count == 0)
            {
                return StatusCode(StatusCodes.Status428PreconditionRequired,
                    new ProblemDetails { Title = "If-Match is required when updating an Observatory." });
            }
            if (!TryParseEtag(Request.Headers.IfMatch.ToString(), out expectedRepresentationSha256))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "If-Match must contain one current strong Observatory location ETag."
                });
            }
        }

        var ownerId = GetOwnerId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }
        if (GetApiKeyObservatoryId() is { } scopeId
            && (request.Id is null || scopeId != request.Id))
        {
            return Forbid();
        }

        Observatory entity;
        try
        {
            entity = await observatoryService.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
                request.Id,
                ownerId,
                request.Name,
                request.LatitudeDegrees,
                request.LongitudeDegrees,
                request.ElevationMeters,
                request.TimeZoneId,
                request.IsActive,
                request.AllowedDeploymentRadiusMeters,
                expectedRepresentationSha256), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return ValidationProblem(ModelState);
        }
        catch (ObservatoryConcurrencyException)
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed,
                new ProblemDetails { Title = "The Observatory location ETag is stale." });
        }

        Response.Headers.ETag = CreateEtag(ObservatoryService.CreateRepresentationSha256(entity));
        return Ok(ToResponse(entity));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "OwnerLocationWrite")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }
        if (GetApiKeyObservatoryId() is { } scopeId && scopeId != id)
        {
            return Forbid();
        }

        var deleted = await observatoryService.DeleteAsync(id, ownerId, cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
    }

    private static ObservatoryResponse ToResponse(ObservatorySummary summary)
    {
        return new ObservatoryResponse(
            summary.Id,
            summary.Name,
            summary.LatitudeDegrees,
            summary.LongitudeDegrees,
            summary.ElevationMeters,
            summary.TimeZoneId,
            summary.AllowedDeploymentRadiusMeters,
            summary.CurrentLocationVersion,
            summary.CurrentLocationCanonicalSha256,
            CreateEtag(ObservatoryService.CreateRepresentationSha256(summary)),
            summary.IsActive,
            summary.CreatedAtUtc,
            summary.UpdatedAtUtc);
    }

    private static ObservatoryResponse ToResponse(Data.Observatory entity)
    {
        return new ObservatoryResponse(
            entity.Id,
            entity.Name,
            entity.LatitudeDegrees,
            entity.LongitudeDegrees,
            entity.ElevationMeters,
            entity.TimeZoneId,
            entity.AllowedDeploymentRadiusMeters,
            entity.CurrentLocationVersion,
            entity.CurrentLocationCanonicalSha256,
            CreateEtag(ObservatoryService.CreateRepresentationSha256(entity)),
            entity.IsActive,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }

    private string? GetOwnerId()
    {
        if (CentralArtifactCredentialAccess.GetSingleCredentialIdentity(User) is null
            || CentralArtifactCredentialAccess.IsSystem(User))
        {
            return null;
        }
        return CentralArtifactCredentialAccess.GetOwnerId(User);
    }

    private Guid? GetApiKeyObservatoryId()
        => Guid.TryParse(User.FindFirst(ApiKeyClaims.ObservatoryId)?.Value, out var observatoryId)
            ? observatoryId
            : null;

    private static string CreateEtag(string sha256) => $"\"{sha256.ToUpperInvariant()}\"";

    private static bool TryParseEtag(string value, out string? sha256)
    {
        sha256 = null;
        if (value.Length != 66 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }
        var hash = value[1..^1];
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }
        sha256 = hash;
        return true;
    }

    internal sealed record ObservatoryRequest(
        Guid? Id,
        [Required, StringLength(200)] string Name,
        [Range(-90, 90)] double LatitudeDegrees,
        [Range(-180, 180)] double LongitudeDegrees,
        [Range(-1000, 10000)] double ElevationMeters,
        [Required, StringLength(128)] string TimeZoneId,
        bool IsActive = true,
        [Range(0, 1000000)] double? AllowedDeploymentRadiusMeters = null);

    internal sealed record ObservatoryResponse(
        Guid Id,
        string Name,
        double LatitudeDegrees,
        double LongitudeDegrees,
        double ElevationMeters,
        string TimeZoneId,
        double? AllowedDeploymentRadiusMeters,
        long? CurrentLocationVersion,
        string? CurrentLocationCanonicalSha256,
        string ETag,
        bool IsActive,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc);
}
