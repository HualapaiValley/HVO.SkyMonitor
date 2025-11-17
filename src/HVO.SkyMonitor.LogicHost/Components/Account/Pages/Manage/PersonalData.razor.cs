using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.LogicHost.Components.Account.Pages.Manage;

public sealed partial class PersonalData
{
    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        var user = await UserManager.GetUserAsync(HttpContext.User);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
        }
    }
}
