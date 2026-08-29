using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Devices;

public partial class DeviceRegistrations : ComponentBase
{
    private readonly List<DeviceRegistrationSummary> registrations = [];
    private EnvelopeViewModel? activeEnvelope;
    private bool isLoading = true;
    private string? loadError;
    private string? envelopeError;
    private Guid? pendingEnvelopeId;
    private DeviceRegistrationSummary? deleteTarget;
    private string deleteConfirmation = string.Empty;
    private bool isDeleting;
    private string? deleteError;
    private string? deleteSuccess;
    private DeviceRegistrationStatus? statusFilter;
    private bool hideRevoked;
    private OwnerContext? ownerContext;
    private const string PortalRevocationMethod = "PortalSelfServiceRevocation";
    private const string UiExceptionJustification = "UI surfaces friendly messages while logging unexpected exceptions.";

    [Inject]
    internal IDeviceRegistrationReadService RegistrationReadService { get; set; } = default!;

    [Inject]
    internal IDeviceRegistrationEnvelopeService EnvelopeService { get; set; } = default!;

    [Inject]
    internal IDeviceRegistrationService RegistrationService { get; set; } = default!;

    [Inject]
    internal TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    internal IJSRuntime JsRuntime { get; set; } = default!;

    [Inject]
    internal ILogger<DeviceRegistrations> Logger { get; set; } = default!;

