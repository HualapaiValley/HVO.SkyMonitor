using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsDashboard : ComponentBase
{
    private readonly List<OperationsObservatorySummary> items = [];
    [Inject]
    internal INetworkOperationsReadService Operations { get; set; } = default!;
    [Inject]
    internal IRegisteredUserPersonalizationService Personalization { get; set; } = default!;

    [CascadingParameter]
    internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;

    internal bool IsLoading { get; private set; } = true;
    internal IReadOnlyList<OperationsObservatorySummary> Items => items;
    internal string? NextCursor { get; private set; }
    internal bool IsLoadingMore { get; private set; }
    internal IReadOnlyList<FollowedObservatorySummary> FollowedObservatories { get; private set; } = [];
    private string? userId;

    protected override async Task OnInitializedAsync()
    {
        var user = (await AuthenticationStateTask).User;
        userId = CentralArtifactCredentialAccess.GetOwnerId(user);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await LoadAsync(null);
            FollowedObservatories = await Personalization.ListFollowedObservatoriesAsync(userId, 25);
        }
        IsLoading = false;
    }

    internal async Task LoadMoreAsync()
    {
        if (userId is null || NextCursor is null || IsLoadingMore) return;
        IsLoadingMore = true;
        await LoadAsync(NextCursor);
        IsLoadingMore = false;
    }

    private async Task LoadAsync(string? cursor)
    {
        var page = await Operations.ListObservatoriesAsync(userId!, 25, cursor);
        items.AddRange(page.Items);
        NextCursor = page.NextCursor;
    }
}
