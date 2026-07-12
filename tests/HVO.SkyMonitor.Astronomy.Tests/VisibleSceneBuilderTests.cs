using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class VisibleSceneBuilderTests
{
    private static readonly DateTimeOffset PrimaryUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedEqualMagnitudeIds = ["a", "b"];

    [TestMethod]
    public async Task BuildAsync_AppliesMaximumResultsAfterExactVisibility()
    {
        var catalog = new InMemoryCelestialCatalog([
            new("sirius", "Sirius", 6.752477, -16.716116, -1.46),
            new("vega", "Vega", 18.615649, 38.783689, 0.03),
            new("polaris", "Polaris", 2.530301, 89.264109, 1.98)
        ]);
        var builder = new VisibleSceneBuilder(catalog);

        var scene = await builder.BuildAsync(CreateRequest(maximumResults: 1)).ConfigureAwait(false);

        Assert.AreEqual(1, scene.Objects.Count);
        Assert.AreEqual("polaris", scene.Objects[0].Id);
        Assert.AreEqual(CelestialObjectKind.Star, scene.Objects[0].Kind);
        Assert.IsTrue(scene.Objects[0].GeometricHorizontal.AltitudeDegrees > 0);
        Assert.AreEqual("4.2", scene.Objects[0].CatalogVersion);
        Assert.AreEqual("equidistant-v1", scene.Objects[0].ProjectionVersion);
        Assert.AreEqual("visible-scene-iau1976-constellation-v2", scene.Objects[0].AlgorithmVersion);
    }

    [TestMethod]
    public async Task BuildAsync_IsDeterministicForEqualMagnitudes()
    {
        var catalog = new InMemoryCelestialCatalog([
            new("b", "B", 2.530301, 89.264109, 2),
            new("a", "A", 2.530301, 89.264109, 2)
        ]);

        var scene = await new VisibleSceneBuilder(catalog).BuildAsync(CreateRequest(maximumResults: 2)).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedEqualMagnitudeIds, scene.Objects.Select(item => item.Id).ToArray());
        Assert.Throws<NotSupportedException>(() => ((IList<ProjectedCelestialObject>)scene.Objects).Clear());
    }

    [TestMethod]
    public async Task BuildAsync_CanceledToken_ThrowsBeforeCatalogWork()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var builder = new VisibleSceneBuilder(new InMemoryCelestialCatalog([]));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await builder.BuildAsync(CreateRequest(1), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public void Request_InvalidSensorContextOrMetadata_ThrowsAtConstruction()
    {
        var metadata = new CatalogMetadata("HYG", "4.2", new Uri("https://astronexus.com/projects/hyg"), "fixture");
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisibleSceneRequest(
            PrimaryUtc,
            new ObserverLocation(35.347, -113.878, 0),
            new EquidistantProjectionContext(968, 608, 0, 595.84),
            new CatalogQuery(6.5, 1),
            metadata));
        Assert.Throws<ArgumentException>(() => new VisibleSceneRequest(
            PrimaryUtc,
            new ObserverLocation(35.347, -113.878, 0),
            new EquidistantProjectionContext(968, 608, 100, 595.84),
            new CatalogQuery(6.5, 1),
            metadata with { Checksum = "" }));
    }

    [TestMethod]
    public async Task BuildAsync_ResolvesRequestedConstellationSegmentsFromSelectedVisibleObjects()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(PrimaryUtc, -113.878) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("from", "From", rightAscension, 35.347, 1, HipparcosId: "1"),
            new("to", "To", rightAscension, 30, 2, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology([
            new ConstellationSegment("TST", "1", "2"),
            new ConstellationSegment("TST", "1", "3")
        ]);
        var request = new VisibleSceneRequest(
            PrimaryUtc, new ObserverLocation(35.347, -113.878, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Circular, 150),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.test"), "fixture"),
            constellationIds: ["TST", "TST"]);

        var scene = await new VisibleSceneBuilder(catalog, topology).BuildAsync(request).ConfigureAwait(false);

        Assert.HasCount(2, scene.Objects);
        Assert.IsGreaterThan(0, scene.Segments.Count);
        Assert.AreEqual("from", scene.Segments[0].FromObjectId);
        Assert.AreEqual(scene.Objects.Single(item => item.Id == "to").Pixel, scene.Segments[^1].ToPixel);
        Assert.HasCount(1, scene.Request.ConstellationIds);
    }

    [TestMethod]
    public async Task BuildAsync_ResolvesTopologyEndpointsIndependentlyOfRenderSelection()
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(PrimaryUtc, -113.878) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("from", "From", rightAscension, 35.347, 1, HipparcosId: "1"),
            new("faint", "Faint", rightAscension, 30, 8, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "2")]);
        var baseRequest = new VisibleSceneRequest(
            PrimaryUtc, new ObserverLocation(35.347, -113.878, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Circular, 150),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.test"), "fixture"),
            constellationIds: ["tst"]);

        var linesOnly = await new VisibleSceneBuilder(catalog, topology).BuildAsync(baseRequest).ConfigureAwait(false);
        var withEndpoints = await new VisibleSceneBuilder(catalog, topology).BuildAsync(new VisibleSceneRequest(
            baseRequest.Utc, baseRequest.Observer, baseRequest.Projection, baseRequest.CatalogQuery,
            baseRequest.CatalogMetadata, constellationIds: ["TST"], includeConstellationEndpointStars: true))
            .ConfigureAwait(false);

        Assert.IsFalse(linesOnly.Objects.Any(item => item.Id == "faint"));
        Assert.IsGreaterThan(0, linesOnly.Segments.Count);
        Assert.IsTrue(withEndpoints.Objects.Any(item => item.Id == "faint"));
        Assert.AreEqual(linesOnly.Segments.Count, withEndpoints.Segments.Count);
        CollectionAssert.AreEqual(
            linesOnly.Segments.Select(item => (item.FromPixel, item.ToPixel)).ToArray(),
            withEndpoints.Segments.Select(item => (item.FromPixel, item.ToPixel)).ToArray());
        Assert.AreEqual("TST", linesOnly.Request.ConstellationIds[0]);
    }

    [TestMethod]
    public async Task BuildAsync_ClipsFigureWhoseEndpointsAreOutsideCircularAperture()
    {
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(PrimaryUtc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("east", "East", (siderealHours + 22) % 24, 0, 1, HipparcosId: "1"),
            new("west", "West", (siderealHours + 2) % 24, 0, 1, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "2")]);
        var request = new VisibleSceneRequest(
            PrimaryUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 100, 100, 100, 100, 200, 200,
                ProjectionAperture.Circular, 50),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.test"), "fixture"),
            constellationIds: ["TST"]);

        var scene = await new VisibleSceneBuilder(catalog, topology).BuildAsync(request).ConfigureAwait(false);

        Assert.IsEmpty(scene.Objects);
        Assert.IsGreaterThan(0, scene.Segments.Count);
        Assert.IsTrue(scene.Segments.SelectMany(item => new[] { item.FromPixel, item.ToPixel })
            .All(point => Math.Sqrt(Math.Pow(point.X - 100, 2) + Math.Pow(point.Y - 100, 2)) <= 50 + 1e-8));
        Assert.IsTrue(scene.Segments.SelectMany(item => new[] { item.FromPixel, item.ToPixel })
            .Any(point => Math.Abs(Math.Sqrt(Math.Pow(point.X - 100, 2) + Math.Pow(point.Y - 100, 2)) - 50) < 1e-6));
    }

    [TestMethod]
    public async Task BuildAsync_ProjectsRequestedSolarSystemBodiesOutsideCatalogLimit()
    {
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = new(new EquatorialPoint(2.530301, 89.264109), -2.5)
        }, "test-planets-v1");
        var request = CreateRequest(maximumResults: 1);
        request = new VisibleSceneRequest(
            request.Utc, request.Observer, request.Projection, request.CatalogQuery, request.CatalogMetadata,
            request.Refraction, request.HorizonPolicy, request.ProjectionVersion, request.AlgorithmVersion,
            solarSystemBodies: [SolarSystemBody.Jupiter, SolarSystemBody.Jupiter]);
        var catalog = new InMemoryCelestialCatalog([
            new("polaris", "Polaris", 2.530301, 89.264109, 1.98)
        ]);

        var scene = await new VisibleSceneBuilder(catalog, planetEphemeris: ephemeris)
            .BuildAsync(request).ConfigureAwait(false);

        Assert.HasCount(2, scene.Objects);
        var jupiter = scene.Objects.Single(item => item.Kind == CelestialObjectKind.SolarSystemBody);
        Assert.AreEqual("solar-system:Jupiter", jupiter.Id);
        Assert.AreEqual(-2.5, jupiter.Magnitude);
        Assert.HasCount(1, scene.Request.SolarSystemBodies);
    }

    private static VisibleSceneRequest CreateRequest(int maximumResults)
        => new(
            PrimaryUtc,
            new ObserverLocation(35.347, -113.878, 0),
            new EquidistantProjectionContext(
                968, 608, 100, 20, 35.51, 359.25, WidthPixels: 1936, HeightPixels: 1216),
            new CatalogQuery(6.5, maximumResults),
            new CatalogMetadata(
                "HYG test subset",
                "4.2",
                new Uri("https://astronexus.com/projects/hyg"),
                "fixture",
                "CC BY-SA 4.0",
                "test-v1"),
            new RefractionOptions(false));
}
