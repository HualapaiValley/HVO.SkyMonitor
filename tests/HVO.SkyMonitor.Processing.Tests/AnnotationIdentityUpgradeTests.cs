using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// Annotation, scene-presentation, and measured-association output identities change exactly when the produced bytes
/// can change. Resolved footprints (#518) took a new identity only for footprint inputs; #526 star label gating moved
/// Annotation to projected-annotation-v4; #526 point-source planet gating and association moved Annotation to
/// projected-annotation-v5, ScenePresentationLayer to v15, SceneStylePresentation to v15, and associations to v2 so
/// retained v1/v4/v13/v14 outputs never collide with new bytes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class AnnotationIdentityUpgradeTests
{
    // Computed by development/v1 b13f0d0e (projected-annotation-v3) for the inputs below.
    private const string V3IdentitySha256 = "58B46537EBA195521B295A4FF032C10C9936E8CA3DB26EC9ABFE9E7DF71F10BA";

    // Computed under projected-annotation-v4 (star-only label policy) for the same inputs.
    private const string V4IdentitySha256 = "6D7AC733C270ACABF7162B38B2C1C24991135DEF9E0F98D3E9335220C2E9538F";

    // Computed under projected-annotation-v5 (#526 star + Mercury-Neptune point-source label policy) for the same inputs.
    private const string V5IdentitySha256 = "1EE2975241BE1C9B63757891C3C7299EF5152F16DD3309D70AD6DA841C7F3FF6";

    [TestMethod]
    public void FootprintFreeAnnotationTakesTheV5LabelPolicyIdentity()
    {
        var identity = BuiltInProcessingRecipes.CreateExecutionIdentity(
            BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new { }), Selector, Annotation);

        Assert.AreEqual("projected-annotation-v5", identity.Descriptor.ImplementationVersion);
        Assert.AreEqual(V5IdentitySha256, identity.IdentitySha256);
        Assert.AreNotEqual(V3IdentitySha256, identity.IdentitySha256);
        Assert.AreNotEqual(V4IdentitySha256, identity.IdentitySha256);
    }

    [TestMethod]
    public async Task PointSourcePlanetUpgradeDoesNotCollideWithV1AssociationOrSceneOrAnnotationIdentities()
    {
        // Identical raw + Jupiter-bearing scene inputs must produce distinct immutable output identities across the
        // v1 -> v2 association upgrade, v13 -> v15 scene presentation, v14 -> v15 scene style, and v4 -> v5 annotation.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var utc = new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
        var site = new ObserverLocation(35.347, -113.878, 520);
        var jupiter = ephemeris.GetPosition(SolarSystemBody.Jupiter, utc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(jupiter.EquatorialJ2000, utc), utc, site.LatitudeDegrees, site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, 96, 96, 500, 500, 192, 192,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var starAltAz = ProjectorFactory.Create(projection).Unproject(new(116, 110))!.Value;
        var starEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(starAltAz, utc, site.LatitudeDegrees, site.LongitudeDegrees), utc);
        var visible = await new VisibleSceneBuilder(
            new InMemoryCelestialCatalog([new("star:ref", "Ref", starEq.RightAscensionHours, starEq.DeclinationDegrees, 2.0)]),
            null, ephemeris).BuildAsync(new VisibleSceneRequest(utc, site, projection, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(192, 192),
            new ProjectedSceneSource(MeasuredStellarAssociationFixtures.CaptureId,
                MeasuredStellarAssociationFixtures.ArtifactId, MeasuredStellarAssociationFixtures.DescriptorIdentity),
            "calibration-v1", "perspective-v1");
        var raw = MeasuredStellarAssociationFixtures.RawArtifact(
            MeasuredStellarAssociationFixtures.Field(192, 192), CameraPixelFormat.Mono16, 192, 192);

        // 1. MeasuredStellarAssociationRecipe v2 vs v1 identity never collide.
        var assocRequest = MeasuredStellarAssociationFixtures.Request(raw, scene);
        var assocOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(assocRequest).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, assocOutcome.Status, assocOutcome.ReasonCode);
        var assocProduct = assocOutcome.Products.Single();
        Assert.AreEqual(MeasuredStellarAssociationsV1.CurrentSchemaVersion, assocProduct.SchemaVersion);
        Assert.AreEqual(MeasuredStellarAssociationRecipe.CurrentImplementationVersion, assocProduct.Recipe.Descriptor.ImplementationVersion);
        var legacyV1RecipeIdentity = ProcessingIdentity.CreateRecipeIdentity(
            HVO.SkyMonitor.AgentCore.RecipeIdentityDescriptor.Create(
                assocProduct.Recipe.Descriptor.Name, assocProduct.Recipe.Descriptor.SemanticVersion,
                MeasuredStellarAssociationRecipe.V1ImplementationVersion, assocProduct.Recipe.Descriptor.Options));
        var legacyV1OutputId = ProcessingIdentity.CreateOutputIdentity(
            assocProduct.Role, assocProduct.Variant, legacyV1RecipeIdentity.IdentitySha256, assocProduct.SourceArtifactIds);
        Assert.AreNotEqual(legacyV1OutputId, assocProduct.OutputIdentitySha256);

        // 2. ExposureAssociationProducts v2 vs v1 implementation version never collide.
        var v2ExposureRecipe = ProcessingIdentity.CreateRecipeIdentity(ExposureAssociationProducts.Definition,
            JsonSerializer.SerializeToElement(new { sample = 1 }));
        var v1ExposureRecipe = ProcessingIdentity.CreateRecipeIdentity(
            new ProcessingRecipeDefinition(ExposureAssociationProducts.RecipeName, "1.0.0",
                ExposureAssociationProducts.V1ImplementationVersion, ProcessingOperationKind.Analyzer),
            JsonSerializer.SerializeToElement(new { sample = 1 }));
        Assert.AreNotEqual(v1ExposureRecipe.IdentitySha256, v2ExposureRecipe.IdentitySha256);

        // 3. ScenePresentationLayer v15 vs v13 and SceneStylePresentation v15 vs v14 never collide.
        var sceneArtifact = new ProcessingArtifact(Guid.NewGuid(), FrameArtifactRole.Metadata, "scene",
            new string('A', 64), StructuredProcessingProductContracts.ProjectedSceneMediaType, null,
            ProjectedSceneJson.Serialize(scene), utc, TimeSpan.FromSeconds(1), raw.Compatibility)
        {
            CaptureId = raw.CaptureId,
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = scene.SchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256
        };
        var payloads = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene);
        var v15Layer = PresentationProcessingProducts.CreateLayerProduct(
            payloads.StarAnnotations, "scene-annotation-layer-v1", [sceneArtifact], PresentationLayerProducers.SceneProducerVersion);
        var v13Layer = PresentationProcessingProducts.CreateLayerProduct(
            payloads.StarAnnotations, "scene-annotation-layer-v1", [sceneArtifact],
            "projected-scene-presentation-v13-plain-circle-bounded-expected-labels");
        Assert.AreNotEqual(v13Layer.OutputIdentitySha256, v15Layer.OutputIdentitySha256);
        var v15StyleLayer = PresentationProcessingProducts.CreateLayerProduct(
            payloads.StarAnnotations, "scene-annotation-layer-v1", [sceneArtifact], SceneStylePresentation.ProducerVersion);
        var v14StyleLayer = PresentationProcessingProducts.CreateLayerProduct(
            payloads.StarAnnotations, "scene-annotation-layer-v1", [sceneArtifact], SceneStylePresentation.PreviousProducerVersion);
        Assert.AreNotEqual(v14StyleLayer.OutputIdentitySha256, v15StyleLayer.OutputIdentitySha256);

        // 4. Historical v1 star-only association payload still parses, validates, and gates labels cleanly, while a
        //    v1 payload smuggling pointSourcePlanets or v2 projectionGeometryIdentitySha256 is rejected.
        var starOnlyScene = await MeasuredStellarAssociationFixtures.StarFieldAsync(("star:legacy", 0, 0, 1.5)).ConfigureAwait(false);
        var starOnlyV2 = MeasuredStellarAssociationFixtures.AllEligible(starOnlyScene);
        var v1Baseline = starOnlyV2 with
        {
            SchemaVersion = MeasuredStellarAssociationsV1.V1SchemaVersion,
            Scene = starOnlyV2.Scene with { ProjectionGeometryIdentitySha256 = null },
            Method = new(MeasuredStellarAssociator.V1AlgorithmVersion, new(), StellarLabelPolicy.V1Version, new())
        };
        var v1Bytes = MeasuredStellarAssociationJson.Serialize(v1Baseline);
        var v1Reparsed = MeasuredStellarAssociationJson.Parse(v1Bytes);
        Assert.IsTrue(v1Reparsed.Validation.IsValid, $"{v1Reparsed.Validation.ReasonCode}:{v1Reparsed.Validation.FieldPath}");
        Assert.AreEqual(MeasuredStellarAssociationsV1.V1SchemaVersion, v1Reparsed.Associations!.SchemaVersion);
        var gatedWithV1 = StellarLabelPolicy.Apply(ProjectedSceneAnnotation.CreateObjects(starOnlyScene, 6.5), v1Reparsed.Associations);
        Assert.IsTrue(gatedWithV1.Single(static o => o.Id == "star:legacy").DrawLabel);

        var v1WithGeometry = v1Baseline with { Scene = starOnlyV2.Scene };
        Assert.IsFalse(MeasuredStellarAssociationJson.Validate(v1WithGeometry).IsValid);

        var parsedCurrent = MeasuredStellarAssociationJson.Parse(assocProduct.Payload).Associations!;
        var v1WithPlanets = parsedCurrent with { SchemaVersion = MeasuredStellarAssociationsV1.V1SchemaVersion };
        Assert.IsFalse(MeasuredStellarAssociationJson.Validate(v1WithPlanets).IsValid);
    }

    [TestMethod]
    public void ResolvedFootprintInputsTakeANewIdentityAndAlgorithmVersion()
    {
        var footprint = Annotation with
        {
            Objects = [Annotation.Objects[0] with { FootprintParts = [new ResolvedFootprintPart(true,
                [new PixelPoint(30, 20), new PixelPoint(40, 30), new PixelPoint(30, 40)])] }]
        };
        var ellipse = Annotation with { ProjectionOverlay = Annotation.ProjectionOverlay! with { ImageCircleRadiusY = 40 } };

        Assert.AreEqual(AnnotationRenderer.PointAnnotationAlgorithmVersion,
            AnnotationRenderer.AlgorithmVersionFor(Annotation.Objects, Annotation.ProjectionOverlay));
        foreach (var changed in new[] { footprint, ellipse })
        {
            Assert.AreEqual(AnnotationRenderer.AlgorithmVersion,
                AnnotationRenderer.AlgorithmVersionFor(changed.Objects, changed.ProjectionOverlay));
            Assert.AreNotEqual(V5IdentitySha256, BuiltInProcessingRecipes.CreateExecutionIdentity(
                BuiltInProcessingRecipes.Annotation, JsonSerializer.SerializeToElement(new { }), Selector, changed)
                .IdentitySha256);
        }
    }

    private static readonly ProcessingInputSelector Selector =
        ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "preview", new string('B', 64));

    [TestMethod]
    public void OnlyDiagnosticNormalizationBindsTheNewFormatterVersion()
    {
        var off = BuiltInProcessingRecipes.NormalizeOptions(BuiltInProcessingRecipes.Annotation,
            JsonSerializer.SerializeToElement(new AnnotationRecipeOptions()));
        Assert.IsFalse(off.TryGetProperty("expectedPositionLabelFormatVersion", out _));
        var on = BuiltInProcessingRecipes.CreateExecutionIdentity(BuiltInProcessingRecipes.Annotation,
            JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(ExpectedPositionDiagnostics: true)), Selector, Annotation);
        var parameters = on.Descriptor.Options.GetProperty("parameters");
        Assert.AreEqual(AnnotationLabelFormatter.Version, parameters.GetProperty("expectedPositionLabelFormatVersion").GetString());
        var legacyParameters = parameters.EnumerateObject().Where(static item => item.Name != "expectedPositionLabelFormatVersion")
            .ToDictionary(static item => item.Name, static item => item.Value.Clone(), StringComparer.Ordinal);
        var legacyOptions = on.Descriptor.Options.EnumerateObject()
            .ToDictionary(static item => item.Name, static item => item.Value.Clone(), StringComparer.Ordinal);
        legacyOptions["parameters"] = JsonSerializer.SerializeToElement(legacyParameters);
        var legacy = ProcessingIdentity.CreateRecipeIdentity(HVO.SkyMonitor.AgentCore.RecipeIdentityDescriptor.Create(
            on.Descriptor.Name, on.Descriptor.SemanticVersion, on.Descriptor.ImplementationVersion,
            JsonSerializer.SerializeToElement(legacyOptions)));
        Assert.AreNotEqual(legacy.IdentitySha256, on.IdentitySha256, "changed diagnostic bytes cannot reuse the old identity");
        var forged = BuiltInProcessingRecipes.NormalizeOptions(BuiltInProcessingRecipes.Annotation,
            JsonSerializer.SerializeToElement(new AnnotationRecipeOptions(ExpectedPositionDiagnostics: true)
            { ExpectedPositionLabelFormatVersion = "caller-algorithm" }));
        Assert.AreEqual(AnnotationLabelFormatter.Version, forged.GetProperty("expectedPositionLabelFormatVersion").GetString());
    }

    private static readonly ProcessingAnnotationInput Annotation = new(
        [new ProjectedAnnotationObject("planet:moon", "Moon", new PixelPoint(35, 30)),
            new ProjectedAnnotationObject("star:1", "STAR", new PixelPoint(12.5, 8), DrawLabel: false)],
        [new ProjectedAnnotationSegment("ORI", new PixelPoint(0, 0), new PixelPoint(7, 7))],
        new PreviewTransform(0.5, 0.75, 3, -2),
        new ProjectedAnnotationOverlay(new PixelPoint(48, 48), 47, new PixelPoint(48, 1), new PixelPoint(1, 48),
            new PixelPoint(48, 95), new PixelPoint(95, 48)),
        new string('A', 64),
        new MetadataCornerOverlay(["HVO"], [], [], ["2025-01-15 08:00Z"]));
}
