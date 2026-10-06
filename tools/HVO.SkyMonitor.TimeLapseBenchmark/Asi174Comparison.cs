using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private static CameraModuleConfig Asi174Config(DateTimeOffset utc, int index)
    {
        var original = Config(1936, 1216, CameraPixelFormat.Mono16, utc, index);
        var options = JsonNode.Parse(original.Module.Options!.Value.GetRawText())!.AsObject();
        options.Remove("asi676Sensor");
        options["asi174Sensor"] = JsonSerializer.SerializeToNode(new { enabled = true, blackLevelAdu = 64 }, Json);
        return original with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(options, Json)),
            Rig = original.Rig with
            {
                Sensor = original.Rig.Sensor with
                {
                    Name = "Virtual ASI174MM", PixelSizeMicrons = 5.86,
                    SensorRecipeVersion = "benchmark-1130-asi174mm-native12-v1"
                }
            }
        };
    }

    private static async Task GenerateAsi174Motion(string root, string catalogRoot)
    {
        if (Directory.Exists(root)) throw new ArgumentException("Use a fresh output directory.");
        Directory.CreateDirectory(root);
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        });
        const int width = 1936, height = 1216, count = 180;
        var start = DateTimeOffset.Parse("2026-10-13T07:00:00Z", CultureInfo.InvariantCulture);
        var combiner = new RollingMono16Combiner(3);
        var pending = new Queue<Task<CameraFrame>>();
        var starHistory = new Queue<Dictionary<string, (double X, double Y)>>();
        var rows = new List<object>();
        for (var index = 0; index < 4; index++) pending.Enqueue(Capture(index));
        for (var index = 0; index < count; index++)
        {
            var frame = await pending.Dequeue().ConfigureAwait(false);
            if (index + 4 < count) pending.Enqueue(Capture(index + 4));
            var scene = frame.Metadata.Scene ?? throw new InvalidDataException("Missing celestial metadata.");
            if (scene.VirtualExposure?.CelestialStartUtc != start.AddSeconds(index * 20))
                throw new InvalidDataException("Wrong celestial time.");
            var combined = combiner.Add(new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame));
            var preview = Mono16DisplayStretch.Apply(width, height, combined.PixelData);
            var jpeg = Encode(preview, width, height, false);
            var file = $"{index:D4}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(root, file), jpeg).ConfigureAwait(false);
            starHistory.Enqueue((scene.Objects ?? []).ToDictionary(s => s.Id, s => (s.PixelX, s.PixelY)));
            if (starHistory.Count > 3) starHistory.Dequeue();
            var stars = (scene.Objects ?? []).Where(s => s.Magnitude < 2.5 && s.PixelX > 12 && s.PixelY > 12 &&
                s.PixelX < width - 12 && s.PixelY < height - 12 && starHistory.All(h => h.ContainsKey(s.Id)))
                .OrderBy(s => s.Magnitude).Take(20).Select(s =>
                {
                    var x = starHistory.Average(h => h[s.Id].X);
                    var y = starHistory.Average(h => h[s.Id].Y);
                    return new { s.Id, s.DisplayName, s.Magnitude, expectedX = x, expectedY = y,
                        measured = Centroid(preview, width, false, x, y) };
                }).ToArray();
            rows.Add(new
            {
                index, file, sourceUtc = start.AddSeconds(index * 20), sourceOffsetSeconds = index * 20,
                sourceDurationSeconds = 20, exposureSeconds = 20, celestialExposure = scene.VirtualExposure,
                rawSha256 = Hash(frame.PixelData.Span), combinedSha256 = Hash(combined.PixelData.Span),
                jpegSha256 = Hash(jpeg), jpegBytes = jpeg.Length, stackCount = combiner.BufferedFrameCount,
                totalIntegrationSeconds = combined.TotalIntegration.TotalSeconds,
                stackSourceIndices = Enumerable.Range(Math.Max(0, index - 2), Math.Min(index + 1, 3)).ToArray(),
                starTracks = stars, sensorModel = frame.Metadata.Extra!["sensorModel"],
                stellarAdmittedCount = frame.Metadata.Extra!["stellarAdmittedCount"]
            });
            if (index % 30 == 0) Console.WriteLine($"ASI174_MOTION {index + 1}/{count}");
        }
        Write(Path.Combine(root, "sequence.json"), new
        {
            width, height, color = false, phase = "night", start, end = start.AddHours(1),
            sourceSpanSeconds = 3600, playbackDurationSeconds = 60, compression = 60, count, rows,
            rig = Asi174Config(start, 0).Rig, snapshot.DatabaseSha256, snapshot.RowCount,
            config = Asi174Config(start, 0), stretch = new Mono16DisplayStretchOptions(),
            boundary = "Native1936x1216 VirtualSky with production ASI174MM sensor response,5.86um pixels,20s exposure/gain0, full catalog and physical admission. Actual rolling-three unaligned mean, initial1/2-frame warmup retained. Expected star centroids average the contributing exposures. No physical camera or image resizing."
        });
        Console.WriteLine($"ASI174_MOTION_COMPLETE {root}");

        Task<CameraFrame> Capture(int index) => Task.Run(async () =>
        {
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(Asi174Config(start.AddSeconds(index * 20), index), CancellationToken.None).ConfigureAwait(false);
            return (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 0, null, null)), CancellationToken.None).ConfigureAwait(false)).Frame
                ?? throw new InvalidOperationException("No ASI174 frame.");
        });
    }
}
