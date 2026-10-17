using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.CameraAgent.Components.Account.Pages.Manage;

/// <summary>
/// The owner's API keys: create one with an access level, see the list, revoke one. The page renders statically from
/// a form post, so a new key appears only in the response to the post that created it and is never stored.
/// </summary>
public sealed partial class ApiKeys
{
    private const string CreateFormName = "create-api-key";
    private const string RevokeFormName = "revoke-api-key";

    private ApplicationUser? user;
    private IReadOnlyList<CameraAgentApiKeyInfo> keys = [];
    private string? message;
    private (string Label, string Token)? created;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private CameraAgentApiKeyStore Store { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm(FormName = CreateFormName)]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromForm(FormName = RevokeFormName)]
    private RevokeModel Revoke { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();
        Revoke ??= new();

        // A response to the post that creates a key carries the key; no response from this page is kept anywhere.
        HttpContext.Response.Headers.CacheControl = "no-store";
        user = await UserManager.GetUserAsync(HttpContext.User);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        keys = await Store.ListAsync(user.Id, HttpContext.RequestAborted);
    }

    private async Task CreateAsync()
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var result = await Store.CreateAsync(user.Id, Input.Label, Input.Access, HttpContext.RequestAborted);
        switch (result.Status)
        {
            case CameraAgentApiKeyCreateStatus.Created:
                created = (result.Key!.Label, result.Token!);
                message = $"Created API key \"{result.Key.Label}\".";
                Input = new();
                break;
            case CameraAgentApiKeyCreateStatus.LimitReached:
                message = $"Error: An owner can have at most {CameraAgentApiKeyStore.MaximumKeysPerOwner} API keys. Revoke one first.";
                break;
            case CameraAgentApiKeyCreateStatus.InvalidAccess:
                message = "Error: Choose read or read-and-change access.";
                break;
            default:
                message = $"Error: The name must be 1 to {CameraAgentApiKeyStore.MaximumLabelLength} characters without control characters.";
                break;
        }

        keys = await Store.ListAsync(user.Id, HttpContext.RequestAborted);
    }

    private async Task RevokeAsync()
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var label = keys.FirstOrDefault(key => string.Equals(key.Id, Revoke.Id, StringComparison.Ordinal))?.Label;
        message = !string.IsNullOrEmpty(Revoke.Id) && await Store.RevokeAsync(user.Id, Revoke.Id, HttpContext.RequestAborted)
            ? $"Revoked API key \"{label}\". It no longer works."
            : "Error: That API key no longer exists.";
        keys = await Store.ListAsync(user.Id, HttpContext.RequestAborted);
    }

    private static string AccessLabel(CameraAgentApiKeyInfo key)
        => !key.Readable ? "Unreadable; revoke it"
            : key.Access == ApiKeyAccessLevel.ReadWrite ? "Read and change" : "Read";

    private static string Created(CameraAgentApiKeyInfo key)
        => key.Readable
            ? key.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : "Unknown";

    private sealed class InputModel
    {
        [Required(ErrorMessage = "Enter a name for the key.")]
        [StringLength(CameraAgentApiKeyStore.MaximumLabelLength, ErrorMessage = "The name must be at most {1} characters long.")]
        public string Label { get; set; } = string.Empty;

        public ApiKeyAccessLevel Access { get; set; } = ApiKeyAccessLevel.Read;
    }

    private sealed class RevokeModel
    {
        public string? Id { get; set; }
    }
}
