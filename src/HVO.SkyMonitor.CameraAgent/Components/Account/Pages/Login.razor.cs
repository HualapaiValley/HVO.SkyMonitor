using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.Pages;

public sealed partial class Login : ComponentBase
{
    private string? errorMessage;
    private EditContext editContext = default!;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? ReturnUrl { get; set; }

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private ILogger<Login> Logger { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    protected override Task OnInitializedAsync()
    {
        Input ??= new();
        editContext = new EditContext(Input);

        return Task.CompletedTask;
    }

    public async Task LoginUser()
    {
        if (!editContext.Validate())
        {
            return;
        }

        var result = await SignInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            Logger.LogInformation("User logged in.");
            var user = await SignInManager.UserManager.FindByEmailAsync(Input.Email).ConfigureAwait(false);
            if (user?.IsSiteOwner == true && user.PasswordChangeRequired)
            {
                RedirectManager.RedirectTo("Account/ReplaceTemporaryPassword");
                return;
            }
            RedirectManager.RedirectTo(ReturnUrl);
            return;
        }

        if (result.RequiresTwoFactor)
        {
            Logger.LogWarning("Two-factor login requested for {Email}, but two-factor authentication is disabled.", Input.Email);
            errorMessage = "Error: Two-factor authentication is not available.";
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
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Login blocked for unconfirmed account {Email}", Input.Email);
            }
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
    }
}
