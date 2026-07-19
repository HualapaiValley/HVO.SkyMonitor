using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/v1.0/devices/{devicePublicId:guid}/rigs/{rigId}/clear-reference")]
[Authorize(AuthenticationSchemes = "Bearer", Policy = "DerivativeJobsRead")]
internal sealed class ClearReferenceDesignationsController(
    ICentralClearReferenceService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<CentralClearReferenceSummary>> GetAsync(
        Guid devicePublicId,
        string rigId,
        CancellationToken cancellationToken)
    {
        try
        {
            var designation = await service.GetAsync(devicePublicId, rigId, cancellationToken).ConfigureAwait(false);
            return designation is null ? NotFound() : Ok(designation);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(CreateInvalidRequestProblem(exception));
        }
    }

    [HttpPut]
    public async Task<ActionResult<CentralClearReferenceSummary>> PutAsync(
        Guid devicePublicId,
        string rigId,
        [FromBody] SetClearReferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Ok(await service.SetAsync(
                devicePublicId,
                rigId,
                request.ArtifactId,
                User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown",
                cancellationToken).ConfigureAwait(false));
        }
        catch (CentralClearReferenceException exception)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Clear-reference designation rejected.",
                Detail = exception.ReasonCode
            });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(CreateInvalidRequestProblem(exception));
        }
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteAsync(
        Guid devicePublicId,
        string rigId,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await service.DeleteAsync(devicePublicId, rigId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(CreateInvalidRequestProblem(exception));
        }
    }

    private static ProblemDetails CreateInvalidRequestProblem(ArgumentException exception) => new()
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Clear-reference request is invalid.",
        Detail = exception.Message
    };
}

internal sealed record SetClearReferenceRequest(Guid ArtifactId);
