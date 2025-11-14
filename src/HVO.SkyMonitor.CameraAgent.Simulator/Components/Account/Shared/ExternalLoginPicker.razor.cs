using System;
using System.Linq;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Simulator.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Components.Account.Shared;

public partial class ExternalLoginPicker : ComponentBase
{
    private AuthenticationScheme[] externalLogins = Array.Empty<AuthenticationScheme>();

    [SupplyParameterFromQuery]
    private string? ReturnUrl { get; set; }

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        externalLogins = (await SignInManager.GetExternalAuthenticationSchemesAsync()).ToArray();
    }
}
