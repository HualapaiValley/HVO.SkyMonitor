using System.Reflection;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Components.Layout;
using HVO.SkyMonitor.LogicHost.Components.Pages;
using HVO.SkyMonitor.LogicHost.Components.Pages.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class NetworkRouteTests
{
    [TestMethod]
    public void PublicAndOperationsRoutes_HaveIndependentAuthorizationBoundaries()
    {
        Type[] publicPages = [typeof(Home), typeof(PublicObservatories), typeof(PublicObservatoryDetail), typeof(PublicEvents)];
        foreach (var page in publicPages)
        {
            page.GetCustomAttribute<AuthorizeAttribute>().Should().BeNull(page.FullName);
        }

        Type[] operationsPages =
        [
            typeof(OperationsDashboard), typeof(OperationsObservatories), typeof(OperationsObservatory),
            typeof(OperationsCamera), typeof(OperationsCaptures), typeof(OperationsCaptureDetail),
            typeof(OperationsProcessing), typeof(OperationsEvents), typeof(OperationsMembers),
            typeof(OperationsNotifications), typeof(OperationsEditorial), typeof(OperationsPublication),
            typeof(AcceptObservatoryInvitation)
        ];
        foreach (var page in operationsPages)
        {
            page.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(page.FullName);
            page.GetCustomAttributes<RouteAttribute>().Should().OnlyContain(route =>
                route.Template.StartsWith("/app", StringComparison.Ordinal));
        }
        typeof(OperationsEditorial).GetCustomAttribute<AuthorizeAttribute>()!.Policy
            .Should().Be(AuthorizationPolicyNames.PlatformEditorialWrite);
    }

    [TestMethod]
    public void Navigation_SwitchesBetweenPublicAndOperationsDestinations()
    {
        using var context = new BunitContext();
        context.AddAuthorization().SetAuthorized("owner");
        var cut = context.Render<MainLayoutNavigation>();

        cut.FindAll(".nav-badge").Select(link => link.TextContent.Trim()).Should()
            .Equal("Discover", "Observatories", "Events");
        cut.Markup.Should().Contain("Open Operations")
            .And.Contain("Subscriptions and notifications")
            .And.NotContain("Public curation");

        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/app");

        cut.WaitForAssertion(() =>
            cut.FindAll(".nav-badge").Select(link => link.TextContent.Trim()).Should()
                .Equal("Dashboard", "Observatories", "Captures", "Processing", "Events"));
        cut.Markup.Should().Contain("Network Operations").And.NotContain("Open Operations");
    }
}
