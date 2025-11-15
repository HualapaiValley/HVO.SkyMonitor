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
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.HashedKey == hashedKey && k.IsActive);

        if (key == null)
        {
            return new ApiKeyValidationResult { IsValid = false };
        }

        if (key.ExpiresUtc.HasValue && key.ExpiresUtc.Value < DateTime.UtcNow)
        {
            return new ApiKeyValidationResult { IsValid = false };
        }

        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == key.UserId);

        if (user == null)
        {
            return new ApiKeyValidationResult { IsValid = false };
        }

        return new ApiKeyValidationResult
        {
            IsValid = true,
            KeyId = key.Id,
            KeyName = key.DisplayName,
            DisplayName = key.DisplayName,
            NameIdentifier = key.UserId,
            Email = user.Email,
            AccessLevel = key.AccessLevel,
            AccountType = user.AccountType.ToString()
        };
    }
}
