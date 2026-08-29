using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace HVO.SkyMonitor.Common.Security;

public static class CanonicalCredentialClaims
{
    public const string AccountTypeClaim = "account_type";
    public const string UserAccountType = "User";
    public const string SystemAccountType = "System";
    public const string BearerAuthenticationType = "AuthenticationTypes.Federation";

    public static bool HasSingleIdentity(ClaimsPrincipal principal)
        => GetSingleIdentity(principal) is not null;

    public static ClaimsIdentity? GetSingleIdentity(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1)
        {
            return null;
        }

        var identity = identities[0];
        var accountType = GetSingleClaimValue(identity, AccountTypeClaim);
        var nameIdentifiers = identity.FindAll(ClaimTypes.NameIdentifier).ToArray();
        var subjects = identity.FindAll("sub").ToArray();
        var hasApiKeyClaims = identity.Claims.Any(claim => claim.Type is
            ApiKeyClaims.AccessLevel or
            ApiKeyClaims.ApiKeyId or
            ApiKeyClaims.ObservatoryId or
            ApiKeyClaims.AuthenticationType);
        var hasScopes = identity.HasClaim(claim => claim.Type == "scope");
        var apiKeyAuthenticationTypes = identity.FindAll(ApiKeyClaims.AuthenticationType).ToArray();
        var apiKeyAccessLevels = identity.FindAll(ApiKeyClaims.AccessLevel).ToArray();
        var apiKeyObservatoryScopes = identity.FindAll(ApiKeyClaims.ObservatoryId).ToArray();

        var valid = identity.AuthenticationType == IdentityConstants.ApplicationScheme
            ? accountType == UserAccountType
                && nameIdentifiers.Length == 1
                && subjects.Length == 0
                && !hasApiKeyClaims
                && !hasScopes
            : identity.AuthenticationType == ApiKeyAuthenticationOptions.AuthenticationScheme
                ? accountType is UserAccountType or SystemAccountType
                    && nameIdentifiers.Length == 1
                    && subjects.Length == 0
                    && !hasScopes
                    && apiKeyAuthenticationTypes.Length == 1
                    && apiKeyAuthenticationTypes[0].Value
                        == ApiKeyAuthenticationOptions.AuthenticationScheme
                    && apiKeyAccessLevels.Length == 1
                    && apiKeyAccessLevels[0].Value is nameof(ApiKeyAccessLevel.Read) or nameof(ApiKeyAccessLevel.ReadWrite)
                    && apiKeyObservatoryScopes.Length <= 1
                    && (apiKeyObservatoryScopes.Length == 0
                        || Guid.TryParse(apiKeyObservatoryScopes[0].Value, out _))
                : identity.AuthenticationType == BearerAuthenticationType
                    && accountType is UserAccountType or SystemAccountType
                    && nameIdentifiers.Length == 0
                    && subjects.Length == 1
                    && !hasApiKeyClaims;

        return valid && !string.IsNullOrWhiteSpace(GetSubject(identity)) ? identity : null;
    }

    public static string? GetSubject(ClaimsPrincipal principal)
        => GetSingleIdentity(principal) is { } identity ? GetSubject(identity) : null;

    public static string? GetOwnerId(ClaimsPrincipal principal)
        => IsSystem(principal) ? null : GetSubject(principal);

    public static string? GetAccountType(ClaimsPrincipal principal)
        => GetSingleClaimValue(GetSingleIdentity(principal), AccountTypeClaim);

    public static bool IsSystem(ClaimsPrincipal principal)
        => string.Equals(
            GetAccountType(principal),
            SystemAccountType,
            StringComparison.Ordinal);

    public static bool IsCookie(ClaimsIdentity? identity)
        => identity?.AuthenticationType == IdentityConstants.ApplicationScheme;

    public static bool IsApiKey(ClaimsIdentity? identity)
        => identity?.AuthenticationType == ApiKeyAuthenticationOptions.AuthenticationScheme;

    public static bool IsBearer(ClaimsIdentity? identity)
        => identity?.AuthenticationType == BearerAuthenticationType;

    public static string? GetApiKeyAccessLevel(ClaimsPrincipal principal)
        => GetSingleIdentity(principal) is { } identity && IsApiKey(identity)
            ? GetSingleClaimValue(identity, ApiKeyClaims.AccessLevel)
            : null;

    public static Guid? GetObservatoryScope(ClaimsPrincipal principal)
        => GetSingleIdentity(principal) is { } identity
            && IsApiKey(identity)
            && Guid.TryParse(GetSingleClaimValue(identity, ApiKeyClaims.ObservatoryId), out var observatoryId)
                ? observatoryId
                : null;

    public static bool HasScope(ClaimsPrincipal principal, string scope)
        => GetSingleIdentity(principal) is { } identity
            && IsBearer(identity)
            && identity.Claims.Where(claim => claim.Type == "scope")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(scope, StringComparer.Ordinal);

    private static string? GetSubject(ClaimsIdentity identity)
        => IsBearer(identity)
            ? GetSingleClaimValue(identity, "sub")
            : GetSingleClaimValue(identity, ClaimTypes.NameIdentifier);

    private static string? GetSingleClaimValue(ClaimsIdentity? identity, string claimType)
    {
        if (identity is null)
        {
            return null;
        }

        var claims = identity.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
}
