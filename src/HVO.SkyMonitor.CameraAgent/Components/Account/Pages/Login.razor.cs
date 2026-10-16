using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.Pages;

public sealed partial class Login : ComponentBase
{
    internal const string InvalidSignInMessage = "Error: Invalid login attempt.";

    internal static readonly string LockedOutMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"Error: This account is locked after {CameraAgentIdentityPolicy.MaximumFailedSignInAttempts} failed sign-in attempts. Try again in {CameraAgentIdentityPolicy.LockoutDuration.TotalMinutes:0} minutes.");

    private static readonly EventId SignInLockedOutEvent = new(4188, "LocalSignInLockedOut");

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

        var user = await CameraAgentIdentityPolicy.FindSignInUserAsync(SignInManager.UserManager, Input.Email);
        if (user is null)
        {
            Logger.LogInformation("Local login attempt rejected.");
            errorMessage = InvalidSignInMessage;
            return;
        }

        var result = await SignInManager.PasswordSignInAsync(user, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            Logger.LogInformation("User logged in.");
            if (user.IsSiteOwner && user.PasswordChangeRequired)
            {
                RedirectManager.RedirectTo("Account/ReplaceTemporaryPassword");
                return;
            }
            RedirectManager.RedirectTo(ReturnUrl);
            return;
        }

        if (result.IsLockedOut)
        {
            Logger.LogWarning(SignInLockedOutEvent, "Local login attempt refused because the account is locked out.");
            errorMessage = LockedOutMessage;
            return;
        }

        Logger.LogInformation("Local login attempt rejected.");
        errorMessage = InvalidSignInMessage;
    }

    private sealed class InputModel
    {
        /// <summary>
        /// The account's user name or email; a value containing <c>@</c> is resolved as an email. The form field keeps
        /// its original <c>Input.Email</c> name so installer, deployment, and test clients that post it keep working.
        /// </summary>
        [Required]
        [StringLength(256)]
        [Display(Name = "Username or email")]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Remember me?")]
        public bool RememberMe { get; set; }
    }
}
