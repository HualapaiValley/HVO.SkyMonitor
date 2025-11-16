using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using HVO.SkyMonitor.Data;

namespace HVO.SkyMonitor.Components.Account.Pages.Manage;

public sealed partial class RenamePasskey
{
    private ApplicationUser? user;
    private UserPasskeyInfo? passkey;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [Parameter]
    public string? Id { get; set; }

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
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

        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(Id);
        }
        catch (FormatException)
        {
            RedirectManager.RedirectToWithStatus(
                "Account/Manage/Passkeys",
                "Error: The specified passkey ID had an invalid format.",
                HttpContext);
            return;
        }

        passkey = await UserManager.GetPasskeyAsync(user, credentialId);
        if (passkey is null)
        {
            RedirectManager.RedirectToWithStatus(
                "Account/Manage/Passkeys",
                "Error: The specified passkey could not be found.",
                HttpContext);
            return;
        }

        if (!string.IsNullOrEmpty(passkey.Name))
        {
            FormModel.Name = passkey.Name;
        }
    }

    private async Task Rename()
    {
        var model = FormModel;

        if (user is null || passkey is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        passkey.Name = model.Name;

        var result = await UserManager.AddOrUpdatePasskeyAsync(user, passkey);
        if (!result.Succeeded)
        {
            RedirectManager.RedirectToWithStatus(
                "Account/Manage/Passkeys",
                "Error: The passkey could not be updated.",
                HttpContext);
            return;
        }

        RedirectManager.RedirectToWithStatus(
            "Account/Manage/Passkeys",
            "Passkey updated successfully.",
            HttpContext);
    }

    private sealed class InputModel
    {
        [Required]
        [StringLength(200, ErrorMessage = "Passkey names must be no longer than {1} characters.")]
        public string Name { get; set; } = string.Empty;
    }
}
