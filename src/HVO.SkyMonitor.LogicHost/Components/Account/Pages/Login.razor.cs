using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using HVO.SkyMonitor.LogicHost.Components.Account;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Components.Account.Pages;

public sealed partial class Login : ComponentBase
{
    private string? errorMessage;
    private EditContext editContext = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? ReturnUrl { get; set; }

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private ILogger<Login> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    private string RegisterUrl => NavigationManager.GetUriWithQueryParameters(
        "Account/Register",
        new Dictionary<string, object?> { ["ReturnUrl"] = ReturnUrl });

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();
        editContext = new EditContext(Input);

        if (HttpMethods.IsGet(HttpContext.Request.Method))
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        }
    }

    public async Task LoginUser()
    {
        if (!string.IsNullOrEmpty(Input.Passkey?.Error))
        {
            errorMessage = $"Error: {Input.Passkey.Error}";
            return;
        }

        SignInResult result;
        if (!string.IsNullOrEmpty(Input.Passkey?.CredentialJson))
        {
            result = await SignInManager.PasskeySignInAsync(Input.Passkey.CredentialJson);
        }
        else
        {
            if (!editContext.Validate())
            {
                return;
            }

            result = await SignInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: false);
        }

        if (result.Succeeded)
        {
            // Check if the account is a SYSTEM account (which should not allow interactive login)
            var user = await UserManager.FindByEmailAsync(Input.Email);
            if (user != null && user.AccountType == AccountType.System)
            {
                // Sign out the user immediately
                await SignInManager.SignOutAsync();
                Logger.LogWarning("System account attempted interactive login: {Email}", Input.Email);
                errorMessage = "Error: System accounts cannot sign in interactively. Please use API key authentication.";
                return;
            }

            Logger.LogInformation("User logged in.");
            RedirectManager.RedirectTo(ReturnUrl);
            return;
        }

        if (result.RequiresTwoFactor)
        {
            RedirectManager.RedirectTo(
                "Account/LoginWith2fa",
                new() { ["returnUrl"] = ReturnUrl, ["rememberMe"] = Input.RememberMe });
            return;
        }

        if (result.IsLockedOut)
        {
            Logger.LogWarning("User account locked out.");
            RedirectManager.RedirectTo("Account/Lockout");
            return;
        }

        if (result.IsNotAllowed)
        {
            Logger.LogInformation("Login blocked for unconfirmed account {Email}", Input.Email);
            errorMessage = "Error: Please confirm your email before signing in. Use the link in your inbox or request another confirmation email.";
            return;
        }

        errorMessage = "Error: Invalid login attempt.";
    }

    private sealed class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me?")]
        public bool RememberMe { get; set; }

        public PasskeyInputModel? Passkey { get; set; }
    }
}
