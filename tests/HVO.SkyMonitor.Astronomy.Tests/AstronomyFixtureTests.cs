using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class AstronomyFixtureTests
{
    [TestMethod]
    public void Polaris_AtHualapaiTimes_MatchesIndependentReferenceFixtures()
    {
        using var manifest = LoadConformanceManifest();
        var root = manifest.RootElement;
        var polaris = root.GetProperty("objects").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "HIP 11767");
        var polarisJ2000 = new EquatorialPoint(
            polaris.GetProperty("rightAscensionHours").GetDouble(),
            polaris.GetProperty("declinationDegrees").GetDouble());
        var full = root.GetProperty("projectionProfiles").GetProperty("full");
        var principal = full.GetProperty("principalPoint");
        var radius = full.GetProperty("imageCircleRadiusPixels").GetDouble();
        var projector = new EquidistantFisheyeProjector(new(
            principal.GetProperty("x").GetDouble(), principal.GetProperty("y").GetDouble(),
            radius / (Math.PI / 2), radius));

        foreach (var fixture in root.GetProperty("astronomyCases").EnumerateArray()
            .Where(item => item.TryGetProperty("expectedFullPixel", out _)))
        {
            var utc = DateTimeOffset.Parse(
                fixture.GetProperty("utc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var observer = fixture.GetProperty("observer");
            var expectedHorizontal = fixture.GetProperty("expectedHorizontal");
            var expectedPixel = fixture.GetProperty("expectedFullPixel");
            var horizontalTolerance = fixture.GetProperty("horizontalToleranceDegrees").GetDouble();
            var pixelTolerance = fixture.GetProperty("pixelTolerance").GetDouble();
            var ofDate = EquatorialPrecession.PrecessJ2000(polarisJ2000, utc);
            var horizontal = CoordinateTransforms.EquatorialToHorizontal(
                ofDate, utc,
                observer.GetProperty("latitudeDegrees").GetDouble(),
                observer.GetProperty("longitudeDegrees").GetDouble());
            var pixel = projector.Project(horizontal);

            Assert.AreEqual(expectedHorizontal.GetProperty("altitudeDegrees").GetDouble(),
                horizontal.AltitudeDegrees, horizontalTolerance);
            AssertAngularEqual(expectedHorizontal.GetProperty("azimuthDegrees").GetDouble(),
                horizontal.AzimuthDegrees, horizontalTolerance);
            Assert.IsNotNull(pixel);
            Assert.AreEqual(expectedPixel.GetProperty("x").GetDouble(), pixel.Value.X, pixelTolerance);
            Assert.AreEqual(expectedPixel.GetProperty("y").GetDouble(), pixel.Value.Y, pixelTolerance);
        }
    }

    [TestMethod]
    public void Polaris_AtGreenwichLatitude_MatchesIndependentReferenceFixture()
    {
        using var manifest = LoadConformanceManifest();
        var root = manifest.RootElement;
        var polaris = root.GetProperty("objects").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "HIP 11767");
        var fixture = root.GetProperty("astronomyCases").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "second-latitude");
        var observer = fixture.GetProperty("observer");
        var expected = fixture.GetProperty("expectedHorizontal");
        var tolerance = fixture.GetProperty("horizontalToleranceDegrees").GetDouble();
        var utc = DateTimeOffset.Parse(
            fixture.GetProperty("utc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var ofDate = EquatorialPrecession.PrecessJ2000(new EquatorialPoint(
            polaris.GetProperty("rightAscensionHours").GetDouble(),
            polaris.GetProperty("declinationDegrees").GetDouble()), utc);

        var horizontal = CoordinateTransforms.EquatorialToHorizontal(
            ofDate, utc,
            observer.GetProperty("latitudeDegrees").GetDouble(),
            observer.GetProperty("longitudeDegrees").GetDouble());

        Assert.AreEqual(expected.GetProperty("altitudeDegrees").GetDouble(), horizontal.AltitudeDegrees, tolerance);
        Assert.AreEqual(expected.GetProperty("azimuthDegrees").GetDouble(), horizontal.AzimuthDegrees, tolerance);
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

    [TestMethod]
    public async Task HualapaiConstellationEndpoints_ProduceCanonicalClippingEvidence()
    {
        using var manifest = LoadStellariumManifest();
        var constellation = manifest.RootElement.GetProperty("constellationValidation");
        var endpoints = constellation.GetProperty("endpoints").EnumerateArray().ToArray();
        var catalog = new InMemoryCelestialCatalog(endpoints.Select(item =>
        {
            var id = item.GetProperty("id").GetString()!;
            var j2000 = item.GetProperty("j2000");
            return new CelestialCatalogObject(
                id, id,
                j2000.GetProperty("rightAscensionDegrees").GetDouble() / 15,
                j2000.GetProperty("declinationDegrees").GetDouble(),
                1,
                HipparcosId: id[4..]);
        }));
        var topology = new InMemoryConstellationTopology(
            constellation.GetProperty("segments").EnumerateArray().Select(item => new ConstellationSegment(
                item.GetProperty("constellationId").GetString()!,
                item.GetProperty("from").GetString()![4..],
                item.GetProperty("to").GetString()![4..])));
        var request = new VisibleSceneRequest(
            new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
            new ObserverLocation(35.347, -113.878, 0),
            new EquidistantProjectionContext(
                968, 608, 595.84 / (Math.PI / 2), 595.84, WidthPixels: 1936, HeightPixels: 1216),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("SIMBAD fixture", "J2000", new Uri("https://simbad.u-strasbg.fr"), "fixture"),
            constellationIds: ["ORI", "VIR"]);

        var scene = await new VisibleSceneBuilder(catalog, topology).BuildAsync(request).ConfigureAwait(false);

        foreach (var item in endpoints)
        {
            var id = item.GetProperty("id").GetString()!;
            var j2000 = item.GetProperty("j2000");
            var horizontal = CoordinateTransforms.EquatorialToHorizontal(
                EquatorialPrecession.PrecessJ2000(new EquatorialPoint(
                    j2000.GetProperty("rightAscensionDegrees").GetDouble() / 15,
                    j2000.GetProperty("declinationDegrees").GetDouble()), request.Utc),
                request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
            var radius = 595.84 * (90 - horizontal.AltitudeDegrees) / 90;
            var azimuth = horizontal.AzimuthDegrees * Math.PI / 180;
            var actual = new PixelPoint(
                968 + radius * Math.Sin(azimuth),
                608 - radius * Math.Cos(azimuth));
            var expected = item.GetProperty("expectedHvoSensor");
            Assert.AreEqual(expected.GetProperty("x").GetDouble(), actual.X, 1e-6, id);
            Assert.AreEqual(expected.GetProperty("y").GetDouble(), actual.Y, 1e-6, id);
        }

        var expectedBoundary = constellation.GetProperty("segments").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "VIR-65474-69701")
            .GetProperty("expectedHvoBoundary");
        var expectedBoundaryPoint = new PixelPoint(
            expectedBoundary.GetProperty("x").GetDouble(), expectedBoundary.GetProperty("y").GetDouble());
        var clippedBoundary = scene.Segments
            .Where(segment => segment.ConstellationId == "VIR")
            .SelectMany(static segment => new[] { segment.FromPixel, segment.ToPixel })
            .MinBy(point => Math.Pow(point.X - expectedBoundaryPoint.X, 2) +
                Math.Pow(point.Y - expectedBoundaryPoint.Y, 2));
        Assert.AreEqual(expectedBoundaryPoint.X, clippedBoundary.X, 1e-6);
        Assert.AreEqual(expectedBoundaryPoint.Y, clippedBoundary.Y, 1e-6);
        Assert.HasCount(9, scene.Segments);
    }

    private static void AssertAngularEqual(double expected, double actual, double tolerance)
    {
        var difference = Math.Abs(expected - actual);
        difference = Math.Min(difference, 360 - difference);
        Assert.IsTrue(difference <= tolerance, $"Expected {expected} deg, actual {actual} deg.");
    }

    private static System.Text.Json.JsonDocument LoadConformanceManifest()
        => System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hualapai-asi174-conformance-v1.json")));

    private static System.Text.Json.JsonDocument LoadStellariumManifest()
        => System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hualapai-fisheye-v1.json")));
}
