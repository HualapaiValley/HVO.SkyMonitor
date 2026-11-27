using System.Globalization;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class MeteorShowerGoldenFixtureTests
{
    private static void ValidateSnapshotShape(JsonElement snapshot)
    {
        Fields(snapshot, "schemaVersion", "snapshotId", "snapshotSha256", "generatedUtc", "identitySource",
            "activitySources", "showers", "removedShowers");
        Assert.AreEqual(SnapshotVersion, String(snapshot, "schemaVersion"));
        Text(snapshot, "snapshotId", 64);
        Utc(snapshot, "generatedUtc");

        var identity = snapshot.GetProperty("identitySource");
        Fields(identity, "providerName", "sourceUrl", "mdcExportDate", "mdcSourceReference", "mdcRawSha256",
            "redistributionApprovalRef", "citationText");
        Assert.AreEqual("HVO Synthetic Identity", String(identity, "providerName"));
        HvoSourceUrl(identity, "sourceUrl");
        DateOnly.ParseExact(String(identity, "mdcExportDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Text(identity, "mdcSourceReference", int.MaxValue);
        Text(identity, "citationText", int.MaxValue);
        Assert.IsTrue(IsNull(identity, "mdcRawSha256"));
        Assert.IsTrue(IsNull(identity, "redistributionApprovalRef"));

        foreach (var source in Sources(snapshot))
        {
            Fields(source, "providerName", "editionId", "editionYear", "sourceUrl", "attributionText", "termsUrl",
                "redistributionApprovalRef", "validFromUtc", "validUntilUtc");
            Assert.AreEqual("HVO Synthetic Activity", String(source, "providerName"));
            Text(source, "editionId", 64);
            Int(source, "editionYear");
            HvoSourceUrl(source, "sourceUrl");
            HvoSourceUrl(source, "termsUrl");
            Text(source, "attributionText", int.MaxValue);
            Assert.IsTrue(IsNull(source, "redistributionApprovalRef"));
            Assert.IsTrue(Utc(source, "validFromUtc") < Utc(source, "validUntilUtc"));
        }

        var showers = Array(snapshot, "showers");
        var tombstones = Array(snapshot, "removedShowers");
        Assert.IsTrue(showers.Length <= 1500 && tombstones.Length <= 1500);
        AssertSortedUniqueNumbers(showers);
        AssertSortedUniqueNumbers(tombstones);
        var all = showers.Concat(tombstones).ToArray();
        Assert.HasCount(all.Length, all.Select(item => Int(item, "iauNumber")).Distinct().ToArray(), "Duplicate IAU number.");
        Assert.HasCount(showers.Length, showers.Select(item => String(item, "iauCode")).Distinct(StringComparer.Ordinal).ToArray(),
            "Duplicate active IAU code.");
        foreach (var item in all)
        {
            Assert.IsGreaterThan(0, Int(item, "iauNumber"));
            var code = Text(item, "iauCode", 3);
            Assert.IsTrue(code.Length == 3 && code.All(letter => letter is >= 'A' and <= 'Z'), code);
        }

        foreach (var shower in showers)
        {
            ValidateShowerShape(shower);
        }
        foreach (var tombstone in tombstones)
        {
            Fields(tombstone, "iauNumber", "iauCode", "formerName", "removedNote", "supersededByIauNumber");
            Text(tombstone, "formerName", 96);
            Text(tombstone, "removedNote", int.MaxValue);
            if (!IsNull(tombstone, "supersededByIauNumber"))
            {
                Assert.IsGreaterThan(0, Int(tombstone, "supersededByIauNumber"));
            }
        }
    }

    private static void ValidateShowerShape(JsonElement shower)
    {
        Fields(shower, "iauNumber", "iauCode", "name", "status", "showerGroupId", "mdcDatasetRef", "referenceRadiant",
            "radiantDrift", "geocentricVelocityKmPerSec", "parentBodyName", "nominalActivity", "annualForecasts");
        Text(shower, "name", 96);
        OneOf(shower, "status", "Established", "Working");
        String(shower, "showerGroupId");
        String(shower, "mdcDatasetRef");
        String(shower, "parentBodyName");
        if (!IsNull(shower, "geocentricVelocityKmPerSec"))
        {
            Assert.IsGreaterThan(0d, Number(shower, "geocentricVelocityKmPerSec"));
        }

        var radiant = shower.GetProperty("referenceRadiant");
        Fields(radiant, "raDeg", "decDeg", "referenceSolarLongitudeDeg", "equinox");
        Assert.AreEqual("J2000", String(radiant, "equinox"));
        Longitude(radiant, "raDeg");
        Longitude(radiant, "referenceSolarLongitudeDeg");
        Assert.IsTrue(Number(radiant, "decDeg") is >= -90 and <= 90);
        if (!IsNull(shower, "radiantDrift"))
        {
            var drift = shower.GetProperty("radiantDrift");
            Fields(drift, "dRaDegPerSolarLonDeg", "dDecDegPerSolarLonDeg");
            Assert.IsTrue(double.IsFinite(Number(drift, "dRaDegPerSolarLonDeg")));
            Assert.IsTrue(double.IsFinite(Number(drift, "dDecDegPerSolarLonDeg")));
        }

        if (!IsNull(shower, "nominalActivity"))
        {
            var nominal = shower.GetProperty("nominalActivity");
            Fields(nominal, "source", "peakSolarLonDeg", "activeStartSolarLonDeg", "activeEndSolarLonDeg");
            OneOf(nominal, "source", "IAU-MDC", "AnnualActivitySource", "OperatorCurated");
            Assert.AreEqual(IsNull(nominal, "activeStartSolarLonDeg"), IsNull(nominal, "activeEndSolarLonDeg"));
            foreach (var key in new[] { "peakSolarLonDeg", "activeStartSolarLonDeg", "activeEndSolarLonDeg" })
            {
                if (!IsNull(nominal, key))
                {
                    Longitude(nominal, key);
                }
            }
        }

        var forecasts = Array(shower, "annualForecasts");
        Assert.IsTrue(forecasts.Length <= 4);
        foreach (var forecast in forecasts)
        {
            ValidateForecastShape(forecast);
        }

        var keys = forecasts.Select(forecast => (Year: Int(forecast, "occurrenceYear"), Start: Utc(forecast, "windowStartUtc"),
            EditionYear: Int(forecast, "editionYear"), EditionId: String(forecast, "editionId"))).ToArray();
        CollectionAssert.AreEqual(keys.OrderBy(key => key.Year).ThenBy(key => key.Start).ThenBy(key => key.EditionYear)
            .ThenBy(key => key.EditionId, StringComparer.Ordinal).ToArray(), keys);
        Assert.HasCount(keys.Length, keys.Select(key => (key.EditionId, key.Year)).Distinct().ToArray());
        Assert.IsTrue(keys.GroupBy(key => key.EditionId, StringComparer.Ordinal).All(group => group.Count() <= 2));
    }

    private static void ValidateForecastShape(JsonElement forecast)
    {
        Fields(forecast, "editionId", "editionYear", "occurrenceYear", "windowStartUtc", "windowEndUtc", "validFromUtc",
            "validUntilUtc", "peakUtc", "peakTimeSource", "peakWindowStartUtc", "peakWindowEndUtc", "peakUncertaintyHours",
            "peakUncertaintySolarLongitudeDeg", "peakProfile", "activityConfidence", "idealizedZhrNominal", "idealizedZhrMin",
            "idealizedZhrMax", "idealizedZhrQualifier", "populationIndexR", "notes");
        Text(forecast, "editionId", 64);
        Int(forecast, "editionYear");
        Int(forecast, "occurrenceYear");
        Assert.IsTrue(Utc(forecast, "windowStartUtc") < Utc(forecast, "windowEndUtc"));
        Assert.IsTrue(Utc(forecast, "validFromUtc") < Utc(forecast, "validUntilUtc"));

        foreach (var key in new[] { "peakUtc", "peakWindowStartUtc", "peakWindowEndUtc" })
        {
            if (!IsNull(forecast, key))
            {
                Utc(forecast, key);
            }
        }
        foreach (var key in new[] { "peakUncertaintyHours", "peakUncertaintySolarLongitudeDeg" })
        {
            if (!IsNull(forecast, key))
            {
                Assert.IsGreaterThanOrEqualTo(0d, Number(forecast, key), key);
            }
        }
        foreach (var key in new[] { "idealizedZhrNominal", "idealizedZhrMin", "idealizedZhrMax" })
        {
            if (!IsNull(forecast, key))
            {
                Assert.IsGreaterThanOrEqualTo(0, Int(forecast, key), key);
            }
        }
        OneOf(forecast, "peakTimeSource", "SourceCalendarUtc", "DerivedFromSolarLongitudeUtc");
        OneOf(forecast, "peakProfile", "Sharp", "Broad", "Variable", "OutburstSuspected", "Unknown");
        OneOf(forecast, "activityConfidence", "NominalAnnual", "VariableOrUncertain", "ModelPrediction", "Unknown");
        Text(forecast, "idealizedZhrQualifier", int.MaxValue);
        if (!IsNull(forecast, "populationIndexR"))
        {
            Assert.IsTrue(double.IsFinite(Number(forecast, "populationIndexR")));
        }
        if (String(forecast, "notes") is { } notes)
        {
            Assert.IsTrue(notes.Length <= 280 && !notes.Contains('<', StringComparison.Ordinal));
        }
    }

    private static void AssertSortedUniqueNumbers(JsonElement[] records)
    {
        var numbers = records.Select(record => Int(record, "iauNumber")).ToArray();
        CollectionAssert.AreEqual(numbers.Distinct().Order().ToArray(), numbers);
    }

    private static void HvoSourceUrl(JsonElement source, string key)
    {
        var uri = new Uri(String(source, key), UriKind.Absolute);
        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("github.com", uri.Host);
        Assert.StartsWith("/HualapaiValley/HVO.SkyMonitor/", uri.AbsolutePath);
    }

    private static void Longitude(JsonElement obj, string key) =>
        Assert.IsTrue(Number(obj, key) is >= 0 and < 360, key);

    private static void OneOf(JsonElement obj, string key, params string[] values) =>
        Assert.Contains(String(obj, key), values, key);

    private static JsonElement[] Sources(JsonElement snapshot) =>
        IsNull(snapshot, "activitySources") ? [] : Array(snapshot, "activitySources");

    private static string? ImportRejection(JsonElement snapshot)
    {
        var sources = Sources(snapshot);
        if (!IsNull(snapshot, "activitySources") && sources.Length is < 1 or > 2)
        {
            return sources.Length == 0 ? "EmptyActivitySources" : "TooManyActivitySources";
        }

        var keys = sources.Select(source => (Year: Int(source, "editionYear"), Id: String(source, "editionId"))).ToArray();
        if (keys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            return "DuplicateEditionId";
        }

        CollectionAssert.AreEqual(keys.OrderBy(key => key.Year).ThenBy(key => key.Id, StringComparer.Ordinal).ToArray(), keys);
        foreach (var forecast in Array(snapshot, "showers").SelectMany(shower => Array(shower, "annualForecasts")))
        {
            var matchProblem = MatchEdition(sources, forecast, out var source);
            if (matchProblem is not null)
            {
                return matchProblem;
            }

            if (Utc(forecast, "validFromUtc") < Utc(source, "validFromUtc") ||
                Utc(forecast, "validUntilUtc") > Utc(source, "validUntilUtc"))
            {
                return "ForecastValidityOutsideSource";
            }
        }

        return null;
    }

    private static string? MatchEdition(JsonElement[] sources, JsonElement forecast, out JsonElement source)
    {
        var id = String(forecast, "editionId");
        var matches = sources.Where(candidate => String(candidate, "editionId") == id).ToArray();
        source = matches.FirstOrDefault();
        return matches.Length switch
        {
            0 => "UnmatchedEdition",
            > 1 => "AmbiguousEdition",
            _ when Int(source, "editionYear") != Int(forecast, "editionYear") => "MismatchedEditionYear",
            _ => null,
        };
    }
}
