using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsLayoutTests
{
    private static readonly RenderFragment Body = builder => builder.AddMarkupContent(0, "<h1>Section body</h1>");
    private static readonly string[] ExpectedSlugs = ["overview", "site", "camera", "sky-map", "registration", "schedule", "focus", "calibration", "executions", "graphs", "pipeline", "environment", "transients", "automations", "delivery", "storage", "data", "quarantine", "health", "control", "software", "system"];
    private static readonly string[] ExpectedGroups = ["Setup", "Capture", "Processing", "Automation", "Data", "System"];

    [TestMethod]
    public void Catalog_ListsEveryWorkspaceSectionOnceWithProtectedRoutes()
    {
        var slugs = OperationsSectionCatalog.Sections.Select(static section => section.Slug).ToArray();
        CollectionAssert.AllItemsAreUnique(slugs);
        CollectionAssert.AreEqual(ExpectedSlugs, slugs);
        Assert.IsTrue(OperationsSectionCatalog.Sections.Where(static section => section.UnavailableReason is null).All(static section =>
            section.Href.StartsWith("/operations", StringComparison.Ordinal) || section.Href == "/devices/bootstrap"));
        Assert.IsTrue(OperationsSectionCatalog.Sections.All(static section => OperationsSectionCatalog.Groups.Contains(section.Group)));
        Assert.IsNull(OperationsSectionCatalog.Resolve("/gallery"));
        Assert.IsNull(OperationsSectionCatalog.Resolve("/"));
        Assert.AreEqual("overview", OperationsSectionCatalog.Resolve("/operations")!.Slug);
        Assert.AreEqual("overview", OperationsSectionCatalog.Resolve("/operations/")!.Slug);
        Assert.AreEqual("quarantine", OperationsSectionCatalog.Resolve("/operations/quarantine")!.Slug);
        Assert.AreEqual("schedule", OperationsSectionCatalog.Resolve("/schedule")!.Slug);
        Assert.AreEqual("registration", OperationsSectionCatalog.Resolve("/devices/bootstrap")!.Slug);
        Assert.IsNull(OperationsSectionCatalog.Resolve("/operations/focus"), "An unavailable entry must not claim a working route.");
    }

    [TestMethod]
    public void Render_GroupsSectionsAndMarksTheCurrentOne()
    {
        using var context = new BunitContext();
        context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js").Mode = JSRuntimeMode.Loose;
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/operations/schedule");

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        var links = cut.FindAll("nav.operations-navigation a");
        Assert.HasCount(OperationsSectionCatalog.Sections.Count(static section => section.UnavailableReason is null), links);
        CollectionAssert.AreEqual(
            OperationsSectionCatalog.Sections.Where(static section => section.UnavailableReason is null).Select(static section => section.Href).ToArray(),
            links.Select(static link => link.GetAttribute("href")).ToArray());
        var groups = cut.FindAll(".operations-navigation__group").Select(static group => group.TextContent.Trim()).ToArray();
        CollectionAssert.AreEqual(ExpectedGroups, groups);
        var current = links.Where(static link => link.GetAttribute("aria-current") == "page").ToArray();
        Assert.HasCount(1, current);
        Assert.AreEqual("Capture schedule", current[0].TextContent.Trim());
        Assert.IsTrue(cut.Markup.Contains("Section body", StringComparison.Ordinal));
        var focus = cut.Find("button[aria-describedby='operations-unavailable-focus']");
        Assert.IsTrue(focus.HasAttribute("disabled"));
        StringAssert.Contains(focus.TextContent, "Focus Assistant", StringComparison.Ordinal);
        Assert.IsNull(focus.GetAttribute("href"));
        StringAssert.Contains(cut.Find("#operations-unavailable-focus").TextContent, "#1017", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_TracksAliasAndChildRoutesAndOverviewOnlyExactly()
    {
        using var context = new BunitContext();
        context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js").Mode = JSRuntimeMode.Loose;
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
            ("/operations/pipeline/executions", "Processing executions"),
            ("/operations/pipeline/executions/00000000-0000-0000-0000-000000000000", "Processing executions"),
            ("/operations/pipeline/replays/new", "Processing executions"),
            ("/operations/pipeline/graphs", "Named graphs"),
            ("/operations/pipeline/graphs/new", "Named graphs"),
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
    public void Render_UsesSharedResponsiveDialogAndNamesEveryUnavailableIssue()
    {
        using var context = new BunitContext();
        context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js").Mode = JSRuntimeMode.Loose;
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        var toggle = cut.Find(".navigation-toggle");
        Assert.AreEqual("false", toggle.GetAttribute("aria-expanded"));
        Assert.AreEqual("operations-sections", toggle.GetAttribute("aria-controls"));
        Assert.AreEqual(940, cut.FindComponent<ResponsiveNavigation>().Instance.Breakpoint);
        foreach (var section in OperationsSectionCatalog.Sections.Where(static section => section.UnavailableReason is not null))
        {
            var button = cut.Find($"button[aria-describedby='operations-unavailable-{section.Slug}']");
            Assert.IsTrue(button.HasAttribute("disabled"));
            Assert.IsNull(button.GetAttribute("href"));
            StringAssert.Contains(button.TextContent, $"#{section.UnavailableIssue}", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find($"#operations-unavailable-{section.Slug}").TextContent, $"#{section.UnavailableIssue}", StringComparison.Ordinal);
            Assert.IsNull(OperationsSectionCatalog.Resolve($"/operations/{section.Slug}"));
        }
    }

    [TestMethod]
    public void Navigation_ClosesTheDrawerOnLocationChange()
    {
        using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        navigation.NavigateTo("/operations/system");
        cut.WaitForAssertion(() => Assert.HasCount(1, module.Invocations["close"]));
    }
}
