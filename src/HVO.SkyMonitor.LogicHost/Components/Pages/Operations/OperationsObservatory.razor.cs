using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public partial class OperationsObservatory : ComponentBase
{
    private string? userId;
    [Parameter] public Guid ObservatoryId { get; set; }
    [Inject] internal INetworkOperationsReadService Operations { get; set; } = default!;
    [CascadingParameter] internal Task<AuthenticationState> AuthenticationStateTask { get; set; } = default!;
    internal OperationsObservatoryDetail? Detail { get; private set; }
    internal OperationsObservatorySummary? Item => Detail?.Observatory;
    internal bool IsLoadingMoreCameras { get; private set; }
    internal bool IsLoadingMoreRegistrations { get; private set; }
    protected override async Task OnParametersSetAsync()
    {
        userId = CentralArtifactCredentialAccess.GetOwnerId((await AuthenticationStateTask).User);
        if (userId is not null) Detail = await Operations.GetObservatoryDetailAsync(userId, ObservatoryId);
    }

    internal async Task LoadMoreCamerasAsync()
    {
        if (userId is null || Detail?.CamerasNextCursor is not { } cursor || IsLoadingMoreCameras) return;
        IsLoadingMoreCameras = true;
        var page = await Operations.ListObservatoryCamerasAsync(userId, ObservatoryId, 50, cursor);
        Detail = Detail with
        {
            Cameras = Detail.Cameras.Concat(page.Items).ToArray(),
            CamerasNextCursor = page.NextCursor
        };
        IsLoadingMoreCameras = false;
    }

    internal async Task LoadMoreRegistrationsAsync()
    {
        if (userId is null || Detail?.RegistrationsNextCursor is not { } cursor || IsLoadingMoreRegistrations) return;
        IsLoadingMoreRegistrations = true;
        var page = await Operations.ListObservatoryRegistrationsAsync(userId, ObservatoryId, 50, cursor);
        Detail = Detail with
        {
            Registrations = Detail.Registrations.Concat(page.Items).ToArray(),
            RegistrationsNextCursor = page.NextCursor
        };
        IsLoadingMoreRegistrations = false;
    }
}
