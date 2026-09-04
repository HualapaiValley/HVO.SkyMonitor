using System.Reflection;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Pages.Devices;
using HVO.SkyMonitor.CameraAgent.Controllers.v1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

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
            typeof(CurrentSkyPage),
            typeof(OperationsPage),
            typeof(QuarantinePage),
            typeof(GalleryPage),
            typeof(GalleryDetail),
            typeof(SchedulePage),
            typeof(CalibrationPage),
            typeof(EnvironmentalPage),
            typeof(TransientPage),
            typeof(TransientDetail),
            typeof(ArchiveCalendarPage),
            typeof(ProductsPage),
            typeof(ProductDetail),
            typeof(SystemStatusPage),
            typeof(PipelineSummaryPage),
            typeof(AutomationsPage),
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
                ("Current sky", "/"), ("Archive", "/gallery"), ("Operations", "/operations")
            },
            links);
        Assert.IsFalse(cut.Markup.Contains("Configuration", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Notifications", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Navigation_IdentifiesDirectChildAndGroupedCurrentRoutes()
    {
        using var context = new BunitContext();
        context.AddAuthorization().SetAuthorized("owner");
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = context.Render<MainLayoutNavigation>();

        foreach (var (path, label) in new[]
        {
            ("/", "Current sky"),
            ("/gallery", "Archive"),
            ("/gallery/capture-id", "Archive"),
            ("/archive/calendar", "Archive"),
            ("/archive/products/artifact-id", "Archive"),
            ("/operations", "Operations"),
            ("/operations/quarantine", "Operations"),
            ("/schedule", "Operations"),
            ("/calibration", "Operations"),
            ("/system", "Operations"),
            ("/environmental", "Operations"),
            ("/transients", "Archive"),
            ("/transients/candidate-id", "Archive"),
            ("/devices", "Operations"),
            ("/operations/pipeline", "Operations"),
            ("/operations/automations", "Operations"),
            ("/operations/schedule", "Operations")
        })
        {
            navigation.NavigateTo(path);
            cut.WaitForAssertion(() =>
            {
                var current = cut.FindAll(".nav-badge")
                    .Where(link => link.GetAttribute("aria-current") == "page")
                    .ToArray();
                Assert.HasCount(1, current, path);
                Assert.AreEqual(label, current[0].TextContent.Trim(), path);
            });
        }
    }

    [TestMethod]
    public void OperationsPages_UseTheWorkspaceLayoutAndKeepAliasRoutes()
    {
        foreach (var (type, routes) in new (Type Type, string[] Routes)[]
        {
            (typeof(OperationsPage), ["/operations"]),
            (typeof(SchedulePage), ["/schedule", "/operations/schedule"]),
            (typeof(CalibrationPage), ["/calibration", "/operations/calibration"]),
            (typeof(EnvironmentalPage), ["/environmental", "/operations/environment"]),
            (typeof(SystemStatusPage), ["/system", "/operations/system"]),
            (typeof(QuarantinePage), ["/operations/quarantine"]),
            (typeof(PipelineSummaryPage), ["/operations/pipeline"]),
            (typeof(AutomationsPage), ["/operations/automations"]),
            (typeof(DeviceBootstrap), ["/devices/bootstrap"])
        })
        {
            var layout = type.GetCustomAttribute<LayoutAttribute>();
            Assert.IsNotNull(layout, type.FullName);
            Assert.AreEqual(typeof(OperationsLayout), layout.LayoutType, type.FullName);
            CollectionAssert.AreEquivalent(
                routes,
                type.GetCustomAttributes<RouteAttribute>().Select(static route => route.Template).ToArray(),
                type.FullName);
        }
        foreach (var type in new[] { typeof(CurrentSkyPage), typeof(GalleryPage), typeof(TransientPage), typeof(ArchiveCalendarPage) })
        {
            Assert.IsNull(type.GetCustomAttribute<LayoutAttribute>(), type.FullName);
        }
    }
}
