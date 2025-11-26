using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/heartbeat")]
[AllowAnonymous]
internal sealed class DeviceHeartbeatController(
    IDeviceHeartbeatService heartbeatService,
    ILogger<DeviceHeartbeatController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeviceHeartbeatResponse>> RecordHeartbeatAsync(
        DeviceHeartbeatRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var result = await heartbeatService.RecordHeartbeatAsync(new DeviceHeartbeatRequest(
                request.DeviceId,
                request.DeviceKey,
                request.SoftwareVersion,
                request.AgentState,
                request.TemperatureCelsius,
                request.CpuPercent), cancellationToken).ConfigureAwait(false);

            return Ok(new DeviceHeartbeatResponse(
                result.RegistrationId,
                result.DevicePublicId,
                result.ObservatoryId,
                result.ObservatoryName,
                result.FriendlyName,
                result.ServerTimeUtc,
                result.RecommendedHeartbeatSeconds));
        }
        catch (DeviceRegistrationException ex)
        {
            logger.LogWarning(ex, "Heartbeat rejected for {DeviceId}", request.DeviceId);
            return Unauthorized(new ProblemDetails
            {
                Title = "Device heartbeat rejected",
                Detail = ex.Message,
                Status = StatusCodes.Status401Unauthorized
            });
        }
    }

    internal sealed record DeviceHeartbeatRequestDto(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(256)] string DeviceKey,
        [StringLength(64)] string? SoftwareVersion,
        [StringLength(64)] string? AgentState,
        double? TemperatureCelsius,
        double? CpuPercent);

    internal sealed record DeviceHeartbeatResponse(
        Guid RegistrationId,
        Guid DevicePublicId,
        Guid ObservatoryId,
        string ObservatoryName,
        string FriendlyName,
        DateTimeOffset ServerTimeUtc,
        int RecommendedHeartbeatSeconds);
}
