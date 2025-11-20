using System;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Controllers;

[TestClass]
public class AuthenticationControllerTests
{
    [TestMethod]
    public void Login_WithLocalReturnUrl_IssuesChallenge()
    {
        var controller = CreateController();

        var result = controller.Login("/weather") as ChallengeResult;

        Assert.IsNotNull(result);
        Assert.AreEqual("/weather", result.Properties?.RedirectUri);
        Assert.IsTrue(result.AuthenticationSchemes!.Contains(CameraAgentAuthenticationSchemes.InteractiveOpenIdConnect, StringComparer.Ordinal));
    }

    [TestMethod]
    public void Login_WithInvalidReturnUrl_FallsBackToRoot()
    {
        var controller = CreateController();

        var result = controller.Login("https://evil.example") as ChallengeResult;

        Assert.IsNotNull(result);
        Assert.AreEqual("/", result.Properties?.RedirectUri);
    }

    [TestMethod]
    public void Logout_ReturnsSignOutWithBothSchemes()
    {
        var controller = CreateController();

        var result = controller.Logout("/weather") as SignOutResult;

        Assert.IsNotNull(result);
        Assert.AreEqual("/weather", result.Properties?.RedirectUri);
        Assert.IsTrue(result.AuthenticationSchemes!.Contains(CameraAgentAuthenticationSchemes.InteractiveCookie, StringComparer.Ordinal));
        Assert.IsTrue(result.AuthenticationSchemes!.Contains(CameraAgentAuthenticationSchemes.InteractiveOpenIdConnect, StringComparer.Ordinal));
    }

    private static AuthenticationController CreateController()
    {
        return new AuthenticationController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
