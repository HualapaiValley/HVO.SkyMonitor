using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsTimeLapseDetail : ComponentBase
{
    [Parameter] public Guid ProductId { get; set; }
    [Inject] internal CentralTimeLapseCatalog Catalog { get; set; } = default!;
    [Inject] internal CentralTimeLapseObjects Objects { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    private CentralTimeLapseProduct? Product { get; set; }
    private bool Loading { get; set; }
    private bool Available { get; set; }
    private long _generation;
    private string VideoUrl => $"/api/v1.0/time-lapses/{ProductId:D}/video";

    protected override async Task OnParametersSetAsync()
    {
        var generation = ++_generation;
        var id = ProductId;
        Loading = true;
        Available = false;
        var product = await Catalog.GetAsync((await AuthenticationStateTask).User, id, CancellationToken.None);
        var available = false;
        if (product is { IsGapFiller: false })
        {
            try
            {
                await using var stream = await Objects.OpenAsync(product, CancellationToken.None);
                available = true;
            }
            catch (Exception exception) when (exception is ObjectStoreException or IOException) { }
        }
        if (generation != _generation) return;
        Product = product;
        Available = available;
        Loading = false;
    }
}
