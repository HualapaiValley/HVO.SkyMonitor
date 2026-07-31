using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages;

public partial class PublicEvents : ComponentBase
{
    private readonly List<PublicEventSummary> items = [];

    [Inject]
    internal IPublicNetworkReadService PublicNetwork { get; set; } = default!;

    [Inject]
    internal IRegisteredUserPersonalizationService Personalization { get; set; } = default!;

    [CascadingParameter]
    internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;

    internal IReadOnlyList<PublicEventSummary> Items => items;
    internal bool IsLoading { get; private set; } = true;
    internal string? NextCursor { get; private set; }
    internal IReadOnlySet<Guid> BookmarkedIds { get; private set; } = new HashSet<Guid>();
    private string? userId;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync(null);
        userId = (await AuthenticationStateTask).User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            BookmarkedIds = await Personalization.ListBookmarkPublicIdsAsync(userId);
        }
        IsLoading = false;
    }

    internal Task LoadMoreAsync() => LoadAsync(NextCursor);

    internal async Task ToggleBookmarkAsync(Guid publicId)
    {
        if (userId is null) return;
        _ = BookmarkedIds.Contains(publicId)
            ? await Personalization.UnbookmarkAsync(userId, publicId)
            : await Personalization.BookmarkAsync(userId, publicId);
        BookmarkedIds = await Personalization.ListBookmarkPublicIdsAsync(userId);
    }

    private async Task LoadAsync(string? cursor)
    {
        if (cursor is null && items.Count > 0)
        {
            return;
        }
        var page = await PublicNetwork.ListEventsAsync(25, cursor);
        items.AddRange(page.Items);
        NextCursor = page.NextCursor;
    }
}
