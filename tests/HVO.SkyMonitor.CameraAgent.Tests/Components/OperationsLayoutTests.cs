using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsLayoutTests
{
    private static readonly RenderFragment Body = builder => builder.AddMarkupContent(0, "<h1>Section body</h1>");
    private static readonly string[] ExpectedSlugs = ["overview", "site", "camera", "registration", "schedule", "focus", "calibration", "pipeline", "environment", "transients", "automations", "delivery", "storage", "health", "control", "software"];
    private static readonly string[] ExpectedGroups = ["Setup", "Capture", "Processing", "Automation", "Data", "System"];
    private static readonly string[] UnavailableSlugs = ["focus", "transients", "delivery", "control", "software"];

    [TestMethod]
    public void Catalog_ListsThePrototypeSectionsOnceWithProtectedRoutes()
    {
        var slugs = OperationsSectionCatalog.Sections.Select(static section => section.Slug).ToArray();
        CollectionAssert.AllItemsAreUnique(slugs);
        CollectionAssert.AreEqual(ExpectedSlugs, slugs);
        CollectionAssert.AreEqual(UnavailableSlugs, OperationsSectionCatalog.Sections
            .Where(static section => section.UnavailableReason is not null).Select(static section => section.Slug).ToArray());
        Assert.IsTrue(OperationsSectionCatalog.Sections.Where(static section => section.UnavailableReason is null).All(static section =>
            section.Href.StartsWith("/operations", StringComparison.Ordinal) || section.Href == "/devices/bootstrap"));
        Assert.IsTrue(OperationsSectionCatalog.Sections.All(static section => OperationsSectionCatalog.Groups.Contains(section.Group)));
        Assert.IsTrue(OperationsSectionCatalog.Sections.All(static section =>
            !string.IsNullOrWhiteSpace(section.Eyebrow) && !string.IsNullOrWhiteSpace(section.Description)));
        // Gaps are explained in words, never by tracker number.
        Assert.IsFalse(OperationsSectionCatalog.Sections.Any(static section =>
            (section.UnavailableReason ?? string.Empty).Contains('#', StringComparison.Ordinal)));
        Assert.IsNull(OperationsSectionCatalog.Resolve("/gallery"));
        Assert.IsNull(OperationsSectionCatalog.Resolve("/"));
        Assert.AreEqual("overview", OperationsSectionCatalog.Resolve("/operations")!.Slug);
        Assert.AreEqual("overview", OperationsSectionCatalog.Resolve("/operations/")!.Slug);
        Assert.AreEqual("storage", OperationsSectionCatalog.Resolve("/operations/quarantine")!.Slug);
        Assert.AreEqual("schedule", OperationsSectionCatalog.Resolve("/schedule")!.Slug);
        Assert.AreEqual("registration", OperationsSectionCatalog.Resolve("/devices/bootstrap")!.Slug);
        Assert.AreEqual("focus", OperationsSectionCatalog.Resolve("/operations/unavailable/focus")?.Slug);
    }

    [TestMethod]
    public void Render_GroupsSectionsAndMarksTheCurrentOne()
    {
        using var context = CreateContext(out _);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/schedule");

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        var links = cut.FindAll("nav.operations-navigation a");
        Assert.HasCount(OperationsSectionCatalog.Sections.Count, links);
        CollectionAssert.AreEqual(OperationsSectionCatalog.Sections.Select(static section => section.NavigationHref).ToArray(),
            links.Select(static link => link.GetAttribute("href")).ToArray());
        CollectionAssert.AreEqual(OperationsSectionCatalog.Sections.Select(static section => section.Label).ToArray(),
            links.Select(static link => link.QuerySelector("span:not(.ops-nav-icon)")!.TextContent.Trim()).ToArray());
        Assert.IsTrue(links.All(static link => link.QuerySelector(".ops-nav-icon svg path") is not null));
        var groups = cut.FindAll(".ops-nav-group").Select(static group => group.TextContent.Trim()).ToArray();
        CollectionAssert.AreEqual(ExpectedGroups, groups);
        var current = links.Where(static link => link.GetAttribute("aria-current") == "page").ToArray();
        Assert.HasCount(1, current);
        Assert.AreEqual("Schedule", current[0].TextContent.Trim());
        Assert.AreEqual("active", current[0].GetAttribute("class"));
        Assert.IsTrue(cut.Markup.Contains("Section body", StringComparison.Ordinal));
        Assert.AreEqual("Local administration", cut.Find(".operations-sidebar-head .eyebrow").TextContent.Trim());
        Assert.AreEqual("Operations", cut.Find(".operations-sidebar-head strong").TextContent.Trim());
        Assert.IsFalse(cut.Find("nav.operations-navigation").TextContent.Contains('#', StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_TracksAliasAndChildRoutesAndOverviewOnlyExactly()
    {
        using var context = CreateContext(out _);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        foreach (var (path, slug) in new[]
        {
            ("/operations", "overview"),
            ("/schedule", "schedule"),
            ("/operations/schedule", "schedule"),
            ("/calibration", "calibration"),
            ("/operations/calibration", "calibration"),
            ("/environmental", "environment"),
            ("/operations/environment", "environment"),
            ("/system", "health"),
            ("/operations/system", "health"),
            ("/operations/quarantine?kind=Artifact", "storage"),
            ("/operations/data", "storage"),
            ("/operations/pipeline", "pipeline"),
            ("/operations/pipeline/executions", "pipeline"),
            ("/operations/pipeline/executions/00000000-0000-0000-0000-000000000000", "pipeline"),
            ("/operations/pipeline/replays/new", "pipeline"),
            ("/operations/pipeline/graphs", "pipeline"),
            ("/operations/pipeline/graphs/new", "pipeline"),
            ("/operations/automations", "automations"),
            ("/operations/camera", "camera"),
            ("/operations/site", "site"),
            ("/operations/sky-map", "site"),
            ("/devices/bootstrap", "registration"),
            ("/operations/unavailable/delivery", "delivery")
        })
        {
            navigation.NavigateTo(path);
            cut.WaitForAssertion(() =>
            {
                var current = cut.FindAll("nav.operations-navigation a[aria-current='page']");
                Assert.HasCount(1, current, path);
                Assert.AreEqual(OperationsSectionCatalog.Get(slug).NavigationHref, current[0].GetAttribute("href"), path);
            });
        }
    }

    [TestMethod]
    public void Render_UsesSharedResponsiveDialogAndExplainsEveryUnavailableSection()
    {
        using var context = CreateContext(out _);
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        var toggle = cut.Find(".navigation-toggle");
        Assert.AreEqual("false", toggle.GetAttribute("aria-expanded"));
        Assert.AreEqual("operations-sections", toggle.GetAttribute("aria-controls"));
        Assert.AreEqual(940, cut.FindComponent<ResponsiveNavigation>().Instance.Breakpoint);
        foreach (var section in OperationsSectionCatalog.Sections.Where(static section => section.UnavailableReason is not null))
        {
            var link = cut.Find($"a[aria-describedby='operations-unavailable-{section.Slug}']");
            Assert.AreEqual($"/operations/unavailable/{section.Slug}", link.GetAttribute("href"));
            Assert.AreEqual("unavailable", link.GetAttribute("class"));
            Assert.AreEqual(section.UnavailableReason, cut.Find($"#operations-unavailable-{section.Slug}").TextContent);
            Assert.AreEqual(section, OperationsSectionCatalog.Resolve($"/operations/unavailable/{section.Slug}"));
        }
    }

    [TestMethod]
    public void Render_HealthyReadShowsHealthyChipScopeAndNoBadges()
    {
        using var context = CreateContext(out _);
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual("Healthy", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim()));
        Assert.AreEqual("state-chip success", cut.Find(".operations-sidebar-head .state-chip").GetAttribute("class"));
        Assert.IsEmpty(cut.FindAll(".ops-nav-badge"));
        Assert.AreEqual("North Camera / Virtual Sky", cut.Find(".operations-scope small").TextContent.Trim());
        Assert.DoesNotContain("agent-test", cut.Find(".operations-scope").TextContent);
        var registration = cut.Find(".operations-scope a");
        Assert.AreEqual("/devices/bootstrap", registration.GetAttribute("href"));
        Assert.AreEqual("View registration", registration.GetAttribute("aria-label"));
    }

    [TestMethod]
    public void Refresh_AfterAPageRenamesTheCamera_ShowsTheNewName()
    {
        using var context = CreateContext(out var service);
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.WaitForAssertion(() => Assert.AreEqual(
            "North Camera / Virtual Sky", cut.Find(".operations-scope small").TextContent.Trim()));
        service.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(
            OperatorUiTestData.Operations() with { DisplayName = "East dome" }));

        cut.Instance.Refresh();

        cut.WaitForAssertion(() => Assert.AreEqual(
            "East dome / Virtual Sky", cut.Find(".operations-scope small").TextContent.Trim()));
    }

    [TestMethod]
    public void Render_WhenNothingNamesTheCamera_SaysThisCameraRatherThanTheAgentId()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(
            OperatorUiTestData.Operations() with { DisplayName = null }));

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual(
            "This camera / Virtual Sky", cut.Find(".operations-scope small").TextContent.Trim()));
        Assert.DoesNotContain("agent-test", cut.Find(".operations-scope").TextContent);
    }

    [TestMethod]
    public void Render_AttentionCountsBadgeTheOwningSectionsOnly()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(
            OperatorUiTestData.Operations(heartbeat: "Unavailable", storagePressure: true)));

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual("Attention", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim()));
        Assert.AreEqual("state-chip warning", cut.Find(".operations-sidebar-head .state-chip").GetAttribute("class"));
        Assert.HasCount(2, cut.FindAll(".ops-nav-badge.attention"));
        Assert.AreEqual("1 open attention item", Badge(cut, "storage"));
        Assert.AreEqual("1 open attention item", Badge(cut, "health"));
        foreach (var section in OperationsSectionCatalog.Sections.Where(static section => section.UnavailableReason is not null))
        {
            Assert.IsNull(cut.Find($"a[href='{section.NavigationHref}']").QuerySelector(".ops-nav-badge"), section.Slug);
        }
    }

    [TestMethod]
    public void Render_FailedReadShowsUnknownAndDeniedReadShowsNoChip()
    {
        using var context = CreateContext(out var service);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "down"));
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.WaitForAssertion(() => Assert.AreEqual("Unknown", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim()));
        Assert.AreEqual("state-chip pending", cut.Find(".operations-sidebar-head .state-chip").GetAttribute("class"));
        Assert.IsEmpty(cut.FindAll(".ops-nav-badge"));
        Assert.AreEqual("CameraAgent", cut.Find(".operations-scope small").TextContent.Trim());

        using var denied = CreateContext(out var deniedService);
        var deniedReads = 0;
        deniedService.OperationsHandler = _ =>
        {
            deniedReads++;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        };
        var deniedCut = denied.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        deniedCut.WaitForAssertion(() => Assert.AreEqual(1, deniedReads));
        Assert.IsEmpty(deniedCut.FindAll(".operations-sidebar-head .state-chip"));
    }

    [TestMethod]
    public async Task RevokedRead_ClearsTheRenderedIdentityAndLeavesAsync()
    {
        using var context = CreateContext(out var service);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.WaitForAssertion(() => Assert.AreEqual("North Camera / Virtual Sky", cut.Find(".operations-scope small").TextContent.Trim()));
        var deniedReads = 0;
        service.OperationsHandler = _ =>
        {
            deniedReads++;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        };

        // An unavailable section makes no read of its own, so only the layout can notice the revocation.
        navigation.NavigateTo("/operations/unavailable/focus");

        cut.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
        Assert.AreEqual("CameraAgent", cut.Find(".operations-scope small").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".operations-sidebar-head .state-chip"));
        Assert.AreEqual(1, deniedReads);

        await cut.InvokeAsync(() => cut.Instance.Publish(OperatorUiTestData.Operations(), refreshFailed: false)).ConfigureAwait(false);
        Assert.AreEqual("CameraAgent", cut.Find(".operations-scope small").TextContent.Trim());
    }

    [TestMethod]
    public async Task Publish_ReplacesTheSidebarStateWithThePagesRead()
    {
        using var context = CreateContext(out _);
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.WaitForAssertion(() => Assert.AreEqual("Healthy", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Instance.Publish(OperatorUiTestData.Operations(lanePressure: 2), refreshFailed: true)).ConfigureAwait(false);

        cut.WaitForAssertion(() => Assert.AreEqual("Attention", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim()));
        Assert.AreEqual("1 open attention item", Badge(cut, "storage"));
        Assert.AreEqual("1 open attention item", Badge(cut, "health"));
    }

    [TestMethod]
    public void Navigation_ClosesTheDrawerOnLocationChange()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        var module = context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js");
        module.Mode = JSRuntimeMode.Loose;
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        navigation.NavigateTo("/operations/system");
        cut.WaitForAssertion(() => Assert.HasCount(1, module.Invocations["close"]));
    }

    [TestMethod]
    public void UnavailableSection_ExplainsTheGapWithoutActionControls()
    {
        using var context = new BunitContext();
        var cut = context.Render<OperationsUnavailablePage>(parameters => parameters.Add(page => page.Section, "focus"));
        var section = OperationsSectionCatalog.Get("focus");
        Assert.AreEqual("Focus", cut.Find(".ops-page-heading h1").TextContent.Trim());
        Assert.AreEqual(section.Eyebrow, cut.Find(".ops-page-heading .eyebrow").TextContent.Trim());
        StringAssert.Contains(cut.Find(".ops-note-banner").TextContent, section.UnavailableReason!, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".ops-note-banner").TextContent, "Not implemented on this CameraAgent.", StringComparison.Ordinal);
        Assert.AreEqual("/operations", cut.Find(".unavailable-section a.button").GetAttribute("href"));
        Assert.IsEmpty(cut.FindAll(".unavailable-section button, .unavailable-section form"));
    }

    [TestMethod]
    public void UnavailableSection_ImplementedOrUnknownSlugSaysTheSectionDoesNotExist()
    {
        using var context = new BunitContext();
        var cut = context.Render<OperationsUnavailablePage>(parameters => parameters.Add(page => page.Section, "camera"));
        Assert.AreEqual("Section unavailable", cut.Find(".ops-page-heading h1").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".ops-note-banner"));
    }

    private static BunitContext CreateContext(out TestOperatorUiService service)
    {
        var context = new BunitContext();
        service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js").Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static string Badge(IRenderedComponent<OperationsLayout> cut, string slug)
        => cut.Find($"a[href='{OperationsSectionCatalog.Get(slug).NavigationHref}'] .ops-nav-badge").TextContent.Trim();
}
