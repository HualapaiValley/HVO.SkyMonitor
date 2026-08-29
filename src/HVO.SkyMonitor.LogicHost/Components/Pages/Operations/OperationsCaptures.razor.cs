using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsCaptures : ComponentBase
{
    private readonly List<OperationsCaptureSummary> items = [];
    private string? userId;
    [SupplyParameterFromQuery] public Guid? ObservatoryId { get; set; }
    [SupplyParameterFromQuery] public Guid? CameraId { get; set; }
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal IReadOnlyList<OperationsCaptureSummary> Items => items;
    internal string? NextCursor { get; private set; }
    internal bool IsLoading { get; private set; } = true;
    protected override async Task OnParametersSetAsync()
    {
        userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        items.Clear();
        if (userId is not null) await LoadAsync(null);
        IsLoading = false;
    }
    internal Task LoadMoreAsync() => LoadAsync(NextCursor);
    private async Task LoadAsync(string? cursor)
    {
        if (userId is null || (cursor is null && items.Count > 0)) return;
        var page = await Operations.ListCapturesAsync(userId, ObservatoryId, CameraId, 50, cursor);
        items.AddRange(page.Items);
        NextCursor = page.NextCursor;
    }
}
