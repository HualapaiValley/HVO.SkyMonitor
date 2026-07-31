using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsObservatories : ComponentBase
{
    private readonly List<OperationsObservatorySummary> items = [];
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal IReadOnlyList<OperationsObservatorySummary> Items => items;
    internal string? NextCursor { get; private set; }
    internal bool IsLoadingMore { get; private set; }
    private string? userId;
    protected override async Task OnInitializedAsync()
    {
        userId = (await AuthenticationStateTask).User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null) await LoadAsync(null);
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
