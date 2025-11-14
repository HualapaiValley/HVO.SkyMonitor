using HVO.SkyMonitor.CameraAgent.Simulator.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Security;

public sealed class CameraAgentApiKeyValidator : IApiKeyValidator
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IApiKeyHasher _hasher;
    private readonly ILogger<CameraAgentApiKeyValidator> _logger;

    public CameraAgentApiKeyValidator(
        ApplicationDbContext dbContext,
        IApiKeyHasher hasher,
        ILogger<CameraAgentApiKeyValidator> logger)
    {
        _dbContext = dbContext;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<ApiKeyValidationResult> ValidateAsync(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var hashedKey = _hasher.Hash(apiKey);
        var currentUtc = DateTimeOffset.UtcNow;

        var keyEntity = await _dbContext.ApiKeys
            .Include(key => key.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(key =>
                key.KeyHash == hashedKey &&
                key.IsActive);

        if (keyEntity is null)
        {
            _logger.LogWarning("API key authentication failed for hash {ApiKeyHash}.", hashedKey);
            return Invalid();
        }

        if (keyEntity.ExpiresUtc.HasValue && keyEntity.ExpiresUtc.Value < currentUtc)
        {
            _logger.LogInformation(
                "API key {ApiKeyId} is expired as of {ExpirationUtc}.",
                keyEntity.Id,
                keyEntity.ExpiresUtc);
            return Invalid();
        }

        if (keyEntity.User is null)
        {
            _logger.LogError("API key {ApiKeyId} is not associated with a user.", keyEntity.Id);
            return Invalid();
        }

        return new ApiKeyValidationResult
        {
            IsValid = true,
            KeyId = keyEntity.Id.ToString(),
            KeyName = keyEntity.DisplayName,
            DisplayName = keyEntity.DisplayName ?? keyEntity.User.UserName ?? keyEntity.User.Email ?? keyEntity.User.Id,
            NameIdentifier = keyEntity.User.Id,
            Email = keyEntity.User.Email,
            AccessLevel = keyEntity.AccessLevel
        };
    }

    private static ApiKeyValidationResult Invalid()
        => new() { IsValid = false };
}
