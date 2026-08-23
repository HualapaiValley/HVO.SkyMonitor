using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Configuration;
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
    UserManager<ApplicationUser> userManager,
    ILookupNormalizer normalizer,
    IOptions<LocalIdentityOptions> identityOptions)
    : AuthorizationHandler<SiteOwnerRequirement>
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly string _configuredNormalizedEmail = normalizer.NormalizeEmail(identityOptions.Value.AdminEmail);

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SiteOwnerRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var user = await _userManager.GetUserAsync(context.User).ConfigureAwait(false);
        if (user?.IsSiteOwner == true &&
            (!requirement.RequireReadyOwner || !user.PasswordChangeRequired) &&
            !string.IsNullOrWhiteSpace(user.NormalizedEmail) &&
            string.Equals(user.NormalizedEmail, _configuredNormalizedEmail, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
    }
}
