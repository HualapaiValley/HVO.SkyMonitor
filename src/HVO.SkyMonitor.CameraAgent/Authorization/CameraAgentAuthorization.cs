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
}

internal static class CameraAgentAuthorizationServiceCollectionExtensions
{
    public static IServiceCollection AddCameraAgentAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OperationsReadV1);
            AddSiteOwnerPolicy(options, CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
        });
        services.AddScoped<IAuthorizationHandler, SiteOwnerAuthorizationHandler>();

        return services;
    }

    private static void AddSiteOwnerPolicy(AuthorizationOptions options, string policyName)
    {
        options.AddPolicy(policyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(SiteOwnerRequirement.Instance);
        });
    }
}

internal sealed class SiteOwnerRequirement : IAuthorizationRequirement
{
    public static SiteOwnerRequirement Instance { get; } = new();

    private SiteOwnerRequirement()
    {
    }
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
            !string.IsNullOrWhiteSpace(user.NormalizedEmail) &&
            string.Equals(user.NormalizedEmail, _configuredNormalizedEmail, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
    }
}
