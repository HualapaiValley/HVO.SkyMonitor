using System.Text;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.ZWO.Components.Account;
using HVO.SkyMonitor.CameraAgent.ZWO.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Components.Account.Pages;

public partial class ConfirmEmailChange : ComponentBase
{
    private string? message;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? UserId { get; set; }

    [SupplyParameterFromQuery]
    private string? Email { get; set; }

    [SupplyParameterFromQuery]
    private string? Code { get; set; }

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        if (UserId is null || Email is null || Code is null)
        {
            RedirectManager.RedirectToWithStatus(
                "Account/Login",
                "Error: Invalid email change confirmation link.",
                HttpContext);
            return;
        }

        var user = await UserManager.FindByIdAsync(UserId);
        if (user is null)
        {
            message = "Unable to find user with Id '{userId}'";
            return;
        }

        var code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Code));
        var result = await UserManager.ChangeEmailAsync(user, Email, code);
        if (!result.Succeeded)
        {
            message = "Error changing email.";
            return;
        }

        var setUserNameResult = await UserManager.SetUserNameAsync(user, Email);
        if (!setUserNameResult.Succeeded)
        {
            message = "Error changing user name.";
            return;
        }

        await SignInManager.RefreshSignInAsync(user);
        message = "Thank you for confirming your email change.";
    }
}
