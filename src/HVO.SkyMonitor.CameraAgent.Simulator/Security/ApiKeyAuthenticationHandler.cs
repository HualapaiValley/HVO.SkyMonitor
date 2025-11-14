using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Simulator.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Security;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IApiKeyHasher _hasher;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApplicationDbContext dbContext,
        IApiKeyHasher hasher)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
        _hasher = hasher;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

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
            Logger.LogWarning("API key authentication failed for hash {ApiKeyHash}.", hashedKey);
            return AuthenticateResult.Fail("Invalid API Key.");
        }

        if (keyEntity.ExpiresUtc.HasValue && keyEntity.ExpiresUtc.Value < currentUtc)
        {
            Logger.LogInformation("API key {ApiKeyId} is expired as of {ExpirationUtc}.", keyEntity.Id, keyEntity.ExpiresUtc);
            return AuthenticateResult.Fail("Invalid API Key.");
        }

        if (keyEntity.User is null)
        {
            Logger.LogError("API key {ApiKeyId} is not associated with a user.", keyEntity.Id);
            return AuthenticateResult.Fail("Invalid API Key.");
        }

        var user = keyEntity.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ApiKeyClaims.ApiKeyId, keyEntity.Id.ToString()),
            new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new(ApiKeyClaims.AccessLevel, keyEntity.AccessLevel.ToString())
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
