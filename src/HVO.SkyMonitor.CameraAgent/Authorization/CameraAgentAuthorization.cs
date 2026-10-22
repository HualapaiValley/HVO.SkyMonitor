using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Authorization;

public static class CameraAgentAuthorizationPolicyNames
{
    /// <summary>Operations reads: the ready site owner's session, or one of the owner's API keys.</summary>
    public const string OperationsReadV1 = "CameraAgent.Operations.Read.V1";

    /// <summary>Operations changes: the ready site owner's session, or one of the owner's read-write API keys.</summary>
    public const string OperationsMutateV1 = "CameraAgent.Operations.Mutate.V1";

    /// <summary>The owner bootstrap state: the configured site owner's session only.</summary>
    public const string OwnerBootstrapReadV1 = "CameraAgent.OwnerBootstrap.Read.V1";

    /// <summary>Creating, listing and revoking API keys: the ready site owner's session only, never a key.</summary>
    public const string ApiKeyManagementV1 = "CameraAgent.ApiKeys.Manage.V1";
}

internal static class CameraAgentAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddCameraAgentAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OperationsReadV1, requireReadyOwner: true,
                CredentialRequirement.ReadAccess);
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OperationsMutateV1, requireReadyOwner: true,
                CredentialRequirement.ReadWriteAccess);
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1, requireReadyOwner: false,
                CredentialRequirement.OwnerSession);
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.ApiKeyManagementV1, requireReadyOwner: true,
                CredentialRequirement.OwnerSession);
        });
        services.AddScoped<IAuthorizationHandler, SiteOwnerAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, CredentialAuthorizationHandler>();

        return services;
    }

    private static void AddSiteOwnerPolicy(
        AuthorizationOptions options,
        string policyName,
        bool requireReadyOwner,
        CredentialRequirement credential)
    {
        options.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(requireReadyOwner
                ? SiteOwnerRequirement.ReadyOwner
                : SiteOwnerRequirement.ConfiguredOwner);
            policy.AddRequirements(credential);
        });
    }
}

internal sealed class CanonicalLocalUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);
        identity.AddClaim(new Claim(
            CanonicalCredentialClaims.AccountTypeClaim,
            CanonicalCredentialClaims.UserAccountType));
        return identity;
    }
}

internal static class CameraAgentCredentialAccess
{
    public static string? GetOwnerId(ClaimsPrincipal principal)
        => CanonicalCredentialClaims.GetOwnerId(principal);

    /// <summary>True when the request authenticated with one of the owner's API keys rather than the session.</summary>
    public static bool IsApiKey(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return CanonicalCredentialClaims.IsApiKey(CanonicalCredentialClaims.GetSingleIdentity(principal));
    }

    /// <summary>
    /// Whether a state-changing request is safe from cross-site request forgery. A session request must carry a valid
    /// antiforgery token. An API key travels only in the <c>Authorization</c> header, which a browser never adds to a
    /// cross-site request by itself, so a key-authenticated request needs none.
    /// </summary>
    public static bool IsForgeryProtected(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsApiKey(context.User) ||
               context.Features.Get<IAntiforgeryValidationFeature>()?.IsValid == true;
    }
}

/// <summary>Which credential a policy accepts, beyond its site-owner checks.</summary>
internal sealed class CredentialRequirement : IAuthorizationRequirement
{
    /// <summary>The owner's session, or an API key with read or read-write access.</summary>
    public static CredentialRequirement ReadAccess { get; } = new(ApiKeyAccessLevel.Read);

    /// <summary>The owner's session, or an API key with read-write access.</summary>
    public static CredentialRequirement ReadWriteAccess { get; } = new(ApiKeyAccessLevel.ReadWrite);

    /// <summary>The owner's session only; no API key qualifies.</summary>
    public static CredentialRequirement OwnerSession { get; } = new(null);

    private CredentialRequirement(ApiKeyAccessLevel? apiKeyAccess)
    {
        ApiKeyAccess = apiKeyAccess;
    }

    /// <summary>The least API key access that qualifies, or null when no API key does.</summary>
    internal ApiKeyAccessLevel? ApiKeyAccess { get; }
}

/// <summary>
/// Accepts the site owner's browser session for every policy, and an API key only where the policy names an access
/// level the key holds. Any other credential, such as a bearer token, is refused.
/// </summary>
internal sealed class CredentialAuthorizationHandler : AuthorizationHandler<CredentialRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CredentialRequirement requirement)
    {
        var identity = CanonicalCredentialClaims.GetSingleIdentity(context.User);
        if (CanonicalCredentialClaims.IsCookie(identity))
        {
            context.Succeed(requirement);
        }
        else if (CanonicalCredentialClaims.IsApiKey(identity) &&
                 requirement.ApiKeyAccess is { } needed &&
                 Enum.TryParse<ApiKeyAccessLevel>(
                     CanonicalCredentialClaims.GetApiKeyAccessLevel(context.User), out var held) &&
                 (held == ApiKeyAccessLevel.ReadWrite || held == needed))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class SiteOwnerRequirement : IAuthorizationRequirement
{
    public static SiteOwnerRequirement ReadyOwner { get; } = new(requireReadyOwner: true);
    public static SiteOwnerRequirement ConfiguredOwner { get; } = new(requireReadyOwner: false);

    private SiteOwnerRequirement(bool requireReadyOwner)
    {
        RequireReadyOwner = requireReadyOwner;
    }

    internal bool RequireReadyOwner { get; }
}

internal sealed class SiteOwnerAuthorizationHandler(
    IServiceScopeFactory scopeFactory,
    ILookupNormalizer normalizer,
    IOptions<LocalIdentityOptions> identityOptions,
    IOptions<IdentityOptions> identityFrameworkOptions)
    : AuthorizationHandler<SiteOwnerRequirement>
{
    private readonly string _configuredNormalizedEmail = normalizer.NormalizeEmail(identityOptions.Value.AdminEmail);

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SiteOwnerRequirement requirement)
    {
        var ownerId = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (ownerId is null)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(ownerId).ConfigureAwait(false);
        if (user?.IsSiteOwner == true &&
            (!requirement.RequireReadyOwner || !user.PasswordChangeRequired) &&
            !string.IsNullOrWhiteSpace(user.NormalizedEmail) &&
            string.Equals(user.NormalizedEmail, _configuredNormalizedEmail, StringComparison.Ordinal) &&
            await HasCurrentSecurityStampAsync(
                userManager, context.User, user, identityFrameworkOptions.Value).ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }

    private static async Task<bool> HasCurrentSecurityStampAsync(
        UserManager<ApplicationUser> userManager,
        ClaimsPrincipal principal,
        ApplicationUser user,
        IdentityOptions options)
    {
        if (!userManager.SupportsUserSecurityStamp)
        {
            return false;
        }

        var principalStamp = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
        var durableStamp = await userManager.GetSecurityStampAsync(user).ConfigureAwait(false);
        return !string.IsNullOrEmpty(principalStamp) &&
               string.Equals(principalStamp, durableStamp, StringComparison.Ordinal);
    }
}
