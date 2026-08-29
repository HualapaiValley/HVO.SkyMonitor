using System.Security.Claims;
using System.Text.Encodings.Web;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The test server authentication service constructs this handler through dependency injection.")]
internal sealed class IntegrationUserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityOptions> identityOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "CameraAgentIntegrationUser";
    internal const string UserIdHeader = "X-CameraAgent-Integration-User";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[UserIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return AuthenticateResult.NoResult();
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        };
        var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
        if (user is not null)
        {
            var securityStamp = await userManager.GetSecurityStampAsync(user).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(securityStamp))
            {
                claims.Add(new Claim(
                    identityOptions.Value.ClaimsIdentity.SecurityStampClaimType,
                    securityStamp));
            }
        }
        var identity = new ClaimsIdentity(
            claims,
            IdentityConstants.ApplicationScheme);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
