using HVO.SkyMonitor.CameraAgent.Security;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.IdentityShared;

public sealed partial class RedirectToLogin : ComponentBase
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    protected override void OnInitialized()
    {
        var baseRelative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var normalized = ReturnUrlHelper.NormalizeReturnUrl(baseRelative);
        var loginPath = ReturnUrlHelper.BuildLoginPath(normalized);
        NavigationManager.NavigateTo(loginPath, forceLoad: true);
    }
}
