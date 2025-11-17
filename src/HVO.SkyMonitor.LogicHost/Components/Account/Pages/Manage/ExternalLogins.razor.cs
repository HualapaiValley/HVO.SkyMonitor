using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.LogicHost.Components.Account.Pages.Manage;

public sealed partial class ExternalLogins
{
    public const string LinkLoginCallbackAction = "LinkLoginCallback";

    private ApplicationUser? user;
    private IList<UserLoginInfo>? currentLogins;
    private IList<AuthenticationScheme>? otherLogins;
    private bool showRemoveButton;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private IUserStore<ApplicationUser> UserStore { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    private string? LoginProvider { get; set; }

    [SupplyParameterFromForm]
    private string? ProviderKey { get; set; }

    [SupplyParameterFromQuery]
    private string? Action { get; set; }

    protected override async Task OnInitializedAsync()
    {
        user = await UserManager.GetUserAsync(HttpContext.User);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        currentLogins = await UserManager.GetLoginsAsync(user);
        otherLogins = (await SignInManager.GetExternalAuthenticationSchemesAsync())
            .Where(auth => currentLogins.All(ul => auth.Name != ul.LoginProvider))
            .ToList();

        string? passwordHash = null;
        if (UserStore is IUserPasswordStore<ApplicationUser> userPasswordStore)
        {
            passwordHash = await userPasswordStore.GetPasswordHashAsync(user, HttpContext.RequestAborted);
        }

        showRemoveButton = passwordHash is not null || currentLogins.Count > 1;

        if (HttpMethods.IsGet(HttpContext.Request.Method) && Action == LinkLoginCallbackAction)
        {
            await OnGetLinkLoginCallbackAsync();
        }
    }

    private async Task OnSubmitAsync()
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var result = await UserManager.RemoveLoginAsync(user, LoginProvider!, ProviderKey!);
        if (!result.Succeeded)
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: The external login was not removed.", HttpContext);
        }
        else
        {
            await SignInManager.RefreshSignInAsync(user);
            RedirectManager.RedirectToCurrentPageWithStatus("The external login was removed.", HttpContext);
        }
    }

    private async Task OnGetLinkLoginCallbackAsync()
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var userId = await UserManager.GetUserIdAsync(user);
        var info = await SignInManager.GetExternalLoginInfoAsync(userId);
        if (info is null)
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: Could not load external login info.", HttpContext);
            return;
        }

        var result = await UserManager.AddLoginAsync(user, info);
        if (result.Succeeded)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            RedirectManager.RedirectToCurrentPageWithStatus("The external login was added.", HttpContext);
        }
        else
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: The external login was not added. External logins can only be associated with one account.", HttpContext);
        }
    }
}
