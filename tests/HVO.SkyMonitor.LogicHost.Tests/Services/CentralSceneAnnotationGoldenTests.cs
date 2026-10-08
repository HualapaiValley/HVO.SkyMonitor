using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralSceneAnnotationGoldenTests
{
    private const string SceneId = "golden-scene";
    private static readonly DateTimeOffset Utc = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly ProcessingInputSelector Selector =
        ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "preview", new string('B', 64));

    // Computed by feature/518-resolved-sun-moon 6bbaf8ea, before central annotation read resolved footprints, for the
    // projected-scene-v1 artifact below. A v1 scene keeps these exact provenance, identity and rendered bytes.
    private const string V1ProvenanceSha256 = "027FF72064484A7A6ADA9E885F74A6FA8B91CDFFC76F8AD762C88703DFE5FA4E";
    private const string V1IdentitySha256 = "B0A0AAA40F21429A53ECF60BB7655B6CD58D3692DF0133ED8FCAE5999FFB5726";
    private const string V1RenderSha256 = "5D91A7B5DDC7135E6E7300B4B3A9DF8CFACB3A7CFF3963C91034620B3E2296B4";

    // Computed by the #518 r0 F1 correction, which binds the resolved outline into the central projected-scene-v2
    // annotation; the point-mark mapping of the same scene has a different identity, asserted below.
    private const string V2ProvenanceSha256 = "8766346CA743E5FCEDE734F62D1288884A79B6A3A540EF6E24072414AA6A3333";
    private const string V2IdentitySha256 = "A16DE0635BAF3BE29DBB7CDCE772A57BE34A895C6372A861CD353F42D9DA67DA";
    private const string V2RenderSha256 = "433F4B78F60CE243B468B63ECF0B1A10F8AF265CE6CBE70D04BC129C884330D6";

    [TestMethod]
    public async Task ProjectedSceneV1CentralAnnotationKeepsItsReleasedBytesAndIdentity()
    {
        var scene = await SceneAsync(resolvedSun: false).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, scene.SchemaVersion);
        Assert.IsNotEmpty(scene.Segments);

        var annotation = CentralProjectedSceneResolver.CreateAnnotation(scene, SceneId);

        Assert.IsTrue(annotation.Objects.All(static item => item.FootprintParts is null));
        Assert.AreEqual(V1ProvenanceSha256, annotation.ProvenanceSha256);
        Assert.AreEqual(V1IdentitySha256, Identity(annotation));
        Assert.AreEqual(V1RenderSha256, Render(annotation));
    }

    [TestMethod]
    public async Task ProjectedSceneV2CentralAnnotationBindsTheResolvedOutlineAndMatchesTheEdgeMapping()
    {
        var scene = await SceneAsync(resolvedSun: true).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, scene.SchemaVersion);
        var footprint = scene.ResolvedFootprints!.Single();

        var annotation = CentralProjectedSceneResolver.CreateAnnotation(scene, SceneId);

        // Regression for #518 r0 F1: the central artifact drew the Sun as a point mark from the same scene the edge
        // outlined. Both hosts now take their objects from the one shared mapping.
        CollectionAssert.AreEqual(ProjectedSceneAnnotation.CreateObjects(scene, 2.5).ToArray(), annotation.Objects.ToArray());
        Assert.AreSame(footprint.Parts, annotation.Objects.Single(static item => item.Id == "solar-system:Sun").FootprintParts);
        var pointMarks = CentralDerivativeJobExecutor.CreateAnnotation(SceneId,
            scene.Objects.Select(static item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            scene.Segments.Select(static item => new ProjectedSegmentProvenance(item.ConstellationId, item.FromObjectId,
                item.ToObjectId, item.FromPixel.X, item.FromPixel.Y, item.ToPixel.X, item.ToPixel.Y, item.PartIndex)).ToArray());
        Assert.AreNotEqual(pointMarks.ProvenanceSha256, annotation.ProvenanceSha256);
        Assert.AreNotEqual(Identity(pointMarks), Identity(annotation));
        Assert.AreNotEqual(Render(pointMarks), Render(annotation));
        Assert.AreEqual(V2ProvenanceSha256, annotation.ProvenanceSha256);
        Assert.AreEqual(V2IdentitySha256, Identity(annotation));
        Assert.AreEqual(V2RenderSha256, Render(annotation));
    }

    [TestMethod]
    public async Task FootprintWithoutACentreObjectIsAnnotatedCentrallyAsAtTheEdge()
    {
        var scene = await SceneAsync(resolvedSun: true, sunAltitude: 62.3, sunRadius: .5).ConfigureAwait(false);
        Assert.IsFalse(scene.Objects.Any(static item => item.Id == "solar-system:Sun"));
        var footprint = scene.ResolvedFootprints!.Single();

        var annotation = CentralProjectedSceneResolver.CreateAnnotation(scene, SceneId);

        var sun = annotation.Objects.Single(static item => item.Id == "solar-system:Sun");
        Assert.AreEqual(ProjectedSceneAnnotation.FootprintAnchor(footprint, scene.ImageTransform), sun.Pixel);
        Assert.IsTrue(sun is { DrawMark: true, DrawLabel: true });
        CollectionAssert.AreEqual(ProjectedSceneAnnotation.CreateObjects(scene, 2.5).ToArray(), annotation.Objects.ToArray());
    }

    private static string Identity(ProcessingAnnotationInput annotation) => BuiltInProcessingRecipes.CreateExecutionIdentity(
        BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new { }), Selector, annotation).IdentitySha256;

    private static string Render(ProcessingAnnotationInput annotation) => Convert.ToHexString(SHA256.HashData(
        AnnotationRenderer.AnnotateMono8WithSegments(new byte[400 * 300], 400, 300, annotation.Objects, annotation.Segments,
            annotation.Transform, new AnnotationOptions(), annotation.ProjectionOverlay).Pixels.Span));

    private static async Task<ProjectedSceneV1> SceneAsync(bool resolvedSun, double sunAltitude = 60, double sunRadius = .25)
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 200, 150, 4000, 4000, 400, 300,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 0);
        var catalog = new InMemoryCelestialCatalog(new (string Id, string Name, AltAzPoint At, double Magnitude, string? Hip)[]
        {
            ("from", "FROM", new AltAzPoint(60.8, .4), 1, "1"),
            ("to", "TO", new AltAzPoint(59.4, -.6), 2, "2"),
            ("faint", "FAINT", new AltAzPoint(59.6, .9), 4, null),
            ("anonymous", "anonymous", new AltAzPoint(60.3, -1.2), 1, null)
        }.Select(static star =>
        {
            var j2000 = EquatorialPrecession.PrecessToJ2000(
                CoordinateTransforms.HorizontalToEquatorial(star.At, Utc, 0, 0), Utc);
            return new CelestialCatalogObject(star.Id, star.Name, j2000.RightAscensionHours, j2000.DeclinationDegrees,
                star.Magnitude, HipparcosId: star.Hip);
        }));
        var visible = await new VisibleSceneBuilder(catalog,
            new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "2")],
                new ConstellationTopologyMetadata("fixture", "1", new Uri("https://example.test/constellations"),
                    new string('E', 64), "test", "v1")),
            new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(Utc,
            new ObserverLocation(0, 0, 0), projection, new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1", constellationIds: ["TST"],
            solarSystemBodies: resolvedSun ? [SolarSystemBody.Sun] : null)).ConfigureAwait(false);
        if (resolvedSun)
            visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, Utc,
                new AltAzPoint(sunAltitude, .3), sunRadius, 0, 1, 0, 149600000)]);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(400, 300),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);
    }
}
