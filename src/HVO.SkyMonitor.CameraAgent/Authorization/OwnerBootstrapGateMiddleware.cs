using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Authorization;

internal sealed class OwnerBootstrapGateMiddleware(
    RequestDelegate next,
    ILogger<OwnerBootstrapGateMiddleware> logger)
{
    private static readonly EventId OwnerOperationDeniedEvent = new(4183, "OwnerOperationDeniedDuringBootstrap");
    internal const string DenialReason = OwnerBootstrapStates.PasswordChangeRequired;
    internal const string AuthorizationReasonHeader = "X-HVO-Authorization-Reason";
    internal const string ReplacementPath = "/Account/ReplaceTemporaryPassword";
    internal const string StatusPath = "/api/internal/owner-bootstrap/status";
    internal const string VerificationPath = "/api/internal/owner-bootstrap/installation-verification";

    public async Task InvokeAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        IOptions<IdentityOptions> identityOptions)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
        if (user?.IsSiteOwner != true)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!await HasCurrentSecurityStampAsync(context.User, user, userManager, identityOptions.Value)
                .ConfigureAwait(false))
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            await DenyUnauthenticatedAsync(context).ConfigureAwait(false);
            return;
        }

        if (!user.PasswordChangeRequired || IsBootstrapPath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    OwnerOperationDeniedEvent,
                    "Denied an owner operation while password replacement is required; {Reason}",
                    DenialReason);
            }
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers[AuthorizationReasonHeader] = DenialReason;
            await context.Response.WriteAsJsonAsync(new
            {
                status = StatusCodes.Status403Forbidden,
                code = DenialReason
            }).ConfigureAwait(false);
            return;
        }

        context.Response.Redirect(ReplacementPath);
    }

    private static bool IsBootstrapPath(PathString path)
    {
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return IsExactPath(path, StatusPath) || IsExactPath(path, VerificationPath);
        }

        return IsExactPath(path, "/Account/Login") ||
               IsExactPath(path, "/Account/Logout") ||
               IsExactPath(path, ReplacementPath) ||
               IsExactPath(path, "/health") ||
               IsExactPath(path, "/alive") ||
               IsExactPath(path, "/metrics") ||
                IsExactPath(path, "/favicon.png") ||
                IsExactPath(path, "/HVO.SkyMonitor.CameraAgent.styles.css") ||
                (path.Value?.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase) ?? false) ||
                path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExactPath(PathString path, string expected)
        => string.Equals(path.Value, expected, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HasCurrentSecurityStampAsync(
        ClaimsPrincipal principal,
        ApplicationUser user,
        UserManager<ApplicationUser> userManager,
        IdentityOptions options)
    {
        var principalStamp = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
        if (principalStamp is null)
        {
            return false;
        }

        var durableStamp = await userManager.GetSecurityStampAsync(user).ConfigureAwait(false);
        return string.Equals(principalStamp, durableStamp, StringComparison.Ordinal);
    }

    private static Task DenyUnauthenticatedAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var returnUrl = string.Concat(context.Request.PathBase, context.Request.Path, context.Request.QueryString);
        context.Response.Redirect($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }
}
