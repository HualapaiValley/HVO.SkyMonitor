using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsCamera : ComponentBase
{
    private readonly List<OperationsCaptureSummary> items = [];
    [Parameter] public Guid CameraId { get; set; }
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal IReadOnlyList<OperationsCaptureSummary> Items => items;
    internal OperationsCameraDetail? Camera { get; private set; }
    internal string? NextCursor { get; private set; }
    internal bool IsLoadingMore { get; private set; }
    internal bool IsLoadingMoreInstallations { get; private set; }
    private string? userId;
    protected override async Task OnParametersSetAsync()
    {
        items.Clear();
        NextCursor = null;
        Camera = null;
        userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        if (userId is not null)
        {
            Camera = await Operations.GetCameraAsync(userId, CameraId);
            await LoadAsync(null);
        }
    }

    internal async Task LoadMoreAsync()
    {
        if (userId is null || NextCursor is null || IsLoadingMore) return;
        IsLoadingMore = true;
        await LoadAsync(NextCursor);
        IsLoadingMore = false;
    }

    internal async Task LoadMoreInstallationsAsync()
    {
        if (userId is null || Camera?.InstallationsNextCursor is not { } cursor || IsLoadingMoreInstallations) return;
        IsLoadingMoreInstallations = true;
        var page = await Operations.ListCameraInstallationsAsync(userId, CameraId, 50, cursor);
        Camera = Camera with
        {
            Installations = Camera.Installations.Concat(page.Items).ToArray(),
            InstallationsNextCursor = page.NextCursor
        };
        IsLoadingMoreInstallations = false;
    }

    private async Task LoadAsync(string? cursor)
    {
        var page = await Operations.ListCapturesAsync(userId!, null, CameraId, 50, cursor);
        items.AddRange(page.Items);
        NextCursor = page.NextCursor;
    }
}
