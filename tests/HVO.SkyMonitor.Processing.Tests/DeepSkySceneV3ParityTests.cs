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
        var outlineOnlyCenter = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(59.2, 179.0), Utc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees),
            Utc);
        var outlineOnlyDso = new DeepSkyObject("IC0434-OUTLINE", "IC 434", "IC 434", "Neb",
            outlineOnlyCenter.RightAscensionHours, outlineOnlyCenter.DeclinationDegrees, "Ori",
            null, null, null, null, null, null, null, null, null, null, "Outline-only nebula");
        var outline = CreateSquareOutline("NGC1976", centerJ2000.RightAscensionHours, centerJ2000.DeclinationDegrees, 0.35);
        var outlineOnlyContour = CreateSquareOutline("IC0434-OUTLINE", outlineOnlyCenter.RightAscensionHours, outlineOnlyCenter.DeclinationDegrees, 0.25);
        var catalog = new DeepSkyCatalog(OpenNgcSemantics, [dso, outlineOnlyDso],
            [
                new("NGC 1976", "NGC1976", DeepSkyAliasKinds.Designation),
                new("M42", "NGC1976", DeepSkyAliasKinds.Messier),
                new("IC 434", "IC0434-OUTLINE", DeepSkyAliasKinds.Designation)
            ],
            [], [outline, outlineOnlyContour]);

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

        // 3. V3 combines solar footprints + deep-sky sourced OpenNGC contours (including an outline-only source
        //    with null axes!) cleanly under transform (crop/bin/mirror/rotate).
        var withBoth = withMoon.WithCuratedDeepSky(catalog,
            new OpenNgcDeepSkyOptions(Enabled: true, MaximumOutlines: 16, PreferredOutlineLevel: DeepSkyOutline.WidestLevel).ToSelection());
        var transform = new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            320, 240, 16, 16, 288, 208, 2, 2, true, false, ProjectedSceneQuarterRotation.Degrees90, 104, 144);
        var v3 = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, withBoth, transform,
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorHash), "cal-v1", "perspective-v1");

        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, v3.SchemaVersion);
        Assert.IsNotNull(v3.DeepSky);
        Assert.HasCount(2, v3.DeepSky.Objects);
        Assert.HasCount(2, v3.DeepSky.Outlines, "V3 must retain sourced OpenNGC contours for both M42 and the outline-only source.");
        Assert.HasCount(1, v3.ResolvedFootprints!, "Solar footprint stays in ResolvedFootprints while outline-represented DSOs use DeepSky.Outlines.");

        var m42SceneObj = v3.DeepSky.Objects.Single(static o => o.CatalogObjectId == "NGC1976");
        var outlineOnlySceneObj = v3.DeepSky.Objects.Single(static o => o.CatalogObjectId == "IC0434-OUTLINE");
        Assert.AreEqual(DeepSkyRepresentation.Outline, m42SceneObj.Representation);
        Assert.AreEqual(DeepSkyOutline.WidestLevel, m42SceneObj.OutlineLevel);
        Assert.AreEqual(DeepSkyRepresentation.Outline, outlineOnlySceneObj.Representation);
        Assert.AreEqual(DeepSkyOutline.WidestLevel, outlineOnlySceneObj.OutlineLevel);

        // Verify the transformed square OpenNGC contour vertices match independently projected & transformed sky corners!
        var m42Outline = v3.DeepSky.Outlines.Single(static o => o.Id == ProjectedDeepSkyObject.SceneId("NGC1976"));
        Assert.IsTrue(m42Outline.Parts.Count > 0);
        var projector = ProjectorFactory.Create(projection);
        foreach (var corner in outline.Rings[0].Points.Take(4))
        {
            var geom = CoordinateTransforms.EquatorialToHorizontal(
                EquatorialPrecession.PrecessJ2000(new EquatorialPoint(corner.RightAscensionDegrees / 15d, corner.DeclinationDegrees), Utc),
                Utc, HvoObserver.LatitudeDegrees, HvoObserver.LongitudeDegrees);
            var nativeCorner = projector.Project(geom)!.Value;
            var expectedCorner = TransformPoint(transform, nativeCorner);
            var minDist = m42Outline.Parts
                .SelectMany(static p => p.Points)
                .Min(pt => double.Hypot(pt.X - expectedCorner.X, pt.Y - expectedCorner.Y));
            Assert.IsTrue(minDist <= 1e-6,
                $"Transformed OpenNGC square vertex must agree with independently transformed sky corner; minDist={minDist:E3} px.");
        }

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

        // 4. PresentationDeepSkyLayerProducer draws both sourced contours as Outline AND real PreviewTransform matches within 1e-8 px!
        var layer = PresentationDeepSkyLayerProducer.Create(v3);
        Assert.HasCount(2, layer.Objects);
        Assert.IsTrue(layer.Objects.All(static o => o.Drawing == PresentationDeepSkyDrawing.Outline));
        Assert.IsTrue(layer.Payload.Segments.Count >= 8, "Both square outlines must emit presentation line segments.");

        var previewTransform = new PreviewTransform(0.5, 0.75);
        var scaledLayer = PresentationDeepSkyLayerProducer.Create(v3, previewTransform: previewTransform, outputWidth: 52, outputHeight: 108);
        Assert.AreEqual(layer.Payload.Segments.Count, scaledLayer.Payload.Segments.Count);

        // Verify exact <= 1e-8 px PreviewTransform.Apply parity against raw outline vertices (and exact 1e-3 px SVG rounding parity on segments).
        var expectedScaledSegments = new List<(PixelPoint From, PixelPoint To)>();
        foreach (var outlineEntry in v3.DeepSky.Outlines.OrderBy(o =>
            v3.DeepSky.Objects.OrderBy(static obj => obj, Comparer<ProjectedDeepSkyObject>.Create(ProjectedDeepSkyObject.ComparePriority))
                .ToList().FindIndex(obj => obj.Id == o.Id)))
        {
            foreach (var part in outlineEntry.Parts)
            {
                var transformedPts = part.Points.Select(pt =>
                {
                    var applied = previewTransform.Apply(pt);
                    Assert.IsTrue(double.Hypot(applied.X - pt.X * previewTransform.ScaleX, applied.Y - pt.Y * previewTransform.ScaleY) <= 1e-8,
                        "PreviewTransform.Apply must agree with independent scaling within 1e-8 px.");
                    return new PixelPoint(
                        Math.Round(applied.X, 3, MidpointRounding.AwayFromZero),
                        Math.Round(applied.Y, 3, MidpointRounding.AwayFromZero));
                }).ToArray();
                var count = part.Closed ? transformedPts.Length : transformedPts.Length - 1;
                for (var k = 0; k < count; k++)
                {
                    expectedScaledSegments.Add((transformedPts[k], transformedPts[(k + 1) % transformedPts.Length]));
                }
            }
        }
        Assert.AreEqual(expectedScaledSegments.Count, scaledLayer.Payload.Segments.Count);
        for (var i = 0; i < scaledLayer.Payload.Segments.Count; i++)
        {
            var dstSeg = scaledLayer.Payload.Segments[i];
            var (expFrom, expTo) = expectedScaledSegments[i];
            Assert.IsTrue(double.Hypot(dstSeg.From.X - expFrom.X, dstSeg.From.Y - expFrom.Y) <= 1e-8,
                $"Scaled presentation outline segment start diverged from PreviewTransform.Apply: actual={dstSeg.From}, expected={expFrom}.");
            Assert.IsTrue(double.Hypot(dstSeg.To.X - expTo.X, dstSeg.To.Y - expTo.Y) <= 1e-8,
                $"Scaled presentation outline segment end diverged from PreviewTransform.Apply: actual={dstSeg.To}, expected={expTo}.");
        }
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

    private static PixelPoint TransformPoint(ProjectedSceneImageTransformV1 transform, PixelPoint source)
    {
        var width = (double)transform.CropWidth / transform.BinX;
        var height = (double)transform.CropHeight / transform.BinY;
        var x = (source.X - transform.CropX) / transform.BinX;
        var y = (source.Y - transform.CropY) / transform.BinY;
        if (transform.HorizontalMirror) x = width - x;
        if (transform.VerticalMirror) y = height - y;
        return transform.Rotation switch
        {
            ProjectedSceneQuarterRotation.Degrees0 => new PixelPoint(x, y),
            ProjectedSceneQuarterRotation.Degrees90 => new PixelPoint(height - y, x),
            ProjectedSceneQuarterRotation.Degrees180 => new PixelPoint(width - x, height - y),
            ProjectedSceneQuarterRotation.Degrees270 => new PixelPoint(y, width - x),
            _ => throw new ArgumentOutOfRangeException(nameof(transform))
        };
    }

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
