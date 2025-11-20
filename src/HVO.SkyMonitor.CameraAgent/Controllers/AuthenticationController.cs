using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Controllers;

/// <summary>
/// Handles interactive authentication flows for the camera agent UI.
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
[Route("auth")]
public sealed class AuthenticationController : Controller
{
    [HttpGet("login")]
    [SuppressMessage("Design", "CA1054:Uri parameters should not be strings", Justification = "Return URLs arrive via query string values and must be validated as strings.")]
    public IActionResult Login([FromQuery] string? returnUrl)
    {
        var redirectUri = ReturnUrlHelper.NormalizeReturnUrl(returnUrl);
        var properties = new AuthenticationProperties
        {
            RedirectUri = redirectUri
        };

        return Challenge(properties, CameraAgentAuthenticationSchemes.InteractiveOpenIdConnect);
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    [SuppressMessage("Design", "CA1054:Uri parameters should not be strings", Justification = "Return URLs arrive via query string values and must be validated as strings.")]
    public IActionResult Logout([FromForm] string? returnUrl)
    {
        var redirectUri = ReturnUrlHelper.NormalizeReturnUrl(returnUrl);
        var properties = new AuthenticationProperties
        {
            RedirectUri = redirectUri
        };

        return SignOut(
            properties,
            CameraAgentAuthenticationSchemes.InteractiveCookie,
            CameraAgentAuthenticationSchemes.InteractiveOpenIdConnect);
    }
}
