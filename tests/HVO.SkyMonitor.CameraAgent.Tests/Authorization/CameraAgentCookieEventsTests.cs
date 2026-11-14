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

        var metrics = new DefaultHttpContext();
        metrics.Request.Path = "/metrics";
        await options.Events.OnRedirectToLogin(new RedirectContext<CookieAuthenticationOptions>(
            metrics, scheme, options, new AuthenticationProperties(), "/Account/Login?ReturnUrl=%2Fmetrics")).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, metrics.Response.StatusCode);
        Assert.IsFalse(metrics.Response.Headers.ContainsKey("Location"));
        Assert.AreEqual("Bearer", metrics.Response.Headers.WWWAuthenticate.ToString());
        Assert.AreEqual("no-store", metrics.Response.Headers.CacheControl.ToString());

        var deniedMetrics = new DefaultHttpContext();
        deniedMetrics.Request.Path = "/metrics";
        await options.Events.OnRedirectToAccessDenied(new RedirectContext<CookieAuthenticationOptions>(
            deniedMetrics, scheme, options, new AuthenticationProperties(), "/Account/AccessDenied")).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status403Forbidden, deniedMetrics.Response.StatusCode);
        Assert.IsFalse(deniedMetrics.Response.Headers.ContainsKey("Location"));
    }

    [TestMethod]
    public void ConfigureApplicationCookie_UsesConfiguredPerAgentName()
    {
        var options = new CookieAuthenticationOptions();

        Program.ConfigureApplicationCookie(options, "CameraAgent.SidingSpring.Auth");

        Assert.AreEqual("CameraAgent.SidingSpring.Auth", options.Cookie.Name);
    }
}
