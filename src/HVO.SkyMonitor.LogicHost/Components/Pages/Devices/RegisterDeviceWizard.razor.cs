using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Devices;

public partial class RegisterDeviceWizard : ComponentBase
{
    private const string PortalConfirmationMethod = "PortalSelfAttested";
    private const string UiExceptionJustification = "UI surfaces friendly messages while logging unexpected exceptions.";

    private readonly RegistrationFormModel formModel = new();
    private readonly List<ObservatorySummary> observatories = [];
    private RegistrationResult? registration;
    private EnvelopeViewModel? activeEnvelope;
    private OwnerContext? ownerContext;
    private bool isLoading = true;
    private bool isSubmitting;
    private bool isIssuingEnvelope;
    private string? loadError;
    private string? submitError;
    private string? submitSuccess;
    private string? envelopeError;

    [Inject]
    internal IObservatoryService ObservatoryService { get; set; } = default!;

    [Inject]
    internal IDeviceRegistrationService RegistrationService { get; set; } = default!;

    [Inject]
    internal IDeviceRegistrationEnvelopeService EnvelopeService { get; set; } = default!;

    [Inject]
    internal ILogger<RegisterDeviceWizard> Logger { get; set; } = default!;

    [Inject]
    internal IJSRuntime JsRuntime { get; set; } = default!;

    [CascadingParameter]
    internal Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    protected override async Task OnInitializedAsync()
    {
        try
        {
            ownerContext = await EnsureOwnerContextAsync();
            var results = await ObservatoryService.GetObservatoriesAsync(ownerContext.UserId);
            observatories.AddRange(results);
        }
        catch (Exception ex)
        {
            loadError = "Unable to load observatories. Try refreshing the page.";
            Logger.LogError(ex, "Failed to load observatories for device registration wizard");
        }
        finally
        {
            isLoading = false;
        }
    }

    private IReadOnlyList<ObservatorySummary> Observatories => observatories;

    private RegistrationFormModel FormModel => formModel;

    private RegistrationResult? Registration => registration;

    private EnvelopeViewModel? ActiveEnvelope => activeEnvelope;

    private bool IsLoading => isLoading;

    private bool IsSubmitting => isSubmitting;

    private bool IsIssuingEnvelope => isIssuingEnvelope;

    private bool HasObservatories => observatories.Count > 0;

    private string? LoadError => loadError;

    private string? SubmitError => submitError;

    private string? SubmitSuccess => submitSuccess;

    private string? EnvelopeError => envelopeError;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    internal async Task OnValidSubmitAsync()
    {
        if (isSubmitting)
        {
            return;
        }

        if (FormModel.ObservatoryId is null)
        {
            submitError = "Select an observatory to continue.";
            return;
        }

        submitError = null;
        envelopeError = null;
        submitSuccess = null;
        activeEnvelope = null;

        var deviceId = (FormModel.DeviceId ?? string.Empty).Trim();
        var verificationCode = (FormModel.VerificationCode ?? string.Empty).Trim();
        var friendlyName = (FormModel.FriendlyName ?? string.Empty).Trim();
        var confirmationNotes = string.IsNullOrWhiteSpace(FormModel.ConfirmationNotes)
            ? null
            : FormModel.ConfirmationNotes.Trim();

        try
        {
            isSubmitting = true;
            var owner = await EnsureOwnerContextAsync();

            var pendingLifetime = TimeSpan.FromMinutes(FormModel.PendingLifetimeMinutes ?? RegistrationFormModel.DefaultPendingMinutes);

            var registrationEntity = await RegistrationService.CreatePendingAsync(new DeviceRegistrationCreateRequest(
                deviceId,
                verificationCode,
                FormModel.ObservatoryId.Value,
                friendlyName,
                owner.UserId,
                owner.DisplayName,
                owner.Email,
                PortalConfirmationMethod,
                confirmationNotes,
                pendingLifetime), default);

            registration = Map(registrationEntity);
            submitSuccess = "Device verified. Issue the envelope before the pending window expires.";
        }
        catch (Exception ex) when (ex is DeviceRegistrationException or InvalidOperationException)
        {
            submitError = ex.Message;
            Logger.LogWarning(ex, "Device verification failed for {DeviceId}", deviceId);
        }
        catch (Exception ex)
        {
            submitError = "Unexpected error verifying the device.";
            Logger.LogError(ex, "Unexpected error verifying device {DeviceId}", deviceId);
        }
        finally
        {
            isSubmitting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal void ResetForm()
    {
        formModel.Reset();
        submitError = null;
        submitSuccess = null;
        envelopeError = null;
        registration = null;
        activeEnvelope = null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    internal async Task IssueEnvelopeAsync()
    {
        if (registration is null || isIssuingEnvelope)
        {
            return;
        }

        envelopeError = null;

        try
        {
            isIssuingEnvelope = true;
            var owner = await EnsureOwnerContextAsync();
            TimeSpan? lifetime = FormModel.EnvelopeLifetimeMinutes is int minutes
                ? TimeSpan.FromMinutes(minutes)
                : null;

            var envelopeResponse = await EnvelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
                registration.RegistrationId,
                registration.DeviceId,
                registration.ObservatoryId,
                owner.UserId,
                lifetime), default);

            activeEnvelope = new EnvelopeViewModel(
                envelopeResponse.RegistrationId,
                registration.DeviceId,
                envelopeResponse.Envelope,
                envelopeResponse.IssuedAtUtc,
                envelopeResponse.ExpiresAtUtc);
        }
        catch (Exception ex) when (ex is DeviceRegistrationException or InvalidOperationException)
        {
            envelopeError = ex.Message;
            Logger.LogWarning(ex, "Unable to issue envelope for registration {RegistrationId}", registration.RegistrationId);
        }
        catch (Exception ex)
        {
            envelopeError = "Unexpected error issuing the envelope.";
            Logger.LogError(ex, "Unexpected error issuing device envelope for {RegistrationId}", registration.RegistrationId);
        }
        finally
        {
            isIssuingEnvelope = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal void ClearEnvelope()
    {
        activeEnvelope = null;
        envelopeError = null;
    }

    internal async Task CopyEnvelopeAsync()
    {
        if (activeEnvelope is null)
        {
            return;
        }

        try
        {
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", activeEnvelope.Envelope);
        }
        catch (JSException ex)
        {
            envelopeError = "Clipboard access was blocked. Copy the envelope manually if needed.";
            Logger.LogWarning(ex, "Clipboard copy failed for envelope {RegistrationId}", activeEnvelope.RegistrationId);
        }
    }

    private async Task<OwnerContext> EnsureOwnerContextAsync()
    {
        if (ownerContext is not null)
        {
            return ownerContext;
        }

        if (AuthenticationStateTask is null)
        {
            throw new InvalidOperationException("Authentication state is unavailable.");
        }

        var authState = await AuthenticationStateTask;
        var user = authState.User;
        var userId = CentralArtifactCredentialAccess.GetOwnerId(user)
            ?? throw new InvalidOperationException("User identifier is missing required claims.");

        var displayName = user.FindFirstValue("name")
            ?? user.Identity?.Name
            ?? user.FindFirstValue(ClaimTypes.Email)
            ?? userId;

        var email = user.FindFirstValue(ClaimTypes.Email);

        ownerContext = new OwnerContext(userId, displayName, email);
        return ownerContext;
    }

    private static RegistrationResult Map(DeviceRegistration entity)
    {
        return new RegistrationResult(
            entity.Id,
            entity.DeviceId,
            entity.ObservatoryId,
            entity.FriendlyName,
            entity.ObservatoryName,
            entity.ObservatoryLatitudeDegrees,
            entity.ObservatoryLongitudeDegrees,
            entity.ObservatoryElevationMeters,
            entity.ObservatoryTimeZoneId,
            entity.OwnerDisplayName,
            entity.OwnerEmail,
            entity.IssuedAtUtc,
            entity.ExpiresAtUtc);
    }

    private string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "—";
        }

        return timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture);
    }

