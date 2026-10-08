// Authored by Codex gpt-6.1-sol/xhigh. Synthetic, Docker-free contract proofs; not campaign evidence.
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

using CloudFailure = HVO.SkyMonitor.CameraAgent.AcceptanceTests.StandaloneW6DockerAcceptanceTests.CloudContractFailure;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class W6CloudOverlayCorrespondenceTests
{
    private const int Size = 3552;
    private static readonly DateTimeOffset FixedUtc = new(2026, 1, 15, 8, 0, 20, TimeSpan.Zero);
    private static readonly Guid CaptureId = Guid.Parse("52600000-0000-0000-0000-000000000001");
    private static readonly ProcessingCompatibilityIdentity Compatibility = new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
    private static readonly FrameLayoutDescriptor Layout = new(Size, Size, Size * 3, CameraPixelFormat.Rgb24,
        FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, 255, Size * (long)Size * 3);
    private static readonly (int Column, int Row)[] DefaultTiles = [(6, 0), (7, 8), (0, 15), (15, 15)];
    // Accepted semantic reference color, deliberately supplied independently of producer defaults.
    private static readonly PresentationColor Cyan = new(57, 197, 207);
    private static readonly int[] ComparisonItems = [1, 2];
    private static readonly int[] ReversedComparisonItems = [2, 1];
    private static readonly string[] StringComparisonItems = ["1", "2"];

    [TestMethod]
    [DataRow("interior")]
    [DataRow("clipped")]
    [DataRow("neighbours")]
    public void ActualProducerAndFullMaterializationMeetIndependentContractOnNonuniformSource(string geometry)
    {
        (int Column, int Row)[] tiles = geometry switch
        {
            "interior" => [(4, 4), (7, 8), (12, 12)],
            "clipped" => [(0, 0), (15, 0), (0, 15), (15, 15)],
            "neighbours" => [(0, 0), (1, 0), (0, 1), (7, 8), (8, 8), (8, 9), (15, 15)],
            _ => throw new ArgumentOutOfRangeException(nameof(geometry))
        };
        if (geometry == "interior") AssertCompleteLayerComparisonControls();
        var fixture = Create(tiles, nonuniform: true, crossingLayer: true);
        var evidence = Check(fixture);
        Assert.AreEqual(tiles.Length, evidence.CloudyTileCount);
        Assert.IsGreaterThan(0, evidence.MatchedBorderPixelCount);
        Assert.AreEqual(0, evidence.UnexpectedBorderPixelCount);
        // Separate raster invocation proves the independent oracle, rather than just executor replay equality.
        var raster = MaskRaster(fixture);
        CheckRaster(fixture, raster);
        if (geometry == "neighbours") AssertNeighbourCompositingControls(fixture, raster);
    }

    [TestMethod]
    [DataRow("orange-producer")]
    [DataRow("inset-teal")]
    [DataRow("missing-rendered-mask")]
    [DataRow("shifted-aa-x")]
    [DataRow("shifted-aa-y")]
    [DataRow("changed-valid-registration")]
    [DataRow("extra-clear-tile-stroke")]
    [DataRow("absent-tile")]
    [DataRow("half-opacity-raster")]
    public void RasterOracleIndividuallyRejectsWrongColorGeometryCoverageAndCounts(string fault)
    {
        var fixture = Create();
        var raster = MaskRaster(fixture);
        CheckRaster(fixture, raster); // Healthy baseline must pass before the individual mutation.
        switch (fault)
        {
            case "orange-producer":
                raster = MaskRaster(Create(color: new(255, 64, 32)));
                break;
            case "inset-teal":
                raster = LegacyInset(fixture);
                break;
            case "missing-rendered-mask":
                raster = fixture.Base.Payload.ToArray();
                break;
            case "shifted-aa-x":
            case "shifted-aa-y":
                raster = ShiftObservedStroke(fixture.Base.Payload.Span, raster, fault == "shifted-aa-x" ? 1 : 0,
                    fault == "shifted-aa-y" ? 1 : 0);
                break;
            case "changed-valid-registration":
                fixture = Create([(7, 0), (7, 8), (0, 15), (15, 15)]);
                break;
            case "extra-clear-tile-stroke":
                raster = MaskRaster(Create([.. DefaultTiles, (3, 5)]));
                break;
            case "absent-tile":
                raster = MaskRaster(Create([(7, 8), (0, 15), (15, 15)]));
                break;
            case "half-opacity-raster":
                raster = MaskRaster(Create(opacity: 500_000));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
        // These faults reach the raster oracle with the expected valid cyan payload. A metadata mismatch or
        // stored-output replay mismatch cannot stand in for the color/paired-profile/no-extra rejection.
        var exception = Assert.ThrowsExactly<AssertFailedException>(() => CheckRaster(fixture, raster), fault);
        var allowedGuards = fault switch
        {
            "orange-producer" or "inset-teal" or "missing-rendered-mask" or "absent-tile" or "half-opacity-raster" =>
                new[] { CloudFailure.RasterCoverage },
            "shifted-aa-x" or "shifted-aa-y" => new[] { CloudFailure.RasterSupport, CloudFailure.RasterCoverage },
            "changed-valid-registration" or "extra-clear-tile-stroke" => new[] { CloudFailure.RasterSupport },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.IsTrue(exception.Data["W6.CloudGuard"] is CloudFailure guard && allowedGuards.Contains(guard),
            $"{fault}: rejection did not reach the intended independent raster guard.");
    }

    [TestMethod]
    [DataRow("missing-layer")]
    [DataRow("missing-payload-mask")]
    [DataRow("payload-mask-mismatch")]
    [DataRow("payload-source-mismatch")]
    [DataRow("assessment-identity-mismatch")]
    [DataRow("capture-identity-mismatch")]
    [DataRow("layer-content-identity-mismatch")]
    [DataRow("layer-lineage-mismatch")]
    [DataRow("wrong-color")]
    [DataRow("wrong-thickness")]
    [DataRow("wrong-opacity")]
    [DataRow("wrong-blend")]
    [DataRow("wrong-style")]
    [DataRow("wrong-z-order")]
    [DataRow("disabled-selection")]
    [DataRow("deselected-output")]
    [DataRow("output-lineage-mismatch")]
    [DataRow("output-identity-mismatch")]
    [DataRow("stored-output-mismatch")]
    [DataRow("fully-occluded-cloud")]
    public void RetainedStackIndividuallyRejectsPayloadIdentityStyleAndSelectionFaults(string fault)
    {
        var healthy = Create();
        Check(healthy); // A blanket early failure cannot count as an individual negative proof.
        var fixture = fault switch
        {
            "missing-layer" => Create(includeMask: false),
            "missing-payload-mask" => Create(payloadTransform: payload => Payload(payload, mask: null)),
            "payload-mask-mismatch" => Create(payloadTransform: payload => Payload(payload,
                mask: payload.TileMask! with { Bits = Assessment([(3, 5)]).Mask!.Bits })),
            "payload-source-mismatch" => Create(payloadTransform: payload => Payload(payload,
                mask: payload.TileMask, sourceIdentity: new string('D', 64))),
            "wrong-color" => Create(color: new(255, 64, 32)),
            "wrong-thickness" => Create(thickness: 2),
            "wrong-opacity" => Create(opacity: 500_000),
            "wrong-blend" => Create(blend: PresentationBlendMode.Lighten),
            "wrong-style" => Create(style: "different-style"),
            "wrong-z-order" => Create(zOrder: 31),
            "disabled-selection" => Create(enabled: false),
            "deselected-output" => Create(selectMask: false),
            "fully-occluded-cloud" => Create(occludingLayer: true),
            _ => healthy
        };
        fixture = fault switch
        {
            "assessment-identity-mismatch" => fixture with { Cloud = fixture.Cloud with { ContentIdentitySha256 = new string('D', 64) } },
            "capture-identity-mismatch" => fixture with { Cloud = fixture.Cloud with { CaptureId = Guid.Parse("52600000-0000-0000-0000-000000000099") } },
            "layer-content-identity-mismatch" => fixture with
            {
                Layers = fixture.Layers.Select(input =>
                input.Layer.LayerKind == "cloud-mask" ? input with { Product = input.Product with { ContentIdentitySha256 = new string('D', 64) } } : input).ToArray()
            },
            "layer-lineage-mismatch" => fixture with
            {
                Layers = fixture.Layers.Select(input =>
                input.Layer.LayerKind == "cloud-mask" ? input with { Product = input.Product with { SourceArtifactIds = [fixture.Base.ArtifactId] } } : input).ToArray()
            },
            "output-lineage-mismatch" => fixture with { Output = fixture.Output with { SourceArtifactIds = [fixture.Base.ArtifactId] } },
            "output-identity-mismatch" => fixture with { Output = fixture.Output with { ContentIdentitySha256 = new string('D', 64) } },
            "stored-output-mismatch" => fixture with { Output = fixture.Output with { Payload = ChangeByte(fixture.Output.Payload) } },
            _ => fixture
        };
        var exception = Assert.ThrowsExactly<AssertFailedException>(() => Check(fixture), fault);
        var expectedGuard = fault switch
        {
            "missing-layer" => CloudFailure.MaskLayer,
            "missing-payload-mask" => CloudFailure.RasterMaskPresent,
            "payload-mask-mismatch" => CloudFailure.RasterMaskBits,
            "payload-source-mismatch" => CloudFailure.PayloadSourceIdentity,
            "assessment-identity-mismatch" => CloudFailure.AssessmentIdentity,
            "capture-identity-mismatch" => CloudFailure.CaptureIdentity,
            "layer-content-identity-mismatch" => CloudFailure.LayerContentIdentity,
            "layer-lineage-mismatch" => CloudFailure.LayerLineage,
            "wrong-color" => CloudFailure.RasterColor,
            "wrong-thickness" => CloudFailure.RasterThickness,
            "wrong-opacity" => CloudFailure.Opacity,
            "wrong-blend" => CloudFailure.BlendMode,
            "wrong-style" => CloudFailure.StyleVersion,
            "wrong-z-order" => CloudFailure.ZOrder,
            "disabled-selection" => CloudFailure.Selection,
            "deselected-output" => CloudFailure.OutputArtifactIdentity,
            "output-lineage-mismatch" => CloudFailure.OutputLineage,
            "output-identity-mismatch" => CloudFailure.OutputContentIdentity,
            "stored-output-mismatch" => CloudFailure.StoredOutput,
            "fully-occluded-cloud" => CloudFailure.FullSelectionVisibility,
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.AreEqual(expectedGuard, exception.Data["W6.CloudGuard"],
            $"{fault}: a different earlier guard cannot count as the intended rejection.");
    }

    [TestMethod]
    [DataRow("base-reference")]
    [DataRow("layer-reference")]
    [DataRow("manifest-identity")]
    public void ExecutorBindingRejectsMismatchedRetainedReferences(string fault)
    {
        var fixture = Create();
        Check(fixture); // Healthy binding must reach and pass the entire contract before mutation.
        fixture = fault switch
        {
            "base-reference" => fixture with { Base = fixture.Base with { ContentIdentitySha256 = new string('D', 64) } },
            "layer-reference" => fixture with
            {
                Layers = fixture.Layers.Select(input =>
                input.Layer.LayerKind == "cloud-labels" ? input with { Product = input.Product with { ContentIdentitySha256 = new string('D', 64) } } : input).ToArray()
            },
            "manifest-identity" => fixture with { ManifestArtifact = fixture.ManifestArtifact with { ContentIdentitySha256 = new string('D', 64) } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var exception = Assert.ThrowsExactly<ArgumentException>(() => Check(fixture), fault);
        Assert.AreEqual(fault switch
        {
            "base-reference" => "reference",
            "layer-reference" => "layerProducts",
            "manifest-identity" => "manifestArtifact",
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        }, exception.ParamName);
        StringAssert.Contains(exception.Message, fault switch
        {
            "base-reference" => "Product reference",
            "layer-reference" => "Layer products",
            "manifest-identity" => "Manifest artifact",
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        }, StringComparison.Ordinal);
    }

    private static void AssertCompleteLayerComparisonControls()
    {
        var reference = new PresentationProductReference(CaptureId, new string('A', 64),
            PresentationLayerPayloadJson.MediaType,
            new(Size, Size, new string('B', 64), new string('C', 64)));
        var options = JsonSerializer.SerializeToElement(new { flag = true, nested = new { items = ComparisonItems, label = "v" } });
        var layer = LayeredPresentationJson.CreateLayer("comparison-control", reference, null,
            PresentationCoordinateSpace.ScenePixels, "comparison-renderer", "comparison-style", 10,
            PresentationBlendMode.Normal, 1_000_000, true, options);
        var second = LayeredPresentationJson.CreateLayer("comparison-second", reference, null,
            PresentationCoordinateSpace.ScenePixels, "comparison-renderer", "comparison-style", 11,
            PresentationBlendMode.Normal, 1_000_000, true, options);
        var parsed = LayeredPresentationJson.ParseLayer(LayeredPresentationJson.Serialize(layer));
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        var reparsed = parsed.Document!;
        // Independent JSON ownership is a positive value-equality control. Record/default Options equality
        // is observed, never asserted as the cause in advance of the authorized runtime counterfactual.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scenario = "independent-layer-roundtrip",
            recordEquals = layer.Equals(reparsed),
            defaultOptionsEquals = layer.Options.Equals(reparsed.Options),
            deepOptionsEquals = JsonElement.DeepEquals(layer.Options, reparsed.Options),
            expectedCanonicalUtf8Hex = Convert.ToHexString(LayeredPresentationJson.Serialize(layer)),
            actualCanonicalUtf8Hex = Convert.ToHexString(LayeredPresentationJson.Serialize(reparsed))
        }));
        StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer], [reparsed]);
        StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer, second], [reparsed, second]);

        (string Member, PresentationLayerV1 Changed)[] changes =
        [
            ("SchemaVersion", layer with { SchemaVersion = "different-schema" }),
            ("LayerIdentitySha256", layer with { LayerIdentitySha256 = new string('D', 64) }),
            ("LayerKind", layer with { LayerKind = "different-kind" }),
            ("SourceProduct.ArtifactId", layer with { SourceProduct = layer.SourceProduct with { ArtifactId = Guid.Empty } }),
            ("SourceProduct.ProductIdentitySha256", layer with { SourceProduct = layer.SourceProduct with { ProductIdentitySha256 = new string('D', 64) } }),
            ("SourceProduct.MediaType", layer with { SourceProduct = layer.SourceProduct with { MediaType = "different/media-type" } }),
            ("SourceProduct.Compatibility.WidthPixels", layer with { SourceProduct = layer.SourceProduct with { Compatibility = layer.SourceProduct.Compatibility with { WidthPixels = Size - 1 } } }),
            ("SourceProduct.Compatibility.HeightPixels", layer with { SourceProduct = layer.SourceProduct with { Compatibility = layer.SourceProduct.Compatibility with { HeightPixels = Size - 1 } } }),
            ("SourceProduct.Compatibility.LayoutIdentitySha256", layer with { SourceProduct = layer.SourceProduct with { Compatibility = layer.SourceProduct.Compatibility with { LayoutIdentitySha256 = new string('D', 64) } } }),
            ("SourceProduct.Compatibility.CoordinateIdentitySha256", layer with { SourceProduct = layer.SourceProduct with { Compatibility = layer.SourceProduct.Compatibility with { CoordinateIdentitySha256 = new string('D', 64) } } }),
            ("SceneIdentitySha256", layer with { SceneIdentitySha256 = new string('D', 64) }),
            ("CoordinateSpace", layer with { CoordinateSpace = PresentationCoordinateSpace.NormalizedImage }),
            ("RendererVersion", layer with { RendererVersion = "different-renderer" }),
            ("StyleVersion", layer with { StyleVersion = "different-style" }),
            ("ZOrder", layer with { ZOrder = 11 }),
            ("BlendMode", layer with { BlendMode = PresentationBlendMode.Lighten }),
            ("OpacityMillionths", layer with { OpacityMillionths = 500_000 }),
            ("EnabledByDefault", layer with { EnabledByDefault = false }),
            ("Options", layer with { Options = JsonSerializer.SerializeToElement(new { flag = false, nested = new { items = ComparisonItems, label = "v" } }) }),
        ];
        foreach (var change in changes)
        {
            var exception = Assert.ThrowsExactly<AssertFailedException>(() =>
                StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer], [change.Changed]));
            Assert.AreEqual(CloudFailure.LayerField, exception.Data["W6.CloudGuard"]);
            Assert.AreEqual(0, exception.Data["W6.CloudLayerIndex"]);
            Assert.AreEqual(change.Member, exception.Data["W6.CloudMember"]);
        }
        // Nested value/type/key/array-order changes must remain detectable, not just top-level Options text.
        var changedOptions = new[]
        {
            JsonSerializer.SerializeToElement(new { flag = true, nested = new { items = ReversedComparisonItems, label = "v" } }),
            JsonSerializer.SerializeToElement(new { flag = true, nested = new { items = ComparisonItems, label = "changed" } }),
            JsonSerializer.SerializeToElement(new { flag = true, nested = new { items = StringComparisonItems, label = "v" } }),
            JsonSerializer.SerializeToElement(new { Flag = true, nested = new { items = ComparisonItems, label = "v" } }),
            JsonSerializer.SerializeToElement(new { flag = true, extra = true, nested = new { items = ComparisonItems, label = "v" } })
        };
        foreach (var changed in changedOptions)
        {
            var exception = Assert.ThrowsExactly<AssertFailedException>(() =>
                StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer], [layer with { Options = changed }]));
            Assert.AreEqual(CloudFailure.LayerField, exception.Data["W6.CloudGuard"]);
            Assert.AreEqual("Options", exception.Data["W6.CloudMember"]);
        }
        var missing = Assert.ThrowsExactly<AssertFailedException>(() =>
            StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer, second], [layer]));
        Assert.AreEqual(CloudFailure.LayerCount, missing.Data["W6.CloudGuard"]);
        var swapped = Assert.ThrowsExactly<AssertFailedException>(() =>
            StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer, second], [second, layer]));
        Assert.AreEqual(CloudFailure.LayerField, swapped.Data["W6.CloudGuard"]);
        Assert.AreEqual(0, swapped.Data["W6.CloudLayerIndex"]);
        Assert.AreEqual("LayerIdentitySha256", swapped.Data["W6.CloudMember"]);
        var duplicate = Assert.ThrowsExactly<AssertFailedException>(() =>
            StandaloneW6DockerAcceptanceTests.AssertRetainedCloudLayerOrder([layer, second], [layer, layer]));
        Assert.AreEqual(CloudFailure.LayerField, duplicate.Data["W6.CloudGuard"]);
        Assert.AreEqual(1, duplicate.Data["W6.CloudLayerIndex"]);
        Assert.AreEqual("LayerIdentitySha256", duplicate.Data["W6.CloudMember"]);
    }

    private static void AssertNeighbourCompositingControls(Fixture fixture, byte[] healthyRaster)
    {
        // The healthy actual raster already passed independently. At this prescribed shared straight
        // edge there are two half profiles: missing a contribution, adding one, or flattening to full
        // geometric-union coverage must each fail RasterCoverage on the valid original payload.
        for (var fault = 0; fault < 3; fault++)
        {
            var wrongCoverage = fault switch
            {
                0 => 0.5,
                1 => 0.875,
                _ => 1d
            };
            var changed = healthyRaster.ToArray();
            for (var x = 221; x <= 222; x++)
            {
                var offset = (100 * Size + x) * 3;
                changed[offset] = Blend(fixture.Base.Payload.Span[offset], Cyan.Red, wrongCoverage);
                changed[offset + 1] = Blend(fixture.Base.Payload.Span[offset + 1], Cyan.Green, wrongCoverage);
                changed[offset + 2] = Blend(fixture.Base.Payload.Span[offset + 2], Cyan.Blue, wrongCoverage);
            }
            var exception = Assert.ThrowsExactly<AssertFailedException>(() => CheckRaster(fixture, changed),
                $"Shared-neighbour contribution control {fault}.");
            Assert.AreEqual(CloudFailure.RasterCoverage, exception.Data["W6.CloudGuard"]);
        }

        static byte Blend(byte source, byte color, double coverage) =>
            (byte)Math.Round(source * (1 - coverage) + color * coverage, MidpointRounding.AwayFromZero);
    }

    private static StandaloneW6DockerAcceptanceTests.CloudOverlayCorrespondenceEvidence Check(Fixture fixture) =>
        StandaloneW6DockerAcceptanceTests.AssertCloudOverlayCorrespondence(fixture.Base, fixture.Output,
            fixture.Cloud, fixture.ManifestArtifact, fixture.Layers);

    private static void CheckRaster(Fixture fixture, byte[] raster) =>
        StandaloneW6DockerAcceptanceTests.AssertCloudBorderRaster(fixture.Base.Payload.Span, raster, Layout,
            fixture.Assessment, PresentationLayerPayloadJson.Parse(fixture.Layers.Single(static input =>
                input.Layer.LayerKind == "cloud-mask").Product.Payload).Payload!);

    private static byte[] MaskRaster(Fixture fixture) => PresentationMaterializationExecutor.MaterializePacked(
        fixture.Base, fixture.ManifestArtifact, fixture.Manifest, fixture.Layers,
        [fixture.Layers.Single(static input => input.Layer.LayerKind == "cloud-mask").Layer.LayerIdentitySha256],
        "w6-annotated-preview").Payload.ToArray();

    private static Fixture Create((int Column, int Row)[]? tiles = null, bool nonuniform = false,
        bool crossingLayer = false, bool occludingLayer = false, bool includeMask = true, bool selectMask = true,
        PresentationColor? color = null, int thickness = 1, int opacity = 1_000_000,
        PresentationBlendMode blend = PresentationBlendMode.Normal, string style = "cloud-presentation-v4-payload-v3",
        int zOrder = 30, bool enabled = true, Func<PresentationLayerPayloadV1, PresentationLayerPayloadV1>? payloadTransform = null)
    {
        var bytes = new byte[Size * Size * 3];
        if (nonuniform)
        {
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                    for (var channel = 0; channel < 3; channel++)
                        bytes[(y * Size + x) * 3 + channel] = (byte)((x * 17 + y * 23 + channel * 47) % 256);
        }
        else Array.Fill(bytes, (byte)12);
        var baseArtifact = new ProcessingArtifact(Guid.Parse("52600000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Preview, "combined-preview", new string('0', 64), "application/x-hvo-packed-image", Layout,
            bytes, FixedUtc, TimeSpan.FromSeconds(1), Compatibility)
        { CaptureId = CaptureId };
        var assessment = Assessment(tiles ?? DefaultTiles);
        var cloud = baseArtifact with
        {
            ArtifactId = Guid.Parse("52600000-0000-0000-0000-000000000003"),
            Role = FrameArtifactRole.Metadata,
            Variant = "cloud-assessment-v1",
            MediaType = "application/json",
            Layout = null,
            Payload = CloudAssessmentJson.Serialize(assessment),
            ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = CloudAssessmentV1.CurrentSchemaVersion,
            ContentIdentitySha256 = assessment.AssessmentIdentitySha256
        };
        var reference = PresentationProcessingProducts.CreateReference(baseArtifact, new string('E', 64));
        // Observed input: actual producer, no replacement renderer or reference raster.
        var groups = PresentationLayerProducers.FromCloudAssessmentGroups(assessment, Size, Size,
            lineThickness: thickness, color: color);
        var mask = payloadTransform is null ? groups.Mask : payloadTransform(groups.Mask);
        var layers = new List<PresentationLayerProductInput>();
        if (crossingLayer)
            Add(PresentationLayerPayloadJson.Create(assessment.AssessmentIdentitySha256, Size, Size,
                segments: [new(new(0, 100), new(Size, 100), 3, new(160, 20, 150))]), "independent-scene-control", 20);
        if (includeMask) Add(mask, "cloud-mask", zOrder, blend, opacity, enabled, style);
        Add(groups.Labels, "cloud-labels", 40, PresentationBlendMode.Lighten);
        if (occludingLayer)
            Add(Payload(groups.Mask, groups.Mask.TileMask! with { LineThickness = 4, Color = new(255, 255, 255) }),
                "independent-occlusion-control", 50);
        var manifest = LayeredPresentationJson.CreateManifest(reference, null, layers.Select(static input => input.Layer));
        layers = layers.OrderBy(static input => input.Layer.ZOrder).ThenBy(static input => input.Layer.LayerIdentitySha256, StringComparer.Ordinal).ToList();
        var manifestArtifact = Artifact(PresentationProcessingProducts.CreateManifestProduct(reference, null, layers,
            "w6-overlay-manifest", baseArtifact));
        var selection = layers.Where(input => input.Layer.EnabledByDefault && (selectMask || input.Layer.LayerKind != "cloud-mask"))
            .Select(static input => input.Layer.LayerIdentitySha256);
        var output = Artifact(PresentationMaterializationExecutor.MaterializePacked(baseArtifact, manifestArtifact,
            manifest, layers, selection, "w6-annotated-preview"));
        return new(baseArtifact, output, cloud, manifestArtifact, manifest, layers.ToArray(), assessment);

        void Add(PresentationLayerPayloadV1 payload, string kind, int z, PresentationBlendMode mode = PresentationBlendMode.Normal,
            int layerOpacity = 1_000_000, bool layerEnabled = true, string layerStyle = "cloud-presentation-v4-payload-v3")
        {
            // A source mismatch control remains a valid canonical product. The committed helper must reject
            // its relationship to the independently retained assessment, rather than setup throwing early.
            var canonicalSource = cloud with { ContentIdentitySha256 = payload.SourceIdentitySha256 };
            var product = Artifact(PresentationProcessingProducts.CreateLayerProduct(payload, kind, [canonicalSource], layerStyle));
            var productReference = new PresentationProductReference(product.ArtifactId, payload.ContentIdentitySha256,
                product.MediaType, reference.Compatibility);
            var layer = LayeredPresentationJson.CreateLayer(kind, productReference, null, PresentationCoordinateSpace.ScenePixels,
                PresentationLayerCompositor.AlgorithmVersion, layerStyle, z, mode, layerOpacity, layerEnabled,
                JsonSerializer.SerializeToElement(new { }));
            layers.Add(new(layer, product));
        }
    }

    private static PresentationLayerPayloadV1 Payload(PresentationLayerPayloadV1 original, PresentationTileMaskV1? mask,
        string? sourceIdentity = null) => PresentationLayerPayloadJson.Create(sourceIdentity ?? original.SourceIdentitySha256,
        Size, Size, tileMask: mask);

    private static ProcessingArtifact Artifact(ProcessingProduct product) => new(
        ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
        product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload, FixedUtc,
        product.TotalIntegration, product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
    {
        ProductKind = product.Kind,
        SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256 ?? product.OutputIdentitySha256,
        CaptureId = CaptureId
    };

    private static CloudAssessmentV1 Assessment((int Column, int Row)[] tiles)
    {
        var regions = new List<CloudAssessmentRegionV1>();
        var bits = new byte[32];
        long total = 0, cloudySamples = 0;
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 16; column++)
            {
                var cloudy = tiles.Contains((column, row));
                var samples = (Size / 16) * (long)(Size / 16);
                total += samples;
                if (cloudy)
                {
                    cloudySamples += samples;
                    var index = row * 16 + column;
                    bits[index >> 3] |= (byte)(1 << (index & 7));
                }
                regions.Add(new(column, row, column * Size / 16, row * Size / 16, Size / 16, Size / 16,
                    samples, samples, 0, cloudy ? 500_000 : 1_000_000, cloudy));
            }
        var assessment = new CloudAssessmentV1(CloudAssessmentV1.CurrentSchemaVersion, CloudAssessmentStatus.Quantified,
            CloudAssessmentQuality.Degraded, [CloudAssessmentReasonCodes.EnvironmentMissing],
            checked((int)((cloudySamples * 1_000_000 + total / 2) / total)), 750_000,
            new(16, 16, 750_000, 256, tiles.Length, total, cloudySamples), regions,
            new(CloudAssessmentMaskV1.RowMajorLsbFirst, 16, 16, bits),
            new(Guid.Parse("52600000-0000-0000-0000-000000000004"), FrameArtifactRole.Calibrated, "synthetic-current", new string('A', 64)),
            new(Guid.Parse("52600000-0000-0000-0000-000000000005"), FrameArtifactRole.Calibrated, "synthetic-clear", new string('B', 64)),
            new(0, ushort.MaxValue, ushort.MaxValue, "calibration", "mask", "sensor", "processing"),
            new(CloudAssessmentEnvironmentV1.CurrentSchemaVersion, CaptureSolarRegime.Night, EnvironmentalObservationMatchStatus.Missing, null, null, false),
            new string('C', 64), [new("cloud-transmission", "v1")]);
        Assert.IsTrue(CloudAssessmentJson.Validate(assessment).IsValid);
        return assessment;
    }

    private static byte[] LegacyInset(Fixture fixture)
    {
        var raster = fixture.Base.Payload.ToArray();
        foreach (var tile in fixture.Assessment.Regions.Where(static region => region.IsCloudy))
        {
            for (var x = tile.X; x < tile.X + tile.Width; x++)
            {
                Set(x, tile.Y);
                Set(x, tile.Y + tile.Height - 1);
            }
            for (var y = tile.Y; y < tile.Y + tile.Height; y++)
            {
                Set(tile.X, y);
                Set(tile.X + tile.Width - 1, y);
            }
        }
        return raster;

        void Set(int x, int y)
        {
            var offset = (y * Size + x) * 3;
            raster[offset] = Cyan.Red;
            raster[offset + 1] = Cyan.Green;
            raster[offset + 2] = Cyan.Blue;
        }
    }

    private static byte[] ShiftObservedStroke(ReadOnlySpan<byte> source, ReadOnlySpan<byte> observed, int dx, int dy)
    {
        var shifted = source.ToArray();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var offset = (y * Size + x) * 3;
                if (source.Slice(offset, 3).SequenceEqual(observed.Slice(offset, 3))) continue;
                if (x + dx < Size && y + dy < Size)
                    observed.Slice(offset, 3).CopyTo(shifted.AsSpan(((y + dy) * Size + x + dx) * 3, 3));
            }
        return shifted;
    }

    private static byte[] ChangeByte(ReadOnlyMemory<byte> bytes)
    {
        var changed = bytes.ToArray();
        changed[0] ^= 1;
        return changed;
    }

    private sealed record Fixture(ProcessingArtifact Base, ProcessingArtifact Output, ProcessingArtifact Cloud,
        ProcessingArtifact ManifestArtifact, OverlayManifestV1 Manifest, PresentationLayerProductInput[] Layers,
        CloudAssessmentV1 Assessment);
}
