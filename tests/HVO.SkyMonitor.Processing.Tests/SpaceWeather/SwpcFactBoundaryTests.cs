using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.Processing.Tests.SpaceWeather;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestCategory("Unit")]
public sealed class SwpcFactBoundaryTests
{
    [TestMethod]
    public void ScenarioRowsCoverEveryCoreScenarioInManifest()
    {
        var rows = typeof(SwpcScenarioFactsTests).GetMethod(nameof(SwpcScenarioFactsTests.PureFactsMatchRecordedScenarioExpectations))!
            .GetCustomAttributes(typeof(DataRowAttribute), false).Cast<DataRowAttribute>().Select(a => (string)a.Data[0]!).ToArray();
        CollectionAssert.AreEquivalent(SwpcScenarioFactsTests.Scenarios().Select(r => (string)r[0]).ToArray(), rows);
    }

    [TestMethod]
    public void TokensEqualContractVocabularies()
    {
        var contract = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/space-weather/space-weather-facts-v1.md"));
        static string Section(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            return text[from..text.IndexOf(end, from + start.Length, StringComparison.Ordinal)];
        }
        static string[] Captures(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        static void Equal(IEnumerable<string> expected, IEnumerable<string> actual) => CollectionAssert.AreEquivalent(expected.ToArray(), actual.ToArray());
        // OVATION remains an unresolved data-only product, excluded by decision D1.
        Equal(Captures(Section(contract, "**Base intervals:**", "The per-product structure"), @"^\| `([^`]+)` \|").Where(t => t != "ovation"), Enum.GetValues<SwpcProduct>().Select(p => p.Token()));
        Equal(Captures(Section(contract, "**Families.**", "**Levels:**"), @"^\| `([^`]+)` \|"), Enum.GetValues<SpaceWeatherFamily>().Select(p => p.Token()));
        Equal(Captures(Section(contract, "**States**", "**Retention.**"), @"^\| `([^`]+)` \|"), Enum.GetValues<SwpcNoticeState>().Select(p => p.Token()));
        var lifecycle = Section(contract, "**Adjustments.**", "**States**");
        Equal(Captures(lifecycle, @"`(superseded|cancelled|capped|expired)`"), Enum.GetValues<SwpcNoticeEndReason>().Select(p => p.Token()));
        Equal(Captures(Section(contract, "## Local relevance", "**OVATION band**"), @"`([A-Z][a-zA-Z]+)`"), Enum.GetValues<LocalRelevanceCategory>().Select(p => p.Token()));
        Equal(Captures(Section(contract, "**Per-record checks.**", "**Identity.**"), @"^\| `([^`]+)` \|"), SwpcTokens.RecordRejectionReasons);
        Equal(Captures(Section(contract, "**Body rejection reasons.**", "**Halts:**"), @"^(?:[0-9]+\. |   - |5\. For JSON products, )`([a-z-]+)`"), SwpcTokens.BodyRejectionReasons);
    }

    [TestMethod]
    public void NormalizationMatchesScenarioRecordHashesAndIsIdempotent()
    {
        foreach (var path in SwpcScenarioFactsTests.Scenarios().Select(row => (string)row[0]))
        {
            var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(SwpcScenarioFactsTests.Pack, path)))!;
            foreach (var step in scenario["steps"]!.AsArray().Select(s => s!))
            {
                if (step["product"]?.GetValue<string>() != "alerts" || step["response"] is not { } response || response["status"]?.GetValue<int>() != 200) continue;
                var parsed = SwpcAlertsParser.Parse(SwpcScenarioFactsTests.Bytes(response), response["contentType"]?.GetValue<string>());
                foreach (var record in parsed.Records)
                {
                    var original = JsonNode.Parse(record.OriginalRecordJson)!;
                    var normalized = SwpcText.Normalize(original["message"]!.GetValue<string>());
                    Assert.AreEqual(record.NormalizedText, normalized);
                    Assert.AreEqual(record.ContentSha256, SwpcHash.Sha256UpperHex(Encoding.UTF8.GetBytes(normalized)));
                    Assert.AreEqual(normalized, SwpcText.Normalize(normalized));
                    Assert.DoesNotContain('\r', normalized);
                }
            }
        }
        // The model returns empty for all-blank input, despite C's "exactly one LF" wording.
        Assert.AreEqual("", SwpcText.Normalize(" \r\n\t\n"));
    }

    [TestMethod]
    [DataRow(64, "no-valid-records")]
    [DataRow(65, "malformed-json")]
    public void JsonDepthCountsTheOuterContainer(int depth, string expected)
    {
        // s12 supplies the expected reasons; this explicitly pins the reader's off-by-one boundary.
        var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(SwpcScenarioFactsTests.Pack, "scenarios/s12-malformed-bodies.json")))!;
        var body = new string('[', depth) + new string(']', depth);
        var step = scenario["steps"]!.AsArray().Single(s => s?["response"]?["body"]?.GetValue<string>() == body)!;
        Assert.AreEqual("body-rejected:" + expected, step["expect"]!["diagnostics"]![0]!.GetValue<string>());
        Assert.AreEqual(expected, SwpcAlertsParser.Parse(Encoding.ASCII.GetBytes(body), "application/json").RejectionReason);
    }

    [TestMethod]
    public void UnterminatedEscapedStringIsBoundedAndMalformed()
    {
        var body = Encoding.ASCII.GetBytes("\"" + string.Concat(Enumerable.Repeat("\\\"", 524287)));
        var timer = Stopwatch.StartNew();
        Assert.AreEqual("malformed-json", SwpcAlertsParser.Parse(body, "application/json").RejectionReason);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), timer.Elapsed);
    }

    [TestMethod]
    public void EnvelopeRejectionOrderPrecedesJsonAndRecordSchema()
    {
        var oversized = new byte[SwpcLimits.AlertsBytes + 1];
        Array.Fill(oversized, (byte)0xff);
        Assert.AreEqual("oversize", SwpcAlertsParser.Parse(oversized, "text/html").RejectionReason);
        Assert.AreEqual("content-type", SwpcAlertsParser.Parse([0xff], "text/html").RejectionReason);
        Assert.AreEqual("encoding", SwpcAlertsParser.Parse([0xff], "application/json").RejectionReason);
        Assert.AreEqual("malformed-json", SwpcAlertsParser.Parse("[NaN]"u8, "APPLICATION/JSON; charset=utf-8").RejectionReason);
        Assert.AreEqual("malformed-json", SwpcAlertsParser.Parse("[] []"u8, "application/json").RejectionReason);
    }

    [TestMethod]
    public void RecordChecksStopAtFirstFailure()
    {
        var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(SwpcScenarioFactsTests.Pack, "scenarios/s01-duplicate-refetch.json")))!;
        var good = JsonNode.Parse(scenario["steps"]![0]!["response"]!["body"]!.GetValue<string>())![0]!;
        var bad = good.DeepClone();
        bad["product_id"] = "bad"; bad["issue_datetime"] = "bad"; bad["message"] = new string('x', SwpcLimits.MessageBytes + 1);
        var mixed = new JsonArray(good.DeepClone(), bad);
        SwpcBodyResult<SwpcMessageRecord> Parse() => SwpcAlertsParser.Parse(Encoding.UTF8.GetBytes(mixed.ToJsonString()), "application/json");
        Assert.AreEqual("record-schema", Parse().Rejections.Single().Reason);
        bad["product_id"] = good["product_id"]!.DeepClone();
        Assert.AreEqual("issue-datetime", Parse().Rejections.Single().Reason);
        bad["issue_datetime"] = good["issue_datetime"]!.DeepClone();
        Assert.AreEqual("record-oversize", Parse().Rejections.Single().Reason);
        bad["message"] = "bad";
        Assert.AreEqual("message-header", Parse().Rejections.Single().Reason);
    }

    [TestMethod]
    public void VersionOrderUsesSourceTextBeforeIngestOrderAndGroupsStayCodeLocal()
    {
        var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(SwpcScenarioFactsTests.Pack, "scenarios/s27-original-record-outer-whitespace.json")))!;
        var records = scenario["steps"]!.AsArray().Where(s => s?["kind"]?.GetValue<string>() == "fetch").Take(2)
            .Select(s => SwpcAlertsParser.Parse(SwpcScenarioFactsTests.Bytes(s!["response"]!), "application/json").Records.Single()).ToArray();
        var groups = SwpcGroupBuilder.Build([new(records[1], 2), new(records[0], 1)]);
        Assert.AreEqual(records[1].ContentSha256, groups.Single().Latest.ContentSha256);
        var earlierText = records[1] with { IssueDatetimeText = "0000-00-00 00:00:00.000" };
        Assert.AreEqual(records[0].ContentSha256, SwpcGroupBuilder.Build([new(earlierText, 3), new(records[0], 1)]).Single().Latest.ContentSha256);
        var anotherCode = records[1] with { Code = "OTHER", Header = records[1].Header with { Link = new("extension", records[0].Serial) } };
        Assert.HasCount(2, SwpcGroupBuilder.Build([new(anotherCode, 2), new(records[0], 1)]));
    }

    [TestMethod]
    public void RelevanceDifferenceUsesEveryGoldenBinaryRoundingVector()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(SwpcScenarioFactsTests.Pack, "rounding-vectors.json")))!;
        foreach (var vector in vectors["relevanceDifference"]!.AsArray().Select(v => v!))
        {
            var input = vector["input"]!;
            var result = SpaceWeatherRelevance.Difference(input["requiredLatitudeDeg"]!.GetValue<double>(), input["geomagneticLatitudeDeg"]!.GetValue<double>());
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(vector["expectedRounded"]!.GetValue<double>()), BitConverter.DoubleToInt64Bits(result));
        }
    }

    [TestMethod]
    public void DarknessQueriesAreBoundedAndCannotChangeRelevance()
    {
        var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var window = new SwpcNoticeWindow(now, now.AddHours(96), now.AddHours(120), SwpcNoticeState.Active, null, SwpcNoticeEndReason.Expired, false);
        var sun = new RecordingSun();
        var category = SpaceWeatherRelevance.Categorize(SpaceWeatherFamily.Watch, "WATA50", "G3", null, 42.2);
        var qualifier = SpaceWeatherDarknessQualifier.Qualify(window, new(35.5599378, -113.9119818, 1000), sun);
        Assert.HasCount(16, sun.Queries);
        Assert.IsEmpty(qualifier.Boundaries); // No boundary evidence makes no polar-day/night claim.
        Assert.AreEqual(LocalRelevanceCategory.Unlikely, category.Category);
        Assert.IsTrue(sun.Queries.All(q => q.End - q.Start <= TimeSpan.FromHours(24)));
        Assert.IsTrue(sun.Queries.All(q => q.Start >= window.FromUtc && q.End <= window.UntilUtc));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SpaceWeatherDarknessQualifier.Qualify(window with { UntilUtc = now.AddHours(97) }, new(0, 0, 0), sun));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DarknessAddsOnlyIntervalsSupportedBySolarBoundaries(bool startsInDaylight)
    {
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        var window = new SwpcNoticeWindow(start, start.AddHours(12), start.AddHours(36), SwpcNoticeState.Active, null, SwpcNoticeEndReason.Expired, false);
        var result = SpaceWeatherDarknessQualifier.Qualify(window, new(35, -113, 1000), new BoundarySun(start, startsInDaylight));
        var daylight = result.Restrictions.Single(r => r.Reason == "daylight");
        Assert.AreEqual(startsInDaylight ? start : start.AddHours(2), daylight.FromUtc);
        Assert.AreEqual(start.AddHours(6), daylight.UntilUtc);
        var twilight = result.Restrictions.Single(r => r.Reason == "daylight-or-twilight");
        Assert.AreEqual(startsInDaylight ? start : start.AddHours(1), twilight.FromUtc);
        Assert.AreEqual(start.AddHours(7), twilight.UntilUtc);
        Assert.IsTrue(result.Restrictions.All(r => r.FromUtc >= window.FromUtc && r.UntilUtc <= window.UntilUtc));
    }

    private sealed class BoundarySun(DateTimeOffset start, bool startsInDaylight) : ISolarEventCalculator
    {
        public SolarEventResult Find(SolarEventKind kind, DateTimeOffset intervalStartUtc, DateTimeOffset intervalEndUtc, double latitudeDegrees, double longitudeDegrees, double elevationMeters)
        {
            var at = kind switch
            {
                SolarEventKind.Sunrise => startsInDaylight ? (DateTimeOffset?)null : start.AddHours(2),
                SolarEventKind.Sunset => start.AddHours(6),
                SolarEventKind.AstronomicalDawn => startsInDaylight ? (DateTimeOffset?)null : start.AddHours(1),
                SolarEventKind.AstronomicalDusk => start.AddHours(7),
                _ => null
            };
            return new(kind, at, "fixture-solar-v1");
        }
    }

    private sealed class RecordingSun : ISolarEventCalculator
    {
        internal List<(DateTimeOffset Start, DateTimeOffset End)> Queries { get; } = [];
        public SolarEventResult Find(SolarEventKind kind, DateTimeOffset intervalStartUtc, DateTimeOffset intervalEndUtc, double latitudeDegrees, double longitudeDegrees, double elevationMeters)
        {
            Queries.Add((intervalStartUtc, intervalEndUtc));
            return new(kind, null, "fixture-solar-v1");
        }
    }
}
