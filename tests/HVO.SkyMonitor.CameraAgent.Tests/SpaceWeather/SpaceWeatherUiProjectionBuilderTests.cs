using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;
using static HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.SpaceWeatherUiTestData;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherUiProjectionBuilderTests
{
    private static readonly string[] OrdinalOrder = ["WARK05/1", "WARK05/10", "WARK05/2"];
    private static readonly string[] PhoenixRestrictions =
    [
        "site daylight or twilight 2026-10-20 05:15–11:00 local (America/Phoenix); 2026-10-20 12:15–18:00 UTC",
        "site daylight 2026-10-20 06:30–11:00 local (America/Phoenix); 2026-10-20 13:30–18:00 UTC"
    ];

    [TestMethod]
    public void ReadTimingIsPinned()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), SpaceWeatherUiBounds.ProjectionTtl);
        Assert.AreEqual(TimeSpan.FromSeconds(10), SpaceWeatherUiBounds.ReadTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(30), SpaceWeatherUiBounds.LayoutRefreshAge);
    }

    [TestMethod]
    public void CurrentListShowsFiftyActiveFirstWithExactCountsBeforeBounding()
    {
        var messages = new List<SwpcMessageRecord>();
        for (var i = 0; i < 40; i++)
        {
            messages.Add(Warning(i + 1, Now.AddHours(-1).AddMinutes(-i), Now.AddHours(2)));
        }
        for (var i = 0; i < 20; i++)
        {
            messages.Add(Warning(41 + i, Now.AddHours(1).AddMinutes(i), Now.AddHours(3), issued: Now.AddMinutes(-30).AddMinutes(-i)));
        }

        var projection = Project(View(messages));

        Assert.HasCount(50, projection.Current);
        Assert.AreEqual(60, projection.CurrentTotal);
        Assert.AreEqual(10, projection.CurrentMore);
        Assert.AreEqual(40, projection.ActiveCount);
        Assert.AreEqual(20, projection.PendingCount);
        Assert.IsTrue(projection.Current.Take(40).All(static n => n.State == SwpcNoticeState.Active));
        Assert.IsTrue(projection.Current.Skip(40).All(static n => n.State == SwpcNoticeState.Pending));
        Assert.AreEqual("WARK05/1", projection.Current[0].Group);
        Assert.AreEqual("WARK05/40", projection.Current[39].Group);
        Assert.AreEqual("WARK05/41", projection.Current[40].Group);
        Assert.AreEqual("WARK05/50", projection.Current[49].Group);
        Assert.AreEqual(60, projection.ReviewCount, "Every current notice is unacknowledged and none reaches HVO.");
        Assert.AreEqual(0, projection.ReachCount);
        Assert.AreEqual(60, projection.NoticeTotal);
    }

    [TestMethod]
    public void RecentListShowsFiftyNewestEndedWithTheRemainderCounted()
    {
        var messages = Enumerable.Range(0, 55)
            .Select(i => Warning(i + 1, Now.AddHours(-6), Now.AddHours(-1).AddMinutes(-i))).ToArray();

        var projection = Project(View(messages));

        Assert.HasCount(50, projection.Recent);
        Assert.AreEqual(55, projection.RecentTotal);
        Assert.AreEqual(5, projection.RecentMore);
        Assert.AreEqual("WARK05/1", projection.Recent[0].Group);
        Assert.AreEqual("WARK05/50", projection.Recent[49].Group);
        Assert.IsTrue(projection.Recent.All(static n => n.EndReason == "expired" && n.Darkness.Count == 0));
        Assert.AreEqual(0, projection.CurrentTotal);
    }

    [TestMethod]
    public void HistoryShowsEightPagesOfTwentyFiveAndCountsOlderNotices()
    {
        var messages = Enumerable.Range(0, 210)
            .Select(i => Warning(i + 1, Now.AddHours(-26).AddMinutes(-i), Now.AddHours(-25).AddMinutes(-i))).ToArray();

        var projection = Project(View(messages));

        Assert.HasCount(200, projection.History);
        Assert.AreEqual(210, projection.HistoryTotal);
        Assert.AreEqual(10, projection.HistoryNotShown);
        Assert.AreEqual(8, projection.HistoryPageCount);
        Assert.HasCount(25, projection.HistoryPage(0));
        Assert.HasCount(25, projection.HistoryPage(7));
        Assert.AreEqual("WARK05/1", projection.HistoryPage(0)[0].Group);
        Assert.AreEqual("WARK05/200", projection.HistoryPage(7)[24].Group);
        Assert.AreEqual(7, projection.ClampHistoryPage(99));
        Assert.AreEqual(0, projection.ClampHistoryPage(-3));
        Assert.IsTrue(projection.History.All(static n => n.StateText == "history"));
    }

    [TestMethod]
    public void EqualIssueTimesBreakTiesByOrdinalGroupIdentity()
    {
        var issued = Now.AddMinutes(-10);
        var projection = Project(View([
            Warning(2, issued, Now.AddHours(1)), Warning(10, issued, Now.AddHours(1)), Warning(1, issued, Now.AddHours(1))]));

        CollectionAssert.AreEqual(OrdinalOrder, projection.Current.Select(static n => n.Group).ToArray());
    }

    [TestMethod]
    public void EscalationIsDerivedFromVersionsRetrievedBeforeTheAcknowledgement()
    {
        var root = Warning(1, Now.AddHours(-2), Now.AddHours(4), level: "G1");
        var raise = Warning(2, Now.AddHours(-2), Now.AddHours(4), level: "G3", issued: Now.AddHours(-1), link: 1);
        var view = View([root, raise]);
        var group = view.Notices.Groups.Single();
        var retrieved = Retrieved(view);
        var rootRetrieved = Now.AddHours(-2).AddMinutes(1);

        Assert.AreEqual(SpaceWeatherEscalation.NotAcknowledged, SpaceWeatherUiProjectionBuilder.Escalation(group, null, retrieved));
        Assert.AreEqual(SpaceWeatherEscalation.Escalated,
            SpaceWeatherUiProjectionBuilder.Escalation(group, new("owner", Now.AddMinutes(-90)), retrieved));
        Assert.AreEqual(SpaceWeatherEscalation.NotEscalated,
            SpaceWeatherUiProjectionBuilder.Escalation(group, new("owner", Now.AddMinutes(-30)), retrieved));
        Assert.AreEqual(SpaceWeatherEscalation.Unknown,
            SpaceWeatherUiProjectionBuilder.Escalation(group, new("owner", rootRetrieved), retrieved), "Equal timestamps are not ordered.");
        Assert.AreEqual(SpaceWeatherEscalation.Unknown,
            SpaceWeatherUiProjectionBuilder.Escalation(group, new("owner", Now.AddHours(-3)), retrieved), "No version before the acknowledgement.");

        var unjoinable = View([root, raise], retrieved: m => m.Serial == 2 ? null : m.IssueMinuteUtc.AddMinutes(1));
        Assert.AreEqual(SpaceWeatherEscalation.Unknown, SpaceWeatherUiProjectionBuilder.Escalation(
            unjoinable.Notices.Groups.Single(), new("owner", Now.AddMinutes(-30)), Retrieved(unjoinable)));
    }

    [TestMethod]
    public void EscalatedNoticeNeedsAcknowledgementAgainAndCountsTowardTheSite()
    {
        var root = Warning(1, Now.AddHours(-2), Now.AddHours(4), code: "WARK09", level: "G4");
        var raise = Warning(2, Now.AddHours(-2), Now.AddHours(4), code: "WARK09", level: "G5", issued: Now.AddHours(-1), link: 1);
        var acknowledgements = new Dictionary<SwpcMessageNode, SwpcAcknowledgement> { [Node("WARK09", 1)] = new("owner", Now.AddMinutes(-90)) };

        var notice = Project(View([root, raise], acknowledgements: acknowledgements)).Current.Single();

        Assert.AreEqual(SpaceWeatherEscalation.Escalated, notice.Escalation);
        Assert.IsTrue(notice.NeedsAcknowledgement);
        Assert.AreEqual("escalated since acknowledged", notice.EscalationText);
        Assert.IsNotNull(notice.AcknowledgedAt);
        Assert.AreEqual(2, notice.VersionCount);
        Assert.AreEqual("G5", notice.Level);

        var settled = Project(View([root, raise], acknowledgements: new Dictionary<SwpcMessageNode, SwpcAcknowledgement>
        {
            [Node("WARK09", 1)] = new("owner", Now.AddMinutes(-30))
        }));
        Assert.AreEqual(SpaceWeatherEscalation.NotEscalated, settled.Current.Single().Escalation);
        Assert.AreEqual("acknowledged", settled.Current.Single().EscalationText);
        Assert.IsFalse(settled.Current.Single().NeedsAcknowledgement);
        Assert.AreEqual(0, settled.ReachCount);
        Assert.AreEqual(0, settled.ReviewCount);
    }

    [TestMethod]
    public void UnknownEscalationIsAcknowledgeableAgainAndStillCountsTowardTheSite()
    {
        var root = Warning(1, Now.AddHours(-2), Now.AddHours(4), code: "WARK09", level: "G5");
        var acknowledgements = new Dictionary<SwpcMessageNode, SwpcAcknowledgement> { [Node("WARK09", 1)] = new("owner", Now.AddHours(-3)) };

        var projection = Project(View([root], acknowledgements: acknowledgements));

        Assert.AreEqual(SpaceWeatherEscalation.Unknown, projection.Current.Single().Escalation);
        Assert.AreEqual("acknowledged; escalation since then cannot be determined", projection.Current.Single().EscalationText);
        Assert.IsTrue(projection.Current.Single().NeedsAcknowledgement);
        Assert.AreEqual(1, projection.ReachCount);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("K4")]
    [DataRow("G1")]
    [DataRow("G2")]
    [DataRow("G3")]
    [DataRow("G4")]
    [DataRow("G5")]
    public void RankMatchesTheAttentionEvaluator(string level)
    {
        ArgumentNullException.ThrowIfNull(level);
        var value = level.Length == 0 ? null : level;
        var message = Warning(1, Now.AddHours(-1), Now.AddHours(2), level: value);
        var groups = SwpcGroupBuilder.Build([new SwpcMessageVersion(message, 1)]);
        var input = new SwpcAttentionInput(groups, [message.Node], ImmutableDictionary<SwpcMessageNode, int>.Empty, Now);

        var raised = SwpcAttentionEvaluator.Evaluate(input).RaisedRanks[groups[0].CanonicalNode];

        Assert.AreEqual(raised, SpaceWeatherUiProjectionBuilder.Rank(value));
    }

    [TestMethod]
    public void AcknowledgementNeverHidesOrEndsANotice()
    {
        var active = Warning(1, Now.AddHours(-1), Now.AddHours(2));
        var expired = Warning(2, Now.AddHours(-5), Now.AddHours(-2));
        var unacknowledgedExpired = Warning(3, Now.AddHours(-5), Now.AddHours(-2));
        var acknowledgements = new Dictionary<SwpcMessageNode, SwpcAcknowledgement>
        {
            [Node("WARK05", 1)] = new("owner", Now.AddMinutes(-10)),
            [Node("WARK05", 2)] = new("owner", Now.AddHours(-3))
        };

        var projection = Project(View([active, expired, unacknowledgedExpired], acknowledgements: acknowledgements));

        var current = projection.Current.Single();
        Assert.AreEqual("WARK05/1", current.Group);
        Assert.AreEqual(SwpcNoticeState.Active, current.State);
        Assert.IsNotNull(current.AcknowledgedAt);
        Assert.IsNull(current.EndReason);
        var acknowledgedEnded = projection.Recent.Single(static n => n.Group == "WARK05/2");
        Assert.AreEqual("expired", acknowledgedEnded.EndReason);
        Assert.IsNotNull(acknowledgedEnded.AcknowledgedAt);
        Assert.AreEqual("recently ended", acknowledgedEnded.StateText);
        var unacknowledgedEnded = projection.Recent.Single(static n => n.Group == "WARK05/3");
        Assert.IsFalse(unacknowledgedEnded.IsCurrent);
        Assert.AreEqual("expired", unacknowledgedEnded.EndReason);
        Assert.AreEqual(1, projection.ActiveCount);
        Assert.AreEqual(0, projection.ReviewCount, "Ended notices never ask for review, acknowledged or not.");
    }

    [TestMethod]
    public void CancellationEndsTheNoticeAndIsShownAsTheLastUpdate()
    {
        var root = Warning(1, Now.AddHours(-1), Now.AddHours(5));
        var cancel = Warning(2, Now.AddHours(-1), Now.AddHours(5), issued: Now.AddMinutes(-30), link: 1, modifier: "CANCEL");

        var notice = Project(View([root, cancel])).Recent.Single();

        Assert.AreEqual("cancelled", notice.EndReason);
        Assert.IsTrue(notice.LastUpdateIsCancellation);
        Assert.AreEqual("G1", notice.Level);
    }

    [TestMethod]
    public void CurrentNoticesMayBeOutOfDateWhenAlertsAreNotFresh()
    {
        var messages = new[] { Warning(1, Now.AddHours(-1), Now.AddHours(2)), Warning(2, Now.AddHours(-5), Now.AddHours(-2)) };

        var stale = Project(View(messages, alerts: SwpcFreshness.Stale));
        var fresh = Project(View(messages));

        Assert.IsTrue(stale.Current.Single().MayBeOutOfDate);
        Assert.IsFalse(stale.Recent.Single().MayBeOutOfDate);
        Assert.IsFalse(fresh.Current.Single().MayBeOutOfDate);
    }

    [TestMethod]
    [DataRow("WARK04", "K4", LocalRelevanceCategory.NotExpected, "not expected at this latitude", "65.0", "22.8")]
    [DataRow("WARK05", "G1", LocalRelevanceCategory.NotExpected, "not expected at this latitude", "60.0", "17.8")]
    [DataRow("WARK06", "G2", LocalRelevanceCategory.NotExpected, "not expected at this latitude", "55.0", "12.8")]
    [DataRow("WARK07", "G3", LocalRelevanceCategory.Unlikely, "unlikely at this latitude", "50.0", "7.8")]
    [DataRow("WARK08", "G4", LocalRelevanceCategory.PossibleNearHorizon, "possibly near the poleward horizon", "45.0", "2.8")]
    [DataRow("WARK09", "G5", LocalRelevanceCategory.InImpactArea, "within the stated impact latitude", "40.0", "-2.2")]
    public void HvoRelevanceIsPinnedThroughTheProjection(string code, string level, LocalRelevanceCategory category, string text,
        string required, string difference)
    {
        var projection = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2), code: code, level: level)]));
        var notice = projection.Current.Single();

        Assert.AreEqual(42.2, Math.Round(projection.Site.GeomagneticLatitude!.Value, 1));
        Assert.AreEqual(category, notice.Relevance);
        Assert.AreEqual(
            $"At this site: {text} (required geomagnetic latitude {required}°, typical for {level}; site geomagnetic latitude 42.2°; " +
            $"d = {difference}°; {SpaceWeatherUiProjectionBuilder.ModelWording}). {SpaceWeatherUiProjectionBuilder.EstimateSuffix}",
            notice.SiteLine);
        Assert.AreEqual($"SWPC: {level} warning", notice.GlobalLine);
        var reaches = category is LocalRelevanceCategory.InImpactArea or LocalRelevanceCategory.PossibleNearHorizon;
        Assert.AreEqual(reaches, notice.ReachesSite);
        Assert.AreEqual(reaches ? 1 : 0, projection.ReachCount);
        Assert.AreEqual(reaches ? 0 : 1, projection.ReviewCount);
    }

    [TestMethod]
    public void StatedLatitudeTakesPrecedenceOverTheTypicalOne()
    {
        var stated = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2), level: "G1", stated: 40)])).Current.Single();
        var typical = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2), level: "G1")])).Current.Single();

        Assert.AreEqual(LocalRelevanceCategory.InImpactArea, stated.Relevance);
        StringAssert.Contains(stated.SiteLine, "required geomagnetic latitude 40.0°, stated;", StringComparison.Ordinal);
        Assert.AreEqual(LocalRelevanceCategory.NotExpected, typical.Relevance);
        StringAssert.Contains(typical.SiteLine, "required geomagnetic latitude 60.0°, typical for G1;", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(SpaceWeatherFamily.XrayAlert, "ALTXMF")]
    [DataRow(SpaceWeatherFamily.RadioSweep, "ALTTP2")]
    [DataRow(SpaceWeatherFamily.FluxAlert, "ALTEF3")]
    [DataRow(SpaceWeatherFamily.FluxAlert, "ALTPX1")]
    [DataRow(SpaceWeatherFamily.Warning, "WARSUD")]
    [DataRow(SpaceWeatherFamily.Warning, "WARPX1")]
    [DataRow(SpaceWeatherFamily.Warning, "WARK03")]
    [DataRow(SpaceWeatherFamily.Summary, "SUMX01")]
    public void NonGeomagneticFamiliesHaveNoLocalObservingImpact(SpaceWeatherFamily family, string code)
    {
        var message = Message(code, 1, Now.AddMinutes(-10), family, level: "S1",
            times: new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal)
            {
                ["Valid From"] = Now.AddMinutes(-10),
                ["Valid To"] = Now.AddHours(2),
                ["Threshold Reached"] = Now.AddMinutes(-10),
                ["Begin Time"] = Now.AddMinutes(-10)
            });

        var projection = Project(View([message]));
        var notice = projection.Current.Concat(projection.Recent).Single();

        Assert.AreEqual(LocalRelevanceCategory.NoLocalObservingImpact, notice.Relevance);
        Assert.AreEqual($"At this site: no local optical observing impact. {SpaceWeatherUiProjectionBuilder.EstimateSuffix}", notice.SiteLine);
        Assert.AreEqual(0, projection.ReachCount);
    }

    [TestMethod]
    public void UnknownRelevanceStatesItsReason()
    {
        var unclassified = Project(View([Message("XYZ123", 1, Now.AddMinutes(-10), SpaceWeatherFamily.Unclassified)])).Current.Single();
        var noSite = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2))]), noSite: true);
        var noLevel = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2), level: null)])).Current.Single();
        var outOfRange = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2), stated: 120)])).Current.Single();

        Assert.AreEqual(LocalRelevanceCategory.Unknown, unclassified.Relevance);
        Assert.AreEqual($"At this site: unknown (unclassified message). {SpaceWeatherUiProjectionBuilder.EstimateSuffix}", unclassified.SiteLine);
        Assert.IsFalse(noSite.Site.Configured);
        Assert.IsNull(noSite.Site.GeomagneticLatitude);
        Assert.AreEqual($"At this site: unknown (site location not configured). {SpaceWeatherUiProjectionBuilder.EstimateSuffix}",
            noSite.Current.Single().SiteLine);
        Assert.AreEqual(SpaceWeatherUiProjectionBuilder.NoSiteDarkness, noSite.Current.Single().Darkness.Single());
        Assert.AreEqual($"At this site: unknown (no level or latitude stated). {SpaceWeatherUiProjectionBuilder.EstimateSuffix}", noLevel.SiteLine);
        Assert.AreEqual("SWPC: warning, level not stated", noLevel.GlobalLine);
        Assert.AreEqual(LocalRelevanceCategory.Unknown, outOfRange.Relevance);
        Assert.AreEqual($"At this site: unknown (the stated latitude is out of range). {SpaceWeatherUiProjectionBuilder.EstimateSuffix}",
            outOfRange.SiteLine);
    }

    [TestMethod]
    public void DarknessIsComputedForCurrentNoticesOnly()
    {
        var sun = new TestSun();
        var projection = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2)), Warning(2, Now.AddHours(-5), Now.AddHours(-2)),
            Warning(3, Now.AddHours(-30), Now.AddHours(-26))]), sun: sun);

        Assert.AreEqual(SpaceWeatherUiProjectionBuilder.UndeterminedDarkness, projection.Current.Single().Darkness.Single());
        Assert.IsEmpty(projection.Recent.Single().Darkness);
        Assert.IsEmpty(projection.History.Single().Darkness);
        Assert.AreEqual(4, sun.Calls, "One bounded query per solar event kind for the single current window.");
    }

    [TestMethod]
    public void DarknessIsNotAssessedForWindowsLongerThanNinetySixHours()
    {
        var sun = new TestSun();
        var notice = Project(View([Warning(1, Now.AddHours(-20), Now.AddHours(200), issued: Now.AddHours(-1))]), sun: sun).Current.Single();

        Assert.AreEqual(SwpcNoticeState.Active, notice.State);
        Assert.AreEqual(SpaceWeatherUiProjectionBuilder.LongWindowDarkness, notice.Darkness.Single());
        Assert.AreEqual(0, sun.Calls);
    }

    [TestMethod]
    public void DarknessRestrictionsAreShownInSiteLocalTimeAndUtc()
    {
        var from = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var sun = new TestSun((SolarEventKind.AstronomicalDawn, from.AddMinutes(15)), (SolarEventKind.Sunrise, from.AddMinutes(90)));

        var notice = Project(View([Warning(1, from, from.AddHours(6))]), sun: sun).Current.Single();

        CollectionAssert.AreEqual(PhoenixRestrictions, notice.Darkness.ToArray());
    }

    [TestMethod]
    public void BoundariesWithoutARestrictionSayNoneWasFound()
    {
        var from = Now.AddHours(-1);
        var notice = Project(View([Warning(1, from, Now.AddHours(2))]), sun: new TestSun((SolarEventKind.Sunset, from))).Current.Single();

        Assert.AreEqual(SpaceWeatherUiProjectionBuilder.NoRestrictionDarkness, notice.Darkness.Single());
    }

    [TestMethod]
    public void DarknessResultsAreReusedAcrossReads()
    {
        var sun = new TestSun();
        var cache = new SpaceWeatherDarknessCache();
        var view = View([Warning(1, Now.AddHours(-1), Now.AddHours(2))]);

        Project(view, sun: sun, cache: cache);
        var calls = sun.Calls;
        Project(view, now: Now.AddMinutes(5), sun: sun, cache: cache);

        Assert.AreEqual(4, calls);
        Assert.AreEqual(calls, sun.Calls);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void DarknessCacheIsBoundedAndClearedOnOverflow()
    {
        var cache = new SpaceWeatherDarknessCache();
        var computed = 0;
        for (var i = 0; i < 256; i++)
        {
            cache.GetOrAdd(Key(i), () => { computed++; return null; });
        }
        Assert.AreEqual(256, cache.Count);
        cache.GetOrAdd(Key(5), () => { computed++; return null; });
        Assert.AreEqual(256, computed, "A hit does not compute.");

        cache.GetOrAdd(Key(256), () => { computed++; return null; });

        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(257, computed);

        static SpaceWeatherDarknessCache.Key Key(int i) => new("WARK05/" + i.ToString(CultureInfo.InvariantCulture), Now, Now.AddHours(1), 35.5, -113.9, 1100);
    }

    [TestMethod]
    public void HaltsDriftAndSourcesAreMapped()
    {
        var since = Now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);
        var status = Status(providerHalt: new("space-weather.http-forbidden", since), products:
        [
            Product(halt: new("space-weather.product-gone", since)),
            Product("scales", rejected: 3, halt: new("space-weather.product-gone", "2026-10-20T06:00:00.0000000-07:00")),
            Product("kp-forecast", configured: false, rejected: 5, halt: new("space-weather.product-gone", since)),
            Product("3-day-forecast", succeeded: false)
        ]);

        var projection = Project(View([], recordDrift: Now.AddHours(-2)), status: status);

        Assert.IsNotNull(projection.ProviderHalt);
        Assert.AreEqual("provider", projection.ProviderHalt.Target);
        Assert.IsTrue(projection.ProviderHalt.CanResume);
        Assert.AreEqual(since, projection.ProviderHalt.SinceUtcText);
        Assert.AreEqual(Now.AddHours(-1), projection.ProviderHalt.SinceUtc);
        Assert.HasCount(3, projection.ProductHalts);
        var alerts = projection.ProductHalts.Single(static h => h.Product == "alerts");
        Assert.IsTrue(alerts.CanResume);
        var offset = projection.ProductHalts.Single(static h => h.Product == "scales");
        Assert.IsNull(offset.SinceUtc, "A non-UTC halt time is never echoed back as a resume precondition.");
        Assert.IsFalse(offset.CanResume);
        Assert.IsFalse(projection.ProductHalts.Single(static h => h.Product == "kp-forecast").CanResume, "Not configured.");
        Assert.AreEqual("scales", projection.FormatDriftProducts.Single());
        Assert.AreEqual("alerts", projection.RecordDriftProducts.Single());
        Assert.HasCount(4, projection.Sources);
        Assert.IsFalse(projection.NoFirstSuccess, "One configured product has succeeded.");
        Assert.AreEqual("fresh", projection.AlertsFreshness);
    }

    [TestMethod]
    public void NoFirstSuccessWhenNoConfiguredProductHasSucceeded()
    {
        var projection = Project(View([]), status: Status(products: [Product(succeeded: false), Product("scales", configured: false)]));

        Assert.IsTrue(projection.NoFirstSuccess);
        Assert.AreEqual(0, projection.NoticeTotal);
    }

    [TestMethod]
    public void UnavailableProjectionClaimsNoActivity()
    {
        var projection = SpaceWeatherUiProjectionBuilder.Unavailable(SpaceWeatherUiState.StoreUnavailable, true, Site(), Now);

        Assert.IsFalse(projection.HasData);
        Assert.AreEqual(0, projection.NoticeTotal);
        Assert.IsEmpty(projection.Current);
        Assert.AreEqual(SpaceWeatherOperations.Credit, projection.Credit);
        Assert.IsTrue(projection.Enabled);
    }

    [TestMethod]
    [DataRow(SpaceWeatherFamily.Watch, "WATA20", "Geomagnetic storm watch")]
    [DataRow(SpaceWeatherFamily.KAlert, "ALTK05", "Geomagnetic K-index alert")]
    [DataRow(SpaceWeatherFamily.Warning, "WARK04", "Geomagnetic K-index warning")]
    [DataRow(SpaceWeatherFamily.Warning, "WARK09", "Geomagnetic K-index warning")]
    [DataRow(SpaceWeatherFamily.Warning, "WARSUD", "Sudden impulse warning")]
    [DataRow(SpaceWeatherFamily.Warning, "WARPX1", "Proton flux warning")]
    [DataRow(SpaceWeatherFamily.Warning, "WAREF3", "Space weather warning (WAREF3)")]
    [DataRow(SpaceWeatherFamily.Warning, "WARK03", "Space weather warning (WARK03)")]
    [DataRow(SpaceWeatherFamily.XrayAlert, "ALTXMF", "X-ray flux alert (radio blackout)")]
    [DataRow(SpaceWeatherFamily.RadioSweep, "ALTTP2", "Solar radio sweep alert")]
    [DataRow(SpaceWeatherFamily.FluxAlert, "ALTEF3", "Electron flux alert")]
    [DataRow(SpaceWeatherFamily.FluxAlert, "ALTPX1", "Proton flux alert")]
    [DataRow(SpaceWeatherFamily.FluxAlert, "ALTPC0", "Proton flux alert")]
    [DataRow(SpaceWeatherFamily.FluxAlert, "ALTZZZ", "Space weather flux alert (ALTZZZ)")]
    [DataRow(SpaceWeatherFamily.Summary, "SUMX01", "Event summary (SUMX01)")]
    [DataRow(SpaceWeatherFamily.Unclassified, "XYZ123", "Unclassified SWPC message (XYZ123)")]
    public void LabelsFollowFamilyAndCode(SpaceWeatherFamily family, string code, string label)
        => Assert.AreEqual(label, SpaceWeatherUiProjectionBuilder.Label(family, code));

    [TestMethod]
    public void GlobalLineMarksSourceActivity()
    {
        Assert.AreEqual("SWPC: G2 watch", SpaceWeatherUiProjectionBuilder.GlobalLine(SpaceWeatherFamily.Watch, "G2"));
        Assert.AreEqual("SWPC: K4 warning", SpaceWeatherUiProjectionBuilder.GlobalLine(SpaceWeatherFamily.Warning, "K4"));
        Assert.AreEqual("SWPC: K-index alert, level not stated", SpaceWeatherUiProjectionBuilder.GlobalLine(SpaceWeatherFamily.KAlert, null));
        Assert.AreEqual("SWPC: message, level not stated", SpaceWeatherUiProjectionBuilder.GlobalLine(SpaceWeatherFamily.Unclassified, null));
    }

    [TestMethod]
    public void YearRolloverWatchIsShownInPhoenixTimeAndEndsAtTheUtcDayBoundary()
    {
        using var scenario = SwpcTestData.Scenario("s14-year-rollover-and-local-day.json");
        var step = scenario.RootElement.GetProperty("steps")[0];
        var body = Encoding.UTF8.GetBytes(step.GetProperty("response").GetProperty("body").GetString()!);
        var records = SwpcAlertsParser.Parse(body, "application/json").Records;
        var retrievedAt = SwpcTestData.Time(step.GetProperty("atUtc").GetString()!);
        var view = View(records, retrieved: _ => retrievedAt);

        var active = Project(view, now: SwpcTestData.Time("2027-01-01T00:00:00Z")).Current.Single(static n => n.Group == "WATA20/1200");
        var ended = Project(view, now: SwpcTestData.Time("2027-01-03T00:00:00Z"));

        Assert.AreEqual(SwpcNoticeState.Active, active.State);
        Assert.AreEqual("2026-12-31 14:00 -07:00 (America/Phoenix)", active.From.LocalText);
        Assert.AreEqual("2027-01-02 17:00 -07:00 (America/Phoenix)", active.Until.LocalText);
        Assert.AreEqual("2026-12-31 21:00 UTC", active.From.UtcText);
        Assert.AreEqual("2027-01-03 00:00 UTC", active.Until.UtcText);
        Assert.IsFalse(ended.Current.Any(static n => n.Group == "WATA20/1200"));
        var recent = ended.Recent.Single(static n => n.Group == "WATA20/1200");
        Assert.AreEqual(SwpcNoticeState.Recent, recent.State);
        Assert.AreEqual("expired", recent.EndReason);
    }

    [TestMethod]
    public void DaylightSavingTransitionKeepsTheOffsetOfEachInstant()
    {
        var time = new CameraAgentSiteTime(ObservingDayCalendar.Create("America/New_York"));

        Assert.AreEqual("2026-11-01 01:30 -04:00 (America/New_York)",
            SpaceWeatherUiTime.Create(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), time).LocalText);
        Assert.AreEqual("2026-11-01 01:30 -05:00 (America/New_York)",
            SpaceWeatherUiTime.Create(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), time).LocalText);
    }

    [TestMethod]
    public void MissingOrInvalidSiteZoneFallsBackToLabelledUtc()
    {
        var noSite = Project(View([Warning(1, Now.AddHours(-1), Now.AddHours(2))]), noSite: true);
        var badZone = SpaceWeatherUiTime.Create(Now, new CameraAgentSiteTime(ObservingDayCalendar.Create("Not/AZone")));

        Assert.AreEqual("UTC (site time zone unavailable)", noSite.Site.ZoneLabel);
        Assert.AreEqual("2026-10-20 13:00 +00:00 (UTC (site time zone unavailable))", noSite.Current.Single().From.LocalText);
        Assert.AreEqual("2026-10-20 14:00 +00:00 (UTC (site time zone unavailable))", badZone.LocalText);
    }

    private static Dictionary<(string Identity, long Sequence), DateTimeOffset> Retrieved(SwpcStoreView view)
        => view.Notices.Versions.ToDictionary(static s => (s.Version.Message.Identity, s.Version.IngestSequence), static s => s.RetrievedUtc);
}
