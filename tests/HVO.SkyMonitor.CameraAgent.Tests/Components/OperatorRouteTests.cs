using System.Reflection;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Pages.Devices;
using HVO.SkyMonitor.CameraAgent.Controllers.v1;
using Microsoft.AspNetCore.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperatorRouteTests
{
    [TestMethod]
    public void ProtectedPagesAndLatestFrames_RequireOperationsReadPolicy()
    {
        foreach (var type in new[]
        {
            typeof(OperationsPage),
            typeof(QuarantinePage),
            typeof(GalleryPage),
            typeof(GalleryDetail),
            typeof(SchedulePage),
            typeof(SystemStatusPage),
            typeof(DeviceBootstrap),
            typeof(FramesController)
        })
        {
            var attribute = type.GetCustomAttributes<AuthorizeAttribute>().SingleOrDefault();
            Assert.IsNotNull(attribute, type.FullName);
            Assert.AreEqual(CameraAgentAuthorizationPolicyNames.OperationsReadV1, attribute.Policy, type.FullName);
        }
    }

    [TestMethod]
    public void Navigation_ContainsOnlyAcceptedOperatorDestinations()
    {
        using var context = new BunitContext();
        context.AddAuthorization().SetAuthorized("owner");

        var cut = context.Render<MainLayoutNavigation>();

        var links = cut.FindAll(".nav-badge").Select(link => (link.TextContent.Trim(), link.GetAttribute("href"))).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                ("Operations", "/"), ("Gallery", "/gallery"), ("Schedule", "/schedule"),
                ("System", "/system"), ("Device", "/devices/bootstrap")
            },
            links);
        Assert.IsFalse(cut.Markup.Contains("Configuration", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Notifications", StringComparison.Ordinal));
    }
}
