// Authored by Codex gpt-6.1-sol/xhigh. Synthetic, Docker-free contract proofs; not campaign evidence.
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

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
        var fixture = Create(tiles, nonuniform: true, crossingLayer: true);
        var evidence = Check(fixture);
        Assert.AreEqual(tiles.Length, evidence.CloudyTileCount);
        Assert.IsGreaterThan(0, evidence.MatchedBorderPixelCount);
        Assert.AreEqual(0, evidence.UnexpectedBorderPixelCount);
        // Separate raster invocation proves the independent oracle, rather than just executor replay equality.
        CheckRaster(fixture, MaskRaster(fixture));
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
        Assert.ThrowsExactly<AssertFailedException>(() => CheckRaster(fixture, raster), fault);
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
            _ => Create()
        };
        fixture = fault switch
        {
            "assessment-identity-mismatch" => fixture with { Cloud = fixture.Cloud with { ContentIdentitySha256 = new string('D', 64) } },
            "capture-identity-mismatch" => fixture with { Cloud = fixture.Cloud with { CaptureId = Guid.Parse("52600000-0000-0000-0000-000000000099") } },
            "layer-content-identity-mismatch" => fixture with { Layers = fixture.Layers.Select(input =>
                input.Layer.LayerKind == "cloud-mask" ? input with { Product = input.Product with { ContentIdentitySha256 = new string('D', 64) } } : input).ToArray() },
            "layer-lineage-mismatch" => fixture with { Layers = fixture.Layers.Select(input =>
                input.Layer.LayerKind == "cloud-mask" ? input with { Product = input.Product with { SourceArtifactIds = [fixture.Base.ArtifactId] } } : input).ToArray() },
            "output-lineage-mismatch" => fixture with { Output = fixture.Output with { SourceArtifactIds = [fixture.Base.ArtifactId] } },
            "output-identity-mismatch" => fixture with { Output = fixture.Output with { ContentIdentitySha256 = new string('D', 64) } },
            "stored-output-mismatch" => fixture with { Output = fixture.Output with { Payload = ChangeByte(fixture.Output.Payload) } },
            _ => fixture
        };
        Assert.ThrowsExactly<AssertFailedException>(() => Check(fixture), fault);
    }

    [TestMethod]
    [DataRow("base-reference")]
    [DataRow("layer-reference")]
    [DataRow("manifest-identity")]
    public void ExecutorBindingRejectsMismatchedRetainedReferences(string fault)
    {
        var fixture = Create();
        fixture = fault switch
        {
            "base-reference" => fixture with { Base = fixture.Base with { ContentIdentitySha256 = new string('D', 64) } },
            "layer-reference" => fixture with { Layers = fixture.Layers.Select(input =>
                input.Layer.LayerKind == "cloud-labels" ? input with { Product = input.Product with { ContentIdentitySha256 = new string('D', 64) } } : input).ToArray() },
            "manifest-identity" => fixture with { ManifestArtifact = fixture.ManifestArtifact with { ContentIdentitySha256 = new string('D', 64) } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var exception = Assert.ThrowsExactly<ArgumentException>(() => Check(fixture), fault);
        StringAssert.Contains(exception.Message, fault switch
        {
            "base-reference" => "Product reference",
            "layer-reference" => "Layer products",
            "manifest-identity" => "Manifest artifact",
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        }, StringComparison.Ordinal);
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
            bytes, FixedUtc, TimeSpan.FromSeconds(1), Compatibility) { CaptureId = CaptureId };
        var assessment = Assessment(tiles ?? DefaultTiles);
        var cloud = baseArtifact with
        {
            ArtifactId = Guid.Parse("52600000-0000-0000-0000-000000000003"), Role = FrameArtifactRole.Metadata,
            Variant = "cloud-assessment-v1", MediaType = "application/json", Layout = null,
            Payload = CloudAssessmentJson.Serialize(assessment), ProductKind = ProcessingProductKind.Metadata,
            SchemaVersion = CloudAssessmentV1.CurrentSchemaVersion, ContentIdentitySha256 = assessment.AssessmentIdentitySha256
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
        ProductKind = product.Kind, SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256 ?? product.OutputIdentitySha256, CaptureId = CaptureId
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
