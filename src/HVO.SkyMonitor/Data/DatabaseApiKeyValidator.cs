using HVO.SkyMonitor.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Validates API keys against the database.
/// </summary>
public class DatabaseApiKeyValidator : IApiKeyValidator
{
    private readonly ApplicationDbContext _context;
    private readonly IApiKeyHasher _hasher;

    public DatabaseApiKeyValidator(ApplicationDbContext context, IApiKeyHasher hasher)
    {
        _context = context;
        _hasher = hasher;
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string apiKey)
    {
        var hashedKey = _hasher.Hash(apiKey);

        var key = await _context.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.HashedKey == hashedKey && k.IsActive);

        if (key == null)
        {
            return new ApiKeyValidationResult { IsValid = false };
        }

        if (key.ExpiresUtc.HasValue && key.ExpiresUtc.Value < DateTime.UtcNow)
        {
            return new ApiKeyValidationResult { IsValid = false };
        }

        var user = key.User as ApplicationUser;

        return new ApiKeyValidationResult
        {
            IsValid = true,
            KeyId = key.Id,
            KeyName = key.DisplayName,
            DisplayName = key.DisplayName,
            NameIdentifier = key.UserId,
            Email = key.User?.Email,
            AccessLevel = key.AccessLevel,
            AccountType = user?.AccountType.ToString() ?? "User"
        };
    }
}
