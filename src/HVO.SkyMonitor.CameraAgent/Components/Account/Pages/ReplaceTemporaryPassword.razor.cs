using System.ComponentModel.DataAnnotations;
using System.Linq;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.Pages;

public sealed partial class ReplaceTemporaryPassword
{
    private string? message;
    private ApplicationUser? user;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private OwnerPasswordReplacementService PasswordReplacement { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [Inject]
    private ILogger<ReplaceTemporaryPassword> Logger { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();
        user = await UserManager.GetUserAsync(HttpContext.User).ConfigureAwait(false);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        if (!user.IsSiteOwner || !user.PasswordChangeRequired)
        {
            RedirectManager.RedirectTo("/");
        }
    }

    private async Task OnValidSubmitAsync()
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var passwordResult = await PasswordReplacement.ReplaceAsync(
            user,
            Input.CurrentPassword,
            Input.NewPassword).ConfigureAwait(false);
        if (!passwordResult.Succeeded)
        {
            message = $"Error: {string.Join(", ", passwordResult.Errors.Select(static error => error.Description))}";
            return;
        }

        await SignInManager.RefreshSignInAsync(user).ConfigureAwait(false);
        Logger.LogInformation(
            new EventId(4182, "OwnerPasswordBootstrapCompleted"),
            "Local owner replaced the temporary password; owner bootstrap is complete");
        RedirectManager.RedirectTo("/");
    }

    private sealed class InputModel
    {
        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [StringLength(100, MinimumLength = 6)]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The new password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
