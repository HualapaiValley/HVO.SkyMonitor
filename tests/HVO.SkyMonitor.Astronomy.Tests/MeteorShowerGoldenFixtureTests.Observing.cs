using System.Globalization;
using System.Text.Json;
using CosineKitty;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class MeteorShowerGoldenFixtureTests
{
    [TestMethod]
    public void SolarLongitudeWrapAndDriftMatchFrozenVectors()
    {
        var fixture = Read(Observing);
        var solar = fixture.GetProperty("solar");
        Fields(solar, "snapshot", "iauCode", "model", "ephemeris", "boundaries", "drift");
        Assert.AreEqual("astronomy-engine-2.1.19-solar-lon-j2000-v1", String(solar, "model"));
        var snapshot = Read(String(solar, "snapshot"));
        var shower = Shower(snapshot, String(solar, "iauCode"));
        var ephemeris = Array(solar, "ephemeris");
        Assert.HasCount(3, ephemeris);
        foreach (var vector in ephemeris)
        {
            Fields(vector, "queryUtc", "longitudeDeg", "expectedActive");
            var utc = Utc(vector, "queryUtc");
            var longitude = SolarLongitude(utc);
            Assert.AreEqual(Number(vector, "longitudeDeg"), longitude, 1e-9, FormatUtc(utc));
            Assert.AreEqual(vector.GetProperty("expectedActive").GetBoolean(), SolarActivity(shower, longitude), FormatUtc(utc));
        }
        var boundaries = Array(solar, "boundaries");
        Assert.HasCount(5, boundaries);
        foreach (var vector in boundaries)
        {
            Fields(vector, "longitudeDeg", "expectedActive");
            var longitude = Number(vector, "longitudeDeg");
            Assert.AreEqual(vector.GetProperty("expectedActive").GetBoolean(), SolarActivity(shower, longitude), $"{longitude:R}");
        }
        var drift = Array(solar, "drift");
        Assert.HasCount(4, drift);
        foreach (var vector in drift)
        {
            Fields(vector, "iauCode", "longitudeDeg", "expectedRaDeg", "expectedDecDeg", "expectedBasis");
            var code = String(vector, "iauCode");
            var actual = Radiant(Shower(snapshot, code), Number(vector, "longitudeDeg"));
            Assert.AreEqual(Number(vector, "expectedRaDeg"), actual.RaDeg, 1e-10, code);
            Assert.AreEqual(Number(vector, "expectedDecDeg"), actual.DecDeg, 1e-10, code);
            Assert.AreEqual(String(vector, "expectedBasis"), actual.Basis, code);
        }
    }

    [TestMethod]
    public void HorizontalRadiantsUseRaHoursAndRetainBelowHorizonAltitude()
    {
        var fixture = Read(Observing);
        var sites = fixture.GetProperty("sites");
        Fields(sites, "hvo", "south");
        var vectors = Array(fixture, "radiants");
        Assert.HasCount(5, vectors);
        foreach (var vector in vectors)
        {
            Fields(vector, "id", "snapshot", "iauCode", "site", "queryUtc", "expected");
            var id = String(vector, "id");
            var site = sites.GetProperty(String(vector, "site"));
            Fields(site, "latitudeDegrees", "longitudeDegrees", "elevationMeters");
            var observer = new ObserverLocation(Number(site, "latitudeDegrees"), Number(site, "longitudeDegrees"),
                Number(site, "elevationMeters"));
            var expected = vector.GetProperty("expected");
            Fields(expected, "raHours", "julianDate", "gmst", "lmst", "ofDateRaHours", "ofDateDecDeg", "altitudeDeg",
                "azimuthDeg", "driftBasis", "upperTransitAltitudeDeg", "horizon");
            var utc = Utc(vector, "queryUtc");
            var shower = Shower(Read(String(vector, "snapshot")), String(vector, "iauCode"));
            var radiant = Radiant(shower, SolarLongitude(utc));
            var j2000 = new EquatorialPoint(radiant.RaDeg / 15d, radiant.DecDeg);
            var ofDate = EquatorialPrecession.PrecessJ2000(j2000, utc);
            var horizontal = CoordinateTransforms.EquatorialToHorizontal(ofDate, utc, observer.LatitudeDegrees, observer.LongitudeDegrees);

            Assert.AreEqual(Number(expected, "raHours"), j2000.RightAscensionHours, 5e-10, id);
            Assert.AreEqual(Number(expected, "julianDate"), AstronomyTime.ToJulianDate(utc), 5e-10, id);
            Assert.AreEqual(Number(expected, "gmst"), AstronomyTime.GreenwichMeanSiderealDegrees(utc), 5e-7, id);
            Assert.AreEqual(Number(expected, "lmst"), AstronomyTime.LocalMeanSiderealDegrees(utc, observer.LongitudeDegrees), 5e-7, id);
            Assert.AreEqual(Number(expected, "ofDateRaHours"), ofDate.RightAscensionHours, 5e-10, id);
            Assert.AreEqual(Number(expected, "ofDateDecDeg"), ofDate.DeclinationDegrees, 5e-7, id);
            Assert.AreEqual(Number(expected, "altitudeDeg"), horizontal.AltitudeDegrees, 5e-7, id);
            Assert.AreEqual(Number(expected, "azimuthDeg"), horizontal.AzimuthDegrees, 5e-7, id);
            Assert.AreEqual(String(expected, "driftBasis"), radiant.Basis, id);
            Assert.AreEqual(Number(expected, "upperTransitAltitudeDeg"),
                90 - Math.Abs(observer.LatitudeDegrees - ofDate.DeclinationDegrees), 5e-7, id);
            Assert.AreEqual(String(expected, "horizon"),
                horizontal.AltitudeDegrees > 0 ? "RadiantAboveHorizon" : "RadiantBelowHorizon", id);

            var unconverted = new EquatorialPoint(radiant.RaDeg, radiant.DecDeg);
            if (radiant.RaDeg >= 24)
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EquatorialPrecession.PrecessJ2000(unconverted, utc), id);
            }
            else
            {
                var wrong = CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(unconverted, utc),
                    utc, observer.LatitudeDegrees, observer.LongitudeDegrees);
                Assert.IsGreaterThan(10d, Math.Abs(wrong.AltitudeDegrees - horizontal.AltitudeDegrees), id);
            }
        }
    }

    [TestMethod]
    public void CivilAndCloudContextsPreserveOffsetsAndMissingEvidence()
    {
        var fixture = Read(Observing);
        Fields(fixture, "schemaVersion", "sites", "radiants", "civil", "cloud", "solar", "availability");
        var civil = Array(fixture, "civil");
        Assert.HasCount(5, civil);
        foreach (var vector in civil)
        {
            Fields(vector, "id", "timeZone", "queryUtc", "expectedLocal");
            var zone = TimeZoneInfo.FindSystemTimeZoneById(String(vector, "timeZone"));
            var utc = Utc(vector, "queryUtc");
            var local = TimeZoneInfo.ConvertTime(utc, zone);
            Assert.AreEqual(String(vector, "expectedLocal"), local.ToString(CivilFormat, CultureInfo.InvariantCulture), String(vector, "id"));
            Assert.AreEqual(String(vector, "queryUtc"), FormatUtc(local), String(vector, "id"));
        }

        var cloud = Array(fixture, "cloud");
        Assert.HasCount(5, cloud);
        var sites = fixture.GetProperty("sites");
        foreach (var vector in cloud)
        {
            Fields(vector, "id", "site", "queryUtc", "evidence", "expectedStatus", "expectedCoverageMillionths");
            sites.GetProperty(String(vector, "site"));
            var evidence = vector.GetProperty("evidence");
            var actualStatus = "Unknown";
            int? actualCoverage = null;
            if (evidence.ValueKind != JsonValueKind.Null)
            {
                Fields(evidence, "site", "observedUtc", "validUntilUtc", "assessment");
                var assessment = evidence.GetProperty("assessment");
                Fields(assessment, "schemaVersion", "status", "coverageMillionths", "confidenceMillionths");
                Assert.AreEqual("cloud-assessment-v1", String(assessment, "schemaVersion"));
                Assert.IsTrue(Int(assessment, "confidenceMillionths") is >= 0 and <= 1_000_000);
                OneOf(assessment, "status", "Quantified", "InsufficientEvidence");
                var observed = Utc(evidence, "observedUtc");
                var until = Utc(evidence, "validUntilUtc");
                Assert.IsTrue(observed < until);
                var query = Utc(vector, "queryUtc");
                var quantified = String(assessment, "status") == "Quantified";
                Assert.AreNotEqual(quantified, IsNull(assessment, "coverageMillionths"));
                if (quantified)
                {
                    Assert.IsTrue(Int(assessment, "coverageMillionths") is >= 0 and <= 1_000_000);
                }
                if (quantified && String(evidence, "site") == String(vector, "site") && observed <= query && query < until)
                {
                    actualStatus = "Quantified";
                    actualCoverage = Int(assessment, "coverageMillionths");
                }
            }

            var id = String(vector, "id");
            Assert.AreEqual(String(vector, "expectedStatus"), actualStatus, id);
            int? expectedCoverage = IsNull(vector, "expectedCoverageMillionths") ? null : Int(vector, "expectedCoverageMillionths");
            Assert.AreEqual(expectedCoverage, actualCoverage, id);
        }
    }

    [TestMethod]
    public void AvailabilityFixturesDistinguishAbsentCorruptAndExpiredData()
    {
        var scenarios = Array(Read(Observing), "availability");
        Assert.HasCount(5, scenarios);
        foreach (var vector in scenarios)
        {
            Fields(vector, "id", "snapshot", "expectedRawSha256", "queryUtc", "expectedStatus", "lastKnownGood", "expectedRetained");
            var id = String(vector, "id");
            var file = String(vector, "snapshot");
            var path = Path.Combine(FixtureRoot, file);
            var expectedHash = String(vector, "expectedRawSha256");
            var lastKnownGood = String(vector, "lastKnownGood");
            Assert.IsNull(ImportRejection(Read(lastKnownGood)), id);
            var retained = lastKnownGood;
            var status = "Absent";
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                status = "HashMismatch";
                if (Hash(bytes) == expectedHash)
                {
                    try
                    {
                        var snapshot = Parse(bytes);
                        Assert.IsNull(ImportRejection(snapshot), id);
                        Assert.IsNotEmpty(Array(snapshot, "showers"), id);
                        status = InactiveMode(snapshot, Utc(vector, "queryUtc")) == "AnnualForecastExpired" ?
                            "AnnualForecastExpired" : "Available";
                        retained = file;
                    }
                    catch (JsonException)
                    {
                        status = "InvalidJson";
                    }
                }
            }

            Assert.AreEqual(String(vector, "expectedStatus"), status, id);
            Assert.AreEqual(String(vector, "expectedRetained"), retained, id);
        }
    }

    private static double SolarLongitude(DateTimeOffset utc)
    {
        var sun = CosineKitty.Astronomy.GeoVector(Body.Sun, new AstroTime(utc.UtcDateTime), Aberration.None);
        const double obliquityRadians = 23.439291111d * Math.PI / 180d;
        var eclipticY = sun.y * Math.Cos(obliquityRadians) + sun.z * Math.Sin(obliquityRadians);
        return UnsignedDegrees(Math.Atan2(eclipticY, sun.x) * 180d / Math.PI);
    }

    private static bool? SolarActivity(JsonElement shower, double longitude)
    {
        var nominal = shower.GetProperty("nominalActivity");
        if (nominal.ValueKind == JsonValueKind.Null || IsNull(nominal, "activeStartSolarLonDeg"))
        {
            return null;
        }

        var first = Number(nominal, "activeStartSolarLonDeg");
        var last = Number(nominal, "activeEndSolarLonDeg");
        return UnsignedDegrees(longitude - first) <= UnsignedDegrees(last - first);
    }

    private static (double RaDeg, double DecDeg, string Basis) Radiant(JsonElement shower, double longitude)
    {
        var reference = shower.GetProperty("referenceRadiant");
        var ra = Number(reference, "raDeg");
        var dec = Number(reference, "decDeg");
        var drift = shower.GetProperty("radiantDrift");
        if (drift.ValueKind == JsonValueKind.Null || SolarActivity(shower, longitude) != true)
        {
            return (ra, dec, "FixedPeakRadiantDriftUnavailable");
        }

        var delta = SignedDegrees(longitude - Number(reference, "referenceSolarLongitudeDeg"));
        return (UnsignedDegrees(ra + delta * Number(drift, "dRaDegPerSolarLonDeg")),
            Math.Clamp(dec + delta * Number(drift, "dDecDegPerSolarLonDeg"), -90, 90), "LinearSolarLongitudeDrift");
    }

    private static double UnsignedDegrees(double value) => (value % 360d + 360d) % 360d;
    private static double SignedDegrees(double value) => UnsignedDegrees(value + 180d) - 180d;
}
