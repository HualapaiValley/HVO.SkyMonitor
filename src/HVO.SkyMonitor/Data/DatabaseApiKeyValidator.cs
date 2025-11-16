using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Data;

/// <summary>
/// Validates API keys against the database.
/// Phase 6: Enhanced with metrics and logging.
/// </summary>
internal sealed class DatabaseApiKeyValidator : IApiKeyValidator
{
    private readonly ApplicationDbContext _context;
    private readonly IApiKeyHasher _hasher;
    private readonly AuthenticationMetrics? _metrics;
    private readonly IAuthenticationEventLogger? _eventLogger;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public DatabaseApiKeyValidator(
        ApplicationDbContext context,
        IApiKeyHasher hasher,
        AuthenticationMetrics? metrics = null,
        IAuthenticationEventLogger? eventLogger = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _context = context;
        _hasher = hasher;
        _metrics = metrics;
        _eventLogger = eventLogger;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string apiKey)
    {
        var hashedKey = _hasher.Hash(apiKey);

        var key = await _context.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.HashedKey == hashedKey && k.IsActive);

        if (key == null)
        {
            // Phase 6: Record failed API key authentication
            _metrics?.RecordApiKeyAuthentication(null, success: false);
            return new ApiKeyValidationResult { IsValid = false };
        }

        if (key.ExpiresUtc.HasValue && key.ExpiresUtc.Value < DateTime.UtcNow)
        {
            // Phase 6: Record expired API key attempt
            _metrics?.RecordApiKeyAuthentication(key.Id, success: false);
            return new ApiKeyValidationResult { IsValid = false };
        }

        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == key.UserId);

        if (user == null)
        {
            // Phase 6: Record orphaned API key (user deleted)
            _metrics?.RecordApiKeyAuthentication(key.Id, success: false);
            return new ApiKeyValidationResult { IsValid = false };
        }

        // Phase 6: Record successful API key authentication and log usage
        _metrics?.RecordApiKeyAuthentication(key.Id, success: true, key.AccessLevel.ToString());

        var endpoint = _httpContextAccessor?.HttpContext?.Request.Path.Value ?? "unknown";
        _eventLogger?.LogApiKeyUsed(key.Id, key.UserId, key.AccessLevel.ToString(), endpoint);

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
