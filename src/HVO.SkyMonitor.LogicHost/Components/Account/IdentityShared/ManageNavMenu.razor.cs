using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.LogicHost.Components.Account.IdentityShared;

public sealed partial class ManageNavMenu : ComponentBase
{
    private bool hasExternalLogins;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        var schemes = await SignInManager.GetExternalAuthenticationSchemesAsync();
        hasExternalLogins = schemes.Any();
    }
}
