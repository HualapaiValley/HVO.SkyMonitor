using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.Components.Account.Shared;

public partial class RedirectToLogin : ComponentBase
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    protected override void OnInitialized()
    {
        var encodedReturnUrl = Uri.EscapeDataString(NavigationManager.Uri);
        NavigationManager.NavigateTo($"Account/Login?returnUrl={encodedReturnUrl}", forceLoad: true);
    }
}
