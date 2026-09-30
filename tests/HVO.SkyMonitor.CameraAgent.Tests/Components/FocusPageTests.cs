using System.Reflection;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class FocusPageTests
{
    private static readonly string[] DisabledControls =
        ["#focus-start", "#focus-select-region", "#focus-exposure", "#focus-gain", "#focus-interval", "#focus-metric-kind", "#focus-sample", "#focus-export"];

    private static readonly string[] ProcedureSteps = ["Choose a field", "Hold the exposure", "Adjust by hand", "Secure & verify"];

    [TestMethod]
    public void Route_IsTheProtectedOperationsFocusPath()
    {
        var routes = typeof(FocusPage).GetCustomAttributes<RouteAttribute>().Select(static route => route.Template).ToArray();
        Assert.HasCount(1, routes);
        Assert.AreEqual("/operations/focus", routes[0]);
        Assert.AreEqual(OperationsSectionCatalog.Get("focus").Href, routes[0]);
        Assert.AreEqual(
            CameraAgentAuthorizationPolicyNames.OperationsReadV1,
            typeof(FocusPage).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [TestMethod]
    public void Render_ShowsTheManualBoundaryHeadingAndCurrentSkyLink()
    {
        using var context = new BunitContext();
        var cut = context.Render<FocusPage>();
        var section = OperationsSectionCatalog.Get("focus");

        Assert.AreEqual("Focus", cut.Find("h1#focus-heading").TextContent.Trim());
        Assert.AreEqual(section.Eyebrow, cut.Find(".ops-page-heading .eyebrow").TextContent.Trim());
        Assert.AreEqual("/", cut.Find(".ops-page-actions a.button").GetAttribute("href"));
        Assert.AreEqual("Current sky", cut.Find(".ops-page-actions a.button").TextContent.Trim());
        var boundary = cut.Find(".focus-boundary").TextContent;
        StringAssert.Contains(boundary, "Manual focus only.", StringComparison.Ordinal);
        StringAssert.Contains(boundary, "does not measure sharpness", StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            ProcedureSteps,
            cut.FindAll(".ops-step strong").Select(static step => step.TextContent.Trim()).ToArray());
    }

    [TestMethod]
    public void Render_DisablesEverySessionControlWithTheAccessibleReason()
    {
        using var context = new BunitContext();
        var cut = context.Render<FocusPage>();
        var reason = cut.Find("#focus-unavailable-reason").TextContent.Trim();

        Assert.AreEqual(OperationsSectionCatalog.Get("focus").CapabilityNote, reason);
        foreach (var selector in DisabledControls)
        {
            var control = cut.Find(selector);
            Assert.IsTrue(control.HasAttribute("disabled"), selector);
            Assert.AreEqual("focus-unavailable-reason", control.GetAttribute("aria-describedby"), selector);
        }
        Assert.AreEqual("Unavailable", cut.Find(".focus-session-state .state-chip").TextContent.Trim());
        // Only the Current sky link is actionable on this page.
        Assert.IsTrue(cut.FindAll("button, input, select").All(static control => control.HasAttribute("disabled")));
        Assert.IsEmpty(cut.FindAll("form"));
    }

    [TestMethod]
    public void Render_FabricatesNoPreviewMetricTrendOrHistory()
    {
        using var context = new BunitContext();
        var cut = context.Render<FocusPage>();

        Assert.IsEmpty(cut.FindAll("img"));
        Assert.AreEqual("— no measurement", cut.Find("#focus-metric").TextContent.Trim());
        var trend = cut.Find(".focus-trend");
        Assert.AreEqual("No focus samples recorded", trend.GetAttribute("aria-label"));
        Assert.IsEmpty(trend.QuerySelectorAll("circle"));
        Assert.HasCount(1, trend.QuerySelectorAll("path"));
        Assert.IsTrue(cut.FindAll(".focus-controls input").All(static input => string.IsNullOrEmpty(input.GetAttribute("value"))));
        var rows = cut.FindAll(".ops-table tbody tr");
        Assert.HasCount(1, rows);
        Assert.AreEqual("No focus sessions are retained on this CameraAgent.", rows[0].TextContent.Trim());
        Assert.IsFalse(cut.Markup.Contains("FWHM", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Accepted", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("detected stars", StringComparison.Ordinal));
    }
}
