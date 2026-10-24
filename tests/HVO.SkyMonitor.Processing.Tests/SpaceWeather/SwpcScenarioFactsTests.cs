using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.Processing.Tests.SpaceWeather;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestCategory("Unit")]
public sealed class SwpcScenarioFactsTests
{
    internal static string Pack => Path.Combine(AppContext.BaseDirectory, "Fixtures/space-weather/swpc-v1");

    public static IEnumerable<object[]> Scenarios()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Pack, "manifest.json")))!;
        foreach (var path in manifest["derived"]!["scenarios"]!.AsArray())
        {
            var name = path!.GetValue<string>();
            // U3 excludes OVATION from S1 and the approved product vocabulary.
            if (!name.Contains("s18-", StringComparison.Ordinal)) yield return [name];
        }
    }

    [TestMethod]
    [DataRow("scenarios/s01-duplicate-refetch.json")]
    [DataRow("scenarios/s02-same-identity-revised-content.json")]
    [DataRow("scenarios/s03-correction-reissue.json")]
    [DataRow("scenarios/s04-continuation-self-reference.json")]
    [DataRow("scenarios/s05-extension-chain-expiry.json")]
    [DataRow("scenarios/s06-extension-before-root.json")]
    [DataRow("scenarios/s07-watch-supersession-and-cancel.json")]
    [DataRow("scenarios/s08-watch-replacement-and-escalation.json")]
    [DataRow("scenarios/s09-synthetic-cancel-warning.json")]
    [DataRow("scenarios/s10-restart-ack-rolling-window.json")]
    [DataRow("scenarios/s11-stale-cdn-out-of-order.json")]
    [DataRow("scenarios/s12-malformed-bodies.json")]
    [DataRow("scenarios/s13-outage-backoff-halts.json")]
    [DataRow("scenarios/s14-year-rollover-and-local-day.json")]
    [DataRow("scenarios/s15-missing-scale-and-location.json")]
    [DataRow("scenarios/s16-kp-estimated-future-bin.json")]
    [DataRow("scenarios/s17-three-day-issue-versus-last-modified.json")]
    [DataRow("scenarios/s19-early-resume-spacing.json")]
    [DataRow("scenarios/s20-record-level-drift.json")]
    [DataRow("scenarios/s21-pending-short-window-and-cap.json")]
    [DataRow("scenarios/s22-gone-client-error-network.json")]
    [DataRow("scenarios/s23-retry-after-forms.json")]
    [DataRow("scenarios/s24-json-nonfinite.json")]
    [DataRow("scenarios/s25-year-bounds.json")]
    [DataRow("scenarios/s26-original-record-verbatim.json")]
    [DataRow("scenarios/s27-original-record-outer-whitespace.json")]
    [DataRow("scenarios/s28-orphan-join-chain.json")]
    public void PureFactsMatchRecordedScenarioExpectations(string scenarioPath)
    {
        var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(Pack, scenarioPath)))!;
        var versions = new List<SwpcMessageVersion>();
        var raised = ImmutableDictionary<SwpcMessageNode, int>.Empty;
        var acks = ImmutableDictionary<SwpcMessageNode, SwpcAcknowledgement>.Empty;
        var products = new Dictionary<string, (string Hash, JsonNode Facts)>();
        long sequence = 0;
        foreach (var step in scenario["steps"]!.AsArray().Select(s => s!))
        {
            var where = scenarioPath + " step " + step["step"];
            var expect = step["expect"];
            var now = step["atUtc"] is { } at ? ParseTime(at) : DateTimeOffset.MinValue;
            if (now != DateTimeOffset.MinValue)
            {
                var priorGroups = SwpcGroupBuilder.Build(versions);
                var expired = priorGroups.Where(g => SwpcNoticeLifecycle.IsRetentionExpired(SwpcNoticeLifecycle.Evaluate(g, priorGroups, now), now)).ToArray();
                foreach (var group in expired)
                {
                    versions.RemoveAll(v => group.Nodes.Contains(v.Message.Node));
                    raised = raised.RemoveRange(group.Nodes);
                    acks = acks.RemoveRange(group.Nodes);
                }
            }
            var events = ImmutableArray<SwpcAttentionEvent>.Empty;
            var kind = step["kind"]!.GetValue<string>();
            if (kind == "fetch" && step["response"] is { } response && response["status"]?.GetValue<int>() == 200)
            {
                var product = step["product"]!.GetValue<string>();
                // S2 owns request admission, fetch outcome selection, validators, freshness,
                // last-known-good state and provider drift. Use the recorded merge outcome here.
                if (product == "ovation") continue;
                var outcome = expect?["outcome"]?.GetValue<string>();
                var body = Bytes(response);
                var media = response["contentType"]?.GetValue<string>();
                string? rejection;
                string? hash;
                JsonNode facts;
                SwpcBodyResult<SwpcMessageRecord>? alerts = null;
                switch (product)
                {
                    case "alerts":
                        alerts = SwpcAlertsParser.Parse(body, media);
                        rejection = alerts.RejectionReason; hash = alerts.SnapshotSha256;
                        facts = new JsonObject { ["records"] = alerts.RecordCount, ["acceptedRecords"] = alerts.Records.Length };
                        break;
                    case "scales":
                        var scales = SwpcScalesParser.Parse(body, media);
                        rejection = scales.RejectionReason; hash = scales.SnapshotSha256;
                        facts = new JsonObject(scales.Records.Select(r => KeyValuePair.Create<string, JsonNode?>(r.Window, JsonSerializer.SerializeToNode(new
                        {
                            stampUtc = Utc(r.StampUtc),
                            rScale = r.RScale,
                            rMinorProbPct = r.RMinorProbPct,
                            rMajorProbPct = r.RMajorProbPct,
                            sScale = r.SScale,
                            sProbPct = r.SProbPct,
                            gScale = r.GScale
                        }))));
                        break;
                    case "kp-forecast":
                        var kp = SwpcKpForecastParser.Parse(body, media);
                        rejection = kp.RejectionReason; hash = kp.SnapshotSha256;
                        facts = new JsonObject
                        {
                            ["rowCount"] = kp.RecordCount,
                            ["firstBinStartUtc"] = kp.Records.IsEmpty ? null : Utc(kp.Records[0].StartUtc),
                            ["lastBinStartUtc"] = kp.Records.IsEmpty ? null : Utc(kp.Records[^1].StartUtc),
                            ["bins"] = new JsonObject(kp.Records.Select(b => KeyValuePair.Create<string, JsonNode?>(Utc(b.StartUtc), JsonSerializer.SerializeToNode(new { kp = b.KpText, label = b.ObservedKind, noaaScale = b.NoaaScale }))))
                        };
                        break;
                    default:
                        var forecast = SwpcThreeDayForecastParser.Parse(body, media);
                        rejection = forecast.RejectionReason; hash = forecast.SnapshotSha256;
                        facts = forecast.Records.IsEmpty ? new JsonObject() : Forecast(forecast.Records[0]);
                        break;
                }
                if (outcome == "rejected")
                {
                    var diagnostics = expect!["diagnostics"]!.AsArray();
                    Assert.AreEqual(diagnostics[0]!.GetValue<string>(), "body-rejected:" + rejection, where);
                }
                else
                {
                    Assert.IsNull(rejection, where);
                    Assert.IsNotNull(hash, where);
                    if (outcome is "updated" or "unchanged") products[product] = (hash, facts);
                    if (alerts is not null && outcome != "unchanged")
                    {
                        var expectedChecks = expect?["diagnostics"]?.AsArray().Select(d => d!.GetValue<string>()).Where(d => d.StartsWith("record-rejected:", StringComparison.Ordinal) || d.StartsWith("issue-time-mismatch:", StringComparison.Ordinal) || d.StartsWith("duplicate-record-in-body:", StringComparison.Ordinal)).ToArray();
                        if (expectedChecks is not null) CollectionAssert.AreEqual(expectedChecks, SwpcAlertsParser.Diagnostics(alerts).ToArray(), where);
                        var touched = ImmutableHashSet.CreateBuilder<SwpcMessageNode>();
                        foreach (var record in alerts.Records)
                        {
                            // S2 owns durable ingest/deduplication and stale-revision policy. This
                            // in-test fixture projection supplies S1's version and touched-node inputs.
                            var stored = versions.Where(v => v.Message.Identity == record.Identity).ToArray();
                            if (stored.Any(v => v.Message.ContentSha256 == record.ContentSha256) || stored.Length > 0 && outcome == "stale-snapshot") continue;
                            versions.Add(new(record, ++sequence)); touched.Add(record.Node);
                        }
                        var groups = SwpcGroupBuilder.Build(versions);
                        var attention = SwpcAttentionEvaluator.Evaluate(new(groups, touched.ToImmutable(), raised, now));
                        events = attention.Events; raised = attention.RaisedRanks;
                    }
                }
                if (expect?["product"] is { } expectedProduct && products.TryGetValue(product, out var storedProduct))
                {
                    if (expectedProduct["lkgSha256"] is { } sha) Assert.AreEqual(sha.GetValue<string>(), storedProduct.Hash, where);
                    if (expectedProduct["facts"] is { } expectedFacts) Subset(expectedFacts, storedProduct.Facts, where + " facts");
                }
            }
            else if (kind == "acknowledge")
            {
                var group = SwpcGroupBuilder.Build(versions).Single(g => g.Identity == step["group"]!.GetValue<string>());
                acks = acks.SetItem(group.CanonicalNode, new(step["operator"]!.GetValue<string>(), now));
            }
            // S3 owns persistence and restart round trips. A restart leaves these pure inputs intact.
            // S2/S4 own resume, transient/HTTP outcomes, drift attention and source freshness.
            if (expect?["attention"] is { } expectedAttention)
            {
                var noticesOnly = new JsonArray(expectedAttention.AsArray().Where(e => !e!["group"]!.GetValue<string>().StartsWith("provider/", StringComparison.Ordinal)).Select(e => e!.DeepClone()).ToArray());
                Subset(noticesOnly, JsonSerializer.SerializeToNode(events.Select(e => new { group = e.Group, reason = e.Reason, level = e.Level }))!, where + " attention");
            }
            if (expect?["originalRecords"] is { } originals)
            {
                var actual = new JsonObject(versions.GroupBy(v => v.Message.Identity).Select(g => KeyValuePair.Create<string, JsonNode?>(g.Key, JsonSerializer.SerializeToNode(g.OrderBy(v => v.IngestSequence).Select(v => v.Message.OriginalRecordJson)))));
                Subset(originals, actual, where + " originals");
            }
            if (expect?["notices"] is { } expectedNotices)
            {
                var groups = SwpcGroupBuilder.Build(versions);
                CollectionAssert.AreEquivalent(expectedNotices.AsObject().Select(p => p.Key).ToArray(), groups.Select(g => g.Identity).ToArray(), where);
                foreach (var group in groups)
                {
                    var window = SwpcNoticeLifecycle.Evaluate(group, groups, now);
                    var relevance = new JsonObject();
                    foreach (var site in scenario["sites"]!.AsArray().Select(s => s!))
                    {
                        var location = site["location"];
                        var phi = location is null ? (double?)null : GeomagneticLatitude.Compute(location["latitudeDeg"]!.GetValue<double>(), location["longitudeDeg"]!.GetValue<double>());
                        relevance[site["siteId"]!.GetValue<string>()] = SpaceWeatherRelevance.Categorize(group.Family, group.Latest.Code, group.Level, group.LatestNonCancel?.Header.StatedLatitude, phi).Category.Token();
                    }
                    var ack = SwpcGroupBuilder.Acknowledgement(group, acks);
                    var actual = JsonSerializer.SerializeToNode(new
                    {
                        family = group.Family.Token(),
                        level = group.Level,
                        state = window.State.Token(),
                        fromUtc = Utc(window.FromUtc),
                        untilUtc = Utc(window.UntilUtc),
                        endReason = window.EndReason?.Token(),
                        latest = group.Latest.Identity,
                        versions = group.Versions.Length,
                        corrected = group.Versions.Any(v => v.Message.Header.Corrected),
                        orphan = group.Orphan,
                        acknowledged = ack is null ? null : new { @operator = ack.Operator, atUtc = Utc(ack.AtUtc) },
                        days = group.LatestNonCancel?.Header.Days?.Select(d => new { dateUtc = d.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), level = d.Level })
                    })!;
                    actual["relevance"] = relevance;
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(scenario["sites"]![0]!["timeZoneId"]!.GetValue<string>());
                    actual["local"] = JsonSerializer.SerializeToNode(new { fromLocal = TimeZoneInfo.ConvertTime(window.FromUtc, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), untilLocal = TimeZoneInfo.ConvertTime(window.UntilUtc, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) });
                    // S2 owns mayBeOutOfDate; all remaining notice fields are S1 facts.
                    var expected = expectedNotices[group.Identity]!.DeepClone();
                    expected.AsObject().Remove("mayBeOutOfDate");
                    Subset(expected, actual, where + " " + group.Identity);
                }
            }
            if (expect?["products"] is { } expectedProducts)
            {
                foreach (var (product, expectedProduct) in expectedProducts.AsObject())
                {
                    if (!products.TryGetValue(product, out var stored)) continue;
                    if (expectedProduct?["facts"] is { } facts) Subset(facts, stored.Facts, where + " stored facts");
                    if (product == "kp-forecast" && expectedProduct?["kpBinRelations"] is { } relations)
                    {
                        foreach (var (start, relation) in relations.AsObject())
                        {
                            var bin = new KpBin(ParseTime(JsonValue.Create(start)!), "", "", null);
                            Assert.AreEqual(relation!.GetValue<string>(), bin.RelationAt(now), where);
                        }
                    }
                }
            }
            if (expect?["product"]?["kpBinRelations"] is { } kpRelations)
                foreach (var (start, relation) in kpRelations.AsObject()) Assert.AreEqual(relation!.GetValue<string>(), new KpBin(ParseTime(JsonValue.Create(start)!), "", "", null).RelationAt(now), where);
        }
    }

    internal static byte[] Bytes(JsonNode response)
    {
        if (response["body"] is { } body) return Encoding.UTF8.GetBytes(body.GetValue<string>());
        if (response["bodyBase64"] is { } base64) return Convert.FromBase64String(base64.GetValue<string>());
        if (response["bodyFile"] is { } file) return File.ReadAllBytes(Path.Combine(Pack, file.GetValue<string>()));
        var synthesis = response["bodySynthesis"]!;
        if (synthesis["kind"]!.GetValue<string>() == "padded-empty-array") return Encoding.ASCII.GetBytes("[" + new string(' ', synthesis["totalBytes"]!.GetValue<int>() - 2) + "]");
        var record = synthesis["record"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = false }).Replace("/", "\\/", StringComparison.Ordinal);
        return Encoding.ASCII.GetBytes("[" + string.Join(',', Enumerable.Repeat(record, synthesis["count"]!.GetValue<int>())) + "]");
    }

    private static JsonNode Forecast(IssuedForecast f) => JsonSerializer.SerializeToNode(new
    {
        issuedUtc = Utc(f.IssuedUtc),
        kpBreakdownBins = f.KpBreakdown.Count,
        kpBreakdown = f.KpBreakdown.ToDictionary(p => Utc(p.Key), p => new { kp = p.Value.KpText, noaaScale = p.Value.NoaaScale }),
        radiationProbabilities = f.RadiationProbabilities.ToDictionary(p => p.Key, p => p.Value.ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Value)),
        radioBlackoutProbabilities = f.RadioBlackoutProbabilities.ToDictionary(p => p.Key, p => p.Value.ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Value))
    })!;

    internal static void Subset(JsonNode? expected, JsonNode? actual, string where)
    {
        if (expected is JsonObject obj)
        {
            Assert.IsInstanceOfType<JsonObject>(actual, where);
            foreach (var (key, value) in obj) { Assert.IsTrue(actual.AsObject().ContainsKey(key), where + "." + key); Subset(value, actual[key], where + "." + key); }
        }
        else if (expected is JsonArray array)
        {
            Assert.IsInstanceOfType<JsonArray>(actual, where);
            Assert.AreEqual(array.Count, actual.AsArray().Count, where);
            for (var i = 0; i < array.Count; i++) Subset(array[i], actual[i], where + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        }
        else Assert.IsTrue(JsonNode.DeepEquals(expected, actual), where + ": expected " + expected + ", actual " + actual);
    }

    private static DateTimeOffset ParseTime(JsonNode node) => DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
