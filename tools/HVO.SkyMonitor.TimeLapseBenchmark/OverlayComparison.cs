using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private static async Task GenerateOverlaySample(string source, string geometry, string output, bool responsive = false)
    {
        if (File.Exists(output)) throw new ArgumentException("Use a fresh output file.");
        var sequence = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "sequence.json")).ConfigureAwait(false))!;
        var row = sequence["rows"]!.AsArray()[0]!;
        var scene = ProjectedSceneJson.Parse(await File.ReadAllBytesAsync(geometry).ConfigureAwait(false)).Scene
            ?? throw new InvalidDataException("Invalid rig geometry.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(source, row["file"]!.GetValue<string>())).ConfigureAwait(false);
        if (Hash(bytes) != row["jpegSha256"]!.GetValue<string>()) throw new InvalidDataException("Source changed.");
        var result = OverlayOperation(bytes, row, scene, true, responsive, sequence["color"]!.GetValue<bool>());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllBytesAsync(output, result.Jpeg).ConfigureAwait(false);
        Write(output + ".json", new { result.SourceIdentity, jpegSha256 = Hash(result.Jpeg), result.Layers });
    }

    private static async Task GenerateOverlayInputs(string source, string output, string catalogRoot, bool responsive = false)
    {
        if (Directory.Exists(output)) throw new ArgumentException("Use a fresh output directory.");
        Directory.CreateDirectory(output);
        var sequence = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "sequence.json")).ConfigureAwait(false))!;
        var rows = sequence["rows"]!.AsArray();
        var width = sequence["width"]!.GetValue<int>();
        var height = sequence["height"]!.GetValue<int>();
        var start = DateTimeOffset.Parse(rows[0]!["sourceUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production") { ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1" });
        var config = Config(width, height, CameraPixelFormat.BayerRggb16, start, 0);
        if (sequence["rig"] is { } rig) config = config with { Rig = rig.Deserialize<CameraRigConfig>(Json)! };
        var projection = RigProjectionContextFactory.Create(config.Rig);
        // The selected overlays need rig geometry, not projected star objects.
        var request = new VisibleSceneRequest(start, new ObserverLocation(35.347, -113.878, 0), projection,
            new CatalogQuery(-30, 1), snapshot.Catalog.Metadata, horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion);
        var visible = await new VisibleSceneBuilder(snapshot.Catalog).BuildAsync(request, CancellationToken.None).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(width, height),
            new(Guid.Parse("00001130-0000-0000-0000-000000000001"), Guid.Parse("00001130-0000-0000-0000-000000000002"),
                rows[0]!["jpegSha256"]!.GetValue<string>()), config.Rig.Optics.CalibrationVersion, config.Rig.Optics.CalibrationVersion);
        await File.WriteAllBytesAsync(Path.Combine(output, "geometry.json"), ProjectedSceneJson.Serialize(scene)).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var name = row!["file"]!.GetValue<string>();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(source, name)).ConfigureAwait(false);
            if (Hash(bytes) != row["jpegSha256"]!.GetValue<string>()) throw new InvalidDataException("Source changed.");
            var operation = OverlayOperation(bytes, row, scene, true, responsive, sequence["color"]!.GetValue<bool>());
            await File.WriteAllBytesAsync(Path.Combine(output, name), operation.Jpeg).ConfigureAwait(false);
            row["sourceJpegSha256"] = row["jpegSha256"]!.DeepClone();
            row["jpegSha256"] = Hash(operation.Jpeg);
            row["jpegBytes"] = operation.Jpeg.Length;
            row["overlaySourceIdentity"] = operation.SourceIdentity;
            row["overlayPayloads"] = JsonSerializer.SerializeToNode(operation.Layers, Json);
            if (row["index"]!.GetValue<int>() % 30 == 0) Console.WriteLine($"OVERLAY_INPUT {name}");
        }
        sequence["overlayBoundary"] = "Existing production JPEG decode/encode and typed presentation producers/compositor, JPEG92. One JPEG round trip from the accepted stack. Cardinal directions and circle use the original rig geometry; corner data uses each source row. No extra filters or invented weather facts.";
        sequence["overlayLayout"] = responsive ? "benchmark-responsive-layout-v3: prototype-proportional compass; metadata backplates within one pixel of image edges; no baked playback speed" : "production-default-layout-v1";
        await File.WriteAllTextAsync(Path.Combine(output, "sequence.json"), sequence.ToJsonString(Json)).ConfigureAwait(false);
        Console.WriteLine($"OVERLAY_INPUTS_COMPLETE {output}");
    }

    private static async Task BenchmarkOverlays(string source, string geometry, string output)
    {
        if (Directory.Exists(output)) throw new ArgumentException("Use a fresh output directory.");
        Directory.CreateDirectory(output);
        var sequence = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "sequence.json")).ConfigureAwait(false))!;
        var rows = sequence["rows"]!.AsArray();
        var scene = ProjectedSceneJson.Parse(await File.ReadAllBytesAsync(geometry).ConfigureAwait(false)).Scene
            ?? throw new InvalidDataException("Invalid rig geometry.");
        using var process = Process.GetCurrentProcess();
        foreach (var enabled in new[] { false, true })
        {
            var expected = new Dictionary<int, string>();
            for (var i = 0; i < 5; i++) _ = Operation(rows[i % rows.Count]!);
            var samples = new List<object>();
            var allocated = GC.GetTotalAllocatedBytes();
            var cpu = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 30; i++)
            {
                var rowIndex = i % rows.Count;
                var began = timer.Elapsed.TotalSeconds;
                var result = Operation(rows[rowIndex]!);
                var finished = timer.Elapsed.TotalSeconds;
                var hash = Hash(result.Jpeg);
                if (expected.TryGetValue(rowIndex, out var previous) && previous != hash) throw new InvalidDataException("Repeated output changed.");
                expected[rowIndex] = hash;
                samples.Add(new { index = i, rowIndex, seconds = finished - began, result.OverlaySeconds,
                    jpegSha256 = hash, jpegBytes = result.Jpeg.Length, result.SourceIdentity });
            }
            process.Refresh();
            Write(Path.Combine(output, enabled ? "overlays.json" : "jpeg-control.json"), new
            {
                enabled, sampleCount = 30, warmups = 5, uniqueSourceCount = rows.Count, samples,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds, process.PeakWorkingSet64,
                boundary = "One-CPU, low-priority worker. Warm source JPEG read, production decode, optional per-row layers and compositor, production JPEG92 encode. Output hashing and disk writes excluded. Control re-encodes without layers; this is not the whole capture pipeline."
            });
            Console.WriteLine($"OVERLAY_BENCH_COMPLETE enabled={enabled}");

            OverlayResult Operation(JsonNode row)
            {
                var bytes = File.ReadAllBytes(Path.Combine(source, row["file"]!.GetValue<string>()));
                return OverlayOperation(bytes, row, scene, enabled);
            }
        }
    }

    private static OverlayResult OverlayOperation(byte[] jpeg, JsonNode row, ProjectedSceneV1 scene, bool enabled,
        bool responsive = false, bool color = true)
    {
        var image = JpegImageCodec.DecodeJpeg(jpeg);
        var layout = new ImageLayout(image.Width, image.Height, image.PixelFormat, image.StrideBytes);
        ReadOnlyMemory<byte> pixels = image.PixelData;
        var identity = row["jpegSha256"]!.GetValue<string>();
        var layers = Array.Empty<PresentationCompositorLayer>();
        var timer = Stopwatch.StartNew();
        if (enabled)
        {
            var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene,
                includeMarkers: false, includeLabels: false, includeConstellations: false);
            var utc = DateTimeOffset.Parse(row["sourceUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var facts = new PresentationMetadataFactsV1(identity,
                [color ? "HVO VIRTUAL SKY" : "VIRTUAL ASI174MM", utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)],
                ["EXPOSURE", FormattableString.Invariant($"{row["exposureSeconds"]!.GetValue<double>():F3} s | Gain 0")],
                ["ROLLING STACK", FormattableString.Invariant($"{row["stackCount"]!.GetValue<int>()} frames | {row["totalIntegrationSeconds"]!.GetValue<double>():F1} s")],
                ["TIMELAPSE", FormattableString.Invariant($"{image.Width} x {image.Height} | {(color ? "Color" : "Mono")}"),
                    responsive ? "20 s capture cadence" : "60x | 20 s cadence"]);
            var metadata = PresentationLayerProducers.FromMetadataFacts(facts, image.Width, image.Height);
            if (responsive) (groups, metadata) = ResponsiveOverlayLayout(groups, metadata);
            layers = new[] { groups.ImageCircle, groups.CardinalDirections, metadata }
                .Select(payload => new PresentationCompositorLayer(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)).ToArray();
            pixels = PresentationLayerCompositor.Composite(layout, image.PixelData, layers);
        }
        var overlaySeconds = timer.Elapsed.TotalSeconds;
        return new(JpegImageCodec.EncodeToJpeg(layout, pixels, 92), overlaySeconds, identity, layers);
    }

    private sealed record OverlayResult(byte[] Jpeg, double OverlaySeconds, string SourceIdentity,
        IReadOnlyList<PresentationCompositorLayer> Layers);
}
