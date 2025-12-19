using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/profile/rig")]
[AllowAnonymous]
internal sealed class DeviceRigProfileController(
    IDeviceRigProfileService rigProfileService,
    ILogger<DeviceRigProfileController> logger) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<DeviceRigProfileUpsertResponse>> UpsertAsync(
        DeviceRigProfileUpsertRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var result = await rigProfileService.UpsertAsync(new DeviceRigProfileUpsertRequest(
                request.DeviceId,
                request.DeviceKey,
                request.RigConfigJson,
                request.SoftwareVersion), cancellationToken).ConfigureAwait(false);

            return Accepted(new DeviceRigProfileUpsertResponse(
                result.RegistrationId,
                result.DevicePublicId,
                result.ObservatoryId,
                result.RigProfileVersion,
                result.RigProfileHash,
                result.AcceptedAtUtc,
                result.CreatedNewVersion));
        }
        catch (DeviceRegistrationException ex)
        {
            logger.LogWarning(ex, "Rig profile rejected for {DeviceId}", request.DeviceId);
            return Unauthorized(new ProblemDetails
            {
                Title = "Device rig profile rejected",
                Detail = ex.Message,
                Status = StatusCodes.Status401Unauthorized
            });
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rig profile payload is not valid JSON for {DeviceId}", request.DeviceId);
            return BadRequest(new ProblemDetails
            {
                Title = "Rig profile payload invalid",
                Detail = "RigConfigJson must be valid JSON.",
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    internal sealed record DeviceRigProfileUpsertRequestDto(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(256)] string DeviceKey,
        [Required, StringLength(262144)] string RigConfigJson,
        [StringLength(64)] string? SoftwareVersion);

    internal sealed record DeviceRigProfileUpsertResponse(
        Guid RegistrationId,
        Guid DevicePublicId,
        Guid ObservatoryId,
        int RigProfileVersion,
        string RigProfileHash,
        DateTimeOffset AcceptedAtUtc,
        bool CreatedNewVersion);
}
