using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using HVO.SkyMonitor.CameraAgent.ZWO.Data;

namespace HVO.SkyMonitor.CameraAgent.ZWO.Components.Account.Pages.Manage;

public partial class Email
{
    private string? message;
    private ApplicationUser? user;
    private string? email;
    private bool isEmailConfirmed;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private IEmailSender<ApplicationUser> EmailSender { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm(FormName = "change-email")]
    private InputModel? Input { get; set; }

    private InputModel FormModel => Input ??= new();

    protected override async Task OnInitializedAsync()
    {
        user = await UserManager.GetUserAsync(HttpContext.User);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        email = await UserManager.GetEmailAsync(user);
        isEmailConfirmed = await UserManager.IsEmailConfirmedAsync(user);

        FormModel.NewEmail ??= email;
    }

    private async Task OnValidSubmitAsync(EditContext _)
    {
        var model = FormModel;

        if (model.NewEmail is null || model.NewEmail == email)
        {
            message = "Your email is unchanged.";
            return;
        }

        if (user is not ApplicationUser currentUser)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var userId = await UserManager.GetUserIdAsync(currentUser);
        var code = await UserManager.GenerateChangeEmailTokenAsync(currentUser, model.NewEmail);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        var callbackUrl = NavigationManager.GetUriWithQueryParameters(
            NavigationManager.ToAbsoluteUri("Account/ConfirmEmailChange").AbsoluteUri,
            new Dictionary<string, object?> { ["userId"] = userId, ["email"] = model.NewEmail, ["code"] = code });

        await EmailSender.SendConfirmationLinkAsync(currentUser, model.NewEmail, HtmlEncoder.Default.Encode(callbackUrl));

        message = "Confirmation link to change email sent. Please check your email.";
    }

    private async Task OnSendEmailVerificationAsync()
    {
        if (email is null)
        {
            return;
        }

        if (user is not ApplicationUser currentUser)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var userId = await UserManager.GetUserIdAsync(currentUser);
        var code = await UserManager.GenerateEmailConfirmationTokenAsync(currentUser);
        code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code));
        var callbackUrl = NavigationManager.GetUriWithQueryParameters(
            NavigationManager.ToAbsoluteUri("Account/ConfirmEmail").AbsoluteUri,
            new Dictionary<string, object?> { ["userId"] = userId, ["code"] = code });

        await EmailSender.SendConfirmationLinkAsync(currentUser, email, HtmlEncoder.Default.Encode(callbackUrl));

        message = "Verification email sent. Please check your email.";
    }

    private sealed class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "New email")]
        public string? NewEmail { get; set; }
    }
}
