using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsLayoutTests
{
    private static readonly RenderFragment Body = builder => builder.AddMarkupContent(0, "<h1>Section body</h1>");
    private static readonly string[] ExpectedSlugs = ["overview", "camera", "sky-map", "registration", "schedule", "calibration", "environment", "pipeline", "automations", "data", "quarantine", "system"];
    private static readonly string[] ExpectedGroups = ["Setup", "Capture", "Processing", "Data", "System"];

    [TestMethod]
    public void Catalog_ListsEveryWorkspaceSectionOnceWithProtectedRoutes()
    {
        var slugs = OperationsSectionCatalog.Sections.Select(static section => section.Slug).ToArray();
        CollectionAssert.AllItemsAreUnique(slugs);
        CollectionAssert.AreEqual(ExpectedSlugs, slugs);
        Assert.IsTrue(OperationsSectionCatalog.Sections.All(static section =>
            section.Href.StartsWith("/operations", StringComparison.Ordinal) || section.Href == "/devices/bootstrap"));
        Assert.IsTrue(OperationsSectionCatalog.Sections.All(static section => OperationsSectionCatalog.Groups.Contains(section.Group)));
        Assert.IsNull(OperationsSectionCatalog.Resolve("/gallery"));
        Assert.IsNull(OperationsSectionCatalog.Resolve("/"));
        Assert.AreEqual("overview", OperationsSectionCatalog.Resolve("/operations")!.Slug);
        Assert.AreEqual("overview", OperationsSectionCatalog.Resolve("/operations/")!.Slug);
        Assert.AreEqual("quarantine", OperationsSectionCatalog.Resolve("/operations/quarantine")!.Slug);
        Assert.AreEqual("schedule", OperationsSectionCatalog.Resolve("/schedule")!.Slug);
        Assert.AreEqual("registration", OperationsSectionCatalog.Resolve("/devices/bootstrap")!.Slug);
    }

    [TestMethod]
    public void Render_GroupsSectionsAndMarksTheCurrentOne()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/operations/schedule");

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        var links = cut.FindAll("nav.operations-navigation a");
        Assert.HasCount(OperationsSectionCatalog.Sections.Count, links);
        CollectionAssert.AreEqual(
            OperationsSectionCatalog.Sections.Select(static section => section.Href).ToArray(),
            links.Select(static link => link.GetAttribute("href")).ToArray());
        var groups = cut.FindAll(".operations-navigation__group").Select(static group => group.TextContent.Trim()).ToArray();
        CollectionAssert.AreEqual(ExpectedGroups, groups);
        var current = links.Where(static link => link.GetAttribute("aria-current") == "page").ToArray();
        Assert.HasCount(1, current);
        Assert.AreEqual("Capture schedule", current[0].TextContent.Trim());
        Assert.IsTrue(cut.Markup.Contains("Section body", StringComparison.Ordinal));
        Assert.AreEqual("Capture schedule", cut.Find(".operations-nav-toggle__current").TextContent.Trim());
    }

    [TestMethod]
    public void Render_TracksAliasAndChildRoutesAndOverviewOnlyExactly()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        foreach (var (path, label) in new[]
        {
            ("/operations", "Overview"),
            ("/schedule", "Capture schedule"),
            ("/calibration", "Calibration"),
            ("/operations/calibration", "Calibration"),
            ("/environmental", "Environment"),
            ("/operations/environment", "Environment"),
            ("/system", "System"),
            ("/operations/system", "System"),
            ("/operations/quarantine?kind=Artifact", "Quarantine & recovery"),
            ("/operations/pipeline", "Pipeline summary"),
            ("/operations/automations", "Automations"),
            ("/operations/data", "Data & storage"),
            ("/operations/camera", "Camera & rig"),
            ("/operations/sky-map", "Sky map & catalog"),
            ("/devices/bootstrap", "Device registration")
        })
        {
            navigation.NavigateTo(path);
            cut.WaitForAssertion(() =>
            {
                var current = cut.FindAll("nav.operations-navigation a[aria-current='page']");
                Assert.HasCount(1, current, path);
                Assert.AreEqual(label, current[0].TextContent.Trim(), path);
            });
        }
    }

    [TestMethod]
    public void Toggle_OpensAndEscapeClosesTheSectionDrawer()
    {
        using var context = new BunitContext();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        var toggle = cut.Find(".operations-nav-toggle");
        Assert.AreEqual("false", toggle.GetAttribute("aria-expanded"));
        Assert.AreEqual("operations-sections", toggle.GetAttribute("aria-controls"));
        Assert.IsEmpty(cut.FindAll(".operations-sidebar__backdrop"));

        toggle.Click();

        Assert.AreEqual("true", cut.Find(".operations-nav-toggle").GetAttribute("aria-expanded"));
        Assert.IsTrue(cut.Find("#operations-sections").ClassList.Contains("operations-sidebar--open"));
        Assert.HasCount(1, cut.FindAll(".operations-sidebar__backdrop"));

        cut.Find(".operations-layout").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("false", cut.Find(".operations-nav-toggle").GetAttribute("aria-expanded"));
            Assert.IsFalse(cut.Find("#operations-sections").ClassList.Contains("operations-sidebar--open"));
            Assert.IsEmpty(cut.FindAll(".operations-sidebar__backdrop"));
        });
    }

    [TestMethod]
    public void Navigation_ClosesTheDrawerOnLocationChange()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.Find(".operations-nav-toggle").Click();
        Assert.AreEqual("true", cut.Find(".operations-nav-toggle").GetAttribute("aria-expanded"));

        navigation.NavigateTo("/operations/system");

        cut.WaitForAssertion(() => Assert.AreEqual("false", cut.Find(".operations-nav-toggle").GetAttribute("aria-expanded")));
    }
}
