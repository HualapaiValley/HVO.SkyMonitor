using System;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.IdentityShared;

public sealed partial class RedirectToLogin : ComponentBase
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    internal ILogger<RedirectToLogin>? Logger { get; set; }
        = default!;

    protected override void OnInitialized()
    {
        var encodedReturnUrl = Uri.EscapeDataString(NavigationManager.Uri);
        var loginPath = $"Account/Login?returnUrl={encodedReturnUrl}";

        try
        {
            NavigationManager.NavigateTo(loginPath, forceLoad: true, replace: true);
        }
        catch (InvalidOperationException ex) when (IsNavigationBlockedDuringPrerender(ex))
        {
            Logger?.LogDebug(ex, "Deferring login redirect until interactive render session is established.");
        }
    }

    private static bool IsNavigationBlockedDuringPrerender(InvalidOperationException exception)
    {
        return exception.Message.Contains("prerender", StringComparison.OrdinalIgnoreCase);
    }
}
