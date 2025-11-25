using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/device/bootstrap")]
[AllowAnonymous]
internal sealed class DeviceBootstrapController(
    IDeviceBootstrapService bootstrapService,
    ILogger<DeviceBootstrapController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<DeviceBootstrapResponse>> BootstrapAsync(
        DeviceBootstrapRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var result = await bootstrapService.BootstrapAsync(new DeviceBootstrapRequest(
                request.DeviceId,
                request.Envelope,
                request.Nonce), cancellationToken).ConfigureAwait(false);

            var response = new DeviceBootstrapResponse(
                result.RegistrationId,
                result.DevicePublicId,
                result.EnvelopeVersion,
                result.DeviceKey,
                new DeviceBootstrapEncryptedPayloadDto(
                    result.Payload.Ciphertext,
                    result.Payload.Nonce,
                    result.Payload.Tag,
                    result.Payload.Algorithm));

            return Ok(response);
        }
        catch (DeviceRegistrationException ex)
        {
            logger.LogWarning(ex, "Device bootstrap failed for {DeviceId}", request.DeviceId);
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid device envelope",
                Detail = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    internal sealed record DeviceBootstrapRequestDto(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(8192)] string Envelope,
        [StringLength(128)] string? Nonce = null);

    internal sealed record DeviceBootstrapResponse(
        Guid RegistrationId,
        Guid DevicePublicId,
        string EnvelopeVersion,
        string DeviceKey,
        DeviceBootstrapEncryptedPayloadDto Payload);

    internal sealed record DeviceBootstrapEncryptedPayloadDto(
        string Ciphertext,
        string Nonce,
        string Tag,
        string Algorithm);
}
