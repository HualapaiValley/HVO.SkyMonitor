using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.CameraAgent.Authentication;

/// <summary>One API key as its owner sees it. The key itself is never kept, so it is never listed.</summary>
/// <param name="Id">The public key identifier, which is also the start of the key.</param>
/// <param name="Readable">False when the stored record cannot be read; such a key never authenticates.</param>
internal sealed record CameraAgentApiKeyInfo(
    string Id,
    string Label,
    ApiKeyAccessLevel Access,
    DateTimeOffset CreatedUtc,
    bool Readable = true);

/// <summary>A presented key that matches a stored one.</summary>
internal sealed record CameraAgentApiKeyMatch(string OwnerId, string Id, ApiKeyAccessLevel Access);

internal enum CameraAgentApiKeyCreateStatus
{
    Created,
    InvalidLabel,
    InvalidAccess,
    LimitReached
}

/// <param name="Token">The new key, returned once and never stored.</param>
internal sealed record CameraAgentApiKeyCreateResult(
    CameraAgentApiKeyCreateStatus Status,
    CameraAgentApiKeyInfo? Key = null,
    string? Token = null);

/// <summary>
/// Keeps the site owner's API keys in the local identity database, as authentication tokens of the owner account: the
/// key identifier is the token name and the value holds the key's SHA-256, its access and its label. No plaintext key
/// is ever written. Deleting the record revokes the key at the next request.
/// </summary>
/// <remarks>
/// The identity schema already has this table, so keys need no schema change. A key belongs to one account; when the
/// configured owner changes, the previous account stops being the site owner and its keys stop authorizing anything.
/// </remarks>
internal sealed partial class CameraAgentApiKeyStore(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<CameraAgentApiKeyStore> logger)
{
    internal const string LoginProvider = "HVO.SkyMonitor.CameraAgent.ApiKey";
    internal const int MaximumKeysPerOwner = 20;
    internal const int MaximumLabelLength = 64;
    private const int RecordSchema = 1;

    private static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<ApiKeyAccessLevel>() }
    };

    public async Task<IReadOnlyList<CameraAgentApiKeyInfo>> ListAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var rows = await dbContext.UserTokens.AsNoTracking()
            .Where(token => token.UserId == ownerId && token.LoginProvider == LoginProvider)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows
            .Select(row => Read(row.Value) is { } record
                ? new CameraAgentApiKeyInfo(row.Name, record.Label, record.Access, record.CreatedUtc)
                : new CameraAgentApiKeyInfo(row.Name, "Unreadable key record", ApiKeyAccessLevel.Read,
                    DateTimeOffset.UnixEpoch, Readable: false))
            .OrderByDescending(key => key.CreatedUtc)
            .ThenBy(key => key.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Creates a key for the owner. The returned token is the only copy: the caller shows it once and never keeps it.
    /// </summary>
    public async Task<CameraAgentApiKeyCreateResult> CreateAsync(
        string ownerId,
        string? label,
        ApiKeyAccessLevel access,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var name = label?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaximumLabelLength || name.Any(char.IsControl))
        {
            return new(CameraAgentApiKeyCreateStatus.InvalidLabel);
        }

        if (access is not (ApiKeyAccessLevel.Read or ApiKeyAccessLevel.ReadWrite))
        {
            return new(CameraAgentApiKeyCreateStatus.InvalidAccess);
        }

        var existing = await dbContext.UserTokens
            .CountAsync(token => token.UserId == ownerId && token.LoginProvider == LoginProvider, cancellationToken)
            .ConfigureAwait(false);
        if (existing >= MaximumKeysPerOwner)
        {
            return new(CameraAgentApiKeyCreateStatus.LimitReached);
        }

        var createdUtc = timeProvider.GetUtcNow();
        var (id, token) = CameraAgentApiKeyToken.Create();
        var row = new IdentityUserToken<string>
        {
            UserId = ownerId,
            LoginProvider = LoginProvider,
            Name = id,
            Value = JsonSerializer.Serialize(
                new StoredKey(RecordSchema, CameraAgentApiKeyToken.Hash(token), access, name, createdUtc), RecordJson)
        };
        dbContext.UserTokens.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        dbContext.Entry(row).State = EntityState.Detached;
        LogCreated(logger, id, access, ownerId);
        return new(CameraAgentApiKeyCreateStatus.Created, new CameraAgentApiKeyInfo(id, name, access, createdUtc), token);
    }

    /// <summary>Deletes one of the owner's keys; false when the owner has no key with that identifier.</summary>
    public async Task<bool> RevokeAsync(string ownerId, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(id);
        var row = await dbContext.UserTokens
            .SingleOrDefaultAsync(
                token => token.UserId == ownerId && token.LoginProvider == LoginProvider && token.Name == id,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        dbContext.UserTokens.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogRevoked(logger, id, ownerId);
        return true;
    }

    /// <summary>
    /// Deletes every key an account has, inside whatever transaction the caller holds on the identity database, and
    /// returns how many it deleted. Owner password recovery calls this, because recovery is the response to losing
    /// control of the account; the caller logs the count with <see cref="LogRevokedAll"/> once its transaction commits.
    /// </summary>
    internal static async Task<int> RemoveAllAsync(
        ApplicationDbContext context,
        string ownerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var rows = await context.UserTokens
            .Where(token => token.UserId == ownerId && token.LoginProvider == LoginProvider)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        context.UserTokens.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>The stored key a presented key matches, or null when it is malformed, unknown, revoked or wrong.</summary>
    public async Task<CameraAgentApiKeyMatch?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (!CameraAgentApiKeyToken.TryReadId(token, out var id))
        {
            return null;
        }

        var rows = await dbContext.UserTokens.AsNoTracking()
            .Where(row => row.LoginProvider == LoginProvider && row.Name == id)
            .OrderBy(static row => row.UserId)
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count != 1 || Read(rows[0].Value) is not { } record)
        {
            return null;
        }

        return CameraAgentApiKeyToken.Matches(token, record.Sha256)
            ? new CameraAgentApiKeyMatch(rows[0].UserId, id, record.Access)
            : null;
    }

    private StoredKey? Read(string? value)
    {
        try
        {
            return value is null ? null : JsonSerializer.Deserialize<StoredKey>(value, RecordJson) is
            {
                Schema: RecordSchema,
                Sha256.Length: 64,
                Access: ApiKeyAccessLevel.Read or ApiKeyAccessLevel.ReadWrite,
                Label: not null
            } record
                ? record
                : null;
        }
        catch (JsonException)
        {
            LogUnreadable(logger);
            return null;
        }
    }

    private sealed record StoredKey(int Schema, string Sha256, ApiKeyAccessLevel Access, string Label, DateTimeOffset CreatedUtc);

    [LoggerMessage(4190, LogLevel.Information, "API key {KeyId} with {Access} access created for owner {OwnerId}")]
    private static partial void LogCreated(ILogger logger, string keyId, ApiKeyAccessLevel access, string ownerId);

    [LoggerMessage(4191, LogLevel.Information, "API key {KeyId} revoked by owner {OwnerId}")]
    private static partial void LogRevoked(ILogger logger, string keyId, string ownerId);

    [LoggerMessage(4192, LogLevel.Information, "Owner password recovery revoked all {Count} API keys of owner {OwnerId}")]
    internal static partial void LogRevokedAll(ILogger logger, int count, string ownerId);

    [LoggerMessage(4196, LogLevel.Warning, "An API key record in the identity database could not be read")]
    private static partial void LogUnreadable(ILogger logger);
}
