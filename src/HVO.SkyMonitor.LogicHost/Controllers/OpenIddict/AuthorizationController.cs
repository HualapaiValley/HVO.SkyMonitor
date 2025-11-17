using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace HVO.SkyMonitor.LogicHost.Controllers.OpenIddict;

/// <summary>
/// Handles OAuth2/OpenID Connect authorization and token issuance.
/// Supports Authorization Code + PKCE and Client Credentials flows.
/// Identity Hardening: Enhanced with rate limiting, metrics, and logging.
/// </summary>
[SuppressMessage("Usage", "CA1515:Consider making the type internal", Justification = "Controllers must remain public for routing.")]
public sealed class AuthorizationController : Controller
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictAuthorizationManager _authorizationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuthenticationMetrics _metrics;
    private readonly IAuthenticationEventLogger _eventLogger;

    public AuthorizationController(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictAuthorizationManager authorizationManager,
        IOpenIddictScopeManager scopeManager,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        AuthenticationMetrics metrics,
        IAuthenticationEventLogger eventLogger)
    {
        _applicationManager = applicationManager;
        _authorizationManager = authorizationManager;
        _scopeManager = scopeManager;
        _signInManager = signInManager;
        _userManager = userManager;
        _metrics = metrics;
        _eventLogger = eventLogger;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Retrieve the user principal stored in the authentication cookie
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        // If the user principal can't be extracted, redirect to login page
        if (!result.Succeeded)
        {
            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(
                        Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList())
                });
        }

        // Retrieve the profile of the logged in user
        var user = await _userManager.GetUserAsync(result.Principal) ??
            throw new InvalidOperationException("The user details cannot be retrieved.");

        // Retrieve the application details from the database
        var application = await _applicationManager.FindByClientIdAsync(request.ClientId!) ??
            throw new InvalidOperationException("Details concerning the calling client application cannot be found.");

        // Create a new ClaimsIdentity containing the claims that will be used to create tokens
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // Add the claims that will be persisted in the tokens
        identity.SetClaim(Claims.Subject, await _userManager.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await _userManager.GetEmailAsync(user))
                .SetClaim(Claims.Name, await _userManager.GetUserNameAsync(user));

        // Add custom claim for account type
        identity.SetClaim("account_type", user.AccountType.ToString());

        // Set the list of scopes granted to the client application
        identity.SetScopes(request.GetScopes());
        identity.SetResources(await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());

        // Create a permanent authorization
        var authorization = await _authorizationManager.CreateAsync(
            identity: identity,
            subject: await _userManager.GetUserIdAsync(user),
            client: (await _applicationManager.GetIdAsync(application))!,
            type: AuthorizationTypes.Permanent,
            scopes: identity.GetScopes());

        identity.SetAuthorizationId(await _authorizationManager.GetIdAsync(authorization));
        identity.SetDestinations(GetDestinations);

        // Return a sign-in result to OpenIddict to generate the authorization response
        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    [EnableRateLimiting("token")] // Identity Hardening: Rate limiting for token endpoint
    public async Task<IActionResult> Exchange()
    {
        // Identity Hardening: Track token request timing
        var stopwatch = Stopwatch.StartNew();

        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        ClaimsPrincipal claimsPrincipal;
        string grantType = "unknown";
        string clientId = request.ClientId ?? "unknown";
        bool success = false;

        try
        {
            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                grantType = request.IsAuthorizationCodeGrantType() ? "authorization_code" : "refresh_token";

                // Retrieve the claims principal stored in the authorization code/refresh token
                var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

                // Retrieve the user profile corresponding to the authorization code/refresh token
                var user = await _userManager.FindByIdAsync(result.Principal!.GetClaim(Claims.Subject)!);
                if (user == null)
                {
                    return Forbid(
                        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                        properties: new AuthenticationProperties(new Dictionary<string, string?>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid."
                        }));
                }

                // Ensure the user is still allowed to sign in
                if (!await _signInManager.CanSignInAsync(user))
                {
                    return Forbid(
                        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                        properties: new AuthenticationProperties(new Dictionary<string, string?>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user is no longer allowed to sign in."
                        }));
                }

                var identity = new ClaimsIdentity(result.Principal!.Claims,
                    authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                    nameType: Claims.Name,
                    roleType: Claims.Role);

                // Override claims in case they changed since the authorization grant
                identity.SetClaim(Claims.Subject, await _userManager.GetUserIdAsync(user))
                        .SetClaim(Claims.Email, await _userManager.GetEmailAsync(user))
                        .SetClaim(Claims.Name, await _userManager.GetUserNameAsync(user));

                identity.SetClaim("account_type", user.AccountType.ToString());
                identity.SetDestinations(GetDestinations);

                claimsPrincipal = new ClaimsPrincipal(identity);

                // Identity Hardening: Log token issuance
                success = true;
                var scopes = identity.GetScopes().ToArray();
                _eventLogger.LogTokenIssued(clientId, grantType, user.Id, scopes);

                if (request.IsRefreshTokenGrantType())
                {
                    _eventLogger.LogTokenRefreshed(clientId, user.Id);
                }

                return SignIn(claimsPrincipal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (request.IsClientCredentialsGrantType())
            {
                grantType = "client_credentials";

                // Note: the client credentials are automatically validated by OpenIddict
                var application = await _applicationManager.FindByClientIdAsync(request.ClientId!) ??
                    throw new InvalidOperationException("The application details cannot be found in the database.");

                // Create a new ClaimsIdentity containing the claims for a service account
                var identity = new ClaimsIdentity(
                    authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                    nameType: Claims.Name,
                    roleType: Claims.Role);

                // Use the client_id as the subject identifier
                identity.SetClaim(Claims.Subject, (await _applicationManager.GetClientIdAsync(application))!)
                        .SetClaim(Claims.Name, (await _applicationManager.GetDisplayNameAsync(application)) ?? "Unknown");

                // Add account_type claim for SYSTEM accounts (client credentials is always SYSTEM)
                identity.SetClaim("account_type", AccountType.System.ToString());

                identity.SetScopes(request.GetScopes());
                identity.SetResources(await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());
                identity.SetDestinations(GetDestinations);

                claimsPrincipal = new ClaimsPrincipal(identity);

                // Identity Hardening: Log token issuance for system account
                success = true;
                var scopes = identity.GetScopes().ToArray();
                _eventLogger.LogTokenIssued(clientId, grantType, null, scopes);

                return SignIn(claimsPrincipal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (request.IsPasswordGrantType())
            {
                grantType = "password";

                if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                {
                    return Forbid(
                        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                        properties: new AuthenticationProperties(new Dictionary<string, string?>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidRequest,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Username and password are required."
                        }));
                }

                var user = await _userManager.FindByNameAsync(request.Username) ??
                           await _userManager.FindByEmailAsync(request.Username);

                if (user == null || !await _signInManager.CanSignInAsync(user) ||
                    !await _userManager.CheckPasswordAsync(user, request.Password))
                {
                    return Forbid(
                        authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                        properties: new AuthenticationProperties(new Dictionary<string, string?>
                        {
                            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The username/password is invalid."
                        }));
                }

                var identity = new ClaimsIdentity(
                    authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                    nameType: Claims.Name,
                    roleType: Claims.Role);

                identity.SetClaim(Claims.Subject, await _userManager.GetUserIdAsync(user))
                        .SetClaim(Claims.Email, await _userManager.GetEmailAsync(user))
                        .SetClaim(Claims.Name, await _userManager.GetUserNameAsync(user))
                        .SetClaim("account_type", user.AccountType.ToString());

                identity.SetScopes(request.GetScopes());
                identity.SetResources(await _scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());
                identity.SetDestinations(GetDestinations);

                claimsPrincipal = new ClaimsPrincipal(identity);

                success = true;
                _eventLogger.LogTokenIssued(clientId, grantType, user.Id, identity.GetScopes().ToArray());

                return SignIn(claimsPrincipal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            throw new InvalidOperationException("The specified grant type is not supported.");
        }
        finally
        {
            // Identity Hardening: Record token request metrics
            stopwatch.Stop();
            _metrics.RecordTokenRequest(clientId, grantType, success, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        // By default, claims are NOT automatically included in tokens.
        // Attach destinations to specify whether they should be in access tokens, identity tokens, or both.

        switch (claim.Type)
        {
            case Claims.Name:
            case Claims.Subject:
            case Claims.Email:
            case "account_type":
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                break;

            default:
                yield return Destinations.AccessToken;
                break;
        }
    }
}
