using System.Globalization;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static HVO.SkyMonitor.CameraAgent.Tests.Components.TestSpaceWeatherUiService;
using SwData = HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.SpaceWeatherUiTestData;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// The aggregated space-weather attention items and their place in the Operations shell: the section badge and
/// health chip, and the Overview's "Needs attention" count, which must equal the sidebar's.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherAttentionTests
{
    private const string SpaceWeatherHref = "/operations/space-weather";
    private static readonly RenderFragment Body = static builder => builder.AddMarkupContent(0, "<h1>Section body</h1>");

    private static readonly RenderFragment OverviewBody = static builder =>
    {
        builder.OpenComponent<OperationsPage>(0);
        builder.CloseComponent();
    };

    private static readonly string[] AllTitles =
    [
        "2 notices reach or approach this site's geomagnetic latitude",
        "2 notices to review",
        "Format drift on alerts",
        "Record drift",
        "SWPC fetching halted (provider)",
        "SWPC fetching halted (alerts)",
    ];

    private static readonly string[] AllTones = ["warning", "info", "info", "info", "info", "info"];
    private static readonly string[] AllIcons = ["warning", "pending", "pending", "pending", "pending", "pending"];
    private static readonly string[] AllLinks = ["Review", "Review", "Inspect", "Inspect", "Inspect", "Inspect"];

    [TestMethod]
    public void From_AggregatesEveryConditionInAFixedOrder()
    {
        var messages = new[]
        {
            SwData.Warning(1, Now.AddHours(-1), Now.AddHours(2), code: "WARK09", level: "G5"),
            SwData.Warning(3, Now.AddHours(-1), Now.AddHours(2), code: "WARK09", level: "G5"),
            SwData.Warning(2, Now.AddHours(1), Now.AddHours(4), level: "G1", issued: Now.AddMinutes(-30)),
            SwData.Warning(4, Now.AddHours(1), Now.AddHours(4), level: "G1", issued: Now.AddMinutes(-30)),
        };
        var status = SwData.Status(providerHalt: Halt(), products: [SwData.Product(rejected: 5, halt: Halt("rejected-repeatedly"))]);
        var projection = SwData.Project(SwData.View(messages, recordDrift: Now.AddHours(-2)), status: status);

        var items = SpaceWeatherAttention.From(projection);

        CollectionAssert.AreEqual(AllTitles, items.Select(static item => item.Title).ToArray());
        CollectionAssert.AreEqual(AllTones, items.Select(static item => item.Tone).ToArray());
        CollectionAssert.AreEqual(AllIcons, items.Select(static item => item.Icon).ToArray());
        CollectionAssert.AreEqual(AllLinks, items.Select(static item => item.LinkText).ToArray());
        Assert.IsTrue(items.All(static item => item.Section == SpaceWeatherAttention.Section && item.Href == SpaceWeatherHref));
        Assert.AreEqual("Some records could not be read for: alerts.", items[3].Detail);
        Assert.AreEqual("An HVO SkyMonitor estimate from SWPC notices that are unacknowledged, changed since acknowledgement, " +
            "or whose escalation since acknowledgement cannot be determined. Cloud is not assessed.", items[0].Detail);
        StringAssert.Contains(items[0].Detail, "HVO SkyMonitor estimate", StringComparison.Ordinal);
        StringAssert.Contains(items[0].Detail, "Cloud is not assessed.", StringComparison.Ordinal);
        foreach (var item in items)
        {
            AssertCompliantWording(item.Title + " " + item.Detail);
        }
    }

    [TestMethod]
    public void From_UsesTheSingularForOneNotice()
    {
        var items = SpaceWeatherAttention.From(Projection());

        Assert.HasCount(2, items);
        Assert.AreEqual("One notice reaches or approaches this site's geomagnetic latitude", items[0].Title);
        Assert.AreEqual("warning", items[0].Tone);
        Assert.AreEqual("One notice to review", items[1].Title);
        Assert.AreEqual("info", items[1].Tone);
    }

    [TestMethod]
    public void From_DropsAnAcknowledgedNoticeThatHasNotRisen()
    {
        var items = SpaceWeatherAttention.From(Acknowledged());

        Assert.AreEqual("One notice to review", items.Single().Title);
    }

    [TestMethod]
    public void From_RaisesHaltItemsOnlyForConfiguredProducts()
    {
        var projection = Projection(SwData.Status(products:
        [
            SwData.Product(configured: false, halt: Halt()),
            SwData.Product(product: "scales", halt: Halt("rejected-repeatedly")),
        ]));

        var titles = SpaceWeatherAttention.From(projection).Select(static item => item.Title).ToArray();

        Assert.HasCount(2, projection.ProductHalts);
        CollectionAssert.DoesNotContain(titles, "SWPC fetching halted (alerts)");
        Assert.AreEqual("SWPC fetching halted (scales)", titles.Single(static title => title.StartsWith("SWPC fetching halted", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void From_ATurnedOffOrMissingFeatureContributesNothing(int arrangement)
    {
        var projection = arrangement switch
        {
            0 => null,
            1 => Projection(SwData.Status(enabled: false, providerHalt: Halt())),
            _ => Unreadable(SpaceWeatherUiState.StoreUnavailable, enabled: false),
        };

        Assert.IsEmpty(SpaceWeatherAttention.From(projection));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void From_AnUnreadableStateReportsOnlyThatTheStoreIsUnavailable(bool storeUnavailable)
    {
        var items = SpaceWeatherAttention.From(
            Unreadable(storeUnavailable ? SpaceWeatherUiState.StoreUnavailable : SpaceWeatherUiState.ReadFailed));

        var item = items.Single();
        Assert.AreEqual("Space-weather store unavailable", item.Title);
        Assert.AreEqual("info", item.Tone);
        Assert.AreEqual("pending", item.Icon);
        CollectionAssert.AreEqual(items.ToArray(), SpaceWeatherAttention.Unavailable(enabled: true).ToArray());
        Assert.IsEmpty(SpaceWeatherAttention.Unavailable(enabled: false));
        AssertCompliantWording(item.Title + " " + item.Detail);
    }

    [TestMethod]
    public void Catalog_PlacesSpaceWeatherUnderProcessing()
    {
        var section = OperationsSectionCatalog.Get(SpaceWeatherAttention.Section);

        Assert.AreEqual("Space weather", section.Label);
        Assert.AreEqual("Processing", section.Group);
        Assert.AreEqual(SpaceWeatherHref, section.Href);
        Assert.AreEqual(SpaceWeatherAttention.Href, section.Href);
        Assert.AreEqual("Processing / external facts", section.Eyebrow);
        Assert.IsEmpty(section.AliasPaths);
        Assert.AreEqual(SpaceWeatherAttention.Section, OperationsSectionCatalog.Resolve(SpaceWeatherHref)!.Slug);
        AssertCompliantWording(section.Description);
    }

    [TestMethod]
    public void Layout_BadgesSpaceWeatherAndCountsItInTheHealthChip()
    {
        using var context = CreateLayoutContext(out _);

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual("2 open attention items", Badge(cut, SpaceWeatherAttention.Section)));
        var chip = cut.Find(".operations-sidebar-head .state-chip");
        Assert.AreEqual("Attention", chip.TextContent.Trim());
        Assert.AreEqual("2 open attention items.", chip.GetAttribute("title"));
        Assert.HasCount(1, cut.FindAll(".ops-nav-badge.attention"));
    }

    [TestMethod]
    public void Layout_AFailedReadAfterASuccessfulOneKeepsOnlyTheStoreItem()
    {
        using var context = CreateLayoutContext(out var spaceWeather, spaceWeatherEnabled: true);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.WaitForAssertion(() => Assert.AreEqual("2 open attention items", Badge(cut, SpaceWeatherAttention.Section)));
        spaceWeather.GetHandler = static _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
            OperatorUiResultKind.Unavailable, SpaceWeatherSummary.UnavailableText));

        navigation.NavigateTo("/operations/system");

        cut.WaitForAssertion(() => Assert.AreEqual("1 open attention item", Badge(cut, SpaceWeatherAttention.Section)));
        Assert.AreEqual("Review", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim());
        Assert.IsTrue(cut.Instance.SpaceWeatherUnavailable);
        Assert.IsNull(cut.Instance.SpaceWeather);
        Assert.AreEqual("Space-weather store unavailable", cut.Instance.SpaceWeatherItems.Single().Title);
        Assert.AreEqual(2, spaceWeather.Reads);
    }

    [TestMethod]
    public void Layout_AFirstReadThatFailsWithTheSwitchOnShowsOnlyTheStoreItem()
    {
        using var context = CreateLayoutContext(out var spaceWeather, spaceWeatherEnabled: true);
        spaceWeather.GetHandler = static _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
            OperatorUiResultKind.Unavailable, SpaceWeatherSummary.UnavailableText));

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual("1 open attention item", Badge(cut, SpaceWeatherAttention.Section)));
        Assert.AreEqual("Review", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim());
        Assert.IsTrue(cut.Instance.SpaceWeatherUnavailable);
        Assert.AreEqual("Space-weather store unavailable", cut.Instance.SpaceWeatherItems.Single().Title);
        Assert.AreEqual(1, spaceWeather.Reads);
    }

    [TestMethod]
    public void Layout_ConsecutiveFailedReadsWithTheSwitchOnKeepTheStoreItem()
    {
        using var context = CreateLayoutContext(out var spaceWeather, spaceWeatherEnabled: true);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));
        cut.WaitForAssertion(() => Assert.AreEqual("2 open attention items", Badge(cut, SpaceWeatherAttention.Section)));
        spaceWeather.GetHandler = static _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
            OperatorUiResultKind.Unavailable, SpaceWeatherSummary.UnavailableText));
        navigation.NavigateTo("/operations/system");
        cut.WaitForAssertion(() => Assert.AreEqual("1 open attention item", Badge(cut, SpaceWeatherAttention.Section)));
        var firstFailure = cut.Instance.SpaceWeatherItems;

        navigation.NavigateTo("/operations/storage");

        cut.WaitForAssertion(() => Assert.AreNotSame(firstFailure, cut.Instance.SpaceWeatherItems));
        Assert.AreEqual(3, spaceWeather.Reads);
        Assert.AreEqual("1 open attention item", Badge(cut, SpaceWeatherAttention.Section));
        Assert.AreEqual("Space-weather store unavailable", cut.Instance.SpaceWeatherItems.Single().Title);
        Assert.IsTrue(cut.Instance.SpaceWeatherUnavailable);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Layout_AFailedReadClaimsNothingWhenAccessIsDeniedOrTheSwitchIsOff(bool denied)
    {
        using var context = CreateLayoutContext(out var spaceWeather, spaceWeatherEnabled: denied);
        spaceWeather.GetHandler = _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
            denied ? OperatorUiResultKind.Unauthorized : OperatorUiResultKind.Unavailable, "down"));

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual(1, spaceWeather.Reads));
        Assert.AreEqual("Healthy", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".ops-nav-badge.attention"));
        Assert.IsEmpty(cut.Instance.SpaceWeatherItems);
        Assert.AreEqual(!denied, cut.Instance.SpaceWeatherUnavailable);
    }

    [TestMethod]
    public void Layout_ATurnedOffFeatureAddsNoBadge()
    {
        using var context = CreateLayoutContext(out var spaceWeather);
        spaceWeather.GetHandler = static _ => Success(View(Projection(SwData.Status(enabled: false, providerHalt: Halt()))));

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, Body));

        cut.WaitForAssertion(() => Assert.AreEqual("Healthy", cut.Find(".operations-sidebar-head .state-chip").TextContent.Trim()));
        Assert.IsEmpty(cut.FindAll(".ops-nav-badge.attention"));
        Assert.IsNotNull(cut.Instance.SpaceWeather);
    }

    [TestMethod]
    public void Overview_CountsTheSameItemsAsTheSidebarAndShowsTheSummary()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        OperationsPageTests.ConfigureOverview(context);
        var spaceWeather = new TestSpaceWeatherUiService();
        context.Services.AddSingleton<ICameraAgentSpaceWeatherUiService>(spaceWeather);

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, OverviewBody));

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, cut.FindAll($".ops-attention-item a[href='{SpaceWeatherHref}']"));
            Assert.HasCount(1, cut.FindAll("#space-weather-summary"));
        });
        var badgeTotal = cut.FindAll(".ops-nav-badge.attention")
            .Sum(static badge => int.Parse(badge.FirstChild!.TextContent, CultureInfo.InvariantCulture));
        var overviewCount = cut.FindAll(".ops-attention-item").Count;
        Assert.AreEqual($"{overviewCount} open",
            cut.Find("#attention-heading").ParentElement!.ParentElement!.QuerySelector(":scope > span")!.TextContent);
        Assert.AreEqual(ChipTitle(overviewCount), cut.Find(".operations-sidebar-head .state-chip").GetAttribute("title"));
        Assert.AreEqual(badgeTotal, overviewCount);
        Assert.AreEqual("2 active or pending (1 active, 1 pending)", cut.Find("#space-weather-summary-count").TextContent);
        Assert.AreEqual(1, spaceWeather.Reads, "The Overview reads space weather only through the layout.");
        AssertCompliantWording(cut.Find("#space-weather-summary").OuterHtml);
    }

    [TestMethod]
    public void Overview_ShowsTheUnavailableSummaryWhenTheLayoutReadFails()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        OperationsPageTests.ConfigureOverview(context);
        var spaceWeather = new TestSpaceWeatherUiService
        {
            GetHandler = static _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
                OperatorUiResultKind.Unavailable, SpaceWeatherSummary.UnavailableText)),
        };
        context.Services.AddSingleton<ICameraAgentSpaceWeatherUiService>(spaceWeather);

        var cut = context.Render<OperationsLayout>(parameters => parameters.Add(static layout => layout.Body, OverviewBody));

        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("#space-weather-summary-unavailable")));
        Assert.IsEmpty(cut.FindAll($".ops-attention-item a[href='{SpaceWeatherHref}']"));
    }

    private static string ChipTitle(int count) => count switch
    {
        0 => "No open attention items.",
        1 => "One open attention item.",
        _ => $"{count} open attention items.",
    };

    private static BunitContext CreateLayoutContext(out TestSpaceWeatherUiService spaceWeather, bool spaceWeatherEnabled = false)
    {
        var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        spaceWeather = new TestSpaceWeatherUiService();
        context.Services.AddSingleton<ICameraAgentSpaceWeatherUiService>(spaceWeather);
        context.Services.AddSingleton(Options.Create(new CameraAgentHostOptions
        {
            SpaceWeather = new SpaceWeatherOptions { Enabled = spaceWeatherEnabled },
        }));
        context.JSInterop.SetupModule("./Components/Layout/ResponsiveNavigation.razor.js").Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static string Badge(IRenderedComponent<OperationsLayout> cut, string slug)
        => cut.Find($"a[href='{OperationsSectionCatalog.Get(slug).Href}'] .ops-nav-badge").TextContent.Trim();
}
