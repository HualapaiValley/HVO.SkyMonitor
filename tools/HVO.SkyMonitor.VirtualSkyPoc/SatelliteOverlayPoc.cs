using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using SkiaSharp;

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static partial class Program
{
    private static readonly string[] SatelliteOverlayNoticeFiles = ["ATTRIBUTION-HYG.md", "LICENSE-HYG.md"];
    private static readonly string[] SatelliteOverlayLimitations =
    [
        "Caller supplies the base/track pairing; this tool does not authenticate a capture manifest or recover geometry from image pixels.",
        "The retained base is a display derivative from explicit synthetic satellite brightness; source raw/detector evidence is unchanged.",
        "Only exact declared exposure segments are drawn. Full-pass planning and measured associations remain separate.",
        "Source age/coverage, query failures and empty populations remain caller facts. Optional labels may be omitted.",
        "This is no live cadence, physical orbit/brightness/visibility or automatic classification qualification."
    ];

    private static void SatelliteOverlayPoc(string[] args)
    {
        if (args.Length is < 5 or > 36 || !OrbitalContract.IsSha256(args[3]))
            throw new ArgumentException("Usage: satellite-overlay NEW_OUTPUT_DIRECTORY BASE_PNG BASE_SHA256 TRACK_JSON [TRACK_JSON ...]");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Use a new empty output directory; retained evidence is immutable.");
        var originalPng = ReadBoundedInput(args[2], 64 * 1024 * 1024);
        var baseSha = Convert.ToHexString(SHA256.HashData(originalPng));
        if (baseSha != args[3]) throw new ArgumentException("The retained base PNG does not match its expected checksum.");
        using var encodedData = SKData.CreateCopy(originalPng);
        using var codec = SKCodec.Create(encodedData);
        if (codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width < 1 || codec.Info.Height < 1 ||
            (long)codec.Info.Width * codec.Info.Height > PresentationLayerCompositor.MaximumCompositionPixels)
            throw new ArgumentException("The base must be a bounded PNG display image.");
        using var bitmap = SKBitmap.Decode(originalPng) ?? throw new ArgumentException("The base PNG cannot be decoded.");
        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha != byte.MaxValue) throw new ArgumentException("The display base must be opaque.");
                var at = (y * width + x) * 3;
                pixels[at] = color.Red; pixels[at + 1] = color.Green; pixels[at + 2] = color.Blue;
            }
        var trackBytes = args.Skip(4).Select(path => ReadBoundedInput(path, 16 * 1024 * 1024)).ToArray();
        var tracks = trackBytes.Select(bytes => OrbitalPredictionJson.ParseTrack(bytes)).ToArray();
        var converted = OrbitalTrackPresentation.CreatePayloadAndSourceFacts(width, height, tracks);
        var payload = converted.Payload;
        var layout = new ImageLayout(width, height, CameraPixelFormat.Rgb24, width * 3);
        var layer = new PresentationCompositorLayer(payload, true, PresentationRasterBlendMode.Normal, 1_000_000);
        var predicted = PresentationLayerCompositor.Composite(layout, pixels, [layer]);
        var disabled = PresentationLayerCompositor.Composite(layout, pixels, [layer with { Enabled = false }]);
        if (!disabled.AsSpan().SequenceEqual(pixels)) throw new InvalidOperationException("A disabled layer changed base pixels.");
        var coordinateHash = CaptureContractJson.ComputeCanonicalJsonSha256(CaptureContractJson.SerializeToElement(new
        {
            Scope = "supplied-output-pixel-coordinate-context-v1",
            tracks[0].Geometry.RigIdentitySha256,
            tracks[0].Geometry.ReadoutIdentitySha256,
            tracks[0].Geometry.CalibrationIdentitySha256,
            tracks[0].Geometry.ImageTransform
        }));
        var compatibility = new PresentationCompatibilityDescriptor(width, height,
            CaptureContractJson.ComputeCanonicalJsonSha256(CaptureContractJson.SerializeToElement(layout)), coordinateHash);
        var baseReference = new PresentationProductReference(ProcessingIdentity.CreateArtifactId(baseSha), baseSha, "image/png", compatibility);
        var layerReference = new PresentationProductReference(ProcessingIdentity.CreateArtifactId(payload.ContentIdentitySha256),
            payload.ContentIdentitySha256, PresentationLayerPayloadJson.MediaType, compatibility);
        var descriptor = LayeredPresentationJson.CreateLayer(OrbitalTrackPresentation.LayerKind, layerReference, null,
            PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
            OrbitalTrackPresentation.ProducerVersion, 500, PresentationBlendMode.Normal, 1_000_000, true,
            JsonSerializer.SerializeToElement(new { Meaning = "Prediction only; no measured association or visibility claim" }));
        var manifest = LayeredPresentationJson.CreateManifest(baseReference, null, [descriptor]);
        var svg = GroupedSvgPresentationRenderer.Render(manifest, [payload], baseSha);
        Directory.CreateDirectory(output);
        var notices = new StringBuilder();
        foreach (var notice in SatelliteOverlayNoticeFiles)
        {
            var source = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, notice);
            if (!File.Exists(source)) continue;
            var bytes = ReadBoundedInput(source, 64 * 1024);
            File.WriteAllBytes(Path.Combine(output, notice), bytes);
            notices.AppendLine(Encoding.UTF8.GetString(bytes));
        }
        File.WriteAllBytes(Path.Combine(output, "base-input.png"), originalPng);
        SavePng(Path.Combine(output, "predicted.png"), predicted, width, height);
        SavePng(Path.Combine(output, "predicted-off.png"), disabled, width, height);
        File.WriteAllBytes(Path.Combine(output, "source-facts.json"), converted.SourceFactsJson);
        File.WriteAllBytes(Path.Combine(output, "layer-payload.json"), PresentationLayerPayloadJson.Serialize(payload));
        File.WriteAllBytes(Path.Combine(output, "overlay-manifest.json"), LayeredPresentationJson.Serialize(manifest));
        File.WriteAllBytes(Path.Combine(output, "predicted.svg"), svg.Svg.ToArray());
        WriteJson(Path.Combine(output, "validation.json"), new
        {
            schema = "hvo-orbital-presentation-example-v1",
            scope = "Offline presentation of retained virtual-camera display pixels and supplied canonical exposure products; not a stored capture or live prediction stage",
            basePngSha256 = baseSha,
            baseRgbSha256 = Convert.ToHexString(SHA256.HashData(pixels)),
            disabledRgbSha256 = Convert.ToHexString(SHA256.HashData(disabled)),
            predictedRgbSha256 = Convert.ToHexString(SHA256.HashData(predicted)),
            disabledPreservesBase = true,
            width,
            height,
            payload.SourceIdentitySha256,
            payload.ContentIdentitySha256,
            descriptor.LayerIdentitySha256,
            manifest.ManifestIdentitySha256,
            tracks = tracks.Select((track, i) => new
            {
                inputSha256 = Convert.ToHexString(SHA256.HashData(trackBytes[i])),
                track.ProductSha256,
                track.Lineage.ObjectIdentity,
                track.RequestedInterval,
                track.Lineage.SnapshotManifestSha256,
                track.Lineage.RecordSha256,
                track.Status,
                track.Reason
            }),
            assembly = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
            limitations = SatelliteOverlayLimitations
        });
        var baseUri = "data:image/png;base64," + Convert.ToBase64String(originalPng);
        var svgUri = "data:image/svg+xml;base64," + Convert.ToBase64String(svg.Svg.Span);
        File.WriteAllText(Path.Combine(output, "comparison.html"), $$"""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Retained satellite presentation</title><style>
            :root{color-scheme:dark}body{margin:24px auto;max-width:1000px;padding:0 16px;font:16px system-ui;background:#101720;color:#e5edf6}
            p{line-height:1.5}.image{position:relative;max-width:720px}.image img{display:block;width:100%;height:auto}.overlay{position:absolute;inset:0}
            input:not(:checked)~.image .overlay{display:none}label{display:inline-block;margin:16px 8px;color:#ffa64d}
            </style><h1>Predicted exposure segment</h1>
            <p>Retained virtual-camera display image and exact canonical orbital products. Brightness is an explicit synthetic stimulus. The dashed layer is a prediction, not a measured identification or optical visibility claim.</p>
            <input id="predicted" type="checkbox" checked><label for="predicted">Predicted satellite tracks</label>
            <div class="image"><img src="{{baseUri}}" alt="Retained virtual-camera display, unchanged"><img class="overlay" src="{{svgUri}}" alt="Separately togglable predicted exposure geometry"></div>
            <p>Disabling the raster layer preserved every decoded base pixel. Labels never enter raw or calibrated detector inputs. This standalone manifest does not publish capture evidence or install a live prediction stage.</p>
            <details><summary>Retained input image attribution and license</summary><pre style="white-space:pre-wrap">{{System.Net.WebUtility.HtmlEncode(notices.ToString())}}</pre></details></html>
            """);
        WriteJson(Path.Combine(output, "checksums.json"), Directory.EnumerateFiles(output).Order(StringComparer.Ordinal)
            .Select(path => new
            {
                file = Path.GetFileName(path),
                bytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            }).ToArray());
    }

    private static byte[] ReadBoundedInput(string path, int maximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 || stream.Length > maximumBytes) throw new ArgumentException("An input exceeds its byte bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new ArgumentException("An input changed while being read.");
        return bytes;
    }
}
