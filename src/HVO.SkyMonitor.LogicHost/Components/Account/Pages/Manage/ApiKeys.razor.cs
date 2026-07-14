using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Components.Account.IdentityShared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.LogicHost.Components.Account.Pages.Manage;

public sealed partial class ApiKeys
{
    private static readonly ApiKeyAccessLevel[] AccessLevelOptions = Enum.GetValues<ApiKeyAccessLevel>();

    private ApplicationUser? user;
    private List<ApiKeyListItem> apiKeys = new();
    private string? statusMessage;
    private string? generatedPlaintextKey;
    private string? _busyKeyId;
    private bool createInProgress;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [Inject]
    private ApplicationDbContext DbContext { get; set; } = default!;

    [Inject]
    private IApiKeyLifecycleService LifecycleService { get; set; } = default!;

    [Inject]
    private ILogger<ApiKeys> Logger { get; set; } = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    private CreateApiKeyInput? Input { get; set; }

    private CreateApiKeyInput FormModel => Input ??= new();

    protected override async Task OnInitializedAsync()
    {
        user = await UserManager.GetUserAsync(HttpContext.User);
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        await LoadKeysAsync(HttpContext.RequestAborted);
    }

    private async Task OnCreateKeyAsync(EditContext _)
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        var model = FormModel;
        createInProgress = true;
        statusMessage = null;
        generatedPlaintextKey = null;

        try
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset? expiresUtc = null;
            if (model.ExpiresOnUtc.HasValue)
            {
                var candidate = DateTime.SpecifyKind(model.ExpiresOnUtc.Value, DateTimeKind.Utc);
                var expiresOffset = new DateTimeOffset(candidate);
                if (expiresOffset <= now)
                {
                    statusMessage = "Error: Expiration must be in the future.";
                    return;
                }

                expiresUtc = expiresOffset;
            }

            var result = await LifecycleService.CreateAsync(
                user.Id,
                user.Email ?? user.UserName ?? user.Id,
                GetDisplayName(model.DisplayName, now),
                model.AccessLevel,
                expiresUtc,
                HttpContext.RequestAborted);

            generatedPlaintextKey = result.PlaintextKey;
            statusMessage = "New API key created. Copy it now before navigating away.";
            Input = new();

            await LoadKeysAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException ex)
        {
            Logger.LogError(ex, "Failed to create API key for user {UserId}.", user.Id);
            statusMessage = "Error: Unable to create API key. Please try again.";
        }
        finally
        {
            createInProgress = false;
        }
    }

    private async Task DeleteKeyAsync(string keyId)
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        _busyKeyId = keyId;
        statusMessage = null;
        generatedPlaintextKey = null;

        try
        {
            if (!await LifecycleService.DeleteAsync(user.Id, keyId, HttpContext.RequestAborted))
            {
                statusMessage = "Error: API key not found.";
                return;
            }

            statusMessage = "API key deleted.";
            await LoadKeysAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException ex)
        {
            Logger.LogError(ex, "Failed to delete API key {ApiKeyId} for user {UserId}.", keyId, user.Id);
            statusMessage = "Error: Unable to delete API key. Please try again.";
        }
        finally
        {
            _busyKeyId = null;
        }
    }

    private async Task ToggleKeyAsync(string keyId, bool desiredState)
    {
        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        _busyKeyId = keyId;
        statusMessage = null;
        generatedPlaintextKey = null;

        try
        {
            var currentState = apiKeys.FirstOrDefault(key => key.Id == keyId)?.IsActive;
            if (currentState is null)
            {
                statusMessage = "Error: API key not found.";
                return;
            }

            if (currentState == desiredState)
            {
                statusMessage = desiredState ? "API key is already active." : "API key is already inactive.";
                return;
            }

            if (!await LifecycleService.SetActiveAsync(user.Id, keyId, desiredState, HttpContext.RequestAborted))
            {
                statusMessage = "Error: API key not found.";
                return;
            }

            statusMessage = desiredState ? "API key activated." : "API key deactivated.";
            await LoadKeysAsync(HttpContext.RequestAborted);
        }
        catch (DbUpdateException ex)
        {
            Logger.LogError(ex, "Failed to toggle API key {ApiKeyId} for user {UserId}.", keyId, user.Id);
            statusMessage = "Error: Unable to update API key. Please try again.";
        }
        finally
        {
            _busyKeyId = null;
        }
    }

    private async Task LoadKeysAsync(CancellationToken cancellationToken)
    {
        if (user is null)
        {
            return;
        }

        var items = await DbContext.ApiKeys
            .Where(key => key.UserId == user.Id)
            .AsNoTracking()
            .Select(key => new ApiKeyListItem(
                key.Id,
                key.DisplayName,
                key.AccessLevel,
                key.IsActive,
                key.CreatedUtc,
                key.ExpiresUtc))
            .ToListAsync(cancellationToken);

        apiKeys = items.OrderByDescending(key => key.CreatedAtUtc).ToList();
    }

    private static string GetAccessLevelLabel(ApiKeyAccessLevel level) => level switch
    {
        ApiKeyAccessLevel.Read => "Read",
        ApiKeyAccessLevel.ReadWrite => "Read & Write",
        _ => level.ToString()
    };

    private static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string FormatExpiration(DateTimeOffset? expiration)
        => expiration.HasValue ? expiration.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Never";

    private static string GetDisplayName(string? candidate, DateTimeOffset nowUtc)
        => string.IsNullOrWhiteSpace(candidate)
            ? string.Format(CultureInfo.InvariantCulture, "Unnamed key ({0:yyyyMMddHHmmss})", nowUtc)
            : candidate.Trim();

    private sealed record ApiKeyListItem(
        string Id,
        string DisplayName,
        ApiKeyAccessLevel AccessLevel,
        bool IsActive,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ExpiresAtUtc);

    private sealed class CreateApiKeyInput
    {
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Required]
        [EnumDataType(typeof(ApiKeyAccessLevel))]
        [Display(Name = "Access level")]
        public ApiKeyAccessLevel AccessLevel { get; set; } = ApiKeyAccessLevel.Read;

        [Display(Name = "Expires on (UTC)")]
        public DateTime? ExpiresOnUtc { get; set; }
    }
}
