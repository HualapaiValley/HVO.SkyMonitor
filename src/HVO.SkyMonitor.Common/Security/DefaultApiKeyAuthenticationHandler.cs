using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Common.Security;

public sealed class DefaultApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly IApiKeyValidator _apiKeyValidator;

    public DefaultApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyValidator apiKeyValidator)
        : base(options, logger, encoder)
    {
        _apiKeyValidator = apiKeyValidator;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var apiKeyHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var providedApiKey = apiKeyHeaderValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            return AuthenticateResult.NoResult();
        }

        var validationResult = await _apiKeyValidator.ValidateAsync(providedApiKey).ConfigureAwait(false);
        if (!validationResult.IsValid)
        {
            return AuthenticateResult.Fail("Invalid API key");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, validationResult.DisplayName ?? validationResult.KeyName ?? validationResult.KeyId ?? "API Key"),
            new(ApiKeyClaims.ApiKeyId, validationResult.KeyId ?? string.Empty),
            new(ApiKeyClaims.AccessLevel, validationResult.AccessLevel.ToString()),
            new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new("account_type", validationResult.AccountType) // Add account type claim
        };

        if (!string.IsNullOrWhiteSpace(validationResult.NameIdentifier))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, validationResult.NameIdentifier));
        }

        if (!string.IsNullOrWhiteSpace(validationResult.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, validationResult.Email));
        }

        if (validationResult.ObservatoryId is { } observatoryId)
        {
            claims.Add(new Claim(ApiKeyClaims.ObservatoryId, observatoryId.ToString("D")));
        }

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.AuthenticationScheme);

        return AuthenticateResult.Success(ticket);
    }
}

public sealed class ApiKeyValidationResult
{
    public bool IsValid { get; init; }
    public string? KeyId { get; init; }
    public string? KeyName { get; init; }
    public string? DisplayName { get; init; }
    public string? NameIdentifier { get; init; }
    public string? Email { get; init; }
    public ApiKeyAccessLevel AccessLevel { get; init; } = ApiKeyAccessLevel.Read;
    public Guid? ObservatoryId { get; init; }
    public string AccountType { get; init; } = "User"; // Default to User for backward compatibility
}

public interface IApiKeyValidator
{
    Task<ApiKeyValidationResult> ValidateAsync(string apiKey);
}
