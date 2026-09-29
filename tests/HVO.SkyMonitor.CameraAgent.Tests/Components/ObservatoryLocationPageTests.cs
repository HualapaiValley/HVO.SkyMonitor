using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ObservatoryLocationPageTests
{
    private const string OwnerId = "7d3c1f0e-4b2a-4c8d-9e1f-2a3b4c5d6e7f";
    private const string OwnerEmail = "owner@home.lan";
    private const string TileTemplate = "https://tile.example.test/{z}/{x}/{y}.png";

    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 4, 0, 0, TimeSpan.Zero);

    private static readonly string[] ExpectedDialogNotes =
    [
        "LogicHost reviews this draft first.",
        "An acknowledged version is already staged.",
        "An earlier draft was superseded."
    ];

    [TestMethod]
    public void Render_ShowsTheProfileLocationCatalogRigAndVisibleSkyWithoutIdentifiers()
    {
        using var context = CreateContext();
        var service = new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState());
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);

        var cut = context.Render<ObservatoryLocationPage>();

        cut.WaitForElement("#site-profile-heading");
        Assert.AreEqual("Observatory & location", cut.Find("h1").TextContent.Trim());
        var profile = Facts(cut, "site-profile-heading");
        Assert.AreEqual("Hualapai Valley Observatory", profile["Observatory"]);
        Assert.AreEqual("East dome", profile["Camera"]);
        Assert.AreEqual("Pat Example", profile["Owner"]);
        Assert.AreEqual("Not recorded", profile["Contact"]);
        Assert.AreEqual("35.200000° N", profile["Latitude"]);
        Assert.AreEqual("111.650000° W", profile["Longitude"]);
        Assert.AreEqual("2,100 m", profile["Elevation"]);
        Assert.AreEqual("America/Phoenix", profile["Timezone"]);
        Assert.AreEqual("Operator entered / ± 5 m", profile["Source"]);
        Assert.Contains($"by {OwnerEmail}", cut.Markup, StringComparison.Ordinal);
        var catalog = Facts(cut, "site-catalog-heading");
        Assert.AreEqual("hvo-hyg-v3", catalog["Catalog"]);
        Assert.AreEqual("3.7.0", catalog["Version"]);
        Assert.AreEqual("200 brightest", catalog["Scene object limit"]);
        Assert.Contains("hvo-skymonitor catalog select", cut.Markup, StringComparison.Ordinal);
        var rig = cut.Find("section[aria-labelledby='site-rig-heading']");
        Assert.Contains("Installed rig / r1", rig.TextContent, StringComparison.Ordinal);
        Assert.AreEqual("/operations/camera", rig.QuerySelector("a")!.GetAttribute("href"));
        Assert.AreEqual("2 objects", cut.Find("section.site-scene .state-chip").TextContent.Trim());
        Assert.Contains("Sirius", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Lyr", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("carries scene provenance", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Location version 1 is active.", cut.Markup, StringComparison.Ordinal);
        // Operators see names, never the identifiers the stores key on.
        Assert.DoesNotContain(OwnerId, cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("hvo-observatory", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("rig-v1", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_WithTheOnlineMapEnabled_PlacesBrowserLoadedTilesAroundThePin()
    {
        using var context = CreateContext();
        var module = context.JSInterop.SetupModule("./Components/Pages/ObservatoryLocationPage.razor.js");
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState()));

        var cut = context.Render<ObservatoryLocationPage>();

        cut.WaitForElement(".site-map-tiles");
        var tiles = cut.FindAll(".site-map-tiles img");
        Assert.HasCount(15, tiles);
        Assert.IsTrue(tiles.All(static tile => tile.GetAttribute("src")!.StartsWith(
            "https://tile.example.test/13/", StringComparison.Ordinal)));
        Assert.HasCount(1, tiles.Where(static tile => tile.GetAttribute("src") == "https://tile.example.test/13/1555/3239.png").ToArray());
        Assert.AreEqual("Hualapai Valley Observatory", cut.Find(".ops-map-label").TextContent);
        var attribution = cut.Find("a.site-map-attribution");
        Assert.AreEqual("https://www.openstreetmap.org/copyright", attribution.GetAttribute("href"));
        Assert.AreEqual("noopener noreferrer", attribution.GetAttribute("rel"));
        Assert.Contains("loaded by your browser from tile.example.test", cut.Markup, StringComparison.Ordinal);
        // The tile watcher is what swaps a blocked or offline mosaic for the schematic.
        module.VerifyInvoke("watchTiles");
    }

    [TestMethod]
    public void Render_WithTheOnlineMapDisabled_ShowsOnlyTheOfflineSchematic()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            SkyMapTestData.Result(Instant), manual: ManualState(), site: SiteView(mapEnabled: false)));

        var cut = context.Render<ObservatoryLocationPage>();

        cut.WaitForElement(".ops-map");
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.IsEmpty(cut.FindAll("iframe"));
        Assert.IsEmpty(cut.FindAll("a.site-map-attribution"));
        Assert.HasCount(1, cut.FindAll(".ops-map-pin"));
        Assert.Contains("online map is turned off", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_AtLowZoomsWhereColumnsWrap_KeepsEveryTilePlacedAcrossChanges()
    {
        using var context = CreateContext();
        context.JSInterop.SetupModule("./Components/Pages/ObservatoryLocationPage.razor.js");
        var service = new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState(), site: SiteView(zoom: 1));
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement(".site-map-tiles");
        AssertEveryPositionPlaced(10);

        // A changed tile set is diffed against the previous one, which is where repeated tile addresses collide.
        service.Site = SiteView(zoom: 2);
        RefreshButton(cut).Click();
        AssertEveryPositionPlaced(15);
        service.Site = SiteView(zoom: 1);
        RefreshButton(cut).Click();
        AssertEveryPositionPlaced(10);

        void AssertEveryPositionPlaced(int expected)
        {
            var tiles = cut.FindAll(".site-map-tiles img");
            Assert.HasCount(expected, tiles);
            // Five columns span more than the whole world at these zooms, so one tile address fills several positions.
            Assert.IsLessThan(expected, tiles.Select(static tile => tile.GetAttribute("src")).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(expected, tiles.Select(static tile => tile.GetAttribute("style")).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [TestMethod]
    public void ComputeTiles_WrapsColumnsAcrossTheAntimeridianAndSkipsRowsOffTheWorld()
    {
        var tiles = ObservatoryLocationPage.ComputeTiles(0, 179.9, "{z}/{x}/{y}", 1);

        // Zoom 1 is two tiles square: the equator is the top of row 1, so the row below it is off the world
        // and the five columns either side of the antimeridian wrap onto the only two that exist.
        Assert.HasCount(10, tiles);
        Assert.IsTrue(tiles.All(static tile => tile.Source.StartsWith("1/", StringComparison.Ordinal)));
        Assert.IsFalse(tiles.Any(static tile => tile.Source.EndsWith("/2", StringComparison.Ordinal)));
        Assert.IsFalse(tiles.Any(static tile => tile.Source.Contains("/-", StringComparison.Ordinal)));
        Assert.HasCount(5, tiles.Where(static tile => tile.Source == "1/0/0" || tile.Source == "1/1/0").ToArray());
        // The pin is at the mosaic origin, so exactly one tile covers it.
        var centre = tiles.Single(static tile => tile.Left <= 0 && tile.Left > -256 && tile.Top <= 0 && tile.Top > -256);
        Assert.AreEqual("1/1/1", centre.Source);
    }

    [TestMethod]
    public void Paging_ShowsTwentyFiveRowsAtATimeAndHighlightsThatPageOnTheDial()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SiteUiService(ManyObjects(30), manual: ManualState()));
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement("nav.site-pager");

        Assert.HasCount(25, cut.FindAll("section.site-scene tbody tr"));
        Assert.AreEqual("1-25 of 30", cut.Find("nav.site-pager [role='status']").TextContent);
        Assert.IsTrue(PagerButton(cut, "Previous").HasAttribute("disabled"));
        Assert.HasCount(30, cut.FindAll("svg.site-dial circle.site-dial-object"));
        Assert.HasCount(25, cut.FindAll("svg.site-dial circle.site-dial-object.on-page"));
        // Brightest first by default.
        Assert.AreEqual("Star 00", FirstObject(cut));

        PagerButton(cut, "Next").Click();

        Assert.HasCount(5, cut.FindAll("section.site-scene tbody tr"));
        Assert.AreEqual("26-30 of 30", cut.Find("nav.site-pager [role='status']").TextContent);
        Assert.IsTrue(PagerButton(cut, "Next").HasAttribute("disabled"));
        Assert.HasCount(5, cut.FindAll("svg.site-dial circle.site-dial-object.on-page"));
    }

    [TestMethod]
    public void Sorting_TogglesDirectionOnTheSameColumnAndAnnouncesIt()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SiteUiService(ManyObjects(30), manual: ManualState()));
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement("nav.site-pager");
        PagerButton(cut, "Next").Click();

        SortButton(cut, "Magnitude").Click();

        // Sorting returns to the first page, and the brightest-first order reverses.
        Assert.AreEqual("1-25 of 30", cut.Find("nav.site-pager [role='status']").TextContent);
        Assert.AreEqual("Star 29", FirstObject(cut));
        Assert.AreEqual("descending", SortButton(cut, "Magnitude").ParentElement!.GetAttribute("aria-sort"));
        Assert.Contains("sorted by magnitude, descending", cut.Find("section.site-scene caption").TextContent, StringComparison.Ordinal);

        SortButton(cut, "Altitude").Click();

        Assert.AreEqual("ascending", SortButton(cut, "Altitude").ParentElement!.GetAttribute("aria-sort"));
        Assert.IsNull(SortButton(cut, "Magnitude").ParentElement!.GetAttribute("aria-sort"));
        Assert.AreEqual("Star 29", FirstObject(cut));
    }

    [TestMethod]
    public void Filtering_ByNameAndBrightness_NarrowsTheTableAndResetsThePage()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SiteUiService(ManyObjects(30), manual: ManualState()));
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement("nav.site-pager");
        PagerButton(cut, "Next").Click();

        cut.Find("#site-scene-search").Input("star 1");

        Assert.AreEqual("1-10 of 10", cut.Find("nav.site-pager [role='status']").TextContent);

        cut.Find("#site-scene-search").Input(string.Empty);
        cut.Find("#site-scene-magnitude").Change("2");

        // Magnitudes run 0.0, 0.2, ... 5.8, so eleven objects are magnitude 2 or brighter.
        Assert.AreEqual("1-11 of 11", cut.Find("nav.site-pager [role='status']").TextContent);

        cut.Find("#site-scene-search").Input("no such object");

        Assert.AreEqual("No matches", cut.Find("nav.site-pager [role='status']").TextContent);
        Assert.Contains("No visible object matches this filter.", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_AtTheProjectionBound_SaysOnlyTheBrightestAreShownAndHowToRaiseIt()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            SkyMapTestData.Result(Instant) with { ObjectsAtBound = true, MaximumObjects = 2 },
            manual: ManualState()));

        var cut = context.Render<ObservatoryLocationPage>();

        var banner = cut.WaitForElement("section.site-scene .ops-note-banner.compact");
        Assert.Contains("Only the 2 brightest objects are projected.", banner.TextContent, StringComparison.Ordinal);
        Assert.Contains("Raise CameraAgent:SkyMap:MaximumObjects", banner.TextContent, StringComparison.Ordinal);
        Assert.AreEqual("2 objects", cut.Find("section.site-scene .state-chip.warning").TextContent.Trim());
    }

    [TestMethod]
    public void Project_WithAChosenInstant_RequestsThatInstantAndUseNowClearsIt()
    {
        using var context = CreateContext();
        var service = new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState());
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement("#site-instant");

        cut.Find("#site-instant").Change("2026-03-01T05:30:00");
        cut.Find("form.site-instant").Submit();

        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 5, 30, 0, TimeSpan.Zero), service.Instants[^1]);

        cut.FindAll("form.site-instant button")
            .Single(static button => button.TextContent.Trim() == "Use now")
            .Click();

        Assert.IsNull(service.Instants[^1]);
    }

    [TestMethod]
    [DataRow("projection")]
    [DataRow("site")]
    [DataRow("rig")]
    public void Render_WhenAnyReadIsUnauthorized_NavigatesToAccessDenied(string read)
    {
        using var context = CreateContext(rigFailure: read == "rig" ? OperatorUiResultKind.Unauthorized : null);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            read == "projection" ? null : SkyMapTestData.Result(Instant),
            manual: ManualState(),
            siteFailure: read == "site"
                ? OperatorUiResult<CameraAgentSiteView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : null));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<ObservatoryLocationPage>();

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_WhenTheRequestedInstantIsRejected_OffersTheCurrentInstant()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            null,
            OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Invalid, CameraAgentSkyMapInstantBounds.RejectionMessage)));

        var cut = context.Render<ObservatoryLocationPage>();

        Assert.Contains("Instant outside the accepted window.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(CameraAgentSkyMapInstantBounds.RejectionMessage, cut.Markup, StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll("button").Where(static button =>
            button.TextContent.Contains("Use the current instant", StringComparison.Ordinal)).ToArray());
        Assert.IsEmpty(cut.FindAll("#site-profile-heading"));
    }

    [TestMethod]
    public void Render_WhenTheProjectionIsUnavailable_ShowsTheErrorStateAndDisablesDrafts()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            null,
            OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The sky map projection is unavailable."),
            manual: ManualState()));

        var cut = context.Render<ObservatoryLocationPage>();

        Assert.Contains("Location and projection unavailable.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("The sky map projection is unavailable.", cut.Markup, StringComparison.Ordinal);
        Assert.IsTrue(cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Render_WhenTheManualContractIsUnsupported_DisablesDraftsAndSaysWhy()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SiteUiService(SkyMapTestData.Result(Instant), manual: null));

        var cut = context.Render<ObservatoryLocationPage>();

        cut.WaitForElement("#site-history-heading");
        var trigger = cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}");
        Assert.IsTrue(trigger.HasAttribute("disabled"));
        Assert.Contains("does not accept local drafts", trigger.GetAttribute("title")!, StringComparison.Ordinal);
        Assert.Contains("does not keep a local change history", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("candidate", "Central review pending.", "LogicHost has not yet acknowledged location version 4. Local geometry version 3 remains active")]
    [DataRow("staged", "Acknowledged version staged.", "Location version 4 was acknowledged by LogicHost and activates at the next CameraAgent start. Version 3 governs")]
    [DataRow("restart", "Local draft awaiting restart.", "Location version 4 was recorded 2026-03-01 04:00:00 UTC and activates at the next CameraAgent start. Version 3 governs")]
    [DataRow("outside", "Outside the effective window.", "Location version 1 did not govern at the chosen instant")]
    [DataRow("active", "Location version 1 is active.", "It governs schedules, the sky projection, and capture evidence")]
    public void Render_StatesWhichLocationVersionGovernsTruthfully(string scenario, string title, string text)
    {
        var pending = new ManualDeploymentLocationOverride(
            31.5, -110.25, 1400, "America/Phoenix", PendingRestart: true, Instant, OwnerId, "moved");
        var result = SkyMapTestData.Result(Instant);
        var manual = scenario switch
        {
            "candidate" => ManualState(knownVersion: 4, activeVersion: 3, pendingVersion: 4, manualSequence: 1,
                centralAcknowledgementRequired: true, candidateAwaitingAcknowledgement: true, pending: pending),
            "staged" => ManualState(knownVersion: 4, activeVersion: 3, pendingVersion: 4, manualSequence: 1,
                centralAcknowledgementRequired: true, stagedAcknowledgementPending: true, pending: pending),
            "restart" => ManualState(knownVersion: 3, manualSequence: 1, pending: pending),
            _ => ManualState()
        };
        if (scenario == "outside")
        {
            result = result with { Observer = result.Observer with { EffectiveAtInstant = false } };
        }
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(result, manual: manual));

        var cut = context.Render<ObservatoryLocationPage>();

        var banner = cut.WaitForElement("section[aria-label='Location state']");
        Assert.AreEqual(title, banner.QuerySelector("strong")!.TextContent);
        Assert.Contains(text, banner.TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_History_NamesTheAccountThatActedAndNeverItsIdentifier()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(
                manualSequence: 2,
                history:
                [
                    new ManualDeploymentLocationAuditEntry(
                        2, Instant, OwnerId, "moved", "key-2", 3, 31.5, -110.25, 1400, "America/Phoenix"),
                    new ManualDeploymentLocationAuditEntry(
                        1, Instant.AddDays(-2), "a-deleted-account", null, "key-1", 2, 30, -110, 1200, "UTC")
                ])));

        var cut = context.Render<ObservatoryLocationPage>();

        var rows = cut.FindAll("section[aria-labelledby='site-history-heading'] tbody tr");
        Assert.HasCount(2, rows);
        Assert.Contains(OwnerEmail, rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("31.500000° N, 110.250000° W / 1,400 m", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Unrecognized account", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Not recorded", rows[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("a-deleted-account", cut.Markup, StringComparison.Ordinal);
        var profileRow = cut.Find("section[aria-labelledby='site-profile-history-heading'] tbody tr");
        Assert.Contains("Hualapai Valley Observatory / camera East dome / owner Pat Example", profileRow.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerId, cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_Assignment_DistinguishesStandaloneUnregisteredAndRegisteredCameras()
    {
        using var context = CreateContext();
        var service = new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState());
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        var assignment = cut.WaitForElement("section[aria-labelledby='site-assignment-heading']");

        Assert.AreEqual("Standalone", assignment.QuerySelector(".state-chip")!.TextContent);
        Assert.Contains("never contacts LogicHost", assignment.TextContent, StringComparison.Ordinal);
        Assert.IsNull(assignment.QuerySelector("a"));

        service.Site = SiteView(assignment: Assignment(CameraAgentSiteAssignmentState.NotRegistered));
        RefreshButton(cut).Click();
        assignment = cut.Find("section[aria-labelledby='site-assignment-heading']");

        Assert.AreEqual("Not registered", assignment.QuerySelector(".state-chip")!.TextContent);
        Assert.Contains("has not completed device registration", assignment.TextContent, StringComparison.Ordinal);
        Assert.AreEqual("/devices/bootstrap", assignment.QuerySelector("a")!.GetAttribute("href"));

        service.Site = SiteView(assignment: Assignment(
            CameraAgentSiteAssignmentState.Registered,
            "North ridge camera",
            DeploymentLocationResolutionStatus.Acknowledged,
            acknowledgedVersion: 3));
        RefreshButton(cut).Click();
        assignment = cut.Find("section[aria-labelledby='site-assignment-heading']");

        // The camera holds LogicHost's review of its location, which is not an Observatory membership decision.
        Assert.AreEqual("Location acknowledged", assignment.QuerySelector(".state-chip")!.TextContent);
        var facts = Facts(cut, "site-assignment-heading");
        Assert.AreEqual("North ridge camera", facts["Registered as"]);
        Assert.AreEqual("Acknowledged by LogicHost", facts["Location review"]);
        Assert.AreEqual("Not reported to this camera", facts["Observatory membership"]);
        Assert.AreEqual("3", facts["Acknowledged version"]);
        Assert.AreEqual("2026-03-01 04:00:00 UTC", facts["Last check"]);
        Assert.DoesNotContain("Assigned", assignment.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Active", assignment.TextContent, StringComparison.Ordinal);

        service.Site = SiteView(assignment: Assignment(
            CameraAgentSiteAssignmentState.Registered,
            "North ridge camera",
            DeploymentLocationResolutionStatus.Rejected));
        RefreshButton(cut).Click();
        assignment = cut.Find("section[aria-labelledby='site-assignment-heading']");

        Assert.AreEqual("Location rejected", assignment.QuerySelector(".state-chip")!.TextContent);
        Assert.AreEqual("Rejected by LogicHost", Facts(cut, "site-assignment-heading")["Location review"]);
    }

    [TestMethod]
    public void Render_WhenTheRigCatalogIsUnavailable_StillShowsTheProjectedGeometry()
    {
        using var context = CreateContext(rigFailure: OperatorUiResultKind.Unavailable);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState()));

        var cut = context.Render<ObservatoryLocationPage>();

        var rig = cut.WaitForElement("section[aria-labelledby='site-rig-heading']");
        Assert.Contains("Rig catalog unavailable", rig.TextContent, StringComparison.Ordinal);
        Assert.Contains("Equidistant Fisheye", rig.TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LocationDraft_OpensSeededAndCancelsWithoutAnyDurableCommand()
    {
        using var context = CreateContext();
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant), manual: ManualState(knownVersion: 4, activeVersion: 3));
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");

        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();

        var dialog = cut.Find("dialog.site-dialog");
        Assert.AreEqual("Create local location draft", dialog.QuerySelector("h2")!.TextContent);
        // The dialog names the version captures keep, which is the active one, not the expected token.
        Assert.Contains("Saving appends location version 5", dialog.TextContent, StringComparison.Ordinal);
        Assert.Contains("keep version 3", dialog.TextContent, StringComparison.Ordinal);
        // The entry fields are seeded from durable state rather than from anything the browser knows.
        Assert.AreEqual("35.200000", cut.Find("#site-latitude").GetAttribute("value"));
        Assert.AreEqual("-111.650000", cut.Find("#site-longitude").GetAttribute("value"));
        Assert.AreEqual("2100.00", cut.Find("#site-elevation").GetAttribute("value"));
        Assert.AreEqual("America/Phoenix", cut.Find("#site-time-zone").GetAttribute("value"));

        cut.Find("#site-latitude").Change("31.500000");
        DialogButton(cut, "Cancel").Click();

        Assert.IsEmpty(cut.FindAll("dialog.site-dialog"));
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    public void LocationDraft_WithCentralStagedAndSupersededState_ExplainsEachInTheDialog()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(
                centralAcknowledgementRequired: true,
                stagedAcknowledgementPending: true,
                supersededAtUtc: Instant.AddDays(-1))));
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");

        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();

        var notes = cut.FindAll("dialog.site-dialog .dialog-note strong").Select(static note => note.TextContent).ToArray();
        CollectionAssert.AreEqual(ExpectedDialogNotes, notes);
        Assert.Contains("changed at 2026-02-28 04:00:00 UTC", cut.Find("dialog.site-dialog").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LocationDraft_AppliesTheEnteredValuesAndStatesWhichCapturesKeepWhichVersion()
    {
        using var context = CreateContext();
        var applied = ManualState(
            knownVersion: 3,
            manualSequence: 1,
            pending: new ManualDeploymentLocationOverride(
                31.5, -110.25, 1400, "America/Phoenix", PendingRestart: true, Instant, OwnerId, "relocated"));
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Success(new ManualDeploymentLocationResult(
                    ManualDeploymentLocationStatus.Applied, null, null, applied))
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();

        cut.Find("#site-latitude").Change("31.500000");
        cut.Find("#site-longitude").Change("-110.250000");
        cut.Find("#site-elevation").Change("1400.00");
        cut.Find("#site-location-reason").Change("relocated");
        DialogButton(cut, "Save location draft").Click();

        var command = service.Commands.Single();
        Assert.AreEqual(31.5, command.LatitudeDegrees);
        Assert.AreEqual(-110.25, command.LongitudeDegrees);
        Assert.AreEqual(1400d, command.ElevationMeters);
        Assert.AreEqual("America/Phoenix", command.TimeZoneId);
        Assert.AreEqual(3L, command.ExpectedVersion);
        Assert.AreEqual(0L, command.ExpectedManualSequence);
        Assert.AreEqual("relocated", command.Reason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(command.IdempotencyKey));
        Assert.IsEmpty(cut.FindAll("dialog.site-dialog"));
        var notice = cut.Find(".site-message[role='status']");
        Assert.Contains("appends it as location version 4", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("captures already recorded keep version 3", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("Local draft awaiting restart.", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LocationDraft_RetryAfterUnavailable_ReusesTheKeyAndAnnouncesInsideTheDialog()
    {
        using var context = CreateContext();
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();
        cut.Find("#site-latitude").Change("31.500000");

        DialogButton(cut, "Save location draft").Click();
        DialogButton(cut, "Save location draft").Click();

        Assert.HasCount(2, service.Commands);
        Assert.AreEqual(service.Commands[0].IdempotencyKey, service.Commands[1].IdempotencyKey);
        Assert.AreEqual(service.Commands[0].ExpectedVersion, service.Commands[1].ExpectedVersion);
        // A modal dialog makes the rest of the document inert, so the outcome has to be announced inside it.
        var alert = cut.Find("dialog.site-dialog [role='alert']");
        Assert.Contains("could not be completed", alert.TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LocationDraft_RetryAfterEditing_MintsANewKeyWithoutReReading()
    {
        using var context = CreateContext();
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(manualSequence: 4),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();
        cut.Find("#site-latitude").Change("31.500000");
        DialogButton(cut, "Save location draft").Click();

        // Editing the payload makes it a different command, so it must not reuse the previous key.
        cut.Find("#site-longitude").Change("-110.250000");
        DialogButton(cut, "Save location draft").Click();

        Assert.HasCount(2, service.Commands);
        Assert.AreNotEqual(service.Commands[0].IdempotencyKey, service.Commands[1].IdempotencyKey);
        // An unavailable outcome must not re-read, so both commands carry the tokens from the first read.
        Assert.AreEqual(4L, service.Commands[0].ExpectedManualSequence);
        Assert.AreEqual(4L, service.Commands[1].ExpectedManualSequence);
        Assert.AreEqual(1, service.ManualReads);
        Assert.AreEqual(-111.65, service.Commands[0].LongitudeDegrees);
        Assert.AreEqual(-110.25, service.Commands[1].LongitudeDegrees);
    }

    [TestMethod]
    public void LocationDraft_OnConflict_ReReadsStateAndKeepsTheOperatorsValues()
    {
        using var context = CreateContext();
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Conflict,
                    "The deployment location changed since this page was read. Refresh before retrying.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();
        cut.Find("#site-latitude").Change("31.500000");
        var readsBefore = service.ManualReads;

        DialogButton(cut, "Save location draft").Click();

        Assert.IsTrue(service.ManualReads > readsBefore);
        Assert.Contains("Refresh before retrying", cut.Find("dialog.site-dialog [role='alert']").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("31.500000", cut.Find("#site-latitude").GetAttribute("value"));
    }

    [TestMethod]
    [DataRow("#site-latitude", "not-a-number", "Latitude must be a number between -90 and 90 degrees.")]
    [DataRow("#site-latitude", "500", "Latitude must be a number between -90 and 90 degrees.")]
    [DataRow("#site-longitude", "181", "Longitude must be a number between -180 and 180 degrees, east positive.")]
    [DataRow("#site-time-zone", " ", "Time zone must be an IANA identifier this host can resolve")]
    public void LocationDraft_WithAnInvalidValue_BlocksTheCommandWithTheAdvertisedBounds(
        string field, string value, string message)
    {
        using var context = CreateContext();
        var service = new SiteUiService(SkyMapTestData.Result(Instant), manual: ManualState());
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.LocationTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.LocationTriggerId}").Click();

        cut.Find(field).Change(value);
        DialogButton(cut, "Save location draft").Click();

        Assert.IsEmpty(service.Commands);
        Assert.Contains(message, cut.Find("dialog.site-dialog [role='alert']").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Profile_OffersTheSignInEmailSavesTrimmedValuesAndShowsTheNewRevision()
    {
        using var context = CreateContext();
        var saved = ProfileState(
            version: 2,
            values: new SiteProfileValues("Hualapai Valley Observatory North", null, "Pat Example", OwnerEmail),
            effectiveCameraName: "hvo-cam-01");
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            profileResults:
            [
                OperatorUiResult<SiteProfileResult>.Success(
                    new SiteProfileResult(SiteProfileStatus.Applied, null, null, saved))
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.ProfileTriggerId}");

        cut.Find($"#{ObservatoryLocationPage.ProfileTriggerId}").Click();

        Assert.AreEqual("Edit site profile", cut.Find("dialog.site-dialog h2").TextContent);
        Assert.AreEqual("Hualapai Valley Observatory", cut.Find("#site-observatory-name").GetAttribute("value"));
        Assert.AreEqual("hvo-cam-01", cut.Find("#site-camera-name").GetAttribute("placeholder"));
        Assert.Contains("falls back to the installation name, hvo-cam-01", cut.Markup, StringComparison.Ordinal);

        DialogButton(cut, $"Use sign-in email {OwnerEmail}").Click();

        Assert.AreEqual(OwnerEmail, cut.Find("#site-owner-contact").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll("button.site-use-email"));

        cut.Find("#site-observatory-name").Change("  Hualapai Valley Observatory North  ");
        cut.Find("#site-camera-name").Change("   ");
        cut.Find("#site-profile-reason").Change("renamed");
        DialogButton(cut, "Save profile").Click();

        var command = service.ProfileCommands.Single();
        Assert.AreEqual(
            new SiteProfileValues("Hualapai Valley Observatory North", null, "Pat Example", OwnerEmail),
            command.Profile);
        Assert.AreEqual(1L, command.ExpectedVersion);
        Assert.AreEqual("renamed", command.Reason);
        Assert.IsEmpty(cut.FindAll("dialog.site-dialog"));
        Assert.Contains("Saved site profile revision 2.", cut.Find(".site-message[role='status']").TextContent, StringComparison.Ordinal);
        var profile = Facts(cut, "site-profile-heading");
        Assert.AreEqual("Hualapai Valley Observatory North", profile["Observatory"]);
        Assert.AreEqual("hvo-cam-01 from installation", profile["Camera"]);
        Assert.AreEqual(OwnerEmail, profile["Contact"]);
    }

    [TestMethod]
    public void Profile_RetryAfterUnavailable_ReusesTheKeyAndExpectedVersion()
    {
        using var context = CreateContext();
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            profileResults:
            [
                OperatorUiResult<SiteProfileResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The site profile could not be saved.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.ProfileTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.ProfileTriggerId}").Click();
        cut.Find("#site-owner-name").Change("Sam Example");

        DialogButton(cut, "Save profile").Click();
        DialogButton(cut, "Save profile").Click();

        Assert.HasCount(2, service.ProfileCommands);
        Assert.AreEqual(service.ProfileCommands[0].IdempotencyKey, service.ProfileCommands[1].IdempotencyKey);
        Assert.AreEqual(1L, service.ProfileCommands[1].ExpectedVersion);
        Assert.Contains("could not be saved", cut.Find("dialog.site-dialog [role='alert']").TextContent, StringComparison.Ordinal);

        cut.Find("#site-owner-name").Change("Sam Example Jr");
        DialogButton(cut, "Save profile").Click();

        Assert.AreNotEqual(service.ProfileCommands[1].IdempotencyKey, service.ProfileCommands[2].IdempotencyKey);
    }

    [TestMethod]
    public void Profile_OnConflict_ReReadsTheProfileAndKeepsTheDialogOpen()
    {
        using var context = CreateContext();
        var service = new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            profileResults:
            [
                OperatorUiResult<SiteProfileResult>.Failure(
                    OperatorUiResultKind.Conflict, "The site profile changed since this page was read.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<ObservatoryLocationPage>();
        cut.WaitForElement($"#{ObservatoryLocationPage.ProfileTriggerId}");
        cut.Find($"#{ObservatoryLocationPage.ProfileTriggerId}").Click();
        cut.Find("#site-owner-name").Change("Sam Example");
        var readsBefore = service.SiteReads;

        DialogButton(cut, "Save profile").Click();

        Assert.IsTrue(service.SiteReads > readsBefore);
        Assert.Contains("changed since this page was read", cut.Find("dialog.site-dialog [role='alert']").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("Sam Example", cut.Find("#site-owner-name").GetAttribute("value"));
    }

    [TestMethod]
    public void Render_WhenTheSiteCannotBeRead_KeepsTheLocationAndOffersNoProfileEdit()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SiteUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            siteFailure: OperatorUiResult<CameraAgentSiteView>.Failure(
                OperatorUiResultKind.Unavailable, "The site profile could not be read.")));

        var cut = context.Render<ObservatoryLocationPage>();

        cut.WaitForElement("#site-profile-heading");
        Assert.IsEmpty(cut.FindAll($"#{ObservatoryLocationPage.ProfileTriggerId}"));
        Assert.Contains("The site profile could not be read. Refresh to try again.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("map settings could not be read", cut.Markup, StringComparison.Ordinal);
        Assert.AreEqual("35.200000° N", Facts(cut, "site-profile-heading")["Latitude"]);
        Assert.AreEqual("Deployment location", cut.Find(".ops-map-label").TextContent);
    }

    private static BunitContext CreateContext(OperatorUiResultKind? rigFailure = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var rig = new Mock<ICameraAgentNamedRigUiService>();
        var profile = SchedulePageTests.Profile();
        var installed = new NamedRigRevision(
            "rig-v1", "rig", 1, "camera-v1", "optics-v1", "mount-v1", profile.Module, profile.Rig, null);
        rig.Setup(service => service.GetAsync(It.IsAny<CancellationToken>())).Returns(() => ValueTask.FromResult(
            rigFailure is { } catalogFailure
                ? OperatorUiResult<NamedRigUiCatalog>.Failure(catalogFailure, "Catalog unavailable")
                : OperatorUiResult<NamedRigUiCatalog>.Success(
                    new(new NamedRigSelection("rig-v1", null, 3), [installed], null, null))));
        rig.Setup(service => service.GetInventoryAsync(It.IsAny<CancellationToken>())).Returns(() => ValueTask.FromResult(
            rigFailure is { } inventoryFailure
                ? OperatorUiResult<NamedRigInventory>.Failure(inventoryFailure, "Inventory unavailable")
                : OperatorUiResult<NamedRigInventory>.Success(new([new NamedRigProfile("rig", "Installed rig")], []))));
        context.Services.AddSingleton(rig.Object);
        return context;
    }

    private static Dictionary<string, string> Facts(IRenderedComponent<ObservatoryLocationPage> cut, string headingId)
        => cut.Find($"section[aria-labelledby='{headingId}'] dl.ops-facts")
            .Children
            .ToDictionary(
                static row => row.QuerySelector("dt")!.TextContent,
                static row => row.QuerySelector("dd")!.TextContent.Trim(),
                StringComparer.Ordinal);

    private static AngleSharp.Dom.IElement DialogButton(IRenderedComponent<ObservatoryLocationPage> cut, string text)
        => cut.FindAll("dialog.site-dialog button").Single(button => button.TextContent.Trim() == text);

    private static AngleSharp.Dom.IElement PagerButton(IRenderedComponent<ObservatoryLocationPage> cut, string text)
        => cut.FindAll("nav.site-pager button").Single(button => button.TextContent.Trim() == text);

    private static AngleSharp.Dom.IElement SortButton(IRenderedComponent<ObservatoryLocationPage> cut, string text)
        => cut.FindAll("button.site-sort").Single(button => button.TextContent.Trim() == text);

    private static AngleSharp.Dom.IElement RefreshButton(IRenderedComponent<ObservatoryLocationPage> cut)
        => cut.Find("button[aria-label='Refresh']");

    private static string FirstObject(IRenderedComponent<ObservatoryLocationPage> cut)
        => cut.Find("section.site-scene tbody tr td strong").TextContent;

    /// <summary>Objects named in brightness order, with altitude falling as magnitude rises.</summary>
    private static CameraAgentSkyMapProjectionResult ManyObjects(int count)
        => SkyMapTestData.Result(Instant) with
        {
            Objects = Enumerable.Range(0, count)
                .Select(static index => new CameraAgentSkyMapObject(
                    $"star-{index:00}",
                    $"Star {index:00}",
                    "Star",
                    index / 5d,
                    80 - index * 2,
                    index * 12,
                    900,
                    600,
                    null))
                .Reverse()
                .ToArray()
        };

    private static ManualDeploymentLocationState ManualState(
        long knownVersion = 3,
        long? activeVersion = null,
        long? pendingVersion = null,
        long manualSequence = 0,
        bool centralAcknowledgementRequired = false,
        bool stagedAcknowledgementPending = false,
        bool candidateAwaitingAcknowledgement = false,
        ManualDeploymentLocationOverride? pending = null,
        DateTimeOffset? supersededAtUtc = null,
        IReadOnlyList<ManualDeploymentLocationAuditEntry>? history = null)
        => new(
            Supported: true,
            LocationId: "hvo-observatory",
            ActiveVersion: activeVersion ?? knownVersion,
            KnownVersion: knownVersion,
            NextVersion: knownVersion + 1,
            PendingVersion: pendingVersion,
            ManualSequence: manualSequence,
            CentralAcknowledgementRequired: centralAcknowledgementRequired,
            StagedAcknowledgementPending: stagedAcknowledgementPending,
            CandidateAwaitingAcknowledgement: candidateAwaitingAcknowledgement,
            Override: pending,
            OverrideSupersededAtUtc: supersededAtUtc,
            History: history ?? []);

    private static SiteProfileState ProfileState(
        long version = 1,
        SiteProfileValues? values = null,
        string? effectiveCameraName = "East dome")
    {
        var profile = values ?? new SiteProfileValues("Hualapai Valley Observatory", "East dome", "Pat Example", null);
        return new SiteProfileState(
            version,
            profile,
            effectiveCameraName,
            "hvo-cam-01",
            Instant,
            OwnerId,
            [new SiteProfileRevision(version, Instant, OwnerId, null, $"key-{version}", profile)]);
    }

    private static CameraAgentSiteAssignment Assignment(
        CameraAgentSiteAssignmentState state,
        string? name = null,
        DeploymentLocationResolutionStatus? locationReview = null,
        long? acknowledgedVersion = null)
        => new(state, name, locationReview, acknowledgedVersion, null, "none", Instant, Instant);

    private static CameraAgentSiteView SiteView(
        SiteProfileState? profile = null,
        CameraAgentSiteAssignment? assignment = null,
        bool mapEnabled = true,
        int zoom = 13)
        => new(
            profile ?? ProfileState(),
            OwnerEmail,
            assignment ?? Assignment(CameraAgentSiteAssignmentState.Standalone),
            new CameraAgentSiteMapSettings(
                mapEnabled,
                TileTemplate,
                "© OpenStreetMap contributors",
                new Uri("https://www.openstreetmap.org/copyright"),
                zoom),
            new Dictionary<string, string>(StringComparer.Ordinal) { [OwnerId] = OwnerEmail });

    internal sealed class SiteUiService(
        CameraAgentSkyMapProjectionResult? state,
        OperatorUiResult<CameraAgentSkyMapProjectionResult>? failure = null,
        ManualDeploymentLocationState? manual = null,
        IReadOnlyList<OperatorUiResult<ManualDeploymentLocationResult>>? applyResults = null,
        CameraAgentSiteView? site = null,
        OperatorUiResult<CameraAgentSiteView>? siteFailure = null,
        IReadOnlyList<OperatorUiResult<SiteProfileResult>>? profileResults = null)
        : ICameraAgentSkyMapUiService
    {
        private ManualDeploymentLocationState? _manual = manual;
        private int _applyIndex;
        private int _profileIndex;

        internal CameraAgentSiteView Site { get; set; } = site ?? SiteView();

        internal List<DateTimeOffset?> Instants { get; } = [];

        internal List<ManualCommand> Commands { get; } = [];

        internal List<ProfileCommand> ProfileCommands { get; } = [];

        internal int ManualReads { get; private set; }

        internal int SiteReads { get; private set; }

        public ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
        {
            Instants.Add(atUtc);
            return ValueTask.FromResult(state is null
                ? failure ?? OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<CameraAgentSkyMapProjectionResult>.Success(state));
        }

        public ValueTask<OperatorUiResult<ManualDeploymentLocationState>> GetManualLocationAsync(
            CancellationToken cancellationToken)
        {
            ManualReads++;
            return ValueTask.FromResult(_manual is null
                ? OperatorUiResult<ManualDeploymentLocationState>.Failure(
                    OperatorUiResultKind.Unavailable, "Manual deployment-location state is unavailable.")
                : OperatorUiResult<ManualDeploymentLocationState>.Success(_manual));
        }

        public ValueTask<OperatorUiResult<ManualDeploymentLocationResult>> ApplyManualLocationAsync(
            double latitudeDegrees,
            double longitudeDegrees,
            double elevationMeters,
            string timeZoneId,
            long expectedVersion,
            long expectedManualSequence,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            Commands.Add(new ManualCommand(
                latitudeDegrees,
                longitudeDegrees,
                elevationMeters,
                timeZoneId,
                expectedVersion,
                expectedManualSequence,
                idempotencyKey,
                reason));
            var results = applyResults ?? [];
            var result = results.Count == 0
                ? OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
                : results[Math.Min(_applyIndex, results.Count - 1)];
            _applyIndex++;
            if (result.IsSuccess && result.Value is { } applied)
            {
                _manual = applied.State;
            }
            return ValueTask.FromResult(result);
        }

        public ValueTask<OperatorUiResult<CameraAgentSiteView>> GetSiteAsync(CancellationToken cancellationToken)
        {
            SiteReads++;
            return ValueTask.FromResult(siteFailure ?? OperatorUiResult<CameraAgentSiteView>.Success(Site));
        }

        public ValueTask<OperatorUiResult<SiteProfileResult>> SaveSiteProfileAsync(
            SiteProfileValues profile,
            long expectedVersion,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            ProfileCommands.Add(new ProfileCommand(profile, expectedVersion, idempotencyKey, reason));
            var results = profileResults ?? [];
            var result = results.Count == 0
                ? OperatorUiResult<SiteProfileResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The site profile could not be saved.")
                : results[Math.Min(_profileIndex, results.Count - 1)];
            _profileIndex++;
            if (result.IsSuccess && result.Value is { } saved)
            {
                Site = Site with { Profile = saved.State };
            }
            return ValueTask.FromResult(result);
        }

        internal sealed record ManualCommand(
            double LatitudeDegrees,
            double LongitudeDegrees,
            double ElevationMeters,
            string TimeZoneId,
            long ExpectedVersion,
            long ExpectedManualSequence,
            string IdempotencyKey,
            string? Reason);

        internal sealed record ProfileCommand(
            SiteProfileValues Profile,
            long ExpectedVersion,
            string IdempotencyKey,
            string? Reason);
    }
}
