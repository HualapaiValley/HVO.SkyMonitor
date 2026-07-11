using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/observatories")]
[Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
internal sealed class ObservatoriesController(IObservatoryService observatoryService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ObservatoryResponse>>> GetAsync(CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var items = await observatoryService.GetObservatoriesAsync(ownerId, cancellationToken).ConfigureAwait(false);
        var response = items.Select(ToResponse).ToList();
        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<ObservatoryResponse>> UpsertAsync(
        ObservatoryRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var ownerId = GetOwnerId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var entity = await observatoryService.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            request.Id,
            ownerId,
            request.Name,
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            request.TimeZoneId,
            request.IsActive), cancellationToken).ConfigureAwait(false);

        return Ok(ToResponse(entity));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
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
            entity.IsActive,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }

    private string? GetOwnerId()
    {
        return User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User?.Identity?.Name;
    }

    internal sealed record ObservatoryRequest(
        Guid? Id,
        [Required, StringLength(200)] string Name,
        [Range(-90, 90)] double LatitudeDegrees,
        [Range(-180, 180)] double LongitudeDegrees,
        [Range(-1000, 10000)] double ElevationMeters,
        [Required, StringLength(128)] string TimeZoneId,
        bool IsActive = true);

    internal sealed record ObservatoryResponse(
        Guid Id,
        string Name,
        double LatitudeDegrees,
        double LongitudeDegrees,
        double ElevationMeters,
        string TimeZoneId,
        bool IsActive,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc);
}
