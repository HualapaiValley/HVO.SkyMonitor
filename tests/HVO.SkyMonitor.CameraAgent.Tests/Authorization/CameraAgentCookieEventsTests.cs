using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using HVO.SkyMonitor.CameraAgent;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCookieEventsTests
{
    [TestMethod]
    public async Task AccessDeniedUsesForbiddenForApiAndRedirectForPages()
    {
        var options = new CookieAuthenticationOptions();
        Program.ConfigureApplicationCookie(options);
        var scheme = new AuthenticationScheme(
            IdentityConstants.ApplicationScheme,
            IdentityConstants.ApplicationScheme,
            typeof(CookieAuthenticationHandler));

        var api = new DefaultHttpContext();
        api.Request.Path = "/api/v1/operations/summary";
        await options.Events.OnRedirectToAccessDenied(new RedirectContext<CookieAuthenticationOptions>(
            api, scheme, options, new AuthenticationProperties(), "/Account/AccessDenied")).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status403Forbidden, api.Response.StatusCode);
        Assert.IsFalse(api.Response.Headers.ContainsKey("Location"));

        var page = new DefaultHttpContext();
        page.Request.Path = "/operations";
        await options.Events.OnRedirectToAccessDenied(new RedirectContext<CookieAuthenticationOptions>(
            page, scheme, options, new AuthenticationProperties(), "/Account/AccessDenied")).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status302Found, page.Response.StatusCode);
        Assert.AreEqual("/Account/AccessDenied", page.Response.Headers.Location.ToString());
    }
}
