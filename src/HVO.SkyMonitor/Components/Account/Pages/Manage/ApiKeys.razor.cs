using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Components.Account.Shared;
using HVO.SkyMonitor.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Components.Account.Pages.Manage;

public partial class ApiKeys
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
            var now = DateTime.UtcNow;
            DateTime? expiresUtc = null;
            if (model.ExpiresOnUtc.HasValue)
            {
                var candidate = DateTime.SpecifyKind(model.ExpiresOnUtc.Value, DateTimeKind.Utc);
                if (candidate <= now)
                {
                    statusMessage = "Error: Expiration must be in the future.";
                    return;
                }

                expiresUtc = candidate;
            }

            var plainKey = GenerateApiKeySecret();
            var hashedKey = KeyHasher.Hash(plainKey);

            var entity = new ApiKey
            {
                Id = Guid.NewGuid().ToString("n"),
                UserId = user.Id,
                Name = GetDisplayName(model.DisplayName, now),
                AccessLevel = model.AccessLevel,
                HashedKey = hashedKey,
                CreatedAt = now,
                ExpiresAt = expiresUtc,
                IsActive = true
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
                key.Name,
                key.AccessLevel,
                key.IsActive,
                key.CreatedAt,
                key.ExpiresAt))
            .ToListAsync(cancellationToken);

        apiKeys = items.OrderByDescending(key => key.CreatedAtUtc).ToList();
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

    private static string FormatTimestamp(DateTime timestamp)
    {
        var utc = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        return utc.ToLocalTime().ToString("g");
    }

    private static string FormatExpiration(DateTime? expiration)
    {
        if (expiration is null)
        {
            return "Never";
        }

        return FormatTimestamp(expiration.Value);
    }

    private static string GetDisplayName(string? candidate, DateTime nowUtc)
        => string.IsNullOrWhiteSpace(candidate)
            ? $"Unnamed key ({nowUtc:yyyyMMddHHmmss})"
            : candidate.Trim();

    private sealed record ApiKeyListItem(
        string Id,
        string Name,
        ApiKeyAccessLevel AccessLevel,
        bool IsActive,
        DateTime CreatedAtUtc,
        DateTime? ExpiresAtUtc);

    private sealed class CreateApiKeyInput
    {
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Required]
        [Display(Name = "Access level")]
        public ApiKeyAccessLevel AccessLevel { get; set; } = ApiKeyAccessLevel.Read;

        [Display(Name = "Expires on (UTC)")]
        public DateTime? ExpiresOnUtc { get; set; }
    }
}
