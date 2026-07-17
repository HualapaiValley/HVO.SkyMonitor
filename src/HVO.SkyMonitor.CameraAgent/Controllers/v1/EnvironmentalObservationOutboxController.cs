using Asp.Versioning;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Controllers.v1;

[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Route("api/v{version:apiVersion}/environmental-observation-outbox")]
public sealed class EnvironmentalObservationOutboxController(
    IEnvironmentalObservationOutbox outbox,
    EnvironmentalObservationDeliveryWakeup wakeup,
    IOptions<CameraAgentHostOptions> hostOptions,
    IOptions<LocalIdentityOptions> localIdentityOptions) : ControllerBase
{
    [HttpGet("dead-letters")]
    public async Task<IActionResult> ReadDeadLettersAsync(
        [FromQuery] int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
        {
            return Forbid();
        }
        if (maximumResults is < 1 or > 1000)
        {
            return BadRequest(new ProblemDetails { Title = "Maximum results must be between 1 and 1000" });
        }
        return Ok(await outbox.ReadDeadLettersAsync(
            hostOptions.Value.RawIngressRoot,
            maximumResults,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("{recordId:long}/replay")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ReplayAsync(
        long recordId,
        EnvironmentalObservationOutboxResolutionRequest? request,
        CancellationToken cancellationToken)
        => ResolveAsync(recordId, request, abandon: false, cancellationToken);

    [HttpPost("{recordId:long}/abandon")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AbandonAsync(
        long recordId,
        EnvironmentalObservationOutboxResolutionRequest? request,
        CancellationToken cancellationToken)
        => ResolveAsync(recordId, request, abandon: true, cancellationToken);

    private async Task<IActionResult> ResolveAsync(
        long recordId,
        EnvironmentalObservationOutboxResolutionRequest? request,
        bool abandon,
        CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return Forbid();
        }
        if (recordId < 1 || request is null || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 512)
        {
            return BadRequest(new ProblemDetails { Title = "Record ID and reason are required" });
        }
        var actor = User.Identity?.Name ?? "authenticated-operator";
        try
        {
            if (abandon)
            {
                await outbox.AbandonAsync(
                    hostOptions.Value.RawIngressRoot,
                    recordId,
                    actor,
                    request.Reason,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await outbox.ReplayAsync(
                    hostOptions.Value.RawIngressRoot,
                    recordId,
                    actor,
                    request.Reason,
                    cancellationToken).ConfigureAwait(false);
            }
            wakeup.Signal();
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return Conflict(new ProblemDetails { Title = "Outbox record cannot be resolved from its current state" });
        }
    }

    private bool IsAdministrator()
        => string.Equals(
            User.Identity?.Name,
            localIdentityOptions.Value.AdminEmail,
            StringComparison.OrdinalIgnoreCase);
}

public sealed record EnvironmentalObservationOutboxResolutionRequest(string Reason);
