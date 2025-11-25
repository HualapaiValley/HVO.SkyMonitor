using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
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

    [Inject]
    internal IDeviceRegistrationReadService RegistrationReadService { get; set; } = default!;

    [Inject]
    internal IDeviceRegistrationEnvelopeService EnvelopeService { get; set; } = default!;

    [Inject]
    internal TimeProvider TimeProvider { get; set; } = default!;

    [Inject]
    internal IJSRuntime JsRuntime { get; set; } = default!;

    [Inject]
    internal ILogger<DeviceRegistrations> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync().ConfigureAwait(false);
    }

    private IReadOnlyList<DeviceRegistrationSummary> Registrations => registrations;

    private bool IsLoading => isLoading;

    private string? LoadError => loadError;

    private string? EnvelopeError => envelopeError;

    private EnvelopeViewModel? ActiveEnvelope => activeEnvelope;

    private async Task ReloadAsync()
    {
        isLoading = true;
        loadError = null;
        envelopeError = null;
        pendingEnvelopeId = null;
        activeEnvelope = null;
        StateHasChanged();

        try
        {
            var result = await RegistrationReadService.GetRegistrationsAsync().ConfigureAwait(false);
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
            var response = await EnvelopeService.CreateEnvelopeAsync(new DeviceRegistrationEnvelopeRequest(
                registration.RegistrationId,
                registration.DeviceId,
                registration.ObservatoryId), default).ConfigureAwait(false);

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

    private string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "—";
        }

        return timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture);
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

    private sealed record EnvelopeViewModel(
        Guid RegistrationId,
        string DeviceId,
        Guid DevicePublicId,
        string FriendlyName,
        string Envelope,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
