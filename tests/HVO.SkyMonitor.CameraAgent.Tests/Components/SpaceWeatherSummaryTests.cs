using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using static HVO.SkyMonitor.CameraAgent.Tests.Components.TestSpaceWeatherUiService;
using SwData = HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.SpaceWeatherUiTestData;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// The Overview's pure space-weather summary: off, unavailable and ready modes, the active and pending count with
/// only the first three rows, the freshness chip, and the credit in every mode.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherSummaryTests
{
    private static readonly string[] SummaryOrder = ["WARK09/1", "WARK05/2"];

    [TestMethod]
    public void Ready_CountsCurrentNoticesAndKeepsBothLinesApart()
    {
        using var context = new BunitContext();
        var projection = Projection();

        var cut = Render(context, projection);

        Assert.AreEqual("Alerts fresh", cut.Find("#space-weather-summary-state").TextContent);
        StringAssert.Contains(cut.Find("#space-weather-summary-state").ClassName, "success", StringComparison.Ordinal);
        Assert.AreEqual("2 active or pending (1 active, 1 pending)", cut.Find("#space-weather-summary-count").TextContent);
        var rows = cut.FindAll("li.space-weather-summary-row");
        CollectionAssert.AreEqual(SummaryOrder, rows.Select(static r => r.GetAttribute("data-group")).ToArray());
        Assert.AreEqual(projection.Current[0].GlobalLine, rows[0].QuerySelector(".space-weather-summary-global")!.TextContent);
        Assert.AreEqual(projection.Current[0].SiteLine, rows[0].QuerySelector(".space-weather-summary-site")!.TextContent);
        Assert.AreEqual("warning", rows[0].QuerySelector(".state-chip")!.ClassList[^1]);
        Assert.AreEqual("pending", rows[1].QuerySelector(".state-chip")!.ClassList[^1]);
        Assert.IsEmpty(cut.FindAll("#space-weather-summary-more"));
        Assert.AreEqual(SpaceWeatherSummary.PageLink, cut.Find("#space-weather-summary-link").GetAttribute("href"));
        Assert.AreEqual(SpaceWeatherOperations.Credit, cut.Find("p.space-weather-credit").TextContent);
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    public void Ready_ShowsOnlyTheFirstThreeRowsAndCountsTheRestFromTheTotal()
    {
        using var context = new BunitContext();
        var messages = Enumerable.Range(1, SpaceWeatherUiBounds.CurrentRows + 10)
            .Select(static serial => SwData.Warning(serial, Now.AddHours(-1), Now.AddHours(2))).ToArray();
        var projection = Projection(messages: messages);
        Assert.HasCount(SpaceWeatherUiBounds.CurrentRows, projection.Current);

        var cut = Render(context, projection);

        Assert.HasCount(SpaceWeatherUiBounds.SummaryRows, cut.FindAll("li.space-weather-summary-row"));
        Assert.AreEqual("60 active or pending (60 active, 0 pending)", cut.Find("#space-weather-summary-count").TextContent);
        Assert.AreEqual("57 more", cut.Find("#space-weather-summary-more").TextContent);
    }

    [TestMethod]
    public void Ready_WithNoCurrentNoticeSaysNoneAreRetained()
    {
        using var context = new BunitContext();

        var cut = Render(context, Projection(messages: []));

        Assert.AreEqual("0 active or pending (0 active, 0 pending)", cut.Find("#space-weather-summary-count").TextContent);
        Assert.AreEqual(SpaceWeatherSummary.NoCurrentText, cut.Find(".ops-panel-empty").TextContent);
        Assert.IsEmpty(cut.FindAll("li.space-weather-summary-row"));
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    public void Inconsistent_SaysTheDataChangedWhileReading()
    {
        using var context = new BunitContext();

        var cut = Render(context, Projection() with { Inconsistent = true });

        Assert.AreEqual(SpaceWeatherPanel.InconsistentText, cut.Find("div.ops-note-banner").TextContent);
        Assert.HasCount(2, cut.FindAll("li.space-weather-summary-row"));
    }

    [TestMethod]
    public void Off_ListsNothingAndLinksToTheSwitch()
    {
        using var context = new BunitContext();

        var cut = Render(context, Projection(SwData.Status(enabled: false)), unavailable: true);

        Assert.AreEqual("Off", cut.Find("#space-weather-summary-state").TextContent);
        StringAssert.StartsWith(cut.Find("#space-weather-summary-off").TextContent.Trim(), "Space weather is off.", StringComparison.Ordinal);
        Assert.AreEqual(SpaceWeatherSummary.SwitchLink, cut.Find("#space-weather-summary-off a").GetAttribute("href"));
        Assert.IsEmpty(cut.FindAll("#space-weather-summary-count"));
        Assert.IsEmpty(cut.FindAll("li.space-weather-summary-row"));
        Assert.AreEqual(SpaceWeatherOperations.Credit, cut.Find("p.space-weather-credit").TextContent);
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Unavailable_ClaimsNothingAboutCurrentNotices(int arrangement)
    {
        using var context = new BunitContext();
        var (projection, unavailable) = arrangement switch
        {
            0 => ((SpaceWeatherUiProjection?)null, false),
            1 => (Projection(), true),
            2 => (Unreadable(SpaceWeatherUiState.StoreUnavailable), false),
            _ => (Unreadable(SpaceWeatherUiState.ReadFailed), false),
        };

        var cut = Render(context, projection, unavailable);

        Assert.AreEqual("Unavailable", cut.Find("#space-weather-summary-state").TextContent);
        Assert.AreEqual(SpaceWeatherSummary.UnavailableText + " Nothing is claimed about current notices.",
            cut.Find("#space-weather-summary-unavailable").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll("#space-weather-summary-count"));
        Assert.IsEmpty(cut.FindAll("li.space-weather-summary-row"));
        Assert.AreEqual(SpaceWeatherOperations.Credit, cut.Find("p.space-weather-credit").TextContent);
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    [DataRow("fresh", "Alerts fresh", "success")]
    [DataRow("stale", "Alerts stale", "warning")]
    [DataRow("outdated", "Alerts outdated", "warning")]
    [DataRow("none", "no successful fetch yet", "pending")]
    [DataRow(null, "no successful fetch yet", "pending")]
    [DataRow("future", "Alerts freshness unknown", "pending")]
    public void FreshnessChip_MapsAlertsFreshness(string? freshness, string text, string tone)
    {
        Assert.AreEqual(new SpaceWeatherSummary.Chip(text, tone), SpaceWeatherSummary.FreshnessChip(freshness));
    }

    [TestMethod]
    public void StaleAlerts_ShowTheStaleChipOnTheRenderedSummary()
    {
        using var context = new BunitContext();

        var cut = Render(context, Projection(SwData.Status(products: [SwData.Product(freshness: "stale")])));

        Assert.AreEqual("Alerts stale", cut.Find("#space-weather-summary-state").TextContent);
        StringAssert.Contains(cut.Find("#space-weather-summary-state").ClassName, "warning", StringComparison.Ordinal);
    }

    private static IRenderedComponent<SpaceWeatherSummary> Render(
        BunitContext context,
        SpaceWeatherUiProjection? projection,
        bool unavailable = false)
        => context.Render<SpaceWeatherSummary>(parameters => parameters
            .Add(static summary => summary.Projection, projection)
            .Add(static summary => summary.Unavailable, unavailable));
}
