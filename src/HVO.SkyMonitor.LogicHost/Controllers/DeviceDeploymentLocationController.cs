using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/deployment-location")]
[AllowAnonymous]
internal sealed class DeviceDeploymentLocationController(
    IDeviceCredentialValidator credentialValidator,
    IDeploymentLocationAuthorityService authorityService,
    ApplicationDbContext dbContext) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(32 * 1024)]
    public async Task<ActionResult<DeploymentLocationAcknowledgment>> ProposeAsync(
        DeviceDeploymentLocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.SourceKind) || request.SourceKind == DeploymentLocationSourceKind.Unspecified)
        {
            return BadRequest(new ProblemDetails { Title = "Deployment location source kind is invalid." });
        }
        try
        {
            var registration = await credentialValidator.ValidateAsync(
                request.DeviceId, request.DeviceKey, cancellationToken).ConfigureAwait(false);
            var acknowledgment = await authorityService.ProposeAsync(
                registration,
                request.DeploymentLocation,
                request.SourceKind,
                "active-device-reconciliation",
                cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(acknowledgment);
        }
        catch (DeviceRegistrationException exception) when (exception.ReasonCode is "registration-invalid"
            or "cross-agent-credential" or "invalid-credential")
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Device deployment-location proposal rejected.",
                Status = StatusCodes.Status401Unauthorized
            });
        }
        catch (DeviceRegistrationException)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Device deployment-location proposal is invalid.",
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    internal sealed record DeviceDeploymentLocationRequest(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(512)] string DeviceKey,
        [Required] DeploymentLocationSnapshot DeploymentLocation,
        DeploymentLocationSourceKind SourceKind);
}
