using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{

    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal _,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            var redirectTarget = BuildLocalRedirectPath(returnUrl);
            return TypedResults.LocalRedirect(redirectTarget);
        });

        return accountGroup;

        static string BuildLocalRedirectPath(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return "~/";
            }

            var trimmed = candidate.Trim();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("\\\\", StringComparison.Ordinal)
                || trimmed.Contains("://", StringComparison.Ordinal)
                || !Uri.TryCreate(trimmed, UriKind.Relative, out _))
            {
                return "~/";
            }

            return $"~/{trimmed.TrimStart('/')}";
        }
    }
}