    [CascadingParameter]
    internal Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync().ConfigureAwait(false);
    }

    private IReadOnlyList<DeviceRegistrationSummary> Registrations => registrations;

    private bool IsLoading => isLoading;

    private string? LoadError => loadError;

    private string? EnvelopeError => envelopeError;

    private EnvelopeViewModel? ActiveEnvelope => activeEnvelope;

    private DeviceRegistrationSummary? DeleteTarget => deleteTarget;

    private string? DeleteError => deleteError;

    private string? DeleteSuccess => deleteSuccess;

    private string DeleteConfirmation
    {
        get => deleteConfirmation;
        set => deleteConfirmation = value;
    }

    private string StatusFilterValue
    {
        get => statusFilter?.ToString() ?? string.Empty;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse<DeviceRegistrationStatus>(value, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                statusFilter = null;
            }
        }
    }

    private bool CanConfirmDeletion => deleteTarget is not null
        && string.Equals(deleteTarget.DeviceId, deleteConfirmation.Trim(), StringComparison.Ordinal);

    private async Task ReloadAsync()
    {
        isLoading = true;
        loadError = null;
        envelopeError = null;
        pendingEnvelopeId = null;
        activeEnvelope = null;
        deleteTarget = null;
        deleteConfirmation = string.Empty;
        deleteError = null;
        isDeleting = false;
        StateHasChanged();

        try
        {
            var owner = await EnsureOwnerContextAsync().ConfigureAwait(false);
            var result = await RegistrationReadService.GetRegistrationsAsync(owner.UserId).ConfigureAwait(false);
            registrations.Clear();
            registrations.AddRange(result);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException or InvalidOperationException)
        {
            loadError = "Failed to load device registrations.";
            Logger.LogError(ex, "Failed to load device registrations.");
        }
        finally
        {
            isLoading = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private async Task IssueEnvelopeAsync(DeviceRegistrationSummary registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (pendingEnvelopeId.HasValue)
        {
            return;
        }

        envelopeError = null;
        pendingEnvelopeId = registration.RegistrationId;
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);

        try
        {
            var owner = await EnsureOwnerContextAsync().ConfigureAwait(false);
            var response = await EnvelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
                registration.RegistrationId,
                registration.DeviceId,
                registration.ObservatoryId,
                owner.UserId), default).ConfigureAwait(false);

            activeEnvelope = new EnvelopeViewModel(
                response.RegistrationId,
                registration.DeviceId,
                response.DevicePublicId,
                registration.FriendlyName,
                response.Envelope,
                response.IssuedAtUtc,
                response.ExpiresAtUtc);
        }
        catch (Exception ex) when (ex is DeviceRegistrationException or InvalidOperationException)
        {
            envelopeError = "Unable to issue envelope. Ensure the registration is still pending.";
            Logger.LogWarning(ex, "Failed to issue device envelope for {DeviceId}", registration.DeviceId);
        }
        finally
        {
            pendingEnvelopeId = null;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private void ClearEnvelope()
    {
        activeEnvelope = null;
        envelopeError = null;
    }

    private bool IsEnvelopeBusy(Guid registrationId)
    {
        return pendingEnvelopeId == registrationId;
    }

    private bool IsDeleteBusy(Guid registrationId)
    {
        return deleteTarget?.RegistrationId == registrationId && isDeleting;
    }

    private void BeginDelete(DeviceRegistrationSummary registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        deleteError = null;
        deleteSuccess = null;
        deleteTarget = registration;
        deleteConfirmation = string.Empty;
    }

    private void CancelDelete()
    {
        deleteTarget = null;
        deleteConfirmation = string.Empty;
        deleteError = null;
    }

    private List<DeviceRegistrationSummary> FilterRegistrations()
    {
        if (!statusFilter.HasValue && !hideRevoked)
        {
            return registrations;
        }

        IEnumerable<DeviceRegistrationSummary> query = registrations;

        if (statusFilter.HasValue)
        {
            query = query.Where(r => r.Status == statusFilter.Value);
        }

        if (hideRevoked)
        {
            query = query.Where(r => r.Status != DeviceRegistrationStatus.Revoked);
        }

        return query.ToList();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    private async Task ConfirmDeleteAsync()
    {
        var target = deleteTarget;
        if (target is null || isDeleting)
        {
            return;
        }

        var confirmation = deleteConfirmation.Trim();
        if (!string.Equals(target.DeviceId, confirmation, StringComparison.Ordinal))
        {
            deleteError = "Type the device identifier to confirm deletion.";
            return;
        }

        try
        {
            isDeleting = true;
            deleteError = null;
            var owner = await EnsureOwnerContextAsync().ConfigureAwait(false);
            var revocationNotes = $"Portal deletion confirmed for {target.DeviceId}";
            await RegistrationService.RevokeAsync(new DeviceRegistrationRevokeRequest(
                target.RegistrationId,
                target.DeviceId,
                owner.UserId,
                owner.DisplayName,
                PortalRevocationMethod,
                revocationNotes), default).ConfigureAwait(false);

            var deletedFriendlyName = target.FriendlyName;
            deleteSuccess = $"Deleted registration for '{deletedFriendlyName}'.";
            await ReloadAsync().ConfigureAwait(false);
            deleteTarget = null;
            deleteConfirmation = string.Empty;
        }
        catch (Exception ex) when (ex is DeviceRegistrationException or InvalidOperationException)
        {
            deleteError = ex.Message;
            Logger.LogWarning(ex, "Unable to delete device registration {RegistrationId}", target.RegistrationId);
        }
        catch (Exception ex)
        {
            deleteError = "Unexpected error deleting the registration.";
            Logger.LogError(ex, "Unexpected error deleting device registration {RegistrationId}", target.RegistrationId);
        }
        finally
        {
            isDeleting = false;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
    }

    private string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "—";
        }

        return timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture);
    }

    private string FormatOwnerEmail(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? "—" : email;
    }

    private string FormatOwnerConfirmation(DeviceRegistrationSummary registration)
    {
        if (registration.OwnerConfirmedAtUtc is null)
        {
            return "Awaiting confirmation";
        }

        var timestamp = FormatTimestamp(registration.OwnerConfirmedAtUtc);
        return string.IsNullOrWhiteSpace(registration.OwnerConfirmationMethod)
            ? timestamp
            : $"{timestamp} · {registration.OwnerConfirmationMethod}";
    }

    private static string FormatCoordinate(double value)
    {
        return value.ToString("F4", CultureInfo.InvariantCulture);
    }

    private static string FormatElevation(double value)
    {
        return value.ToString("F0", CultureInfo.InvariantCulture);
    }

    private string FormatRelativeDuration(DateTimeOffset expiresAt)
    {
        var now = TimeProvider.GetUtcNow();
        var remaining = expiresAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "seconds";
        }

        if (remaining.TotalMinutes < 1)
        {
            return "seconds";
        }

        if (remaining.TotalHours < 1)
        {
            return $"{Math.Floor(remaining.TotalMinutes)} minutes";
        }

        return $"{Math.Floor(remaining.TotalHours)} hours";
    }

    private string GetStatusBadgeClass(DeviceRegistrationStatus status)
    {
        return status switch
        {
            DeviceRegistrationStatus.Pending => "bg-warning-subtle text-warning",
            DeviceRegistrationStatus.Active => "bg-success-subtle text-success",
            DeviceRegistrationStatus.Revoked => "bg-danger-subtle text-danger",
            _ => "bg-secondary"
        };
    }

    private async Task CopyEnvelopeAsync()
    {
        if (activeEnvelope is null)
        {
            return;
        }

        try
        {
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", activeEnvelope.Envelope).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            envelopeError = "Browser blocked clipboard access. Copy manually if needed.";
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

        var authState = await AuthenticationStateTask.ConfigureAwait(false);
        var user = authState.User;
        var userId = CentralArtifactCredentialAccess.GetOwnerId(user)
            ?? throw new InvalidOperationException("User identifier is missing required claims.");

        var displayName = user.FindFirstValue("name")
            ?? user.Identity?.Name
            ?? user.FindFirstValue(ClaimTypes.Email)
            ?? userId;

        ownerContext = new OwnerContext(userId, displayName);
        return ownerContext;
    }

    private sealed record EnvelopeViewModel(
        Guid RegistrationId,
        string DeviceId,
        Guid DevicePublicId,
        string FriendlyName,
        string Envelope,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record OwnerContext(string UserId, string DisplayName);
}
