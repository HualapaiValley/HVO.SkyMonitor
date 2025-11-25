using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.LogicHost.Controllers;

[ApiController]
[Route("api/internal/devices")]
[Authorize(Policy = AuthorizationPolicyNames.ApiKeyReadWrite)]
internal sealed class DeviceRegistrationsController(
    IDeviceRegistrationService registrationService,
    IDeviceRegistrationEnvelopeService envelopeService) : ControllerBase
{
    [HttpPost("verify")]
    public async Task<ActionResult<DeviceRegistrationResponse>> VerifyDeviceAsync(
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var registration = await registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
            request.DeviceId,
            request.VerificationCode,
            request.ObservatoryId,
            request.FriendlyName,
            TimeSpan.FromMinutes(request.PendingLifetimeMinutes ?? 15)), cancellationToken).ConfigureAwait(false);

        var response = new DeviceRegistrationResponse(
            registration.Id,
            registration.Status,
            registration.IssuedAtUtc,
            registration.ExpiresAtUtc,
            registration.FriendlyName);

        return Ok(response);
    }

    [HttpPost("envelope")]
    public async Task<ActionResult<DeviceRegistrationEnvelopeDto>> CreateEnvelopeAsync(
        DeviceRegistrationEnvelopeDtoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        TimeSpan? lifetime = request.EnvelopeLifetimeMinutes is int minutes
            ? TimeSpan.FromMinutes(minutes)
            : null;

        var envelope = await envelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
            request.RegistrationId,
            request.DeviceId,
            request.ObservatoryId,
            lifetime), cancellationToken).ConfigureAwait(false);

        var response = new DeviceRegistrationEnvelopeDto(
            envelope.RegistrationId,
            envelope.DevicePublicId,
            envelope.IssuedAtUtc,
            envelope.ExpiresAtUtc,
            envelope.Envelope,
            envelope.EnvelopeVersion);

        return Ok(response);
    }

    internal sealed record DeviceRegistrationRequest(
        [Required, StringLength(128)] string DeviceId,
        [Required, StringLength(32, MinimumLength = 4)] string VerificationCode,
        Guid ObservatoryId,
        [Required, StringLength(200)] string FriendlyName,
        int? PendingLifetimeMinutes = null);

    internal sealed record DeviceRegistrationResponse(
        Guid RegistrationId,
        DeviceRegistrationStatus Status,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        string FriendlyName);

    internal sealed record DeviceRegistrationEnvelopeDtoRequest(
        [Required] Guid RegistrationId,
        [Required, StringLength(128)] string DeviceId,
        Guid ObservatoryId,
        [Range(1, 30)] int? EnvelopeLifetimeMinutes = null);

    internal sealed record DeviceRegistrationEnvelopeDto(
        Guid RegistrationId,
        Guid DevicePublicId,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        string Envelope,
        string EnvelopeVersion);
}
