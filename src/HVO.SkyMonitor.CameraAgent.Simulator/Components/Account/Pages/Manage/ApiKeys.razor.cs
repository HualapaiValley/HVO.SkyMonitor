using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HVO.SkyMonitor.CameraAgent.Simulator.Components.Account;
using HVO.SkyMonitor.CameraAgent.Simulator.Data;
using HVO.SkyMonitor.CameraAgent.Security;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Components.Account.Pages.Manage;

public partial class ApiKeys
{
    private static readonly ApiKeyAccessLevel[] AccessLevelOptions = Enum.GetValues<ApiKeyAccessLevel>();

    private ApplicationUser? user;
    private List<ApiKeyListItem> apiKeys = new();
    private string? statusMessage;
    private string? generatedPlaintextKey;
    private Guid? _busyKeyId;
    private bool createInProgress;

    [Inject]
    private UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    private IdentityRedirectManager RedirectManager { get; set; } = default!;

    [Inject]
    private ApplicationDbContext DbContext { get; set; } = default!;

    [Inject]
    private IApiKeyHasher KeyHasher { get; set; } = default!;

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
        var model = FormModel;

        if (user is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

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
                expiresUtc = new DateTimeOffset(candidate);
                if (expiresUtc <= now)
                {
                    statusMessage = "Error: Expiration must be in the future.";
                    return;
                }
            }

            var plainKey = GenerateApiKeySecret();
            var hashedKey = KeyHasher.Hash(plainKey);

            var entity = new ApplicationUserApiKey
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                KeyHash = hashedKey,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim(),
                AccessLevel = model.AccessLevel,
                IsActive = true,
                CreatedUtc = now,
                ExpiresUtc = expiresUtc,
                CreatedBy = user.UserName,
            };

            DbContext.ApiKeys.Add(entity);
            await DbContext.SaveChangesAsync(HttpContext.RequestAborted);

            generatedPlaintextKey = plainKey;
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

    private async Task DeleteKeyAsync(Guid keyId)
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
            var key = await DbContext.ApiKeys.FirstOrDefaultAsync(
                k => k.Id == keyId && k.UserId == user.Id,
                HttpContext.RequestAborted);
            if (key is null)
            {
                statusMessage = "Error: API key not found.";
                return;
            }

            DbContext.ApiKeys.Remove(key);
            await DbContext.SaveChangesAsync(HttpContext.RequestAborted);

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

    private async Task ToggleKeyAsync(Guid keyId, bool desiredState)
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
            var key = await DbContext.ApiKeys.FirstOrDefaultAsync(
                k => k.Id == keyId && k.UserId == user.Id,
                HttpContext.RequestAborted);
            if (key is null)
            {
                statusMessage = "Error: API key not found.";
                return;
            }

            if (key.IsActive == desiredState)
            {
                statusMessage = desiredState ? "API key is already active." : "API key is already inactive.";
                return;
            }

            key.IsActive = desiredState;
            await DbContext.SaveChangesAsync(HttpContext.RequestAborted);

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
                key.ExpiresUtc,
                key.LastUsedUtc))
            .ToListAsync(cancellationToken);

        apiKeys = items
            .OrderByDescending(key => key.CreatedUtc)
            .ToList();
    }

    private static string GenerateApiKeySecret()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return $"smk_{Convert.ToHexString(buffer).ToLowerInvariant()}";
    }

    private static string GetAccessLevelLabel(ApiKeyAccessLevel level) => level switch
    {
        ApiKeyAccessLevel.Read => "Read",
        ApiKeyAccessLevel.ReadWrite => "Read & Write",
        _ => level.ToString()
    };

    private static string FormatTimestamp(DateTimeOffset? timestamp)
        => timestamp.HasValue ? timestamp.Value.ToLocalTime().ToString("g") : "—";

    private static string FormatExpiration(DateTimeOffset? expiration)
        => expiration.HasValue ? expiration.Value.ToLocalTime().ToString("g") : "Never";

    private sealed record ApiKeyListItem(
        Guid Id,
        string? DisplayName,
        ApiKeyAccessLevel AccessLevel,
        bool IsActive,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? ExpiresUtc,
        DateTimeOffset? LastUsedUtc);

    private sealed class CreateApiKeyInput
    {
        [StringLength(128)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Required]
        [Display(Name = "Access level")]
        public ApiKeyAccessLevel AccessLevel { get; set; } = ApiKeyAccessLevel.Read;

        [Display(Name = "Expires on (UTC)")]
        public DateTime? ExpiresOnUtc { get; set; }
    }
}
