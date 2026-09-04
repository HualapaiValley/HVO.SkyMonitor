using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Authorization;

public static class CameraAgentAuthorizationPolicyNames
{
    public const string OperationsReadV1 = "CameraAgent.Operations.Read.V1";
    public const string OperationsMutateV1 = "CameraAgent.Operations.Mutate.V1";
    public const string OwnerBootstrapReadV1 = "CameraAgent.OwnerBootstrap.Read.V1";
}

internal static class CameraAgentAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddCameraAgentAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OperationsReadV1, requireReadyOwner: true);
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OperationsMutateV1, requireReadyOwner: true);
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OwnerBootstrapReadV1, requireReadyOwner: false);
        });
        services.AddScoped<IAuthorizationHandler, SiteOwnerAuthorizationHandler>();

        return services;
    }

    private static void AddSiteOwnerPolicy(
        AuthorizationOptions options,
        string policyName,
        bool requireReadyOwner)
    {
        options.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(requireReadyOwner
                ? SiteOwnerRequirement.ReadyOwner
                : SiteOwnerRequirement.ConfiguredOwner);
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
