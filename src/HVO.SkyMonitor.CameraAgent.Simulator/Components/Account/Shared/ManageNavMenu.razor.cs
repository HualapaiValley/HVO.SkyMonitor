using System.Linq;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Simulator.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Components.Account.Shared;

public partial class ManageNavMenu : ComponentBase
{
    private bool hasExternalLogins;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        hasExternalLogins = (await SignInManager.GetExternalAuthenticationSchemesAsync()).Any();
    }
}
