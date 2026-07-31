using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.LogicHost.Components.Pages;

public partial class PublicObservatories : ComponentBase, IAsyncDisposable
{
    private readonly List<PublicObservatorySummary> items = [];

    [Inject]
    internal IPublicNetworkReadService PublicNetwork { get; set; } = default!;

    [Inject]
    internal IJSRuntime JS { get; set; } = default!;

    internal IReadOnlyList<PublicObservatorySummary> Items => items;
    internal bool IsLoading { get; private set; } = true;
    internal bool IsLoadingMore { get; private set; }
    internal string? NextCursor { get; private set; }
    internal string? ErrorMessage { get; private set; }
    private IJSObjectReference? module;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync(null);
        IsLoading = false;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || ErrorMessage is not null) return;
        module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/PublicObservatories.razor.js");
        await module.InvokeVoidAsync("initialize", (object)MapMarkers());
    }

    internal async Task LoadMoreAsync()
    {
        if (NextCursor is null || IsLoadingMore)
        {
            return;
        }
        IsLoadingMore = true;
        await LoadAsync(NextCursor);
        IsLoadingMore = false;
        if (module is not null) await module.InvokeVoidAsync("updateMarkers", (object)MapMarkers());
    }

    private async Task LoadAsync(string? cursor)
    {
        try
        {
            var page = await PublicNetwork.ListObservatoriesAsync(25, cursor);
            items.AddRange(page.Items);
            NextCursor = page.NextCursor;
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = "The public observatory directory is temporarily unavailable.";
        }
    }

    internal static string LocationLabel(PublicObservatorySummary item)
        => item.Location.RegionLabel ?? (item.Location.DisclosureLevel == Data.ObservatoryLocationDisclosureLevel.Hidden
            ? "Location private"
            : "Published location");

    internal async Task SetMapStyleAsync(string style)
    {
        if (module is not null) await module.InvokeVoidAsync("setStyle", style);
    }

    private object[] MapMarkers()
        => items.Where(item => item.Location.LatitudeDegrees.HasValue && item.Location.LongitudeDegrees.HasValue)
            .Select(item => (object)new
            {
                item.Slug,
                item.DisplayName,
                Region = LocationLabel(item),
                Latitude = item.Location.LatitudeDegrees!.Value,
                Longitude = item.Location.LongitudeDegrees!.Value
            }).ToArray();

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (module is null) return;
        try
        {
            await module.InvokeVoidAsync("dispose");
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
