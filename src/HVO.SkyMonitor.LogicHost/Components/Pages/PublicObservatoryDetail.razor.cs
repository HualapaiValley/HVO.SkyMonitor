using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages;

public partial class PublicObservatoryDetail : ComponentBase
{
    [Parameter]
    public string Slug { get; set; } = string.Empty;

    [Inject]
    internal IPublicNetworkReadService PublicNetwork { get; set; } = default!;

    [Inject]
    internal IRegisteredUserPersonalizationService Personalization { get; set; } = default!;

    [CascadingParameter]
    internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;

    internal Services.PublicObservatoryDetail? Detail { get; private set; }
    internal bool IsLoading { get; private set; } = true;
    internal string LocationLabel => Detail?.Observatory.Location.RegionLabel ?? "Location private";
    internal bool IsFollowing { get; private set; }
    internal bool IsPersonalizationBusy { get; private set; }
    internal bool IsLoadingMoreCameras { get; private set; }
    internal bool IsLoadingMoreProducts { get; private set; }
    private string? userId;

    protected override async Task OnParametersSetAsync()
    {
        IsLoading = true;
        Detail = await PublicNetwork.GetObservatoryAsync(Slug);
        userId = (await AuthenticationStateTask).User.FindFirstValue(ClaimTypes.NameIdentifier);
        IsFollowing = userId is not null && Detail is not null
            && await Personalization.IsFollowingAsync(userId, Detail.Observatory.Slug);
        IsLoading = false;
    }

    internal async Task ToggleFollowAsync()
    {
        if (userId is null || Detail is null || IsPersonalizationBusy) return;
        IsPersonalizationBusy = true;
        var outcome = IsFollowing
            ? await Personalization.UnfollowAsync(userId, Detail.Observatory.Slug)
            : await Personalization.FollowAsync(userId, Detail.Observatory.Slug);
        if (outcome is PersonalizationMutationOutcome.Applied or PersonalizationMutationOutcome.Unchanged)
        {
            IsFollowing = !IsFollowing;
        }
        IsPersonalizationBusy = false;
    }

    internal async Task LoadMoreCamerasAsync()
    {
        if (Detail?.CamerasNextCursor is not { } cursor || IsLoadingMoreCameras) return;
        IsLoadingMoreCameras = true;
        var page = await PublicNetwork.ListObservatoryCamerasAsync(Detail.Observatory.Slug, 50, cursor);
        Detail = Detail with
        {
            Cameras = Detail.Cameras.Concat(page.Items).ToArray(),
            CamerasNextCursor = page.NextCursor
        };
        IsLoadingMoreCameras = false;
    }

    internal async Task LoadMoreProductsAsync()
    {
        if (Detail?.ProductsNextCursor is not { } cursor || IsLoadingMoreProducts) return;
        IsLoadingMoreProducts = true;
        var page = await PublicNetwork.ListObservatoryProductsAsync(Detail.Observatory.Slug, 12, cursor);
        Detail = Detail with
        {
            Products = Detail.Products.Concat(page.Items).ToArray(),
            ProductsNextCursor = page.NextCursor
        };
        IsLoadingMoreProducts = false;
    }
}
