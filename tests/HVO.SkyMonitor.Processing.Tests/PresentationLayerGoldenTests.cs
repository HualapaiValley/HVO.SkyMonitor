using System.Security.Cryptography;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationLayerGoldenTests
{
    // Captured from development/v1 b13f0d0e (projected-scene-presentation-v9) and moved once by #526
    // (projected-scene-presentation-v11), whose label policy suppresses every star label that has no measured
    // association. A scene without resolved footprints must keep producing these payload bytes.
    private const string ExpectedPayloadsSha256 = "0324F60974C96DDF871D156B92A0BB7EA7D6DBDD4360276F62A6058FDA378B4E";

    [TestMethod]
    public async Task FootprintFreeScenesProduceTheV9PayloadBytes()
    {
        const int width = 1936, height = 1216;
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("tl", "TOP LEFT", (siderealHours - 40d / 15 + 24) % 24, 30, 0),
            new CelestialCatalogObject("tr", "TOP RIGHT", (siderealHours + 40d / 15) % 24, 30, 0.1),
            new CelestialCatalogObject("bl", "BOTTOM LEFT", (siderealHours - 40d / 15 + 24) % 24, -30, 0.2),
            new CelestialCatalogObject("center", "CENTER", siderealHours, 0, 1),
            new CelestialCatalogObject("near", "NEAR", siderealHours + .02, .5, 1.5)
        ]), null, new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(utc,
            new ObserverLocation(0, 0, 0), new ProjectionContext(ProjectionModel.EquidistantFisheye,
                width / 2, height / 2, 568, 568, width, height, ProjectionAperture.Circular,
                ImageCircleRadiusPixels: 595.84, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1",
            solarSystemBodies: [SolarSystemBody.Sun, SolarSystemBody.Moon])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
                width, height, 0, 0, width, height, 2, 2, true, false, ProjectedSceneQuarterRotation.Degrees90,
                height / 2, width / 2),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);

        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene);
        var legacy = PresentationLayerProducers.FromProjectedSceneGroups(scene);
        var payloads = new[]
        {
            PresentationLayerProducers.FromProjectedScene(scene),
            legacy.AnnotationAndGeometry, legacy.Constellations,
            groups.StarAnnotations, groups.CardinalDirections, groups.ImageCircle, groups.Constellations
        };

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var payload in payloads) hash.AppendData(PresentationLayerPayloadJson.Serialize(payload));
        Assert.AreEqual(ExpectedPayloadsSha256, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
