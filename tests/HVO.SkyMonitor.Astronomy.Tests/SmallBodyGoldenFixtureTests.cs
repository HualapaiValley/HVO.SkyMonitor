using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed partial class SmallBodyGoldenFixtureTests
{
    private const string AsteroidSpkId = "20999999";
    private const string CometSpkId = "999990001";
    private static readonly string FixtureRoot = FindFixtureRoot();
    private static readonly string[] ProducingVersions =
        ["parserVersion", "normalizerVersion", "horizonsApiVersion", "sbdbApiVersion", "lookupApiVersion"];

    [TestMethod]
    public void InventoryCoversEveryFileAndAllJsonIsCanonical()
    {
        const string indexName = "fixture-manifest.json";
        using var index = Open(indexName);
        var manifest = index.RootElement;
        Assert.AreEqual("hvo-small-body-golden-fixtures-v1", Str(manifest, "schemaVersion"));
        Assert.AreEqual("RFC 8785 JCS UTF-8", Str(manifest, "encoding"));
        Assert.AreEqual("HVO-authored synthetic", Str(manifest, "dataClassification"));
        Assert.AreEqual("770fb1f2017e368dd2221f859768fb8126fea4dc", Str(manifest, "contractCommit"));
        AssertDigest(manifest, "manifestSha256");

        var files = Items(manifest, "files");
        var listedNames = files.Select(file => Str(file, "path")!).ToArray();
        var actualNames = Directory.GetFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(FixtureRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(actualNames.Where(name => name != indexName).ToArray(), listedNames);

        foreach (var descriptor in files)
        {
            var name = Str(descriptor, "path")!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(Str(descriptor, "provenance")), name);
            var bytes = Read(name);
            Assert.AreEqual(Int(descriptor, "bytes"), bytes.Length, name);
            Assert.AreEqual(Str(descriptor, "sha256"), Sha256(bytes), name);
        }
        foreach (var name in actualNames.Where(name => name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var bytes = Read(name);
            CollectionAssert.AreEqual(bytes, SmallBodyFixtureCanonicalJson.Encode(bytes), name);
        }
    }

    [TestMethod]
    public void CanonicalEncodingMatchesFrozenUtf8AndRejectsInvalidInput()
    {
        using var document = Open("canonical/jcs-vectors.json");
        foreach (var test in Items(document.RootElement, "accepted"))
        {
            var name = Str(test, "id")!;
            var canonical = SmallBodyFixtureCanonicalJson.Encode(SmallBodyFixtureCanonicalJson.Utf8.GetBytes(Str(test, "input")!));
            CollectionAssert.AreEqual(Convert.FromHexString(Str(test, "utf8Hex")!), canonical, name);
            Assert.AreEqual(Str(test, "canonical"), SmallBodyFixtureCanonicalJson.Utf8.GetString(canonical), name);
            Assert.AreEqual(Int(test, "bytes"), canonical.Length, name);
            Assert.AreEqual(Str(test, "sha256"), Sha256(canonical), name);
        }
        foreach (var test in Items(document.RootElement, "rejected"))
        {
            var bytes = test.TryGetProperty("utf8Hex", out var hex) ? Convert.FromHexString(hex.GetString()!) :
                SmallBodyFixtureCanonicalJson.Utf8.GetBytes(Str(test, "input")!);
            try
            {
                _ = SmallBodyFixtureCanonicalJson.Encode(bytes);
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or System.Text.DecoderFallbackException)
            {
                continue;
            }
            Assert.Fail($"Expected invalid JCS input to be rejected: {Str(test, "id")}");
        }
    }

    [TestMethod]
    public void SnapshotsBindExactPayloadSourcesObserverAndProducingVersions()
    {
        using var manifest1 = Open("snapshots/manifest-v1.json");
        using var manifest2 = Open("snapshots/manifest-v2.json");
        using var payload1 = Open("snapshots/payload-v1.json");
        using var payload2 = Open("snapshots/payload-v2.json");
        var m1 = manifest1.RootElement;
        var m2 = manifest2.RootElement;
        ValidateSnapshot(m1, payload1.RootElement, "snapshots/payload-v1.json",
            ["providers/asteroid.horizons.json", "providers/comet.horizons.json"]);
        ValidateSnapshot(m2, payload2.RootElement, "snapshots/payload-v2.json",
            ["providers/asteroid.horizons-refreshed.json"]);
        Assert.AreEqual(Str(m1.GetProperty("observerBinding"), "observerBindingSha256"),
            Str(m2.GetProperty("observerBinding"), "observerBindingSha256"));
        Assert.AreEqual(0, Int(m1, "carriedForwardCount"));
        Assert.AreEqual(1, Int(m2, "carriedForwardCount"));

        var priorComet = Series(payload1.RootElement, CometSpkId);
        var carriedComet = Series(payload2.RootElement, CometSpkId);
        Assert.IsTrue(carriedComet.GetProperty("carriedForward").GetBoolean());
        Assert.AreEqual(Str(m1, "manifestSha256"), Str(carriedComet, "originManifestSha256"));
        CollectionAssert.AreEqual(SmallBodyFixtureCanonicalJson.Encode(priorComet, "carriedForward", "originManifestSha256"),
            SmallBodyFixtureCanonicalJson.Encode(carriedComet, "carriedForward", "originManifestSha256"));
        var failure = Items(m2, "failedRefreshObjects").Single();
        Assert.AreEqual(CometSpkId, Str(failure, "spkId"));
        Assert.AreEqual("SolutionMetadataMismatch", Str(failure, "reasonCode"));
        Assert.IsTrue(failure.GetProperty("carryForwardApplied").GetBoolean());

        var priorAsteroid = Series(payload1.RootElement, AsteroidSpkId);
        var refreshedAsteroid = Series(payload2.RootElement, AsteroidSpkId);
        Assert.AreEqual(24d, (Instant(m2, "retrievedUtc") - Instant(m1, "retrievedUtc")).TotalHours);
        Assert.IsTrue(Instant(m2, "retrievedUtc") < Instant(carriedComet, "validUntilUtc"));
        Assert.AreNotEqual(Str(priorAsteroid, "recordSha256"), Str(refreshedAsteroid, "recordSha256"));
        foreach (var version in ProducingVersions.Take(2))
        {
            Assert.AreNotEqual(Str(refreshedAsteroid, version), Str(carriedComet, version), version);
        }
    }

    private static void ValidateSnapshot(JsonElement manifest, JsonElement payload, string payloadPath, string[] rawPaths)
    {
        AssertKeys(manifest, "schemaVersion manifestSha256 normalizedPayloadSha256 normalizedPayloadBytes provider " +
            "horizonsApiVersion sbdbApiVersion lookupApiVersion parserVersion normalizerVersion termsReference attributionText " +
            "observerBinding retrievedUtc watchlistCount refreshedCount carriedForwardCount failedRefreshObjects " +
            "overallValidFromUtc overallValidUntilUtc rawSources");
        AssertKeys(payload, "observerBinding schemaVersion series");
        Assert.AreEqual("hvo-small-body-manifest-v1", Str(manifest, "schemaVersion"));
        Assert.AreEqual("hvo-small-body-ephemeris-v1", Str(payload, "schemaVersion"));
        Assert.AreEqual("nasa-jpl-ssd", Str(manifest, "provider"));
        Assert.AreEqual("https://ssd-api.jpl.nasa.gov/doc/", Str(manifest, "termsReference"));
        Assert.AreEqual("Small-body ephemerides and orbit metadata: NASA/JPL-Caltech Solar System Dynamics " +
            "(Horizons / Small-Body Database). Displayed by HVO SkyMonitor; not endorsed by NASA, JPL, or Caltech.",
            Str(manifest, "attributionText"));

        AssertDigest(manifest, "manifestSha256");
        var payloadBytes = Read(payloadPath);
        Assert.AreEqual(Str(manifest, "normalizedPayloadSha256"), Sha256(payloadBytes));
        Assert.AreEqual(Int(manifest, "normalizedPayloadBytes"), payloadBytes.Length);
        Assert.IsTrue(payloadBytes.Length <= 16 * 1024 * 1024);

        var observer = payload.GetProperty("observerBinding");
        AssertKeys(observer, "deploymentLocationId deploymentLocationVersion elevationMeters heightDatum " +
            "latitudeDegrees longitudeDegrees observerBindingSha256");
        AssertDigest(observer, "observerBindingSha256");
        JsonEqual(observer, manifest.GetProperty("observerBinding"));

        var series = Items(payload, "series");
        Assert.AreEqual(Int(manifest, "watchlistCount"), series.Length);
        Assert.IsTrue(series.Length is > 0 and <= 32);
        Assert.AreEqual(series.Length, series.Select(s => Str(s.GetProperty("identity"), "spkId")).Distinct().Count());
        Assert.AreEqual(Instant(manifest, "overallValidFromUtc"), series.Min(s => Instant(s, "validFromUtc")));
        Assert.AreEqual(Instant(manifest, "overallValidUntilUtc"), series.Max(s => Instant(s, "validUntilUtc")));

        var refreshed = series.Where(s => !s.GetProperty("carriedForward").GetBoolean()).ToArray();
        Assert.AreEqual(Int(manifest, "refreshedCount"), refreshed.Length);
        Assert.AreEqual(Int(manifest, "carriedForwardCount"), series.Length - refreshed.Length);
        var rawSources = Items(manifest, "rawSources");
        Assert.AreEqual(refreshed.Length, rawSources.Length);
        Assert.AreEqual(rawPaths.Length, rawSources.Length);
        Assert.IsTrue(refreshed.Length > 0);
        for (var i = 0; i < rawSources.Length; i++)
        {
            var source = rawSources[i];
            AssertKeys(source, "spkId querySummary rawSha256 rawBytes httpStatus");
            Assert.AreEqual(Str(refreshed[i].GetProperty("identity"), "spkId"), Str(source, "spkId"));
            var bytes = Read(rawPaths[i]);
            Assert.AreEqual(Str(source, "rawSha256"), Sha256(bytes));
            Assert.AreEqual(Int(source, "rawBytes"), bytes.Length);
            Assert.AreEqual(200, Int(source, "httpStatus"));
            Assert.IsTrue(bytes.Length <= 2 * 1024 * 1024);
        }
        foreach (var failure in Items(manifest, "failedRefreshObjects"))
            AssertKeys(failure, "spkId reasonCode carryForwardApplied");

        foreach (var item in series)
            ValidateSeries(item);
        foreach (var item in refreshed)
        {
            Assert.IsNull(Str(item, "originManifestSha256"));
            Assert.AreEqual(Instant(manifest, "retrievedUtc"), Instant(item, "seriesRetrievedUtc"));
            foreach (var version in ProducingVersions)
                Assert.AreEqual(Str(manifest, version), Str(item, version), version);
        }
    }

    private static void ValidateSeries(JsonElement series)
    {
        AssertKeys(series, "identity solution recordSha256 seriesRetrievedUtc carriedForward originManifestSha256 " +
            "parserVersion normalizerVersion horizonsApiVersion sbdbApiVersion lookupApiVersion ephemerisProfile center refractionApplied " +
            "stepSeconds validFromUtc validUntilUtc paddedStartUtc paddedEndUtc minimumRangeAu interpolationVerificationMethod " +
            "maxVerifiedMidpointResidualArcsec interpolationBoundVerified knots");
        AssertDigest(series, "recordSha256", "seriesRetrievedUtc", "carriedForward", "originManifestSha256");
        var identity = series.GetProperty("identity");
        AssertKeys(identity, "aliases authority bodyKind fragmentCode fullName isNeo isPha mpcPackedDesignation orbitClassCode " +
            "primaryDesignation shortName spkId");
        Assert.AreEqual("jpl-spk", Str(identity, "authority"));
        var spkId = Str(identity, "spkId")!;
        Assert.IsTrue(spkId.Length is > 0 and <= 12 && spkId[0] != '0' && spkId.All(char.IsAsciiDigit));
        var aliases = StringArray(identity, "aliases");
        CollectionAssert.AreEqual(aliases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), aliases);
        Assert.IsFalse(aliases.Contains(Str(identity, "primaryDesignation"), StringComparer.Ordinal));

        var solution = series.GetProperty("solution");
        AssertKeys(solution, "conditionCode dataArcDays epochTdbJd firstObsDateRaw hasCovariance horizonsEopCoverage horizonsEopFile " +
            "horizonsRecordNumber lastObsDateRaw magnitudeModel nonGravitationalModel notValidAfterUtc notValidBeforeUtc observationCount " +
            "orbitId planetaryEphemeris residualRmsArcsec smallBodyPerturbers solutionAuthority solutionDateSourceRaw " +
            "solutionDateSourceTimeZone solutionDateUtc");
        AssertKeys(solution.GetProperty("horizonsEopCoverage"), "dataBasedStartDate dataBasedEndDate predictsEndDate");
        AssertKeys(solution.GetProperty("magnitudeModel"), "kind absoluteMagnitudeH slopeParameterG totalMagnitudeM1 totalSlopeK1 " +
            "nuclearMagnitudeM2 nuclearSlopeK2 phaseCoefficient");
        if (solution.GetProperty("nonGravitationalModel").ValueKind == JsonValueKind.Object)
        {
            var model = solution.GetProperty("nonGravitationalModel");
            AssertKeys(model, "A1 A2 A3 DT S0");
            foreach (var parameter in model.EnumerateObject())
                AssertKeys(parameter.Value, "value unit");
        }

        Assert.AreEqual("jpl-horizons-observer-airless-v1", Str(series, "ephemerisProfile"));
        Assert.AreEqual("coord@399", Str(series, "center"));
        Assert.IsFalse(series.GetProperty("refractionApplied").GetBoolean());
        var step = Int(series, "stepSeconds");
        Assert.IsTrue(step is 3600 or 900 or 300 or 120 or 60);
        var knots = Items(series, "knots");
        Assert.IsTrue(knots.Length is >= 4 and <= 1024);
        Assert.AreEqual(Instant(series, "validFromUtc").AddSeconds(-step), Instant(series, "paddedStartUtc"));
        Assert.AreEqual(Instant(series, "validUntilUtc").AddSeconds(step), Instant(series, "paddedEndUtc"));
        Assert.AreEqual(Instant(series, "paddedEndUtc"), Instant(knots[^1], "utc"));
        Assert.AreEqual(Num(series, "minimumRangeAu"), knots.Min(knot => Num(knot, "rangeAu")));

        var residual = series.GetProperty("maxVerifiedMidpointResidualArcsec");
        var unverified = Str(series, "interpolationVerificationMethod") == "Unverified";
        Assert.AreEqual(unverified, residual.ValueKind == JsonValueKind.Null);
        Assert.AreEqual(!unverified && residual.GetDouble() is >= 0 and <= 1,
            series.GetProperty("interpolationBoundVerified").GetBoolean());

        for (var i = 0; i < knots.Length; i++)
        {
            var knot = knots[i];
            AssertKeys(knot, "utc astrometricRaDeg astrometricDecDeg apparentRaDeg apparentDecDeg predictedTotalMagnitude " +
                "predictedNuclearOrSurface rangeAu rangeRateKmPerSec solarElongationDeg solarElongationSide phaseAngleDeg " +
                "ra3SigmaArcsec dec3SigmaArcsec");
            Assert.AreEqual(Instant(series, "paddedStartUtc").AddSeconds(i * step), Instant(knot, "utc"));
            Assert.IsTrue(Num(knot, "astrometricRaDeg") is >= 0 and < 360 && Num(knot, "apparentRaDeg") is >= 0 and < 360);
            Assert.IsTrue(Num(knot, "astrometricDecDeg") is >= -90 and <= 90 && Num(knot, "apparentDecDeg") is >= -90 and <= 90);
            Assert.IsTrue(Num(knot, "rangeAu") > 0);
            Assert.IsTrue(Num(knot, "solarElongationDeg") is >= 0 and <= 180 && Num(knot, "phaseAngleDeg") is >= 0 and <= 180);
        }
    }

    private static void AssertKeys(JsonElement item, string expected) =>
        CollectionAssert.AreEquivalent(expected.Split(' '), item.EnumerateObject().Select(property => property.Name).ToArray());

    private static void AssertDigest(JsonElement item, string property, params string[] omit) =>
        Assert.AreEqual(Str(item, property), Sha256(SmallBodyFixtureCanonicalJson.Encode(item, [property, .. omit])), property);

    private static JsonElement Series(JsonElement payload, string spkId) =>
        Items(payload, "series").Single(value => Str(value.GetProperty("identity"), "spkId") == spkId);

    private static string FindFixtureRoot()
    {
        // Resolve the checked-in bytes the same way as the architecture tests.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
                return Path.Combine(directory.FullName, "tests", "fixtures", "small-bodies", "v1");
        }
        throw new DirectoryNotFoundException("The small-body fixture tests require the solution checkout.");
    }

    private static byte[] Read(string path) => File.ReadAllBytes(Path.Combine(FixtureRoot, path));
    private static JsonDocument Open(string path) => JsonDocument.Parse(Read(path));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string? Str(JsonElement item, string name) => item.GetProperty(name).GetString();
    private static int Int(JsonElement item, string name) => item.GetProperty(name).GetInt32();
    private static double Num(JsonElement item, string name) => item.GetProperty(name).GetDouble();
    private static JsonElement[] Items(JsonElement item, string name) => item.GetProperty(name).EnumerateArray().ToArray();
    private static string?[] StringArray(JsonElement item, string name) => Items(item, name).Select(value => value.GetString()).ToArray();
    private static void JsonEqual(JsonElement expected, JsonElement actual) => Assert.IsTrue(JsonElement.DeepEquals(expected, actual));
    private static DateTimeOffset Instant(JsonElement item, string name) => Instant(Str(item, name)!);
    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
