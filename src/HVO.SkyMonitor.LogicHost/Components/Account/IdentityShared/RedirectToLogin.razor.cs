using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Account.IdentityShared;

public sealed partial class RedirectToLogin : ComponentBase
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    protected override void OnInitialized()
    {
        var encodedReturnUrl = Uri.EscapeDataString(NavigationManager.Uri);
        NavigationManager.NavigateTo($"Account/Login?returnUrl={encodedReturnUrl}", forceLoad: true);
    }
}
