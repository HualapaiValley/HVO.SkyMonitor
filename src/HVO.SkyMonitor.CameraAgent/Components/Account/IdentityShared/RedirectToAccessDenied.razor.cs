using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.IdentityShared;

public sealed partial class RedirectToAccessDenied : ComponentBase
{
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    protected override void OnInitialized()
    {
        try
        {
            NavigationManager.NavigateTo("/Account/AccessDenied", forceLoad: true, replace: true);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("prerender", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
