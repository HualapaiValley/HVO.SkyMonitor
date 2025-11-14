using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.ZWO.Components.Account;
using HVO.SkyMonitor.CameraAgent.ZWO.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Components.Account.Pages;

public partial class LoginWith2fa : ComponentBase
{
    private string? message;
    private ApplicationUser user = default!;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? ReturnUrl { get; set; }

    [SupplyParameterFromQuery]
    private bool RememberMe { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();

        user = await SignInManager.GetTwoFactorAuthenticationUserAsync()
            ?? throw new InvalidOperationException("Unable to load two-factor authentication user.");
    }

    private async Task OnValidSubmitAsync()
    {
        var authenticatorCode = (Input.TwoFactorCode ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        var result = await SignInManager.TwoFactorAuthenticatorSignInAsync(authenticatorCode, RememberMe, Input.RememberMachine);
        var userId = await UserManager.GetUserIdAsync(user);

        if (result.Succeeded)
        {
            Logger.LogInformation("User with ID '{UserId}' logged in with 2fa.", userId);
            RedirectManager.RedirectTo(ReturnUrl);
            return;
        }

        if (result.IsLockedOut)
        {
            Logger.LogWarning("User with ID '{UserId}' account locked out.", userId);
            RedirectManager.RedirectTo("Account/Lockout");
            return;
        }

        Logger.LogWarning("Invalid authenticator code entered for user with ID '{UserId}'.", userId);
        message = "Error: Invalid authenticator code.";
    }

    private sealed class InputModel
    {
        [Required]
        [StringLength(7, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
        [DataType(DataType.Text)]
        [Display(Name = "Authenticator code")]
        public string? TwoFactorCode { get; set; }

        [Display(Name = "Remember this machine")]
        public bool RememberMachine { get; set; }
    }
}
