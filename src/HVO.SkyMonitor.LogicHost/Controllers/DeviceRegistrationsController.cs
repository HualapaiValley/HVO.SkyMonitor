using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
    private const string PortalConfirmationMethod = "PortalSelfAttested";
    private const string PortalRevocationMethod = "PortalSelfServiceRevocation";

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

        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }

        DeviceRegistration registration;
        try
        {
            registration = await registrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
                request.DeviceId,
                request.VerificationCode,
                request.ObservatoryId,
                request.FriendlyName,
                ownerUserId,
                GetUserDisplayName() ?? ownerUserId,
                GetUserEmail(),
                PortalConfirmationMethod,
                null,
                TimeSpan.FromMinutes(request.PendingLifetimeMinutes ?? 15)), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRegistrationException ex) when (ex.ReasonCode == DeviceRegistrationException.NotFoundReasonCode)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }

        var response = new DeviceRegistrationResponse(
            registration.Id,
            registration.DeviceId,
            registration.Status,
            registration.IssuedAtUtc,
            registration.ExpiresAtUtc,
            registration.FriendlyName,
            registration.ObservatoryId,
            registration.ObservatoryName,
            registration.ObservatoryLatitudeDegrees,
            registration.ObservatoryLongitudeDegrees,
            registration.ObservatoryElevationMeters,
            registration.ObservatoryTimeZoneId,
            registration.OwnerUserId,
            registration.OwnerDisplayName,
            registration.OwnerEmail,
            registration.OwnerConfirmedAtUtc,
            registration.OwnerConfirmationMethod);

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
        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }

        DeviceRegistrationEnvelopeResponse envelope;
        try
        {
            envelope = await envelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
                request.RegistrationId,
                request.DeviceId,
                request.ObservatoryId,
                ownerUserId,
                lifetime), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRegistrationException ex) when (ex.ReasonCode == DeviceRegistrationException.NotFoundReasonCode)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }

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
        string DeviceId,
        DeviceRegistrationStatus Status,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        string FriendlyName,
        Guid ObservatoryId,
        string ObservatoryName,
        double ObservatoryLatitudeDegrees,
        double ObservatoryLongitudeDegrees,
        double ObservatoryElevationMeters,
        string ObservatoryTimeZoneId,
        string OwnerUserId,
        string OwnerDisplayName,
        string? OwnerEmail,
        DateTimeOffset? OwnerConfirmedAtUtc,
        string OwnerConfirmationMethod);

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

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteRegistrationAsync(
        DeviceRegistrationDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var ownerUserId = GetUserIdentifier();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return Unauthorized();
        }

        try
        {
            await registrationService.RevokeAsync(new DeviceRegistrationRevokeRequest(
                request.RegistrationId,
                request.DeviceId,
                ownerUserId,
                GetUserDisplayName() ?? ownerUserId,
                PortalRevocationMethod,
                request.Reason), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceRegistrationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }

        return NoContent();
    }

    internal sealed record DeviceRegistrationDeleteRequest(
        [Required] Guid RegistrationId,
        [Required, StringLength(128)] string DeviceId,
        [StringLength(256)] string? Reason);

    private string? GetUserIdentifier()
    {
        return User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User?.FindFirstValue("sub")
            ?? User?.Identity?.Name;
    }

    private string? GetUserDisplayName()
    {
        return User?.FindFirstValue("name")
            ?? User?.Identity?.Name
            ?? GetUserEmail();
    }

    private string? GetUserEmail()
    {
        return User?.FindFirstValue(ClaimTypes.Email)
            ?? User?.FindFirstValue("preferred_username");
    }
}
