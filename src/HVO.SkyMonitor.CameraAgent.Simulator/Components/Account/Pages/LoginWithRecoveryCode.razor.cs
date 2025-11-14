using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Simulator.Components.Account;
using HVO.SkyMonitor.CameraAgent.Simulator.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Components.Account.Pages;

public partial class LoginWithRecoveryCode : ComponentBase
{
    private string? message;
    private ApplicationUser user = default!;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? ReturnUrl { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();

        user = await SignInManager.GetTwoFactorAuthenticationUserAsync()
            ?? throw new InvalidOperationException("Unable to load two-factor authentication user.");
    }

    private async Task OnValidSubmitAsync()
    {
        var recoveryCode = (Input.RecoveryCode ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        var result = await SignInManager.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
        var userId = await UserManager.GetUserIdAsync(user);

        if (result.Succeeded)
        {
            Logger.LogInformation("User with ID '{UserId}' logged in with a recovery code.", userId);
            RedirectManager.RedirectTo(ReturnUrl);
            return;
        }

        if (result.IsLockedOut)
        {
            Logger.LogWarning("User account locked out.");
            RedirectManager.RedirectTo("Account/Lockout");
            return;
        }

        Logger.LogWarning("Invalid recovery code entered for user with ID '{UserId}' ", userId);
        message = "Error: Invalid recovery code entered.";
    }

    private sealed class InputModel
    {
        [Required]
        [DataType(DataType.Text)]
        [Display(Name = "Recovery Code")]
        public string RecoveryCode { get; set; } = string.Empty;
    }
}
