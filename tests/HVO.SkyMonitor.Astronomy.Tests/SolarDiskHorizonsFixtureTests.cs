using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Compares resolved Sun and Moon disks with JPL Horizons (DE441) responses recorded offline in
/// tests/fixtures/horizons/issue-518. Every response is SHA-256 verified before it is read; no test uses the network.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SolarDiskHorizonsFixtureTests
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "horizons", "issue-518");

    private static readonly CatalogMetadata Metadata =
        new("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1");

    [TestMethod]
    public void ManifestPinsEveryRecordedResponse()
    {
        var cases = LoadCases();
        Assert.HasCount(6, cases);
        Assert.HasCount(12, Directory.GetFiles(FixtureDirectory, "*.txt"));
        foreach (var fixture in cases)
        {
            StringAssert.Contains(fixture.AirlessText, "Atmos refraction: NO", StringComparison.Ordinal);
            StringAssert.Contains(fixture.RefractedText, "Atmos refraction: YES", StringComparison.Ordinal);
            StringAssert.Contains(fixture.AirlessText, "{source: DE441}", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void TopocentricDiscsMatchHorizonsPositionSizeRangeAndPhase()
    {
        foreach (var fixture in LoadCases())
        {
            var airless = fixture.Airless;
            var disk = SolarDiskEphemeris.Get(fixture.Body, fixture.Utc, fixture.Site);

            // Astronomy Engine against DE441: well inside one arcminute of topocentric apparent direction.
            var separation = SeparationArcMinutes(disk.Direction, new AltAzPoint(airless.Elevation, airless.Azimuth));
            Assert.IsLessThan(0.5, separation, $"{fixture.Id}: {separation:F3}′ from Horizons.");
            // Horizons Ang-diam is the full apparent diameter in arcseconds; the same mean radii make it agree closely.
            Assert.AreEqual(airless.AngularDiameterArcSeconds, disk.AngularRadiusDegrees * 7200,
                airless.AngularDiameterArcSeconds * 0.001, fixture.Id);
            Assert.AreEqual(airless.RangeKilometers, disk.DistanceKilometers, airless.RangeKilometers * 0.0002, fixture.Id);
            // Horizons reports the topocentric fraction; the engine's phase fraction is geocentric. Parallax moves the
            // phase angle by at most the Moon's maximum horizontal parallax π_max ≈ 61.5′, and dF/di = ½·sin(i), so
            // |ΔF| ≤ ½·sin(i)·π_max ≤ 0.0089. The 0.009 tolerance is that physical bound, not a fitted value.
            Assert.AreEqual(airless.IlluminatedPercent / 100, disk.IlluminatedFraction, 0.009, fixture.Id);
        }
    }

    [TestMethod]
    public void BennettRefractionTracksTheHorizonsRefractedElevation()
    {
        foreach (var fixture in LoadCases())
        {
            var refracted = AtmosphericRefraction.Apply(fixture.Airless.Elevation, new RefractionOptions(true));
            // At 22–68° the two standard-atmosphere models differ by a few arcseconds.
            Assert.AreEqual(fixture.Refracted.Elevation, refracted, 5d / 3600, fixture.Id);
            Assert.AreEqual(fixture.Airless.Azimuth, fixture.Refracted.Azimuth, 1e-9, fixture.Id);
        }
    }

    [TestMethod]
    public void ResolvedFootprintsCarryTheRefractedApparentCentreAndItsModel()
    {
        foreach (var fixture in LoadCases())
        {
            var request = new VisibleSceneRequest(fixture.Utc, fixture.Site,
                new ProjectionContext(ProjectionModel.EquidistantFisheye, 2000, 2000, 1200, 1200, 4000, 4000,
                    ProjectionAperture.Circular, 1900, BoresightAltitudeDegrees: 90),
                new CatalogQuery(6, 10), Metadata, new RefractionOptions(true), projectionVersion: "fisheye-v1",
                solarSystemBodies: [fixture.Body]);
            var scene = new VisibleScene(request, [], [], null)
                .WithResolvedBodies([SolarDiskEphemeris.Get(fixture.Body, fixture.Utc, fixture.Site)]);

            var footprint = scene.ResolvedFootprints.Single();
            Assert.AreEqual(AtmosphericRefraction.ModelVersion, footprint.RefractionModel, fixture.Id);
            Assert.AreEqual(SolarDiskEphemeris.RadiusSource, footprint.Extent.Source, fixture.Id);
            var refracted = new AltAzPoint(fixture.Refracted.Elevation, fixture.Refracted.Azimuth);
            Assert.IsLessThan(0.5, SeparationArcMinutes(footprint.ApparentCenter, refracted), fixture.Id);
            Assert.IsLessThan(0.5, SeparationArcMinutes(footprint.GeometricCenter,
                new AltAzPoint(fixture.Airless.Elevation, fixture.Airless.Azimuth)), fixture.Id);
            var point = scene.Objects.Single(item => item.Id == footprint.Id);
            Assert.AreEqual(footprint.ApparentCenter, point.ApparentHorizontal, fixture.Id);
        }
    }

    [TestMethod]
    public void RecordedExtremesKeepTheirPhysicalOrderingAndLunarParallax()
    {
        var cases = LoadCases().ToDictionary(static fixture => fixture.Id, StringComparer.Ordinal);
        double Radius(string id) =>
            SolarDiskEphemeris.Get(cases[id].Body, cases[id].Utc, cases[id].Site).AngularRadiusDegrees;

        Assert.IsGreaterThan(Radius("sun-aphelion-hvo"), Radius("sun-perihelion-hvo"));
        Assert.IsGreaterThan(Radius("moon-apogee-hvo"), Radius("moon-perigee-lapalma"));
        Assert.IsGreaterThan(Radius("moon-apogee-tololo"), Radius("moon-perigee-sutherland"));
        // Same instant, different sites: the higher Moon is nearer, as Horizons records; the size gap is parallax.
        var lapalma = Radius("moon-perigee-lapalma");
        var sutherland = Radius("moon-perigee-sutherland");
        Assert.IsGreaterThan(sutherland, lapalma);
        var horizonsRatio = cases["moon-perigee-lapalma"].Airless.AngularDiameterArcSeconds /
            cases["moon-perigee-sutherland"].Airless.AngularDiameterArcSeconds;
        Assert.AreEqual(horizonsRatio, lapalma / sutherland, 1e-4);
    }

    private static double SeparationArcMinutes(AltAzPoint a, AltAzPoint b)
    {
        var first = CameraBasis.FromHorizontal(a);
        var second = CameraBasis.FromHorizontal(b);
        var dot = Math.Clamp(first.East * second.East + first.North * second.North + first.Up * second.Up, -1, 1);
        return Math.Acos(dot) * 180 / Math.PI * 60;
    }

    private static List<HorizonsCase> LoadCases()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureDirectory, "manifest.json")));
        Assert.AreEqual("issue-518-horizons-solar-lunar-disks-v1", manifest.RootElement.GetProperty("fixtureId").GetString());
        var cases = new List<HorizonsCase>();
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var files = item.GetProperty("files");
            var airlessText = ReadVerified(files.GetProperty("airless"));
            var refractedText = ReadVerified(files.GetProperty("refracted"));
            cases.Add(new HorizonsCase(
                item.GetProperty("id").GetString()!,
                Enum.Parse<SolarSystemBody>(item.GetProperty("body").GetString()!),
                DateTimeOffset.Parse(item.GetProperty("utc").GetString()!, CultureInfo.InvariantCulture),
                new ObserverLocation(item.GetProperty("latitudeDegrees").GetDouble(),
                    item.GetProperty("longitudeDegrees").GetDouble(), item.GetProperty("elevationMeters").GetDouble()),
                airlessText, refractedText, FirstRow(airlessText), FirstRow(refractedText)));
        }
        return cases;
    }

    private static string ReadVerified(JsonElement file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, file.GetProperty("file").GetString()!));
        Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)),
            "A recorded Horizons response changed.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static HorizonsRow FirstRow(string text)
    {
        var start = text.IndexOf("$$SOE", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "Missing $$SOE.");
        var line = text[(start + 5)..].TrimStart('\r', '\n').Split('\n')[0];
        var fields = line.Split(',');
        double Field(int index) => double.Parse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture);
        return new HorizonsRow(Field(3), Field(4), Field(5), Field(6), Field(7));
    }

    private sealed record HorizonsRow(double Azimuth, double Elevation, double IlluminatedPercent,
        double AngularDiameterArcSeconds, double RangeKilometers);

    private sealed record HorizonsCase(string Id, SolarSystemBody Body, DateTimeOffset Utc, ObserverLocation Site,
        string AirlessText, string RefractedText, HorizonsRow Airless, HorizonsRow Refracted);
}
