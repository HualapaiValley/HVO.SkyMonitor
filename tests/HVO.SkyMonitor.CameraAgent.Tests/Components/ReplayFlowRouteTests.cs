using System.Reflection;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ReplayFlowRouteTests
{
    [TestMethod]
    public void ReplayPages_RequireTheOperationsReadPolicy()
    {
        foreach (var type in new[] { typeof(ReplaySubmitPage), typeof(ReplayExecutionPage) })
        {
            var attribute = type.GetCustomAttributes<AuthorizeAttribute>().SingleOrDefault();
            Assert.IsNotNull(attribute, type.FullName);
            Assert.AreEqual(CameraAgentAuthorizationPolicyNames.OperationsReadV1, attribute.Policy, type.FullName);
        }
    }

    [TestMethod]
    public void ReplayPages_UseTheOperationsWorkspaceLayoutAndTheirOwnRoutes()
    {
        foreach (var (type, routes) in new (Type Type, string[] Routes)[]
        {
            (typeof(ReplaySubmitPage), ["/operations/pipeline/replays/new"]),
            (typeof(ReplayExecutionPage), ["/operations/pipeline/replays/{ExecutionId:guid}"])
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
    }
}
