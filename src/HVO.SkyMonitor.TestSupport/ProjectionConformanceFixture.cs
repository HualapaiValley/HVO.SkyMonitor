using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.TestSupport;

/// <summary>Shared projection fixture used by host test projects to detect consumer drift.</summary>
public static class ProjectionConformanceFixture
{
    private static readonly DateTimeOffset Utc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Projects a stable cardinal direction through the shared perspective projector.</summary>
    public static PixelPoint ProjectReferenceDirection()
    {
        var projector = ProjectorFactory.CreatePerspective(new PerspectiveProjectionContext(100, 100, 100, 100, 200, 200));
        return projector.Project(new AltAzPoint(60, 90))
            ?? throw new InvalidOperationException("Reference direction should be visible.");
    }

    /// <summary>Builds identical catalog-backed visible scenes for every supported optical model.</summary>
    public static async Task<IReadOnlyList<SceneConformanceResult>> BuildReferenceScenesAsync()
    {
        const double latitude = 35.347;
        const double longitude = -113.878;
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(Utc, longitude) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("center", "Center", rightAscension, latitude, 0, HipparcosId: "1"),
            new CelestialCatalogObject("edge", "Edge", rightAscension, 25, 1, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology([
            new ConstellationSegment("TST", "1", "2")
        ]);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = new(new EquatorialPoint(rightAscension, latitude), -2.5)
        }, "conformance-planets-v1");
        var metadata = new CatalogMetadata("conformance", "1", new Uri("https://example.test"), "fixture");
        var models = new[]
        {
            ProjectionModel.EquidistantFisheye,
            ProjectionModel.EquisolidFisheye,
            ProjectionModel.OrthographicFisheye,
            ProjectionModel.StereographicFisheye,
            ProjectionModel.Perspective
        };
        var results = new List<SceneConformanceResult>();
        foreach (var model in models)
        {
            var context = model == ProjectionModel.Perspective
                ? new ProjectionContext(model, 100, 100, 100, 100, 200, 200, ProjectionAperture.Rectangular)
                : new ProjectionContext(model, 100, 100, 100, 100, 200, 200, ProjectionAperture.Circular, 100);
            var request = new VisibleSceneRequest(
                Utc, new ObserverLocation(latitude, longitude, 0), context,
                new CatalogQuery(6.5, 10), metadata, constellationIds: ["TST"],
                solarSystemBodies: [SolarSystemBody.Jupiter]);
            var scene = await new VisibleSceneBuilder(catalog, topology, ephemeris)
                .BuildAsync(request).ConfigureAwait(false);
            results.Add(new SceneConformanceResult(model, scene.Objects, scene.Segments));
        }

        return results;
    }
}

/// <summary>One model's shared host conformance result.</summary>
public sealed record SceneConformanceResult(
    ProjectionModel Model,
    IReadOnlyList<ProjectedCelestialObject> Objects,
    IReadOnlyList<ProjectedConstellationSegment> Segments);
