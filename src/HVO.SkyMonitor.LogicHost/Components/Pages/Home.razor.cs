using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages;

public partial class Home : ComponentBase
{
    [Inject]
    internal IPublicNetworkReadService PublicNetwork { get; set; } = default!;

    internal bool IsLoading { get; private set; } = true;
    internal string? ErrorMessage { get; private set; }
    internal IReadOnlyList<PublicObservatorySummary> Observatories { get; private set; } = [];
    internal IReadOnlyList<PublicEventSummary> Events { get; private set; } = [];
    internal IReadOnlyList<PublicImageSummary> Images { get; private set; } = [];

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var home = await PublicNetwork.GetHomeAsync(3);
            Observatories = home.Observatories;
            Events = home.Events;
            Images = home.Images;
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = "Released network activity is temporarily unavailable.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal static string LocationLabel(PublicObservatorySummary observatory)
        => observatory.Location.DisclosureLevel switch
        {
            Data.ObservatoryLocationDisclosureLevel.Region => observatory.Location.RegionLabel ?? "Published region",
            Data.ObservatoryLocationDisclosureLevel.Approximate => observatory.Location.RegionLabel ?? "Approximate location",
            Data.ObservatoryLocationDisclosureLevel.Exact => observatory.Location.RegionLabel ?? "Published location",
            _ => "Location private"
        };
}
