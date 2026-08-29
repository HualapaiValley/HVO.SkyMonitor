using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsNotifications : ComponentBase
{
    [Inject] internal IRegisteredUserPersonalizationService Personalization { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal bool IsLoading { get; private set; } = true;
    internal bool IsBusy { get; private set; }
    internal bool IsSubscribed { get; set; }
    internal bool InAppEnabled { get; set; } = true;
    internal bool EmailEnabled { get; set; }
    internal string? StatusMessage { get; private set; }
    internal IReadOnlyList<RegisteredUserNotificationSummary> Notifications { get; private set; } = [];
    private string? userId;

    protected override async Task OnInitializedAsync()
    {
        userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        if (userId is not null) await LoadAsync();
        IsLoading = false;
    }

    internal async Task SaveAsync()
    {
        if (userId is null || IsBusy) return;
        IsBusy = true;
        _ = await Personalization.SetVerifiedEventSubscriptionAsync(userId, IsSubscribed);
        _ = await Personalization.SetPreferenceAsync(userId, new RegisteredUserPreference(InAppEnabled, EmailEnabled));
        StatusMessage = "Notification preferences saved.";
        IsBusy = false;
    }

    internal async Task MarkReadAsync(Guid notificationId)
    {
        if (userId is null) return;
        _ = await Personalization.MarkNotificationReadAsync(userId, notificationId);
        Notifications = await Personalization.ListNotificationsAsync(userId, 50);
    }

    private async Task LoadAsync()
    {
        IsSubscribed = await Personalization.HasVerifiedEventSubscriptionAsync(userId!);
        var preference = await Personalization.GetPreferenceAsync(userId!);
        InAppEnabled = preference.InAppEnabled;
        EmailEnabled = preference.EmailEnabled;
        Notifications = await Personalization.ListNotificationsAsync(userId!, 50);
    }
}
