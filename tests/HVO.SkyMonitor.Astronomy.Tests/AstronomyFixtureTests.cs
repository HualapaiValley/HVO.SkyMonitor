using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class AstronomyFixtureTests
{
    private static readonly EquatorialPoint PolarisJ2000 = new(2.530301, 89.264109);

    [TestMethod]
    public void Polaris_AtHualapaiTimes_MatchesIndependentReferenceFixtures()
    {
        // J2000 position: SIMBAD/Hipparcos. Reference horizontal positions were
        // independently generated with Astropy FK5/ERFA and rounded to 0.01 deg.
        // The 0.05 deg tolerance covers IAU 1976 vs IAU 2006 precession and UTC-vs-TT.
        (DateTimeOffset Utc, double Altitude, double Azimuth, double X, double Y)[] fixtures =
        [
            (new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero), 35.51, 359.25, 963.28, 247.27),
            (new(2025, 1, 15, 8, 59, 50, 170, TimeSpan.Zero), 35.34, 359.23, 963.11, 246.19),
            (new(2025, 7, 15, 8, 0, 0, TimeSpan.Zero), 35.16, 0.74, 972.68, 244.99)
        ];
        var projector = new EquidistantFisheyeProjector(new(968, 608, 595.84 / (Math.PI / 2), 595.84));

        foreach (var fixture in fixtures)
        {
            var ofDate = EquatorialPrecession.PrecessJ2000(PolarisJ2000, fixture.Utc);
            var horizontal = CoordinateTransforms.EquatorialToHorizontal(ofDate, fixture.Utc, 35.347, -113.878);
            var pixel = projector.Project(horizontal);

            Assert.AreEqual(fixture.Altitude, horizontal.AltitudeDegrees, 0.05);
            AssertAngularEqual(fixture.Azimuth, horizontal.AzimuthDegrees, 0.05);
            Assert.IsNotNull(pixel);
            Assert.AreEqual(fixture.X, pixel.Value.X, 0.35);
            Assert.AreEqual(fixture.Y, pixel.Value.Y, 0.35);
        }
    }

    [TestMethod]
    public void Polaris_AtGreenwichLatitude_MatchesIndependentReferenceFixture()
    {
        var utc = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var ofDate = EquatorialPrecession.PrecessJ2000(PolarisJ2000, utc);

        var horizontal = CoordinateTransforms.EquatorialToHorizontal(ofDate, utc, 51.4779, 0);

        Assert.AreEqual(50.85, horizontal.AltitudeDegrees, 0.05);
        Assert.AreEqual(0.16, horizontal.AzimuthDegrees, 0.05);
    }

    [TestMethod]
    public async Task HualapaiScene_MatchesStellariumManifestStarsWithinModelBudget()
    {
        var catalog = new InMemoryCelestialCatalog([
            new("HIP 32349", "Sirius", 101.28715533 / 15, -16.71611586, -1.46),
            new("HIP 24608", "Capella", 79.17232794 / 15, 45.99799147, 0.08),
            new("HIP 37279", "Procyon", 114.8254935 / 15, 5.22499307, 0.34),
            new("HIP 27989", "Betelgeuse", 88.792939 / 15, 7.407064, 0.42)
        ]);
        var metadata = new CatalogMetadata("Hipparcos fixture", "J2000", new Uri("https://simbad.u-strasbg.fr"), "fixture");
        var request = new VisibleSceneRequest(
            new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
            new ObserverLocation(35.347, -113.878, 0),
            new EquidistantProjectionContext(968, 608, 595.84 / (Math.PI / 2), 595.84, WidthPixels: 1936, HeightPixels: 1216),
            new CatalogQuery(6.5, 10), metadata);

        var scene = await new VisibleSceneBuilder(catalog).BuildAsync(request).ConfigureAwait(false);
        var expected = new Dictionary<string, PixelPoint>(StringComparer.Ordinal)
        {
            ["HIP 32349"] = new(823.038, 944.670),
            ["HIP 24608"] = new(781.535, 492.441),
            ["HIP 37279"] = new(924.336, 806.377),
            ["HIP 27989"] = new(748.681, 764.817)
        };

        Assert.HasCount(4, scene.Objects);
        foreach (var item in scene.Objects)
        {
            var reference = expected[item.Id];
            var error = Math.Sqrt(Math.Pow(item.Pixel.X - reference.X, 2) + Math.Pow(item.Pixel.Y - reference.Y, 2));
            Assert.IsTrue(error <= 6.1291, $"{item.Id} differed by {error:R} sensor pixels.");
        }
    }

    private static void AssertAngularEqual(double expected, double actual, double tolerance)
    {
        var difference = Math.Abs(expected - actual);
        difference = Math.Min(difference, 360 - difference);
        Assert.IsTrue(difference <= tolerance, $"Expected {expected} deg, actual {actual} deg.");
    }
}
