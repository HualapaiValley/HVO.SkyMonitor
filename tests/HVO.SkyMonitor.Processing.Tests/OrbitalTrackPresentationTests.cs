using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OrbitalTrackPresentationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 19, 4, 0, 0, TimeSpan.Zero);
    private static readonly OrbitalSnapshot SyntheticSnapshot = CreateSyntheticSnapshot();

    [TestMethod]
    public void SourceIdentityBytesCanBeRetainedWithExactTracksThroughExistingLayerProductLineage()
    {
        var track = Track();
        var converted = OrbitalTrackPresentation.CreatePayloadAndSourceFacts(640, 480, [track]);
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(converted.SourceFactsJson));
        Assert.AreEqual(converted.Payload.SourceIdentitySha256, sourceHash);
        Assert.AreEqual(Payload(track).ContentIdentitySha256, converted.Payload.ContentIdentitySha256);
        using var facts = JsonDocument.Parse(converted.SourceFactsJson);
        Assert.AreEqual(OrbitalTrackPresentation.ProducerVersion, facts.RootElement.GetProperty("ProducerVersion").GetString());
        Assert.AreEqual(track.ProductSha256,
            facts.RootElement.GetProperty("Tracks")[0].GetProperty("ProductSha256").GetString());

        var compatibility = new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
        var trackArtifact = new ProcessingArtifact(Guid.NewGuid(), FrameArtifactRole.Metadata, "orbital-track", Sha('A'),
            "application/json", null, OrbitalPredictionJson.SerializeTrack(track), Epoch, TimeSpan.Zero, compatibility)
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = track.SchemaVersion,
            ContentIdentitySha256 = track.ProductSha256
        };
        var sourceArtifact = new ProcessingArtifact(Guid.NewGuid(), FrameArtifactRole.Metadata, "orbital-presentation-source", Sha('B'),
            "application/json", null, converted.SourceFactsJson, Epoch, TimeSpan.Zero, compatibility,
            SourceArtifactIds: [trackArtifact.ArtifactId])
        {
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = OrbitalTrackPresentation.ProducerVersion,
            ContentIdentitySha256 = sourceHash
        };
        var layer = PresentationProcessingProducts.CreateLayerProduct(converted.Payload, "satellites",
            [sourceArtifact, trackArtifact], OrbitalTrackPresentation.ProducerVersion);

        CollectionAssert.AreEqual(new[] { sourceArtifact.ArtifactId, trackArtifact.ArtifactId }, layer.SourceArtifactIds.ToArray());
        Assert.AreEqual(converted.Payload.ContentIdentitySha256, layer.ContentIdentitySha256);
        var parsed = PresentationLayerPayloadJson.Parse(layer.Payload);
        Assert.IsNotNull(parsed.Payload);
        Assert.AreEqual(converted.Payload.ContentIdentitySha256,
            parsed.Payload.ContentIdentitySha256);
    }

    [TestMethod]
    [DataRow("snapshot")]
    [DataRow("normalized-payload")]
    [DataRow("source")]
    [DataRow("selection-policy")]
    [DataRow("population")]
    [DataRow("observer")]
    [DataRow("exposure")]
    [DataRow("timing")]
    [DataRow("model")]
    [DataRow("rig")]
    [DataRow("readout")]
    [DataRow("calibration")]
    [DataRow("projection")]
    [DataRow("transform")]
    public void RejectsCanonicalProductsFromIncompatibleContexts(string scenario)
    {
        var a = Track();
        var b = Track("2");
        var changed = scenario switch
        {
            "snapshot" => b with { Lineage = b.Lineage with { SnapshotManifestSha256 = Sha('D') } },
            "normalized-payload" => b with { Lineage = b.Lineage with { NormalizedPayloadSha256 = Sha('D') } },
            "source" => b with { Lineage = b.Lineage with { SourceDataset = "celestrak:gp:stations" } },
            "selection-policy" => b with { Lineage = b.Lineage with { SelectionPolicySha256 = Sha('D') } },
            "population" => b with
            {
                Lineage = b.Lineage with { PopulationCoverage = b.Lineage.PopulationCoverage with { KnownExclusions = ["different synthetic population"] } }
            },
            "observer" => b with { Observer = new(35.2, -113.8, 1000) },
            "exposure" => ShiftExposure(b),
            "timing" => b with { Timing = b.Timing with { EvidenceReference = "different synthetic capture" } },
            "model" => b with { Model = b.Model with { Propagator = b.Model.Propagator with { Version = "2" } } },
            "rig" => b with { Geometry = b.Geometry with { RigIdentitySha256 = Sha('A') } },
            "readout" => b with { Geometry = b.Geometry with { ReadoutIdentitySha256 = Sha('A') } },
            "calibration" => b with { Geometry = b.Geometry with { CalibrationIdentitySha256 = Sha('A') } },
            "projection" => b with { Geometry = b.Geometry with { Projection = b.Geometry.Projection with { PrincipalPointX = 321 } } },
            _ => b with { Geometry = b.Geometry with { ImageTransform = b.Geometry.ImageTransform with { HorizontalMirror = true } } }
        };
        // Each input is individually canonical. Rejection must come from the consumer's common context check.
        changed = OrbitalPredictionJson.CreateTrack(changed);
        Assert.ThrowsExactly<ArgumentException>(() => Payload(a, changed));
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("pixel")]
    [DataRow("segment")]
    [DataRow("lineage")]
    public void RejectsForgedOrOutdatedProductHashes(string scenario)
    {
        var track = Track();
        var forged = scenario switch
        {
            "identity" => track with { ProductSha256 = Sha('A') },
            "pixel" => track with { Samples = [track.Samples[0] with { Pixel = new(101, 150) }, track.Samples[1]] },
            "segment" => track with { Segments = [new(0, 1, OrbitalClipReason.WindowBoundary, OrbitalClipReason.Sensor)] },
            _ => track with { Lineage = track.Lineage with { RecordSha256 = Sha('A') } }
        };
        Assert.ThrowsExactly<ArgumentException>(() => Payload(forged));
    }

    [TestMethod]
    public void RejectsWrongDimensionsDuplicatesAndUnrenderableOrUnboundedInput()
    {
        var track = Track();
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalTrackPresentation.CreatePayload(639, 480, [track]));
        Assert.ThrowsExactly<ArgumentException>(() => Payload(track, track));
        Assert.ThrowsExactly<ArgumentException>(() => Payload(track, Track("1", [new(101, 150), new(201, 150)])));
        Assert.ThrowsExactly<ArgumentException>(() => Payload());
        Assert.ThrowsExactly<ArgumentException>(() => Payload([.. Enumerable.Repeat(track, OrbitalTrackPresentation.MaximumTracks + 1)]));
        var tooMany = Enumerable.Repeat<PixelPoint?>(new(100, 100), OrbitalTrackPresentation.MaximumSamplesPerTrack + 1).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => Payload(Track(pixels: tooMany)));
        var tiny = Track(pixels: [new(1, 1)], width: 8, height: 8);
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalTrackPresentation.CreatePayload(8, 8, [tiny]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OrbitalTrackPresentation.CreatePayload(65_536, 65_536, [track]));
        var absent = track with { Samples = null! };
        Assert.ThrowsExactly<ArgumentException>(() => Payload(absent));
    }

    [TestMethod]
    public void RejectsExcessTotalSamplesPrimitivesAndRendererWorkWithoutTruncation()
    {
        var stationary = Enumerable.Repeat<PixelPoint?>(new(200, 200), 1024).ToArray();
        var overTotal = Enumerable.Range(1, 9).Select(index => Track(index.ToString(CultureInfo.InvariantCulture), stationary)).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => Payload(overTotal));
        var shortSteps = Enumerable.Range(0, 1024).Select(index => (PixelPoint?)new PixelPoint(200 + index % 2, 200)).ToArray();
        var overPrimitives = Enumerable.Range(1, 5).Select(index => Track(index.ToString(CultureInfo.InvariantCulture), shortSteps)).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => Payload(overPrimitives));
        var longSteps = Enumerable.Range(0, 1024).Select(index => (PixelPoint?)(index % 2 == 0 ? new PixelPoint(10, 10) : new PixelPoint(630, 470))).ToArray();
        var overWork = Enumerable.Range(1, 2).Select(index => Track(index.ToString(CultureInfo.InvariantCulture), longSteps)).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => Payload(overWork));
    }

    [TestMethod]
    public void DrawsOnlyConsecutiveDeclaredPixelsNeverAcrossGapsOrSegmentBoundaries()
    {
        PixelPoint?[] pixels = [new(10, 100), new(13, 100), null, new(30, 100), new(35, 100), new(40, 100), new(50, 100), new(60, 100)];
        OrbitalTrackSegment[] segments =
        [
            new(0, 2, OrbitalClipReason.WindowBoundary, OrbitalClipReason.ProjectionFailure),
            new(3, 3, OrbitalClipReason.ProjectionFailure, OrbitalClipReason.Sensor),
            new(6, 1, OrbitalClipReason.Aperture, OrbitalClipReason.Aperture)
        ];
        var payload = Payload(Track(pixels: pixels, segments: segments));
        CollectionAssert.AreEqual(new[] { (new PixelPoint(10, 100), new PixelPoint(13, 100)),
            (new PixelPoint(30, 100), new PixelPoint(35, 100)), (new PixelPoint(35, 100), new PixelPoint(40, 100)) },
            payload.Segments.Select(static s => (s.From, s.To)).ToArray());
        Assert.AreEqual(0d, payload.Segments[0].Stroke!.DashOffsetPixels);
        Assert.AreEqual(0d, payload.Segments[1].Stroke!.DashOffsetPixels);
        Assert.AreEqual(5d, payload.Segments[2].Stroke!.DashOffsetPixels);
        Assert.AreEqual(5, payload.Markers.Count);
        Assert.IsFalse(payload.Markers.Any(static m => m.Center.X == 60), "A nonnull pixel outside every declared segment is not an endpoint.");
        Assert.AreEqual(0, payload.Ellipses.Count, "Styling never invents a physical uncertainty band.");
        Assert.IsNull(payload.TileMask);
    }

    [TestMethod]
    [DataRow("null-inside-segment")]
    [DataRow("nonfinite")]
    [DataRow("off-raster")]
    public void RejectsInvalidPixelsRatherThanBridgingOrReclipping(string scenario)
    {
        var track = Track();
        PixelPoint? pixel = scenario switch
        {
            "null-inside-segment" => null,
            "nonfinite" => new(double.NaN, 150),
            _ => new(641, 150)
        };
        Assert.ThrowsExactly<ArgumentException>(() => Payload(track with { Samples = [track.Samples[0], track.Samples[1] with { Pixel = pixel }] }));
    }

    [TestMethod]
    public void SingleStationaryUnprojectedAndClippedTracksRetainTheirActualGeometry()
    {
        var point = Payload(Track(pixels: [new(100, 150)]));
        Assert.AreEqual(0, point.Segments.Count);
        Assert.AreEqual(new PixelPoint(100, 150), point.Markers.Single().Center);
        var stationary = Payload(Track(pixels: [new(100, 150), new(100, 150), new(100, 150)]));
        Assert.AreEqual(0, stationary.Segments.Count);
        Assert.AreEqual(1, stationary.Markers.Count);
        var unprojected = Payload(Track(pixels: [null, null], segments: []));
        Assert.AreEqual(0, unprojected.Segments.Count);
        Assert.AreEqual(0, unprojected.Markers.Count);
        Assert.AreEqual(1, unprojected.TextBlocks.Count);
        var clipped = Payload(Track(pixels: [new(0, 0), new(640, 480)],
            segments: [new(0, 2, OrbitalClipReason.Horizon, OrbitalClipReason.Sensor)]));
        Assert.AreEqual(new PixelPoint(0, 0), clipped.Segments.Single().From);
        Assert.AreEqual(new PixelPoint(640, 480), clipped.Segments.Single().To);
    }

    [TestMethod]
    public void PartialTracksRetainOnlyCoveredSamplesAndCannotLoseTheirVisibleQualification()
    {
        var track = Track(partial: true);
        track = OrbitalPredictionJson.CreateTrack(track with
        {
            Segments = [new(0, 2, OrbitalClipReason.WindowBoundary, OrbitalClipReason.SamplingBudget)],
            Sampling = track.Sampling with { AchievedChordErrorPixels = null },
            Uncertainty = track.Uncertainty with { Sampling = new(null, "pixels", "synthetic budget exhaustion") }
        });
        var payload = Payload(track);
        Assert.IsTrue(track.CoveredInterval.EndUtc < track.RequestedInterval.EndUtc);
        Assert.AreEqual(track.Samples[^1].Pixel, payload.Segments.Single().To);
        CollectionAssert.Contains(payload.TextBlocks[0].Lines.ToArray(), OrbitalTrackPresentation.PartialLegend);
        StringAssert.Contains(payload.TextBlocks[1].Lines[2], "PARTIAL: SampleBudget", StringComparison.Ordinal);
        var atEdge = Track(pixels: [new(640, 0)], partial: true);
        var omitted = Payload(atEdge);
        Assert.AreEqual(1, omitted.TextBlocks.Count, "Off-frame optional label is omitted.");
        CollectionAssert.Contains(omitted.TextBlocks[0].Lines.ToArray(), OrbitalTrackPresentation.PartialLegend);
    }

    [TestMethod]
    [DataRow(OrbitalProjectionCoordinates.NativeSensor)]
    [DataRow(OrbitalProjectionCoordinates.ReadoutView)]
    public void UsesAlreadyTransformedPixelsExactlyOnce(OrbitalProjectionCoordinates basis)
    {
        var track = Track(pixels: [new(10.25, 20.75), new(100.5, 80.125)], width: 240, height: 320);
        var geometry = track.Geometry with
        {
            Readout = new(800, 600, 80, 60, 640, 480, 2, 2, FrameBinningAlgorithm.DigitalAverageV1, null, null),
            ProjectionCoordinates = basis,
            Projection = basis == OrbitalProjectionCoordinates.NativeSensor
                ? new(ProjectionModel.Perspective, 400, 300, 200, 200, 800, 600, ProjectionAperture.Rectangular)
                : new(ProjectionModel.Perspective, 160, 120, 100, 100, 320, 240, ProjectionAperture.Rectangular),
            ImageTransform = basis == OrbitalProjectionCoordinates.NativeSensor
                ? new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 800, 600, 80, 60, 640, 480, 2, 2,
                    true, true, ProjectedSceneQuarterRotation.Degrees90, 240, 320)
                : new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 320, 240, 0, 0, 320, 240, 1, 1,
                    true, true, ProjectedSceneQuarterRotation.Degrees90, 240, 320)
        };
        track = OrbitalPredictionJson.CreateTrack(track with { Geometry = geometry });
        var payload = OrbitalTrackPresentation.CreatePayload(240, 320, [track]);
        Assert.AreEqual(track.Samples[0].Pixel, payload.Segments.Single().From);
        Assert.AreEqual(track.Samples[1].Pixel, payload.Segments.Single().To);
        Assert.AreEqual(240, payload.WidthPixels);
        Assert.AreEqual(320, payload.HeightPixels);
    }

    [TestMethod]
    public void InputOrderingNamesAndCanonicalRoundTripDeterminePayloadIdentity()
    {
        var a = Track("2");
        var b = Track("10", [new(300, 250), new(400, 250)]);
        var firstNames = new Dictionary<OrbitalObjectIdentity, string> { [a.Lineage.ObjectIdentity] = "Synthetic A", [b.Lineage.ObjectIdentity] = "Synthetic B" };
        var secondNames = new Dictionary<OrbitalObjectIdentity, string> { [b.Lineage.ObjectIdentity] = "Synthetic B", [a.Lineage.ObjectIdentity] = "Synthetic A" };
        var first = OrbitalTrackPresentation.CreatePayload(640, 480, [a, b], firstNames);
        var second = OrbitalTrackPresentation.CreatePayload(640, 480, [b, a], secondNames);
        var bytes = PresentationLayerPayloadJson.Serialize(first);
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(second));
        Assert.AreEqual(new PixelPoint(300, 250), first.Segments[0].From, "Canonical ordering uses ordinal catalog text, as the source contract does.");
        var parsed = PresentationLayerPayloadJson.Parse(bytes);
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(parsed.Payload!));
        Assert.AreEqual(first.ContentIdentitySha256, PresentationLayerPayloadJson.ComputeIdentity(first));
        var omittedNameTrack = Track(pixels: [new(640, 0)]);
        var without = Payload(omittedNameTrack);
        var with = OrbitalTrackPresentation.CreatePayload(640, 480, [omittedNameTrack],
            new Dictionary<OrbitalObjectIdentity, string> { [omittedNameTrack.Lineage.ObjectIdentity] = "Synthetic" });
        Assert.AreEqual(1, with.TextBlocks.Count);
        Assert.AreNotEqual(without.SourceIdentitySha256, with.SourceIdentitySha256);
        Assert.AreNotEqual(without.ContentIdentitySha256, with.ContentIdentitySha256);
    }

    [TestMethod]
    public void LabelPlacementUsesPinnedMetricsAndOmitsCrowdedOrOffFrameNames()
    {
        var tracks = Enumerable.Range(1, 32).Select(index => Track(index.ToString(CultureInfo.InvariantCulture))).ToArray();
        var crowded = Payload(tracks);
        Assert.AreEqual(2, crowded.TextBlocks.Count, "Only the first ordinal object's right-hand label fits this shared anchor.");
        StringAssert.StartsWith(crowded.TextBlocks[1].Lines[0], "Predicted NORAD 1", StringComparison.Ordinal);
        var bounds = crowded.TextBlocks.Select(block => TextBounds(block, 640, 480)).ToArray();
        foreach (var box in bounds) Assert.IsTrue(box.Left >= 0 && box.Top >= 0 && box.Right <= 640 && box.Bottom <= 480);
        Assert.IsFalse(bounds[0].IntersectsWith(bounds[1]));
        using var font = PresentationFont.Create(crowded.TextBlocks[1], 0);
        Assert.AreEqual("DejaVu Sans Mono", font.Typeface!.FamilyName);
        Assert.IsTrue(crowded.TextBlocks.All(static block => block.Lines.All(static line => line.Length <= PresentationLayerPayloadV1.MaximumLineCharacters)));
        var edge = Payload(Track(pixels: [new(640, 0)]));
        Assert.AreEqual(1, edge.TextBlocks.Count);
        var fallbackLeft = Payload(Track(pixels: [new(620, 200)]));
        Assert.AreEqual(2, fallbackLeft.TextBlocks.Count);
        Assert.IsTrue(TextBounds(fallbackLeft.TextBlocks[1], 640, 480).Right < 620);
    }

    [TestMethod]
    public void OptionalLabelsRespectTheirCountAndTextBoundsWithoutTruncatingNames()
    {
        var tracks = Enumerable.Range(1, 32).Select(index => Track(index.ToString(CultureInfo.InvariantCulture),
            [new(20 + (index - 1) % 4 * 800, 50 + (index - 1) / 4 * 300)], width: 3200, height: 3200)).ToArray();
        var payload = OrbitalTrackPresentation.CreatePayload(3200, 3200, tracks);
        Assert.AreEqual(OrbitalTrackPresentation.MaximumLabels + 1, payload.TextBlocks.Count);
        Assert.AreEqual(32, payload.Markers.Count, "Label omission never drops track geometry.");
        foreach (var block in payload.TextBlocks)
        {
            var bounds = TextBounds(block, 3200, 3200);
            Assert.IsTrue(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 3200 && bounds.Bottom <= 3200);
        }
        var t = Track();
        foreach (var invalid in new[] { "", "\nSynthetic", new string('x', OrbitalTrackPresentation.MaximumDisplayNameCharacters + 1), "Synthetic\u2028name" })
            Assert.ThrowsExactly<ArgumentException>(() => OrbitalTrackPresentation.CreatePayload(640, 480, [t],
                new Dictionary<OrbitalObjectIdentity, string> { [t.Lineage.ObjectIdentity] = invalid }));
        Assert.ThrowsExactly<ArgumentException>(() => OrbitalTrackPresentation.CreatePayload(640, 480, [t],
            new Dictionary<OrbitalObjectIdentity, string> { [new("norad", "2")] = "Absent object" }));
    }

    [TestMethod]
    public void LabelsDiscloseStaleElementsUnknownSourceValidityAndDiagnosticGeometry()
    {
        var track = Track();
        track = OrbitalPredictionJson.CreateTrack(track with
        {
            Lineage = track.Lineage with { EpochUtc = track.Lineage.EpochUtc.AddHours(-50), StartEpochAgeHours = 52, EndEpochAgeHours = 52 + 1d / 3600, Freshness = OrbitalElementFreshness.Stale }
        });
        var payload = Payload(track);
        StringAssert.Contains(payload.TextBlocks[1].Lines[1], "EPOCH AGE <= 52.1H | STALE | SOURCE UNKNOWN", StringComparison.Ordinal);
        StringAssert.Contains(payload.TextBlocks[1].Lines[2], "CONFIGURED | ZERO EOP", StringComparison.Ordinal);
        CollectionAssert.Contains(payload.TextBlocks[0].Lines.ToArray(), OrbitalTrackPresentation.UncertaintyLegend);
        Assert.IsTrue(track.Samples.All(static s => s.Illumination == OrbitalIllumination.Unknown));
        Assert.AreEqual(1, payload.Segments.Count, "Unknown shadow/brightness is not a physical visibility filter.");
    }

    [TestMethod]
    public void CallerMutationsCannotChangeTheReturnedPayload()
    {
        var canonical = Track();
        var samples = canonical.Samples.ToArray();
        var segments = canonical.Segments.ToArray();
        var names = new Dictionary<OrbitalObjectIdentity, string> { [canonical.Lineage.ObjectIdentity] = "Synthetic" };
        var payload = OrbitalTrackPresentation.CreatePayload(640, 480, [canonical with { Samples = samples, Segments = segments }], names);
        var bytes = PresentationLayerPayloadJson.Serialize(payload);
        samples[0] = samples[0] with { Pixel = new(400, 400) };
        segments[0] = segments[0] with { SampleCount = 1 };
        names[canonical.Lineage.ObjectIdentity] = "Changed";
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(payload));
    }

    [TestMethod]
    public void ExistingSvgAndRasterConsumersPreserveDashesAndIndependentLayerSelection()
    {
        var payload = Payload(Track(pixels: [new(10, 100), new(13, 100), new(30, 100)]));
        var compatibility = new PresentationCompatibilityDescriptor(640, 480, Sha('D'), Sha('E'));
        var source = new PresentationProductReference(Guid.Parse("11111111-1111-1111-1111-111111111111"), Sha('A'), "image/png", compatibility);
        PresentationLayerV1 Layer(string kind, bool enabled, int order) => LayeredPresentationJson.CreateLayer(kind,
            source, null, PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
            OrbitalTrackPresentation.ProducerVersion, order, PresentationBlendMode.Normal, 1_000_000, enabled, JsonSerializer.SerializeToElement(new { }));
        var predicted = Layer(OrbitalTrackPresentation.LayerKind, false, 1);
        var measured = Layer(TransientTrailPresentation.LayerKind, true, 0);
        var measuredPayload = PresentationLayerPayloadJson.Create(Sha('A'), 640, 480,
            markers: [new(new(600, 50), 0, new(115, 232, 157))]);
        var svg = GroupedSvgPresentationRenderer.Render(LayeredPresentationJson.CreateManifest(source, null, [measured, predicted]),
            [measuredPayload, payload], Sha('B'));
        var document = XDocument.Parse(Encoding.UTF8.GetString(svg.Svg.Span));
        XNamespace ns = "http://www.w3.org/2000/svg";
        var groups = document.Root!.Elements(ns + "g").ToArray();
        Assert.AreEqual(TransientTrailPresentation.LayerKind, groups[0].Attribute("data-layer-kind")!.Value);
        Assert.IsNull(groups[0].Attribute("display"));
        Assert.AreEqual("none", groups[1].Attribute("display")!.Value);
        Assert.AreEqual(OrbitalTrackPresentation.LayerKind, groups[1].Attribute("data-layer-kind")!.Value);
        var lines = groups[1].Elements(ns + "line").ToArray();
        Assert.AreEqual(2, lines.Length);
        Assert.AreEqual("6 4", lines[1].Attribute("stroke-dasharray")!.Value);
        Assert.AreEqual("3", lines[1].Attribute("stroke-dashoffset")!.Value);
        Assert.AreEqual("#FFA64D", lines[1].Attribute("stroke")!.Value);
        Assert.AreEqual(2, groups[1].Elements(ns + "circle").Count());

        var layout = new ImageLayout(640, 480, CameraPixelFormat.Rgb24, 640 * 3);
        var basePixels = new byte[layout.RequiredByteLength];
        var measuredLayer = new PresentationCompositorLayer(measuredPayload, true, PresentationRasterBlendMode.Normal, 1_000_000);
        var predictedLayer = new PresentationCompositorLayer(payload, true, PresentationRasterBlendMode.Normal, 1_000_000);
        var enabled = PresentationLayerCompositor.Composite(layout, basePixels, [measuredLayer, predictedLayer]);
        Assert.AreEqual(new PresentationColor(255, 166, 77), Rgb(enabled, 640, 14, 100));
        Assert.AreEqual(default, Rgb(enabled, 640, 17, 100), "A dash gap survives the three-pixel subdivision.");
        Assert.AreEqual(new PresentationColor(115, 232, 157), Rgb(enabled, 640, 600, 50));
        var disabled = PresentationLayerCompositor.Composite(layout, basePixels, [measuredLayer, predictedLayer with { Enabled = false }]);
        Assert.AreEqual(default, Rgb(disabled, 640, 14, 100));
        Assert.AreEqual(new PresentationColor(115, 232, 157), Rgb(disabled, 640, 600, 50));
        Assert.IsTrue(basePixels.All(static b => b == 0));
    }

    private static PresentationLayerPayloadV1 Payload(params OrbitalExposureTrackV1[] tracks) =>
        OrbitalTrackPresentation.CreatePayload(640, 480, tracks);

    private static SKRect TextBounds(PresentationTextBlockV1 block, int width, int height)
    {
        using var font = PresentationFont.Create(block, 0);
        var bounds = PresentationFont.BackplateBounds(block, width, height, font);
        bounds.Inflate(2, 2);
        return bounds;
    }

    private static PresentationColor Rgb(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 3;
        return new(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static OrbitalExposureTrackV1 ShiftExposure(OrbitalExposureTrackV1 t) => t with
    {
        RequestedInterval = new(t.RequestedInterval.StartUtc.AddSeconds(1), t.RequestedInterval.EndUtc.AddSeconds(1)),
        CoveredInterval = new(t.CoveredInterval.StartUtc.AddSeconds(1), t.CoveredInterval.EndUtc.AddSeconds(1)),
        Timing = t.Timing with
        {
            Exposure = new(t.RequestedInterval.StartUtc.AddSeconds(1), t.RequestedInterval.EndUtc.AddSeconds(1)),
            ReadoutCompletedUtc = t.Timing.ReadoutCompletedUtc!.Value.AddSeconds(1)
        },
        Samples = t.Samples.Select(static s => s with { Utc = s.Utc.AddSeconds(1) }).ToArray(),
        Lineage = t.Lineage with
        {
            StartEpochAgeHours = (t.RequestedInterval.StartUtc.AddSeconds(1) - t.Lineage.EpochUtc).TotalHours,
            EndEpochAgeHours = (t.RequestedInterval.EndUtc.AddSeconds(1) - t.Lineage.EpochUtc).TotalHours
        }
    };

    // These are explicit synthetic contract products, not numerical propagation/projection reference data.
    // Canonical factories establish valid content identities; no orbit engine, files or network are involved.
    private static OrbitalExposureTrackV1 Track(string catalog = "1", PixelPoint?[]? pixels = null,
        OrbitalTrackSegment[]? segments = null, int width = 640, int height = 480, bool partial = false)
    {
        pixels ??= [new(100, 150), new(200, 150)];
        var start = Epoch.AddHours(2);
        var covered = new OrbitalUtcInterval(start, start.AddSeconds(pixels.Length - 1));
        var requested = covered with { EndUtc = covered.EndUtc.AddSeconds(partial ? 1 : 0) };
        var record = SyntheticSnapshot.Records.Single(r => r.ObjectIdentity.CatalogNumber == catalog);
        var lineage = new OrbitalProductLineage(SyntheticSnapshot.Manifest.ManifestSha256, SyntheticSnapshot.Manifest.NormalizedPayloadSha256,
            record.RecordSha256, record.ObjectIdentity, record.SourceDataset, Sha('C'), Epoch,
            (requested.StartUtc - Epoch).TotalHours, (requested.EndUtc - Epoch).TotalHours, OrbitalElementFreshness.Fresh,
            OrbitalSourceValidity.Unknown, SyntheticSnapshot.Manifest.Coverage);
        var geometry = new OrbitalGeometryFacts(OrbitalGeometryBasis.ConfiguredRig, Sha('D'), Sha('E'),
            new(width, height, 0, 0, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null), Sha('F'), null, null,
            OrbitalProjectionCoordinates.NativeSensor,
            new(ProjectionModel.Perspective, width / 2d, height / 2d, 200, 200, width, height, ProjectionAperture.Rectangular),
            ProjectedSceneImageTransformV1.Identity(width, height), HorizonPolicy.GeometricHorizon);
        var timing = new OrbitalTimingFacts(requested, OrbitalTimingBasis.Estimated, requested.EndUtc.AddMilliseconds(20),
            OrbitalTimingBasis.Reported, "synthetic capture clock; not sensor evidence", AstrometricClockFacts.NotSupplied);
        var model = new OrbitalPredictionModelFacts(new("synthetic-not-propagated", "1", Sha('A')), "WGS72", "a", "synthetic-teme-v1",
            "utc-assumed-ut1", "zero-assumed", null, null, OrbitalEarthOrientationStatus.ApproximateZero,
            "linear-dut1-polar-no-extrapolation-v1", true, Sha('B'), new(Epoch.AddDays(-1), Epoch.AddDays(8)),
            "reject-crossing-v1", "wgs84-ellipsoidal-assumed", "none-geometric", null);
        return OrbitalPredictionJson.CreateTrack(new(OrbitalPredictionJson.TrackSchemaVersion, "", lineage, requested, covered,
            new(35.1, -113.8, 1000), geometry, timing, model,
            pixels.Select((pixel, index) => new OrbitalTrackSample(start.AddSeconds(index), new(50, index % 360), pixel,
                null, null, null, OrbitalIllumination.Unknown)).ToArray(),
            segments ?? [new(0, pixels.Length, OrbitalClipReason.WindowBoundary, OrbitalClipReason.WindowBoundary)],
            new(new(null, "degrees", "synthetic unknown orbit error"), new(null, "seconds", "clock not supplied"),
                new(null, "pixels", "configured rig only"), new(.1, "pixels", "synthetic declared chord bound")),
            new(1, .25, pixels.Length > 1 ? 1 : 0, .1),
            partial ? OrbitalExecutionStatus.Partial : OrbitalExecutionStatus.Complete,
            partial ? OrbitalPredictionReason.SampleBudget : OrbitalPredictionReason.None));
    }

    private static OrbitalSnapshot CreateSyntheticSnapshot()
    {
        var parser = new OrbitalSoftwareIdentity("synthetic-parser", "1", Sha('A'));
        var normalizer = new OrbitalSoftwareIdentity("synthetic-normalizer", "1", Sha('B'));
        var records = Enumerable.Range(1, 64).Select(index => OrbitalContract.CreateRecord(new("",
            new("norad", index.ToString(CultureInfo.InvariantCulture)), "celestrak:gp:active", null, "Synthetic", null, null, null,
            Epoch, null, null,
            new("EARTH", "TEME", "UTC", "SGP4", OrbitalContract.GpDerivativeConvention, OrbitalContract.CelesTrakGpProfile, parser, normalizer),
            new(15.5, .0002, 51.6, 125, 45, 270, .0001, .00002, -.0000003, 0, "U", 999, 50000))));
        return OrbitalSnapshot.Create(records,
            [new("celestrak:gp:active", "synthetic", "no fetched data", new("https://example.test/orbits"), Epoch.AddHours(1),
                null, null, null, Sha('C'), 1234, parser, normalizer, new("https://example.test/terms"), "Synthetic facts for consumer tests only.")],
            new("synthetic-test-population", OrbitalPopulationCompleteness.Partial, ["Not a real satellite population"]));
    }

    private static string Sha(char c) => new(c, 64);
}