    private static string FormatOwnerEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? "—" : email;
    }

    private static string FormatCoordinate(double value)
    {
        return value.ToString("F4", CultureInfo.InvariantCulture);
    }

    private static string FormatElevation(double value)
    {
        return value.ToString("F0", CultureInfo.InvariantCulture);
    }

    private static string FormatRelativeDuration(int? minutes)
    {
        var value = minutes is > 0 ? minutes!.Value : RegistrationFormModel.DefaultEnvelopeMinutes;
        return value == 1 ? "1 minute" : $"{value} minutes";
    }

    private sealed record OwnerContext(string UserId, string DisplayName, string? Email);

    private sealed record RegistrationResult(
        Guid RegistrationId,
        string DeviceId,
        Guid ObservatoryId,
        string FriendlyName,
        string ObservatoryName,
        double ObservatoryLatitudeDegrees,
        double ObservatoryLongitudeDegrees,
        double ObservatoryElevationMeters,
        string ObservatoryTimeZoneId,
        string OwnerDisplayName,
        string? OwnerEmail,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset? ExpiresAtUtc);

    private sealed record EnvelopeViewModel(
        Guid RegistrationId,
        string DeviceId,
        string Envelope,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    internal sealed class RegistrationFormModel
    {
        public const int DefaultPendingMinutes = 15;
        public const int DefaultEnvelopeMinutes = 10;

        [Required]
        public Guid? ObservatoryId { get; set; }

        [Required, StringLength(200)]
        public string FriendlyName { get; set; } = string.Empty;

        [Required, StringLength(128)]
        public string DeviceId { get; set; } = string.Empty;

        [Required, StringLength(32, MinimumLength = 4)]
        public string VerificationCode { get; set; } = string.Empty;

        [Range(5, 60)]
        public int? PendingLifetimeMinutes { get; set; } = DefaultPendingMinutes;

        [Range(5, 30)]
        public int? EnvelopeLifetimeMinutes { get; set; } = DefaultEnvelopeMinutes;

        [StringLength(512)]
        public string? ConfirmationNotes { get; set; }

        public void Reset()
        {
            ObservatoryId = null;
            FriendlyName = string.Empty;
            DeviceId = string.Empty;
            VerificationCode = string.Empty;
            PendingLifetimeMinutes = DefaultPendingMinutes;
            EnvelopeLifetimeMinutes = DefaultEnvelopeMinutes;
            ConfirmationNotes = null;
        }
    }
}
