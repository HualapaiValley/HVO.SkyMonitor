using System.Security.Cryptography;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IApiKeyLifecycleService
{
    Task<ApiKeyCreationResult> CreateAsync(
        string userId,
        string createdBy,
        string displayName,
        ApiKeyAccessLevel accessLevel,
        DateTimeOffset? expiresUtc,
        CancellationToken cancellationToken);

    Task<bool> SetActiveAsync(
        string userId,
        string keyId,
        bool active,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        string userId,
        string keyId,
        CancellationToken cancellationToken);
}

internal sealed record ApiKeyCreationResult(string PlaintextKey, ApiKey Entity);

internal sealed class ApiKeyLifecycleService(
    ApplicationDbContext dbContext,
    IApiKeyHasher keyHasher,
    IApiKeyAuditLogger auditLogger,
    TimeProvider timeProvider) : IApiKeyLifecycleService
{
    public async Task<ApiKeyCreationResult> CreateAsync(
        string userId,
        string createdBy,
        string displayName,
        ApiKeyAccessLevel accessLevel,
        DateTimeOffset? expiresUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!Enum.IsDefined(accessLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(accessLevel));
        }

        var plaintextKey = GenerateSecret();
        var entity = new ApiKey
        {
            Id = Guid.NewGuid().ToString("n"),
            UserId = userId,
            DisplayName = displayName,
            AccessLevel = accessLevel,
            HashedKey = keyHasher.Hash(plaintextKey),
            CreatedUtc = timeProvider.GetUtcNow(),
            ExpiresUtc = expiresUtc,
            CreatedBy = createdBy,
            IsActive = true
        };

        dbContext.ApiKeys.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        auditLogger.LogKeyCreated(entity.Id, userId, entity.DisplayName, entity.AccessLevel.ToString(), entity.ExpiresUtc);
        return new ApiKeyCreationResult(plaintextKey, entity);
    }

    public async Task<bool> SetActiveAsync(
        string userId,
        string keyId,
        bool active,
        CancellationToken cancellationToken)
    {
        var key = await FindOwnedKeyAsync(userId, keyId, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return false;
        }

        if (key.IsActive == active)
        {
            return true;
        }

        key.IsActive = active;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (active)
        {
            auditLogger.LogKeyActivated(key.Id, userId, key.DisplayName);
        }
        else
        {
            auditLogger.LogKeyDeactivated(key.Id, userId, key.DisplayName);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(
        string userId,
        string keyId,
        CancellationToken cancellationToken)
    {
        var key = await FindOwnedKeyAsync(userId, keyId, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return false;
        }

        dbContext.ApiKeys.Remove(key);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        auditLogger.LogKeyDeleted(key.Id, userId, key.DisplayName);
        return true;
    }

    private Task<ApiKey?> FindOwnedKeyAsync(
        string userId,
        string keyId,
        CancellationToken cancellationToken)
        => dbContext.ApiKeys.FirstOrDefaultAsync(
            key => key.Id == keyId && key.UserId == userId,
            cancellationToken);

    private static string GenerateSecret()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return $"smk_{Convert.ToHexString(buffer).ToLowerInvariant()}";
    }
}
