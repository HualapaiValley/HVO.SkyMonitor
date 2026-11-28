using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class MeteorShowerGoldenFixtureTests
{
    [TestMethod]
    public void FrozenOccurrencesMatchEditionWindowAndCardModeRules()
    {
        var fixture = Read(Occurrences);
        Fields(fixture, "schemaVersion", "cases", "rejected", "statuses");
        var cases = Array(fixture, "cases");
        Assert.HasCount(18, cases);
        var optionalFields = new[] { "expectedForecast", "expectedFallback" };
        foreach (var vector in cases)
        {
            var extraFields = optionalFields.Where(key => vector.TryGetProperty(key, out _));
            Fields(vector, ["id", "snapshot", "iauCode", "queryUtc", "lookaheadDays", "expectedDecisions", "expectedMode", .. extraFields]);
            var id = String(vector, "id");
            var snapshot = Read(String(vector, "snapshot"));
            var shower = Shower(snapshot, String(vector, "iauCode"));
            var query = Utc(vector, "queryUtc");
            Assert.AreEqual(30, Int(vector, "lookaheadDays"), id);
            var forecasts = Array(shower, "annualForecasts");
            var decisions = EvaluateOccurrences(Sources(snapshot), forecasts, query, Int(vector, "lookaheadDays"));
            var expected = Array(vector, "expectedDecisions");
            Assert.HasCount(forecasts.Length, expected, id);
            for (var index = 0; index < expected.Length; index++)
            {
                Fields(expected[index], "editionId", "occurrenceYear", "decision");
                Assert.AreEqual(String(expected[index], "editionId"), String(forecasts[index], "editionId"), id);
                Assert.AreEqual(Int(expected[index], "occurrenceYear"), Int(forecasts[index], "occurrenceYear"), id);
                Assert.AreEqual(String(expected[index], "decision"), decisions[index], id);
            }

            var selected = forecasts.Where((_, index) => IsListed(decisions[index])).ToArray();
            var mode = selected.Length > 0 ? "AnnualActivity" : InactiveMode(snapshot, query);
            Assert.AreEqual(String(vector, "expectedMode"), mode, id);
            if (vector.TryGetProperty("expectedForecast", out var expectedForecast))
            {
                AssertSelectedForecast(expectedForecast, selected.Single(), id);
            }
            if (id == "inclusive-validity-and-null-nominal")
            {
                Assert.IsTrue(IsNull(shower, "nominalActivity"), id);
            }
            if (vector.TryGetProperty("expectedFallback", out var fallback))
            {
                Assert.IsEmpty(selected, id);
                AssertFallback(shower, fallback, query, id);
            }
        }

        var statuses = fixture.GetProperty("statuses");
        Fields(statuses, "snapshot", "queryUtc", "lookaheadDays", "defaultCodes", "optInCodes", "removedCode", "removedName",
            "removedNote", "supersededByCode");
        var statusSnapshot = Read(String(statuses, "snapshot"));
        var activeOrUpcoming = Array(statusSnapshot, "showers").Where(shower =>
            EvaluateOccurrences(Sources(statusSnapshot), Array(shower, "annualForecasts"), Utc(statuses, "queryUtc"),
                Int(statuses, "lookaheadDays")).Any(IsListed)).ToArray();
        CollectionAssert.AreEqual(Array(statuses, "defaultCodes").Select(code => code.GetString()).ToArray(),
            activeOrUpcoming.Where(shower => String(shower, "status") == "Established").Select(shower => String(shower, "iauCode")).ToArray());
        CollectionAssert.AreEqual(Array(statuses, "optInCodes").Select(code => code.GetString()).ToArray(),
            activeOrUpcoming.Select(shower => String(shower, "iauCode")).ToArray());
        var removedCode = String(statuses, "removedCode");
        Assert.IsFalse(Array(statusSnapshot, "showers").Any(shower => String(shower, "iauCode") == removedCode));
        var tombstone = Array(statusSnapshot, "removedShowers").Single(item => String(item, "iauCode") == removedCode);
        Assert.AreEqual(String(statuses, "removedName"), String(tombstone, "formerName"));
        Assert.AreEqual(String(statuses, "removedNote"), String(tombstone, "removedNote"));
        Assert.AreEqual(Int(Shower(statusSnapshot, String(statuses, "supersededByCode")), "iauNumber"),
            Int(tombstone, "supersededByIauNumber"));
    }

    [TestMethod]
    public void RejectedSnapshotsFailBothImportAndDefensiveStageOne()
    {
        var vectors = Array(Read(Occurrences), "rejected");
        var manifestNames = Read(Manifest).GetProperty("files").EnumerateObject()
            .Where(entry => String(entry.Value, "kind") == "rejected-snapshot").Select(entry => entry.Name).ToArray();
        CollectionAssert.AreEquivalent(manifestNames, vectors.Select(vector => String(vector, "snapshot")).ToArray());
        Assert.HasCount(5, vectors);

        foreach (var vector in vectors)
        {
            Fields(vector, "snapshot", "iauCode", "queryUtc", "importReason", "stage1Reason");
            var file = String(vector, "snapshot");
            var snapshot = Read(file);
            Assert.AreEqual(String(vector, "importReason"), ImportRejection(snapshot), file);
            var forecast = Array(Shower(snapshot, String(vector, "iauCode")), "annualForecasts").Single();
            var query = Utc(vector, "queryUtc");
            Assert.AreEqual(String(vector, "stage1Reason"), StageOneRejection(Sources(snapshot), forecast, query), file);
            // The widened forecast is eligible if and only if the source's later start is ignored.
            Assert.IsTrue(Utc(forecast, "validFromUtc") <= query && query < Utc(forecast, "validUntilUtc"), file);
        }
    }

    private static string? StageOneRejection(JsonElement[] sources, JsonElement forecast, DateTimeOffset query)
    {
        var problem = MatchEdition(sources, forecast, out var source);
        if (problem is not null)
        {
            return problem;
        }

        var from = new[] { Utc(source, "validFromUtc"), Utc(forecast, "validFromUtc") }.Max();
        var until = new[] { Utc(source, "validUntilUtc"), Utc(forecast, "validUntilUtc") }.Min();
        return from >= until ? "EmptyEffectiveValidity" : query < from ? "NotYetValid" : query >= until ? "Expired" : null;
    }

    // Contract oracle for the corpus. Slices M1/M2 will exercise these same inputs through runtime APIs.
    private static string[] EvaluateOccurrences(JsonElement[] sources, JsonElement[] forecasts, DateTimeOffset query, int lookaheadDays)
    {
        var decisions = forecasts.Select(forecast => StageOneRejection(sources, forecast, query) ??
            (Utc(forecast, "windowEndUtc") <= query ? "Past" : "Eligible")).ToArray();
        var occurrenceGroups = Enumerable.Range(0, forecasts.Length)
            .Where(index => decisions[index] == "Eligible")
            .GroupBy(index => Int(forecasts[index], "occurrenceYear"));
        foreach (var group in occurrenceGroups)
        {
            var winner = group.OrderByDescending(index => Int(forecasts[index], "editionYear"))
                .ThenByDescending(index => String(forecasts[index], "editionId"), StringComparer.Ordinal).First();
            foreach (var index in group)
            {
                decisions[index] = "Superseded";
            }

            var start = Utc(forecasts[winner], "windowStartUtc");
            decisions[winner] = start <= query ? "Active" : start < query.AddDays(lookaheadDays) ? "Upcoming" : "OutsideWindow";
        }
        return decisions;
    }

    private static bool IsListed(string decision) => decision is "Active" or "Upcoming";

    private static string InactiveMode(JsonElement snapshot, DateTimeOffset query)
    {
        if (IsNull(snapshot, "activitySources"))
        {
            return "IdentityRadiantOnly";
        }

        return Sources(snapshot).All(source => query >= Utc(source, "validUntilUtc"))
            ? "AnnualForecastExpired" : "NoForecastForOccurrence";
    }

    private static void AssertSelectedForecast(JsonElement expected, JsonElement actual, string id)
    {
        Fields(expected, "windowStartUtc", "windowEndUtc", "peakUtc", "peakTimeSource", "peakWindowStartUtc", "peakWindowEndUtc",
            "peakUncertaintyHours", "peakUncertaintySolarLongitudeDeg", "peakProfile", "activityConfidence", "idealizedZhrNominal",
            "idealizedZhrMin", "idealizedZhrMax", "idealizedZhrQualifier");
        foreach (var property in expected.EnumerateObject())
        {
            Assert.AreEqual(property.Value.GetRawText(), actual.GetProperty(property.Name).GetRawText(), $"{id}: {property.Name}");
        }
    }

    private static void AssertFallback(JsonElement shower, JsonElement expected, DateTimeOffset query, string id)
    {
        Fields(expected, "activeWindow", "derivedPeakUtc", "idealizedZhr", "peakTimeSource");
        Assert.IsEmpty(Array(shower, "annualForecasts"), id);
        var nominal = shower.GetProperty("nominalActivity");
        Assert.IsTrue(IsNull(expected, "activeWindow"), id);
        Assert.IsNull(SolarActivity(shower, SolarLongitude(query)), id);
        Assert.IsTrue(IsNull(expected, "idealizedZhr"), id);
        if (nominal.ValueKind == JsonValueKind.Null || IsNull(nominal, "peakSolarLonDeg"))
        {
            Assert.IsTrue(IsNull(expected, "derivedPeakUtc"), id);
            Assert.IsTrue(IsNull(expected, "peakTimeSource"), id);
            Longitude(shower.GetProperty("referenceRadiant"), "referenceSolarLongitudeDeg");
            return;
        }

        Assert.IsTrue(IsNull(nominal, "activeStartSolarLonDeg") && IsNull(nominal, "activeEndSolarLonDeg"), id);
        Assert.AreEqual("DerivedFromSolarLongitudeUtc", String(expected, "peakTimeSource"), id);
        var peak = Utc(expected, "derivedPeakUtc");
        var target = Number(nominal, "peakSolarLonDeg");
        Assert.AreEqual(query.Year, peak.Year, id);
        Assert.IsLessThan(0d, SignedDegrees(SolarLongitude(peak.AddSeconds(-1)) - target), id);
        Assert.IsGreaterThanOrEqualTo(0d, SignedDegrees(SolarLongitude(peak) - target), id);
        Assert.AreEqual(0d, SignedDegrees(SolarLongitude(peak) - target), 0.000012d, id);
    }
}
