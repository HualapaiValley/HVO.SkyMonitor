using System.Buffers.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using HVO.SkyMonitor.LogicHost.Components.Account;
using HVO.SkyMonitor.LogicHost.Components.Account.IdentityShared;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.LogicHost.Components.Account.Pages.Manage;

public sealed partial class Passkeys
{
    private const int MaxPasskeyCount = 100;

    private ApplicationUser? user;
    private IList<UserPasskeyInfo>? currentPasskeys;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    private string? Action { get; set; }

    [SupplyParameterFromForm]
    private string? CredentialId { get; set; }

    [SupplyParameterFromForm(FormName = "add-passkey")]
    private PasskeyInputModel? Input { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();

        user = await UserManager.GetUserAsync(HttpContext.User);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        currentPasskeys = await UserManager.GetPasskeysAsync(user);
    }

    private async Task AddPasskey()
    {
        var model = Input ??= new();

        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        if (!string.IsNullOrEmpty(model.Error))
        {
            RedirectManager.RedirectToCurrentPageWithStatus($"Error: {model.Error}", HttpContext);
            return;
        }

        if (string.IsNullOrEmpty(model.CredentialJson))
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: The browser did not provide a passkey.", HttpContext);
            return;
        }

        if (currentPasskeys is { Count: >= MaxPasskeyCount })
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: You have reached the maximum number of allowed passkeys.", HttpContext);
            return;
        }

        var attestationResult = await SignInManager.PerformPasskeyAttestationAsync(model.CredentialJson);
        if (!attestationResult.Succeeded)
        {
            RedirectManager.RedirectToCurrentPageWithStatus(
                $"Error: Could not add the passkey: {attestationResult.Failure.Message}",
                HttpContext);
            return;
        }

        var addPasskeyResult = await UserManager.AddOrUpdatePasskeyAsync(user, attestationResult.Passkey);
        if (!addPasskeyResult.Succeeded)
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: The passkey could not be added to your account.", HttpContext);
            return;
        }

        var credentialIdBase64Url = Base64Url.EncodeToString(attestationResult.Passkey.CredentialId);
        RedirectManager.RedirectTo($"Account/Manage/RenamePasskey/{credentialIdBase64Url}");
    }

    private async Task UpdatePasskey()
    {
        switch (Action)
        {
            case "rename":
                RedirectManager.RedirectTo($"Account/Manage/RenamePasskey/{CredentialId}");
                break;
            case "delete":
                await DeletePasskey();
                break;
            default:
                RedirectManager.RedirectToCurrentPageWithStatus($"Error: Unknown action '{Action}'.", HttpContext);
                break;
        }
    }

    private async Task DeletePasskey()
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(CredentialId);
        }
        catch (FormatException)
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: The specified passkey ID had an invalid format.", HttpContext);
            return;
        }

        var result = await UserManager.RemovePasskeyAsync(user, credentialId);
        if (!result.Succeeded)
        {
            RedirectManager.RedirectToCurrentPageWithStatus("Error: The passkey could not be deleted.", HttpContext);
            return;
        }

        RedirectManager.RedirectToCurrentPageWithStatus("Passkey deleted successfully.", HttpContext);
    }
}
