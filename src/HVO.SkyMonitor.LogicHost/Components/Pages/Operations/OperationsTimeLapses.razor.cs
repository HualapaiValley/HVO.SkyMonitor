using System.Globalization;
using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsTimeLapses : ComponentBase
{
    [SupplyParameterFromQuery(Name = "date")] public string? Date { get; set; }
    [Inject] internal CentralTimeLapseCatalog Catalog { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    private IReadOnlyList<CentralTimeLapseJobView>? Jobs { get; set; }
    private string? Error { get; set; }
    private long _generation;

    protected override async Task OnParametersSetAsync()
    {
        var generation = ++_generation;
        Error = null;
        Jobs = null;
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(Date))
        {
            if (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                Error = "Choose a valid reporting date.";
                return;
            }
            date = value;
        }
        var jobs = await Catalog.ListAsync((await AuthenticationStateTask).User, null, date, null, CancellationToken.None);
        if (generation == _generation) Jobs = jobs;
    }
}
