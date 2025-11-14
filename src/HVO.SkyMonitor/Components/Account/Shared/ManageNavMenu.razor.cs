using HVO.SkyMonitor.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Components.Account.Shared;

public partial class ManageNavMenu : ComponentBase
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
