using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Observatories;

public partial class ManageObservatories : ComponentBase
{
    private const string UiExceptionJustification = "UI surfaces friendly messages while logging unexpected exceptions.";
    private const string AuthenticationRequiredMessage = "Your session expired. Please sign in again to manage observatories.";
    private readonly List<ObservatorySummary> observatories = [];
    private readonly ObservatoryFormModel formModel = new();
    private bool isBusy;
    private string formTitle = "Add Observatory";
    private string? statusMessage;
    private string? errorMessage;
    private string? ownerUserId;

    [Inject]
    internal IObservatoryService ObservatoryService { get; set; } = default!;

    [Inject]
    internal ILogger<ManageObservatories> Logger { get; set; } = default!;

    [CascadingParameter]
    internal Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (!await TryEnsureOwnerIdAsync())
        {
            return;
        }

        await LoadObservatoriesAsync();
    }

    internal IReadOnlyList<ObservatorySummary> Observatories => observatories;

    internal ObservatoryFormModel FormModel => formModel;

    internal string FormTitle => formTitle;

    internal string? StatusMessage => statusMessage;

    internal string? ErrorMessage => errorMessage;

    internal bool IsBusy => isBusy;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    internal async Task OnValidSubmitAsync()
    {
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        statusMessage = null;
        errorMessage = null;
        RequestRender();

        try
        {
            var ownerId = await EnsureOwnerIdAsync();
            await ObservatoryService.CreateOrUpdateAsync(new ObservatoryUpsertRequest(
                formModel.Id,
                ownerId,
                formModel.Name,
                formModel.LatitudeDegrees,
                formModel.LongitudeDegrees,
                formModel.ElevationMeters,
                formModel.TimeZoneId,
                formModel.IsActive));

            statusMessage = "Observatory saved.";
            await LoadObservatoriesAsync();
            NewObservatory();
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to save observatory.";
            Logger.LogError(ex, "Failed to save observatory {Name}", formModel.Name);
        }
        finally
        {
            isBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal void NewObservatory()
    {
        formModel.Reset();
        formTitle = "Add Observatory";
        statusMessage = null;
        errorMessage = null;
        RequestRender();
    }

    internal void EditObservatory(ObservatorySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        formModel.Id = summary.Id;
        formModel.Name = summary.Name;
        formModel.LatitudeDegrees = summary.LatitudeDegrees;
        formModel.LongitudeDegrees = summary.LongitudeDegrees;
        formModel.ElevationMeters = summary.ElevationMeters;
        formModel.TimeZoneId = summary.TimeZoneId;
        formModel.IsActive = summary.IsActive;
        formTitle = "Edit Observatory";
        RequestRender();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    internal async Task DeleteObservatoryAsync(ObservatorySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        errorMessage = null;
        statusMessage = null;
        RequestRender();

        try
        {
            var ownerId = await EnsureOwnerIdAsync();
            var deleted = await ObservatoryService.DeleteAsync(summary.Id, ownerId);
            if (deleted)
            {
                statusMessage = "Observatory deleted.";
                await LoadObservatoriesAsync();
                if (formModel.Id == summary.Id)
                {
                    NewObservatory();
                }
            }
            else
            {
                errorMessage = "Observatory not found or already deleted.";
            }
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to delete observatory.";
            Logger.LogError(ex, "Failed to delete observatory {ObservatoryId}", summary.Id);
        }
        finally
        {
            isBusy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    private async Task LoadObservatoriesAsync()
    {
        try
        {
            var ownerId = await EnsureOwnerIdAsync();
            var results = await ObservatoryService.GetObservatoriesAsync(ownerId);
            observatories.Clear();
            observatories.AddRange(results);
        }
        catch (Exception ex)
        {
            errorMessage = "Failed to load observatories.";
            Logger.LogError(ex, "Failed to load observatories");
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<string> EnsureOwnerIdAsync()
    {
        if (!string.IsNullOrEmpty(ownerUserId))
        {
            return ownerUserId;
        }

        if (AuthenticationStateTask is null)
        {
            throw new InvalidOperationException("Authentication state is unavailable.");
        }

        var authState = await AuthenticationStateTask;
        var user = authState.User;
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.Identity?.Name;

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("User identity is missing required claims.");
        }

        ownerUserId = id;
        return ownerUserId;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = UiExceptionJustification)]
    private async Task<bool> TryEnsureOwnerIdAsync()
    {
        try
        {
            await EnsureOwnerIdAsync();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            errorMessage = AuthenticationRequiredMessage;
            Logger.LogWarning(ex, "Unable to resolve authenticated user.");
        }
        catch (Exception ex)
        {
            errorMessage = "Unexpected error while resolving user identity.";
            Logger.LogError(ex, "Unexpected failure while resolving authenticated user.");
        }

        await InvokeAsync(StateHasChanged);
        return false;
    }

    private void RequestRender()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    internal sealed class ObservatoryFormModel
    {
        public Guid? Id { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Range(-90, 90)]
        public double LatitudeDegrees { get; set; }

        [Range(-180, 180)]
        public double LongitudeDegrees { get; set; }

        [Range(-1000, 10000)]
        public double ElevationMeters { get; set; }

        [Required, StringLength(128)]
        public string TimeZoneId { get; set; } = "Pacific/Honolulu";

        public bool IsActive { get; set; } = true;

        public void Reset()
        {
            Id = null;
            Name = string.Empty;
            LatitudeDegrees = 0;
            LongitudeDegrees = 0;
            ElevationMeters = 0;
            TimeZoneId = TimeZoneInfo.Local?.Id ?? "UTC";
            IsActive = true;
        }
    }
}
