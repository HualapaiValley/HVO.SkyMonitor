using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>Final typed and raster consumers must retain explicit diagnostic semantics within their label budget.</summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ExpectedPositionLabelOutputTests
{
    private const int Width = 800, Height = 600;

    [TestMethod]
    [DataRow("Vega", 0, null)]
    [DataRow("Vega", 1, null)]
    [DataRow("Vega", 4, null)]
    [DataRow("Vega", 9, null)]
    [DataRow("Vega", 10, "(expected)")]
    [DataRow("Vega", 11, "(expected)")]
    [DataRow("Vega", 12, "V (expected)")]
    [DataRow("Vega", 24, "Vega (expected)")]
    [DataRow("ABCDEFGHIJKLMNOPQRSTUVWXYZABCD", 24, "ABCDEFGHIJKLM (expected)")]
    public async Task FinalTypedAndRasterDiagnosticsPreserveMarkerOrSuppressLabel(
        string catalogueName, int limit, string? expected)
    {
        var scene = await SceneAsync(catalogueName).ConfigureAwait(false);
        var typed = Labels(scene, limit, null, diagnostics: true);
        Assert.HasCount(expected is null ? 0 : 1, typed.TextBlocks);
        if (expected is not null)
        {
            Assert.AreEqual(expected, typed.TextBlocks.Single().Lines.Single());
            Assert.IsTrue(expected.Length <= limit);
            var roundTrip = PresentationLayerPayloadJson.Parse(PresentationLayerPayloadJson.Serialize(typed)).Payload!;
            Assert.AreEqual(expected, roundTrip.TextBlocks.Single().Lines.Single());
        }
        var original = ProjectedSceneAnnotation.CreateObjects(scene, 2.5).Select(static item => item with { DrawMark = false }).ToArray();
        var diagnostic = StellarLabelPolicy.Apply(original, null, expectedPositionDiagnostics: true);
        Assert.IsTrue(diagnostic.Single().ExpectedPosition);
        Assert.AreEqual(catalogueName, diagnostic.Single().DisplayName);
        var actual = Raster(diagnostic, limit);
        var literal = original.Single() with { DisplayName = expected ?? string.Empty, DrawLabel = expected is not null };
        CollectionAssert.AreEqual(Raster([literal], limit), actual,
            "independent literal marker raster, not a formatter-derived expected value");
        CollectionAssert.AreEqual(RasterRgb([literal], limit), RasterRgb(diagnostic, limit));
        if (limit > 0 && expected != catalogueName[..Math.Min(catalogueName.Length, limit)])
            Assert.IsFalse(Raster(original, limit).AsSpan().SequenceEqual(actual),
                "the old ordinary-looking prefix is an individually discriminated wrong raster");
        Assert.IsEmpty(Labels(scene, limit, null, diagnostics: false).TextBlocks);
        CollectionAssert.AreEqual(new byte[Width * Height], Raster(StellarLabelPolicy.Apply(original, null), limit));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(24)]
    public async Task MeasuredAndSolarLabelsKeepOrdinaryClippingAndDoNotInferDiagnosticsFromNames(int limit)
    {
        const string name = "Vega (expected)";
        var scene = await SceneAsync(name).ConfigureAwait(false);
        var measured = MeasuredStellarAssociationFixtures.AllEligible(scene);
        var expected = name[..Math.Min(name.Length, limit)];
        var typed = Labels(scene, limit, measured, diagnostics: true);
        Assert.HasCount(limit == 0 ? 0 : 1, typed.TextBlocks);
        if (limit > 0) Assert.AreEqual(expected, typed.TextBlocks.Single().Lines.Single());
        var original = ProjectedSceneAnnotation.CreateObjects(scene, 2.5).Select(static item => item with { DrawMark = false }).ToArray();
        var eligible = StellarLabelPolicy.Apply(original, measured, expectedPositionDiagnostics: true);
        Assert.IsFalse(eligible.Single().ExpectedPosition);
        CollectionAssert.AreEqual(Raster(original, limit), Raster(eligible, limit));
        var solar = original.Select(static item => item with { Id = "solar-system:Moon" }).ToArray();
        var gatedSolar = StellarLabelPolicy.Apply(solar, null, expectedPositionDiagnostics: true);
        Assert.IsFalse(gatedSolar.Single().ExpectedPosition);
        CollectionAssert.AreEqual(Raster(solar, limit), Raster(gatedSolar, limit));
        var solarScene = await SceneAsync("Sun", resolvedSunOnly: true).ConfigureAwait(false);
        var solarTyped = Labels(solarScene, limit, null, diagnostics: true);
        var solarOff = Labels(solarScene, limit, null, diagnostics: false);
        CollectionAssert.AreEqual(solarOff.TextBlocks.Select(static item => item.Lines.Single()).ToArray(),
            solarTyped.TextBlocks.Select(static item => item.Lines.Single()).ToArray());
        Assert.HasCount(limit == 0 ? 0 : 1, solarTyped.TextBlocks);
        if (limit > 0) Assert.AreEqual("Sun"[..Math.Min(3, limit)], solarTyped.TextBlocks.Single().Lines.Single());
    }

    [TestMethod]
    public void ExplicitDiagnosticFlagBindsIdentityWithoutChangingLegacyOrdinaryJson()
    {
        var ordinary = new ProjectedAnnotationObject("star:vega", "Vega", new PixelPoint(100, 200));
        var json = JsonSerializer.Serialize(ordinary, JsonSerializerOptions.Web);
        Assert.IsFalse(json.Contains("expectedPosition", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Deserialize<ProjectedAnnotationObject>(json, JsonSerializerOptions.Web)!.ExpectedPosition);
        var diagnostic = ordinary with { ExpectedPosition = true };
        var flagged = JsonSerializer.Serialize(diagnostic, JsonSerializerOptions.Web);
        Assert.IsTrue(JsonSerializer.Deserialize<ProjectedAnnotationObject>(flagged, JsonSerializerOptions.Web)!.ExpectedPosition);
        Assert.AreNotEqual(HVO.SkyMonitor.AgentCore.CaptureContractJson.ComputeCanonicalJsonSha256(
                JsonSerializer.SerializeToElement(ordinary)),
            HVO.SkyMonitor.AgentCore.CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(diagnostic)));
    }

    [TestMethod]
    [DataRow(755, 200)]
    [DataRow(100, 1)]
    [DataRow(100, 598)]
    public void RasterSuppressesAClippedDiagnosticWhileOrdinaryLabelsKeepTheirExistingPixels(int x, int y)
    {
        ProjectedAnnotationObject[] original = [new("star:vega", "Vega", new PixelPoint(x, y), DrawMark: false)];
        var diagnostic = StellarLabelPolicy.Apply(original, null, expectedPositionDiagnostics: true);
        CollectionAssert.AreEqual(new byte[Width * Height], Raster(diagnostic, 24));
        Assert.IsTrue(Raster(original, 24).AsSpan().IndexOfAnyExcept((byte)0) >= 0,
            "the ordinary label control still draws its historical clipped glyphs");
    }

    private static PresentationLayerPayloadV1 Labels(ProjectedSceneV1 scene, int limit,
        MeasuredStellarAssociationsV1? associations, bool diagnostics) => PresentationLayerProducers.FromProjectedSceneGroupsV2(
            scene, new PresentationAnnotationStyleV1(MaximumLabelCharacters: limit), includeMarkers: false,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false,
            associations: associations, expectedPositionDiagnostics: diagnostics).StarAnnotations;

    private static byte[] Raster(IReadOnlyList<ProjectedAnnotationObject> objects, int limit) =>
        AnnotationRenderer.AnnotateMono8(new byte[Width * Height], Width, Height, objects,
            new PreviewTransform(1, 1), new AnnotationOptions { MaximumLabelCharacters = limit }).Pixels.ToArray();

    private static byte[] RasterRgb(IReadOnlyList<ProjectedAnnotationObject> objects, int limit) =>
        AnnotationRenderer.AnnotateRgb24WithSegments(new byte[Width * Height * 3], Width, Height, objects, [],
            new PreviewTransform(1, 1), new AnnotationOptions { MaximumLabelCharacters = limit }).Pixels.ToArray();

    private static async Task<ProjectedSceneV1> SceneAsync(string name, bool resolvedSunOnly = false)
    {
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var position = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(90, 0), utc, 0, 0), utc);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(resolvedSunOnly ? [] :
            [new CelestialCatalogObject("star:vega", name, position.RightAscensionHours, position.DeclinationDegrees, 1)]),
            null, new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(utc,
            new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 100, 200, 400, 400, Width, Height,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1",
            solarSystemBodies: resolvedSunOnly ? [SolarSystemBody.Sun] : null)).ConfigureAwait(false);
        if (resolvedSunOnly)
            visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, utc,
                new AltAzPoint(90, 0), .25, 0, 1, 0, 149600000)]);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(MeasuredStellarAssociationFixtures.CaptureId,
                MeasuredStellarAssociationFixtures.ArtifactId, MeasuredStellarAssociationFixtures.DescriptorIdentity),
            "calibration-v1", visible.Request.ProjectionVersion);
    }
}
