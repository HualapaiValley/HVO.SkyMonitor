using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class DeepSkySceneV3ParityTests
{
    private static readonly DateTimeOffset Utc = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation HvoObserver = new(35.347, -113.878, 1000);
    private static readonly Guid CaptureId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly Guid ArtifactId = Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd");
    private static readonly string DescriptorHash = new('D', 64);
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig-v1", "north-up", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");

    private static readonly DeepSkySemantics OpenNgcSemantics = new(
        "OpenNGC",
        "v20260501",
        "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
        new Uri("https://github.com/mattiaverga/OpenNGC"),
        "CC BY-SA 4.0",
        "equatorial-j2000-icrs-aligned",
        "J2000.0",
        "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive",
        "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote",
        new CatalogSnapshotIdentity("openngc-fixture", "openngc-fixture-1", "5", "6", new string('E', 64)));

    [TestMethod]
    public async Task ProjectedSceneRecipeAcceptsV3WithDeepSkyOutlinesAndSolarFootprintsAndFailsClosedOnInvalidVersions()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 160, 120, 1200, 1200, 320, 240,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 180);
        var centerJ2000 = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(60, 180), Utc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees),
            Utc);
        var dso = new DeepSkyObject("NGC1976", "NGC 1976", "M42", "Cl+N",
            centerJ2000.RightAscensionHours, centerJ2000.DeclinationDegrees, "Ori",
            60, 45, 0, 4.0, 4.0, null, null, 42, null, null, "Great Orion Nebula");
        var outline = CreateSquareOutline("NGC1976", centerJ2000.RightAscensionHours, centerJ2000.DeclinationDegrees, 0.35);
        var catalog = new DeepSkyCatalog(OpenNgcSemantics, [dso],
            [new("NGC 1976", "NGC1976", DeepSkyAliasKinds.Designation), new("M42", "NGC1976", DeepSkyAliasKinds.Messier)],
            [], [outline]);

        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, new AstronomyEnginePlanetEphemeris())
            .BuildAsync(new VisibleSceneRequest(
                Utc, HvoObserver, projection, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/cat"), new string('A', 64), "CC0", "2"),
                projectionVersion: "perspective-v1",
                solarSystemBodies: [SolarSystemBody.Moon])).ConfigureAwait(false);

        // 1. V1 stays v1 byte-for-byte when no footprints/DSO are attached.
        var v1 = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(320, 240),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorHash), "cal-v1", "perspective-v1");
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, v1.SchemaVersion);
        Assert.IsNull(v1.DeepSky);
        Assert.IsNull(v1.ResolvedFootprints);

        // 2. V2 stays v2 when only solar-system footprints are attached.
        var withMoon = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Moon, Utc, new AltAzPoint(61, 181), 0.25, -12, 0.5, 90, 384_400)]);
        var v2 = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, withMoon,
            ProjectedSceneImageTransformV1.Identity(320, 240),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorHash), "cal-v1", "perspective-v1");
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, v2.SchemaVersion);
        Assert.IsNull(v2.DeepSky);
        Assert.HasCount(1, v2.ResolvedFootprints!);

        // 3. V3 combines solar footprints + deep-sky query footprints cleanly under transform (crop/bin/mirror/rotate).
        var withBoth = withMoon.WithCuratedDeepSky(catalog, new OpenNgcDeepSkyOptions(Enabled: true).ToSelection());
        var transform = new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            320, 240, 16, 16, 288, 208, 2, 2, true, false, ProjectedSceneQuarterRotation.Degrees90, 104, 144);
        var v3 = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, withBoth, transform,
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorHash), "cal-v1", "perspective-v1");

        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, v3.SchemaVersion);
        Assert.IsNotNull(v3.DeepSky);
        Assert.HasCount(1, v3.DeepSky.Objects);
        Assert.HasCount(2, v3.ResolvedFootprints!, "V3 should carry both Moon and M42 resolved footprints.");

        var payload = ProjectedSceneJson.Serialize(v3);
        var payloadChecksum = ProcessingIdentity.ComputePayloadSha256(payload);
        var executor = new ProcessingRecipeExecutor();
        var rawInput = CreateRawInput(104, 144);
        var recipeRequest = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ProjectedScene,
            JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
            ProcessingInputSelector.Raw(rawInput.Variant),
            [rawInput],
            "projected-scene-v3",
            AuxiliaryInputs: [new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: v3.SchemaVersion, IdentitySha256: v3.SceneIdentitySha256, Payload: payload)
                { ChecksumSha256 = payloadChecksum }]);

        var outcome = await executor.ExecuteAsync(recipeRequest).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"Outcome={outcome}");
        var product = outcome.Products.Single();
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, product.SchemaVersion);
        Assert.AreEqual(v3.SceneIdentitySha256, product.ContentIdentitySha256);
        CollectionAssert.AreEqual(payload, product.Payload.ToArray());

        // 4. PresentationDeepSkyLayerProducer + ProjectedSceneAnnotation work consistently on transformed v3.
        var layer = PresentationDeepSkyLayerProducer.Create(v3);
        Assert.HasCount(1, layer.Objects);
        Assert.IsTrue(layer.Payload.Segments.Count > 0);
        var scaledLayer = PresentationDeepSkyLayerProducer.Create(v3, previewTransform: new PreviewTransform(0.5, 0.5), outputWidth: 52, outputHeight: 72);
        Assert.AreEqual(layer.Payload.Segments.Count, scaledLayer.Payload.Segments.Count);
        var starAndSolarAnnotations = ProjectedSceneAnnotation.CreateObjects(v3, 6.5);
        Assert.IsTrue(starAndSolarAnnotations.Any(static a => a.Id == "solar-system:Moon"));
        Assert.IsFalse(starAndSolarAnnotations.Any(static a => a.Id.StartsWith("deep-sky:", StringComparison.Ordinal)));

        // 5. Fail-closed checks: unsupported schema version and v2 smuggling deepSky must fail cleanly.
        var unsupportedOutcome = await executor.ExecuteAsync(recipeRequest with
        {
            AuxiliaryInputs = [new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: "projected-scene-v99", IdentitySha256: v3.SceneIdentitySha256, Payload: payload)
                { ChecksumSha256 = payloadChecksum }]
        }).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, unsupportedOutcome.Status);

        var downgradedBytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(payload).Replace("\"projected-scene-v3\"", "\"projected-scene-v2\"", StringComparison.Ordinal));
        var downgradedParse = ProjectedSceneJson.Parse(downgradedBytes);
        Assert.IsFalse(downgradedParse.IsValid, "A v2 reader must reject payloads containing deepSky.");

        var downgradedOutcome = await executor.ExecuteAsync(recipeRequest with
        {
            AuxiliaryInputs = [new ProcessingAuxiliaryInput("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: "projected-scene-v2", IdentitySha256: v3.SceneIdentitySha256, Payload: downgradedBytes)
                { ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(downgradedBytes) }]
        }).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, downgradedOutcome.Status);
    }

    private static ProcessingArtifact CreateRawInput(int width, int height) => new(
        ArtifactId,
        FrameArtifactRole.Raw,
        "raw-v1",
        new string('A', 64),
        "application/x-hvo-packed-frame",
        new FrameLayoutDescriptor(
            width,
            height,
            width * 2,
            CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian,
            16,
            16,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            65535,
            width * height * 2),
        new byte[width * height * 2],
        Utc,
        TimeSpan.FromSeconds(1),
        Compatibility)
    {
        CaptureId = CaptureId,
        DescriptorIdentitySha256 = DescriptorHash
    };

    private static DeepSkyOutline CreateSquareOutline(string objectId, double raHours, double decDeg, double halfSizeDeg)
    {
        var cosDec = Math.Max(0.2, Math.Cos(decDeg * Math.PI / 180d));
        var dRaDeg = halfSizeDeg / cosDec;
        var raDeg = raHours * 15d;
        double WrapRa(double d) => ((d % 360d) + 360d) % 360d;
        var p0 = new DeepSkyOutlinePoint(WrapRa(raDeg - dRaDeg), decDeg - halfSizeDeg);
        return new DeepSkyOutline(objectId, DeepSkyOutline.WidestLevel,
        [
            new DeepSkyOutlineRing(
            [
                p0,
                new DeepSkyOutlinePoint(WrapRa(raDeg + dRaDeg), decDeg - halfSizeDeg),
                new DeepSkyOutlinePoint(WrapRa(raDeg + dRaDeg), decDeg + halfSizeDeg),
                new DeepSkyOutlinePoint(WrapRa(raDeg - dRaDeg), decDeg + halfSizeDeg),
                p0
            ])
        ]);
    }
}
