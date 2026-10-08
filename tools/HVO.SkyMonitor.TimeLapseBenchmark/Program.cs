using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly FixedDisplayTransferOptions Transfer = new();

    public static async Task Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "generate")
            await Generate(args[1], args[2]).ConfigureAwait(false);
        else if (args.Length == 3 && args[0] == "motion")
            await GenerateMotion(args[1], args[2]).ConfigureAwait(false);
        else if (args.Length == 3 && args[0] == "day5")
            await GenerateMotion(args[1], args[2], true).ConfigureAwait(false);
        else if (args.Length == 3 && args[0] == "visual")
            await GenerateVisualComparison(args[1], args[2]).ConfigureAwait(false);
        else if (args.Length == 3 && args[0] == "display-motion")
            await GenerateDisplayMotion(args[1], args[2]).ConfigureAwait(false);
        else if (args.Length == 3 && args[0] == "asi174-motion")
            await GenerateAsi174Motion(args[1], args[2]).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "overlay-inputs")
            await GenerateOverlayInputs(args[1], args[2], args[3]).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "overlay-bench")
            await BenchmarkOverlays(args[1], args[2], args[3]).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "overlay-sample")
            await GenerateOverlaySample(args[1], args[2], args[3]).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "overlay-layout")
            await GenerateOverlayInputs(args[1], args[2], args[3], true).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "overlay-layout-sample")
            await GenerateOverlaySample(args[1], args[2], args[3], true).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "production-inputs")
            await GenerateProductionInputs(args[1], args[2], args[3]).ConfigureAwait(false);
        else if (args.Length == 7 && args[0] == "production")
            await BenchmarkProduction(args[1], args[2], int.Parse(args[3], CultureInfo.InvariantCulture),
                Enum.Parse<HVO.SkyMonitor.Video.FFmpeg.TimeLapseEncoderProfile>(args[4]),
                int.Parse(args[5], CultureInfo.InvariantCulture), int.Parse(args[6], CultureInfo.InvariantCulture)).ConfigureAwait(false);
        else if (args.Length == 4 && args[0] == "production-contend")
            await BenchmarkProductionContention(args[1], args[2], bool.Parse(args[3])).ConfigureAwait(false);
        else if (args.Length == 5 && args[0] == "process")
            await ProcessFrames(args[1], args[2], int.Parse(args[3], CultureInfo.InvariantCulture),
                double.Parse(args[4], CultureInfo.InvariantCulture)).ConfigureAwait(false);
        else throw new ArgumentException("generate|motion|day5|visual|display-motion OUTPUT CATALOG | overlay-inputs SOURCE OUTPUT CATALOG | overlay-bench SOURCE GEOMETRY_JSON OUTPUT | overlay-sample SOURCE GEOMETRY_JSON OUTPUT_JPEG | process DATASET OUTPUT_JSON COUNT INTERVAL_SECONDS");
    }

    private static async Task GenerateMotion(string root, string catalogRoot, bool shortDay = false)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Use a fresh evidence directory.");
        Directory.CreateDirectory(root);
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        });
        var assemblyBytes = await File.ReadAllBytesAsync(typeof(Program).Assembly.Location).ConfigureAwait(false);
        Write(Path.Combine(root, "identity.json"), new
        {
            schema = "hvo-timelapse-motion-input-v1", snapshot.DatabaseSha256, snapshot.RowCount,
            assemblySha256 = Hash(assemblyBytes),
            boundary = shortDay
                ? "Genuine five minutes, 60 independently rendered native-resolution daytime frames at five-second cadence and the existing POC solar-background exposure target. No source replay or resizing."
                : "Genuine continuous hour, 180 independently rendered native-resolution frames at 20-second cadence; night exposure 20 seconds, daytime uses the existing POC solar-background exposure target. No source replay or resizing.",
            generationConcurrency = 4, transfer = Transfer, shortDay
        });
        var cases = shortDay ? new[] { (3552, true, true) }
            : new[] { (1280, true, false), (3552, false, false), (3552, true, false), (3552, true, true) };
        foreach (var (size, color, daytime) in cases)
        {
            var folder = Path.Combine(root, $"{(color ? "color" : "mono")}-{size}x{size}-{(daytime ? "day-stable" : "motion-hour")}");
            Directory.CreateDirectory(folder);
            var start = DateTimeOffset.Parse(daytime ? "2026-10-12T19:00:00Z" : "2026-10-13T07:00:00Z", CultureInfo.InvariantCulture);
            var interval = shortDay ? 5 : 20;
            var rows = new object[shortDay ? 60 : 180];
            await Parallel.ForEachAsync(Enumerable.Range(0, rows.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (index, cancellationToken) =>
                {
                    var utc = start.AddSeconds(index * interval);
                    var config = Config(size, size, color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, utc, index);
                    var exposure = 20d;
                    if (daytime)
                    {
                        var projection = RigProjectionContextFactory.Create(config.Rig);
                        var altitude = SolarAltitudeClassifier.DirectionAt(new AstronomyEnginePlanetEphemeris(), utc, 35.347, -113.878).AltitudeDegrees;
                        var nightRate = SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, 1000,
                            projection.FocalLengthXPixels, projection.FocalLengthYPixels);
                        exposure = Math.Clamp(2500 / StellarSkyBackgroundModel.Resolve(nightRate, altitude), .000001, 20);
                    }
                    var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                    await using var lifetime = module.ConfigureAwait(false);
                    await module.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
                    var frame = (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(interval),
                        CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(exposure), 0, null, null)), cancellationToken)
                        .ConfigureAwait(false)).Frame ?? throw new InvalidOperationException("No frame.");
                    var scene = frame.Metadata.Scene ?? throw new InvalidOperationException("No scene.");
                    if (scene.VirtualExposure?.CelestialStartUtc != utc)
                        throw new InvalidOperationException("The renderer did not use the requested sky time.");
                    var preview = FixedDisplayTransfer.Apply(frame.Layout!, frame.PixelData, Transfer, cancellationToken);
                    var jpeg = Encode(preview, size, size, color);
                    var file = $"{index:D4}.jpg";
                    await File.WriteAllBytesAsync(Path.Combine(folder, file), jpeg, cancellationToken).ConfigureAwait(false);
                    var stars = (scene.Objects ?? []).Where(s => s.Magnitude < 2.5 && s.PixelX > 12 && s.PixelY > 12 &&
                            s.PixelX < size - 12 && s.PixelY < size - 12)
                        .OrderBy(s => s.Magnitude).Take(20).Select(s => new
                        {
                            s.Id, s.DisplayName, s.Magnitude, expectedX = s.PixelX, expectedY = s.PixelY,
                            measured = Centroid(preview, size, color, s.PixelX, s.PixelY)
                        }).ToArray();
                    rows[index] = new
                    {
                        index, file, sourceUtc = utc, sourceOffsetSeconds = index * interval,
                        sourceDurationSeconds = interval, exposureSeconds = exposure,
                        celestialExposure = scene.VirtualExposure, scene.SceneUtc,
                        rawSha256 = Hash(frame.PixelData.Span), jpegSha256 = Hash(jpeg), jpegBytes = jpeg.Length,
                        previewPixelsSha256 = Hash(preview), config, starTracks = stars,
                        stellarAdmittedCount = frame.Metadata.Extra!["stellarAdmittedCount"]
                    };
                    if (index % 15 == 0) Console.WriteLine($"motion {Path.GetFileName(folder)} {index + 1}/{rows.Length}");
                }).ConfigureAwait(false);
            Write(Path.Combine(folder, "sequence.json"), new
            {
                width = size, height = size, color, phase = daytime ? "day" : "night", start, end = start.AddSeconds(rows.Length * interval),
                sourceSpanSeconds = rows.Length * interval, playbackDurationSeconds = rows.Length * interval / 60d,
                compression = 60, count = rows.Length, rows,
                gapPolicy = "Contiguous observations at the recorded cadence. Every source is independently rendered at its actual celestial time."
            });
            Console.WriteLine($"MOTION_COMPLETE {Path.GetFileName(folder)}");
        }
    }

    private static object Centroid(byte[] pixels, int width, bool color, double expectedX, double expectedY)
    {
        var cx = (int)Math.Floor(expectedX);
        var cy = (int)Math.Floor(expectedY);
        double sum = 0, weightedX = 0, weightedY = 0, maximum = 0;
        var border = new List<double>();
        for (var y = cy - 7; y <= cy + 7; y++)
            for (var x = cx - 7; x <= cx + 7; x++)
                if (Math.Abs(x - cx) == 7 || Math.Abs(y - cy) == 7) border.Add(Value(x, y));
        border.Sort();
        var background = border[border.Count / 2];
        for (var y = cy - 5; y <= cy + 5; y++)
            for (var x = cx - 5; x <= cx + 5; x++)
            {
                var value = Value(x, y);
                maximum = Math.Max(maximum, value);
                var weight = Math.Max(0, value - background - 8);
                sum += weight;
                weightedX += (x + .5) * weight;
                weightedY += (y + .5) * weight;
            }
        return new { x = sum > 0 ? weightedX / sum : (double?)null,
            y = sum > 0 ? weightedY / sum : (double?)null, contrast = maximum - background, sum };
        double Value(int x, int y)
        {
            var offset = (y * width + x) * (color ? 3 : 1);
            return color ? (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3d : pixels[offset];
        }
    }

    private static async Task Generate(string root, string catalogRoot)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("Use a fresh evidence directory.");
        Directory.CreateDirectory(root);
        var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        });
        Write(Path.Combine(root, "identity.json"), new
        {
            schema = "hvo-timelapse-benchmark-input-v1",
            snapshot.DatabaseSha256, snapshot.RowCount, snapshot.SnapshotVersion,
            assemblies = new[] { typeof(Program).Assembly, typeof(VirtualSkyCameraModule).Assembly,
                typeof(FixedDisplayTransfer).Assembly }.Select(a => new
                {
                    name = a.GetName().Name, sha256 = Hash(File.ReadAllBytes(a.Location))
                }),
            transfer = Transfer,
            boundary = "Actual native-resolution VirtualSky captures, fixed display transfer and JPEG quality 92; no image resize.",
            note = "Source generation is separate from encoding. Geometry is a synthetic all-sky profile, not physical sensor qualification."
        });
        foreach (var (width, height, color) in new[]
        {
            (640, 640, false), (640, 640, true), (1280, 1280, false), (1280, 1280, true),
            (1936, 1216, false), (3096, 2080, true), (3552, 3552, false), (3552, 3552, true)
        })
        {
            await Sequence(root, snapshot.Catalog, width, height, color, "night").ConfigureAwait(false);
        }
        await Sequence(root, snapshot.Catalog, 1280, 1280, true, "day").ConfigureAwait(false);
        await Sequence(root, snapshot.Catalog, 1280, 1280, true, "twilight").ConfigureAwait(false);
    }

    private static async Task Sequence(string root, ICelestialCatalog catalog, int width, int height, bool color, string phase)
    {
        var folder = Path.Combine(root, $"{(color ? "color" : "mono")}-{width}x{height}-{phase}");
        Directory.CreateDirectory(folder);
        var start = DateTimeOffset.Parse(phase switch
        {
            "day" => "2026-10-12T19:00:00Z",
            "twilight" => "2026-10-13T01:25:00Z",
            _ => "2026-10-13T07:00:00Z"
        }, CultureInfo.InvariantCulture);
        var format = color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16;
        var rows = new List<object>();
        double offset = 0;
        using var process = Process.GetCurrentProcess();
        var timer = Stopwatch.StartNew();
        var cpu = process.TotalProcessorTime;
        for (var index = 0; index < 24; index++)
        {
            var interval = index < 12 ? 5d : 20d;
            var exposure = phase == "day" ? (index % 2 == 0 ? .001 : .005)
                : phase == "twilight" ? (index < 12 ? .02 : .1) : interval;
            var utc = start.AddSeconds(offset);
            var config = Config(width, height, format, utc, index);
            var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var frame = (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(interval),
                CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(exposure), 0, null, null)),
                CancellationToken.None).ConfigureAwait(false)).Frame ?? throw new InvalidOperationException("No frame.");
            var layout = frame.Layout ?? throw new InvalidOperationException("Frame layout is missing.");
            var preview = FixedDisplayTransfer.Apply(layout, frame.PixelData, Transfer);
            var jpeg = Encode(preview, width, height, color);
            var filename = $"{index:D4}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(folder, filename), jpeg).ConfigureAwait(false);
            if (index == 0)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, "foreground.raw"), frame.PixelData.ToArray()).ConfigureAwait(false);
                Write(Path.Combine(folder, "foreground.json"), new Foreground(layout, color,
                    Hash(frame.PixelData.Span), Hash(jpeg)));
            }
            rows.Add(new
            {
                index, file = filename, sourceUtc = utc, sourceOffsetSeconds = offset,
                sourceDurationSeconds = interval, exposureSeconds = exposure,
                acquisitionUtc = frame.TimestampUtc,
                celestialExposure = frame.Metadata.Scene!.VirtualExposure,
                rawSha256 = Hash(frame.PixelData.Span), jpegSha256 = Hash(jpeg), jpegBytes = jpeg.Length,
                previewPixelsSha256 = Hash(preview), config,
                solarAltitudeDegrees = frame.Metadata.Extra!["stellarSolarAltitudeDegrees"]
            });
            offset += interval;
        }
        process.Refresh();
        Write(Path.Combine(folder, "sequence.json"), new
        {
            width, height, color, phase, start, end = start.AddSeconds(offset),
            sourceSpanSeconds = offset, playbackDurationSeconds = offset / 60, compression = 60,
            count = rows.Count, rows,
            captureElapsedSeconds = timer.Elapsed.TotalSeconds,
            captureCpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
            process.PeakWorkingSet64,
            gapPolicy = "This workload has contiguous planned capture intervals. Exposure may be shorter than cadence; displayed holds are sampling intervals, not continuous exposure."
        });
        Console.WriteLine($"generated {Path.GetFileName(folder)}: {rows.Count} actual sources, {offset / 60:R}s video");
    }

    private static CameraModuleConfig Config(int width, int height, CameraPixelFormat format, DateTimeOffset utc, int seed,
        double maximumMagnitude = 6.5, int maximumResults = 32768)
        => new(new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                illuminationMode = "SolarDriven", fixedSceneUtc = utc, renderSolarSystemDisks = true,
                seed = 1130 + seed, maximumMagnitude, maximumResults,
                magnitudeZeroElectronsPerSecond = 1000, bortleClass = 3,
                asi676Sensor = new { enabled = true, blackLevelAdu = 64 },
                shotNoiseEnabled = true, vignettingStrength = .15, psfSigmaPixels = .85, psfRadiusPixels = 3.5
            })),
            new CameraRigConfig(new SensorProfile("Native benchmark all-sky sensor", width, height, 2,
                    format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    format, format == CameraPixelFormat.Mono16 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw,
                    SensorRecipeVersion: "benchmark-1130-native12-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    width / 2d, height / 2d, Math.Min(width, height) * .49, HorizontalFlip: true,
                    CalibrationVersion: "benchmark-1130-upward-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(.1), TimeSpan.FromSeconds(20), 0, 0)),
            CapturePipelineConfig.Empty);

    private static byte[] Encode(byte[] preview, int width, int height, bool color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rgba = new byte[checked(width * height * 4)];
        for (var index = 0; index < width * height; index++)
        {
            rgba[index * 4] = preview[index * (color ? 3 : 1)];
            rgba[index * 4 + 1] = preview[index * (color ? 3 : 1) + (color ? 1 : 0)];
            rgba[index * 4 + 2] = preview[index * (color ? 3 : 1) + (color ? 2 : 0)];
            rgba[index * 4 + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private static async Task ProcessFrames(string folder, string output, int count, double interval)
    {
        var facts = JsonSerializer.Deserialize<Foreground>(await File.ReadAllTextAsync(Path.Combine(folder, "foreground.json")).ConfigureAwait(false), Json)!;
        var rawFile = Path.Combine(folder, "foreground.raw");
        var results = new List<object>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes();
        for (var warmup = 0; warmup < 5; warmup++) _ = Operation();
        var timer = Stopwatch.StartNew();
        Console.WriteLine(FormattableString.Invariant($"FOREGROUND_READY {count}"));
        for (var index = 0; index < count; index++)
        {
            var scheduled = index * interval;
            var delay = scheduled - timer.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            var began = timer.Elapsed.TotalSeconds;
            var jpeg = Operation();
            var finished = timer.Elapsed.TotalSeconds;
            hashes.Add(Hash(jpeg));
            results.Add(new { index, scheduledSeconds = scheduled, beganSeconds = began,
                finishedSeconds = finished, serviceSeconds = finished - began,
                responseSeconds = finished - scheduled, deadlineMiss = interval > 0 && finished > scheduled + interval });
            Console.WriteLine($"processed {index + 1}/{count}: {finished - began:F4}s");
        }
        process.Refresh();
        Write(output, new
        {
            schema = "hvo-1130-foreground-v1", dataset = Path.GetFileName(folder), count, interval,
            warmupCount = 5, elapsedSeconds = timer.Elapsed.TotalSeconds,
            cpuSecondsIncludingWarmup = (process.TotalProcessorTime - cpu).TotalSeconds,
            allocatedBytesIncludingWarmup = GC.GetTotalAllocatedBytes() - allocated,
            process.PeakWorkingSet64, process.WorkingSet64, hashes, expectedJpegSha256 = facts.JpegSha256,
            outputsMatch = hashes.Count == 1 && hashes.Single() == facts.JpegSha256, results,
            boundary = "Raw file read + fixed native display transfer + Bayer reconstruction where applicable + JPEG encode. This is the real image algorithm path, not the complete CameraAgent durable capture pipeline."
        });
        if (hashes.Count != 1 || hashes.Single() != facts.JpegSha256) throw new InvalidOperationException("Output changed under workload.");
        byte[] Operation()
        {
            var raw = File.ReadAllBytes(rawFile);
            var preview = FixedDisplayTransfer.Apply(facts.Layout, raw, Transfer);
            return Encode(preview, facts.Layout.Width, facts.Layout.Height, facts.Color);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private sealed record Foreground(FrameLayoutDescriptor Layout, bool Color, string RawSha256, string JpegSha256);
}
