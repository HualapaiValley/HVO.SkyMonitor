using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest public discovery.")]
public sealed class SceneStylePresentationEvidenceTests
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    private const int Width = 1936, Height = 1216;
    public TestContext TestContext { get; set; } = null!;
    private static readonly PresentationAnnotationStyleV1 Annotation = new(LabelScale: 2, SegmentThickness: 2);

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("MonoRegular")]
    [DataRow("MonoBold")]
    [DataRow("PlexRegular")]
    public async Task SceneStyleFacesPreserveGeometryScientificCaptionsAndBoundedDenseLabels(string face)
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var bytes = ProjectedSceneJson.Serialize(scene);
        var legacy = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, Annotation, expectedPositionDiagnostics: true);
        var selected = new ScenePresentationStyleV1(Annotation with
        { MarkerColor = new(255, 128, 0), LabelColor = new(80, 240, 140), CardinalColor = new(120, 180, 255), ImageCircleColor = new(220, 120, 200) },
            face, face, "Crosshair");
        var styled = SceneStylePresentation.Create(scene, selected);
        Assert.AreEqual(selected.IdentitySha256, styled.StarAnnotations.SceneStyleIdentitySha256);
        Assert.IsTrue(styled.StarAnnotations.Markers.All(marker => marker.Crosshair));
        CollectionAssert.AreEqual(legacy.StarAnnotations.Markers.Select(marker => marker.Center).ToArray(), styled.StarAnnotations.Markers.Select(marker => marker.Center).ToArray());
        Assert.IsTrue(styled.StarAnnotations.TextBlocks.Count <= 64);
        Assert.IsTrue(styled.StarAnnotations.TextBlocks.SelectMany(block => block.Lines).Contains(PresentationDeepSkyLayerProducer.Legend, StringComparer.Ordinal));
        Assert.IsTrue(styled.StarAnnotations.TextBlocks.SelectMany(block => block.Lines).Any(line => line.StartsWith("STAR", StringComparison.Ordinal)));
        var caption = styled.StarAnnotations.TextBlocks.Single(block => block.Lines.Contains(PresentationDeepSkyLayerProducer.Legend, StringComparer.Ordinal));
        var oldCaption = legacy.StarAnnotations.TextBlocks.Single(block => block.Lines.Contains(PresentationDeepSkyLayerProducer.Legend, StringComparer.Ordinal));
        Assert.AreEqual(JsonSerializer.Serialize(oldCaption), JsonSerializer.Serialize(caption));
        Assert.IsTrue(styled.StarAnnotations.Segments.Any(segment => segment.Stroke is { DashPixels: > 0 }));
        Assert.IsTrue(styled.StarAnnotations.TextBlocks.SelectMany(block => block.Lines).Where(line => line.StartsWith("STAR", StringComparison.Ordinal))
            .All(line => line.Contains("expected", StringComparison.OrdinalIgnoreCase)), "Style must not invent measured star labels.");
        var raw = new byte[Width * Height];
        Array.Fill(raw, (byte)12);
        var checksum = Hash(raw);
        var layout = new ImageLayout(Width, Height, CameraPixelFormat.Mono8, Width);
        var layers = Layers(styled);
        var mono = PresentationLayerCompositor.Composite(layout, raw, layers);
        var color = PresentationLayerCompositor.CompositeDisplay(layout, raw, layers);
        Assert.AreEqual(CameraPixelFormat.Rgb24, color.Layout.PixelFormat);
        Assert.AreEqual(raw.Length, mono.Length);
        Assert.AreEqual(raw.Length * 3, color.Pixels.Length);
        var rgb = Enumerable.Repeat((byte)12, raw.Length * 3).ToArray();
        var rgbChecksum = Hash(rgb);
        var rgbOutput = PresentationLayerCompositor.Composite(new ImageLayout(Width, Height, CameraPixelFormat.Rgb24, Width * 3), rgb, layers);
        CollectionAssert.AreEqual(color.Pixels, rgbOutput);
        Assert.AreEqual(rgbChecksum, Hash(rgb));
        Assert.IsFalse(color.Pixels.Where((_, index) => index % 3 == 0).SequenceEqual(color.Pixels.Where((_, index) => index % 3 == 1)));
        var disabled = PresentationLayerCompositor.Composite(layout, raw, layers.Select(layer => layer with { Enabled = false }).ToArray());
        CollectionAssert.AreEqual(raw, disabled);
        Assert.AreEqual(checksum, Hash(raw));
        CollectionAssert.AreEqual(bytes, ProjectedSceneJson.Serialize(scene));
        foreach (var payload in Payloads(styled))
        {
            var retained = PresentationLayerPayloadJson.Parse(PresentationLayerPayloadJson.Serialize(payload));
            Assert.IsTrue(retained.IsValid);
            Assert.AreEqual(payload.SceneStyleIdentitySha256, retained.Payload!.SceneStyleIdentitySha256);
            Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Serialize(payload with { SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion }));
        }
        if (face == "PlexRegular")
        {
            SaveImage("scene-style-changed.png", color.Pixels);
            var defaults = PresentationLayerCompositor.CompositeDisplay(layout, raw, Layers(SceneStylePresentation.Create(scene, new(Annotation))));
            SaveImage("scene-style-default.png", defaults.Pixels);
            Save("scene-style-input.json", bytes);
            Save("scene-style-default-payload.json", PresentationLayerPayloadJson.Serialize(SceneStylePresentation.Create(scene, new(Annotation)).StarAnnotations));
            Save("scene-style-changed-payload.json", PresentationLayerPayloadJson.Serialize(styled.StarAnnotations));
            Save("scene-style-identities.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                scene = scene.SceneIdentitySha256,
                defaultStyle = new ScenePresentationStyleV1(Annotation).IdentitySha256,
                changedStyle = selected.IdentitySha256,
                source = checksum,
                defaultOutput = Hash(defaults.Pixels),
                changedOutput = Hash(color.Pixels),
                labels = styled.StarAnnotations.TextBlocks.Count,
                markers = styled.StarAnnotations.Markers.Count,
                fonts = PresentationRichText.FontSetIdentity,
                sourceBytes = raw.Length,
                width = Width,
                height = Height
            }));
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SceneStyleChangingHiddenStyleStillChangesImmutableIdentityAndIndependentGroupsStayEmpty()
    {
        var scene = await SceneAsync().ConfigureAwait(false);
        var before = SceneStylePresentation.Create(scene, new(Annotation), false, false, false, false, false);
        var after = SceneStylePresentation.Create(scene, new(Annotation, PointGlyph: "Crosshair"), false, false, false, false, false);
        Assert.IsEmpty(before.CardinalDirections.TextBlocks);
        Assert.IsEmpty(before.ImageCircle.Ellipses);
        Assert.IsEmpty(before.Constellations.Segments);
        Assert.AreNotEqual(before.ImageCircle.ContentIdentitySha256, after.ImageCircle.ContentIdentitySha256);
        Assert.AreEqual(before.StarAnnotations.SourceIdentitySha256, after.StarAnnotations.SourceIdentitySha256);
        var strict = SceneStylePresentation.Create(scene, new(Annotation, PointGlyph: "Crosshair"), expectedPositionDiagnostics: false);
        Assert.IsFalse(strict.StarAnnotations.TextBlocks.SelectMany(block => block.Lines).Any(line => line.StartsWith("STAR", StringComparison.Ordinal)));
        Assert.IsTrue(strict.StarAnnotations.TextBlocks.SelectMany(block => block.Lines).Contains(PresentationDeepSkyLayerProducer.Legend, StringComparer.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => SceneStylePresentation.Create(scene, new(Annotation, "PlexBold")));
        Assert.ThrowsExactly<ArgumentException>(() => SceneStylePresentation.Create(scene, new(Annotation, PointGlyph: "★")));
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task SceneStyleFocusedBeforeAfterCpuAllocationWorkingSetAndLatencyEvidence()
    {
        if (Environment.GetEnvironmentVariable("HVO_SCENE_STYLE_MEASURE") != "1")
            Assert.Inconclusive("Explicit affected-path measurement only.");
        var scene = await SceneAsync().ConfigureAwait(false);
        var raw = new byte[Width * Height];
        var layout = new ImageLayout(Width, Height, CameraPixelFormat.Mono8, Width);
        (string Name, Func<PresentationSceneLayerPayloadsV2> Produce)[] profiles =
        [
            ("baseline-v13", () => PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, Annotation, expectedPositionDiagnostics: true)),
            ("default-v14", () => SceneStylePresentation.Create(scene, new(Annotation))),
            ("changed-v14", () => SceneStylePresentation.Create(scene, new(Annotation with { MarkerColor = new(255, 128, 0) }, "PlexRegular", "MonoRegular", "Crosshair")))
        ];
        var times = profiles.Select(_ => new double[30]).ToArray();
        var producerTimes = profiles.Select(_ => new double[30]).ToArray();
        var allocations = new long[3];
        var cpuTimes = new double[3];
        var cpuSamples = profiles.Select(_ => new double[30]).ToArray();
        var gen0 = new int[3];
        var gen1 = new int[3];
        var gen2 = new int[3];
        var workingSets = new long[3];
        var checksums = new string?[3];
        var styleIdentities = new string?[3];
        using var process = Process.GetCurrentProcess();
        for (var warmup = 0; warmup < 5; warmup++)
            for (var profile = 0; profile < 3; profile++) _ = Run(profile);
        // Rotate ordering: each representation receives the same JIT/GC/cache and host conditions.
        for (var iteration = 0; iteration < 30; iteration++)
            for (var offset = 0; offset < 3; offset++)
            {
                var profile = (iteration + offset) % 3;
                var cpuStart = process.TotalProcessorTime;
                var generation0 = GC.CollectionCount(0);
                var generation1 = GC.CollectionCount(1);
                var generation2 = GC.CollectionCount(2);
                var allocationStart = GC.GetTotalAllocatedBytes(true);
                var result = Run(profile);
                allocations[profile] += GC.GetTotalAllocatedBytes(true) - allocationStart;
                cpuSamples[profile][iteration] = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                cpuTimes[profile] += cpuSamples[profile][iteration];
                gen0[profile] += GC.CollectionCount(0) - generation0;
                gen1[profile] += GC.CollectionCount(1) - generation1;
                gen2[profile] += GC.CollectionCount(2) - generation2;
                times[profile][iteration] = result.Total;
                producerTimes[profile][iteration] = result.Producer;
                styleIdentities[profile] = result.StyleIdentity;
                var checksum = Hash(result.Output);
                checksums[profile] ??= checksum;
                Assert.AreEqual(checksums[profile], checksum);
                process.Refresh();
                workingSets[profile] = Math.Max(workingSets[profile], process.WorkingSet64);
            }
        Assert.AreEqual(checksums[0], checksums[1], "Default styling preserves original rendered pixels.");
        var results = new List<object>();
        for (var profile = 0; profile < 3; profile++)
        {
            var latencySamples = times[profile].ToArray();
            Array.Sort(times[profile]);
            Array.Sort(producerTimes[profile]);
            results.Add(new
            {
                name = profiles[profile].Name,
                scene = scene.SceneIdentitySha256,
                style = styleIdentities[profile],
                width = Width,
                height = Height,
                warmup = 5,
                operations = 30,
                concurrency = 1,
                ordering = "rotating-interleaved",
                cpuMs = cpuTimes[profile],
                allocatedBytesPerOperation = allocations[profile] / 30,
                maximumWorkingSet = workingSets[profile],
                medianMs = times[profile][15],
                p95Ms = times[profile][28],
                producerMedianMs = producerTimes[profile][15],
                producerP95Ms = producerTimes[profile][28],
                outputSha256 = checksums[profile],
                inputSha256 = Hash(raw),
                ioBytes = 0,
                backlog = 0,
                fonts = PresentationRichText.FontSetIdentity,
                latencySamplesMs = latencySamples,
                cpuSamplesMs = cpuSamples[profile],
                gen0Collections = gen0[profile],
                gen1Collections = gen1[profile],
                gen2Collections = gen2[profile]
            });
        }
        Save("scene-style-performance.json", JsonSerializer.SerializeToUtf8Bytes(results, EvidenceJson));
        (double Total, double Producer, byte[] Output, string? StyleIdentity) Run(int profile)
        {
            var start = Stopwatch.GetTimestamp();
            var groups = profiles[profile].Produce();
            var produced = Stopwatch.GetTimestamp();
            var output = PresentationLayerCompositor.CompositeDisplay(layout, raw, Layers(groups));
            return (Stopwatch.GetElapsedTime(start).TotalMilliseconds, Stopwatch.GetElapsedTime(start, produced).TotalMilliseconds,
                output.Pixels, groups.StarAnnotations.SceneStyleIdentitySha256);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static PresentationLayerPayloadV1[] Payloads(PresentationSceneLayerPayloadsV2 groups) =>
        [groups.StarAnnotations, groups.CardinalDirections, groups.ImageCircle, groups.Constellations];
    private static PresentationCompositorLayer[] Layers(PresentationSceneLayerPayloadsV2 groups) =>
        Payloads(groups).Select(payload => new PresentationCompositorLayer(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)).ToArray();

    private void Save(string name, byte[] bytes)
    {
        var root = Environment.GetEnvironmentVariable("HVO_SCENE_STYLE_EVIDENCE_ROOT") ?? TestContext.TestResultsDirectory!;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        TestContext.AddResultFile(path);
    }

    private void SaveImage(string name, byte[] rgb)
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rgba = new byte[Width * Height * 4];
        for (var pixel = 0; pixel < Width * Height; pixel++)
        { rgba[pixel * 4] = rgb[pixel * 3]; rgba[pixel * 4 + 1] = rgb[pixel * 3 + 1]; rgba[pixel * 4 + 2] = rgb[pixel * 3 + 2]; rgba[pixel * 4 + 3] = 255; }
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        Save(name, png.ToArray());
    }

    private static async Task<ProjectedSceneV1> SceneAsync()
    {
        var utc = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var projection = new ProjectionContext(ProjectionModel.EquidistantFisheye, Width / 2, Height / 2, 568, 568,
            Width, Height, ProjectionAperture.Circular, ImageCircleRadiusPixels: 595, BoresightAltitudeDegrees: 90);
        var projector = ProjectorFactory.Create(projection);
        var stars = new List<CelestialCatalogObject>();
        for (var i = 0; i < 120; i++)
        {
            var direction = projector.Unproject(new(400 + i % 12 * 100, 110 + i / 12 * 105));
            if (direction is null || direction.Value.AltitudeDegrees < 0) continue;
            var equatorial = CoordinateTransforms.HorizontalToEquatorial(direction.Value, utc, 30, 0);
            stars.Add(new($"star-{i:D3}", $"STAR {i:D3}", equatorial.RightAscensionHours, equatorial.DeclinationDegrees, 1));
        }
        var request = new VisibleSceneRequest(utc, new(30, 0, 0), projection, new CatalogQuery(6, 128),
            new CatalogMetadata("fixture", "scene-style-1660-v1", new Uri("https://example.test/catalog"), new string('C', 64)), projectionVersion: "equidistant-v1");
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(stars)).BuildAsync(request).ConfigureAwait(false);
        var ra = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var obj = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G", ra, 30, "And", 120, 60, 90, null, null, null, null, 31, null, null, null);
        var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
            new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
            "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote");
        visible = visible.WithCuratedDeepSky(new DeepSkyCatalog(semantics, [obj],
            [new DeepSkyAlias(obj.Designation, obj.Id, DeepSkyAliasKinds.Designation), new DeepSkyAlias("M31", obj.Id, DeepSkyAliasKinds.Messier)], [], []), new(128, 32, 0, 1, .1));
        return ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, visible, ProjectedSceneImageTransformV1.Identity(Width, Height),
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('D', 64)),
            "style-fixture-calibration-v1", "equidistant-v1");
    }
}
