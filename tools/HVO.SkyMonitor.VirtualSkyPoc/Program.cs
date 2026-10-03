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

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static class Program
{
    private const int Size = 640;
    private const int PathRows = 361;
    private const double Latitude = 35.347;
    private const double Longitude = -113.878;
    private const double Elevation = 0;
    private const double MagnitudeZeroRate = 1000;
    private static readonly DateTimeOffset LocalDateStart = new(2026, 10, 12, 7, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly AstronomyEnginePlanetEphemeris Ephemeris = new();
    private static readonly AstronomyEngineSolarEventCalculator Events = new();
    private static readonly CaptureSetpoint Nominal = new(TimeSpan.FromSeconds(20), 0, null, null);

    public static async Task Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("samples" or "day" or "measure" or "details"))
            throw new ArgumentException("Usage: VirtualSkyPoc samples|day|measure|details OUTPUT_DIRECTORY VERIFIED_CATALOG_ROOT");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Use a new empty output directory; retained evidence is immutable.");
        Directory.CreateDirectory(output);
        WriteIdentity(output);
        var snapshot = CatalogSnapshotResolver.Resolve(new(args[2], "hyg-v42-production")
        {
            ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1"
        });
        var config = Config("SolarDriven");
        var start = Find(SolarEventKind.Sunrise, LocalDateStart, LocalDateStart.AddDays(1));
        var end = Find(SolarEventKind.Sunrise, LocalDateStart.AddDays(1), LocalDateStart.AddDays(2));
        WriteJson(Path.Combine(output, "scenario.json"), new
        {
            recipe = "virtualsky-full-day-poc-v2",
            label = "2026-10-12",
            timezone = "America/Phoenix",
            dateMapping = "Starting sunrise: the 12th covers sunrise on the 12th through sunrise on the 13th",
            interval = "[start,end)",
            start,
            end,
            publicationEligibleUtc = end,
            finality = "Full-period output eligible only after end AND source processing settles; no production scheduler",
            historicalArchiveCompatibility = "Not implemented; historical noon-based captures are unchanged",
            latitude = Latitude,
            longitude = Longitude,
            elevationMeters = Elevation,
            solarEventAlgorithm = AstronomyEngineSolarEventCalculator.Version,
            snapshot.SnapshotVersion,
            snapshot.DatabaseSha256,
            snapshot.RowCount,
            renderingPrototype = "33712845b6b31c9382b24bb8b0eb8ac63723b486",
            composerPrototype = "75185e7e794b1028f28ac3f09e7125c14a1b4e98",
            display = "black=64 white=4095 stored native 12-bit; fixed gamma 2.2 after linear Bayer reconstruction; no per-frame stretch",
            exposurePolicy = "Open-loop POC formula: clamp(2500 / modeled scalar sky rate, 1 microsecond, 20 seconds); gain 0. Not production automatic exposure.",
            cadenceSeconds = 60,
            exposureIsNotCadence = true,
            config
        });
        Console.WriteLine($"Verified catalog: {snapshot.RowCount} rows, {snapshot.DatabaseSha256}; {start:O} to {end:O}");
        using var process = Process.GetCurrentProcess();
        var beforeCpu = process.TotalProcessorTime;
        var beforeAllocated = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        if (args[0] == "samples") await Samples(snapshot.Catalog, output, start, end).ConfigureAwait(false);
        else if (args[0] == "day") await Day(snapshot.Catalog, output, start, end).ConfigureAwait(false);
        else if (args[0] == "measure") await Measure(snapshot.Catalog, output).ConfigureAwait(false);
        else await Details(snapshot.Catalog, output).ConfigureAwait(false);
        process.Refresh();
        WriteJson(Path.Combine(output, args[0] + "-resources.json"), new
        {
            elapsedSeconds = timer.Elapsed.TotalSeconds,
            processCpuSeconds = (process.TotalProcessorTime - beforeCpu).TotalSeconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - beforeAllocated,
            process.PeakWorkingSet64,
            process.WorkingSet64,
            runtime = Environment.Version.ToString(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            concurrency = 1,
            frameWidth = Size,
            frameHeight = Size,
            retention = "Matched samples keep raw. Sequence raw is checksummed then released; one current source and bounded product buffers in memory. PNG and per-source evidence remain on disk.",
            backlog = 0,
            externalServices = "none",
            build = "Release"
        });
    }

    private static void WriteIdentity(string output, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var assemblies = new[] { typeof(Program).Assembly, typeof(VirtualSkyCameraModule).Assembly,
            typeof(SolarDiskEphemeris).Assembly, typeof(SolarDiskRenderPlan).Assembly };
        var identities = assemblies.Select(assembly => new
        {
            name = assembly.GetName().Name,
            informationalVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
        }).ToArray();
        File.Copy(source, Path.Combine(output, "harness-source.cs"));
        WriteJson(Path.Combine(output, "harness-identity.json"), new
        {
            assemblies = identities,
            sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
            sourceBinding = "Source retained at launch; compiled assembly hashes and embedded Git identities are authoritative"
        });
    }

    private static DateTimeOffset Find(SolarEventKind kind, DateTimeOffset start, DateTimeOffset end)
        => Events.Find(kind, start, end, Latitude, Longitude, Elevation).Utc
            ?? throw new InvalidOperationException($"No {kind} in the selected site/date interval.");

    private static async Task Measure(ICelestialCatalog catalog, string output)
    {
        var measurements = new List<object>();
        foreach (var phase in new[] { "day", "night" })
        {
            var utc = LocalDateStart.AddHours(phase == "day" ? 12 : 24);
            foreach (var (mode, disks) in new[] { ("LegacyScalarSolar", false), ("SolarDriven", false), ("SolarDriven", true) })
            {
                var setpoint = Policy(utc);
                for (var i = 0; i < 5; i++) _ = await Capture(catalog, utc, setpoint, mode, renderDisks: disks).ConfigureAwait(false);
                var times = new List<double>();
                var hashes = new HashSet<string>(StringComparer.Ordinal);
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes();
                for (var i = 0; i < 30; i++)
                {
                    var clock = Stopwatch.StartNew();
                    var frame = await Capture(catalog, utc, setpoint, mode, renderDisks: disks).ConfigureAwait(false);
                    times.Add(clock.Elapsed.TotalMilliseconds);
                    hashes.Add(Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)));
                }
                process.Refresh();
                if (hashes.Count != 1) throw new InvalidOperationException("Repeated source pixels are not deterministic.");
                times.Sort();
                var result = new
                {
                    phase,
                    mode,
                    renderSolarSystemDisks = disks,
                    sourceUtc = utc,
                    setpoint,
                    warmup = 5,
                    measured = 30,
                    concurrency = 1,
                    medianMilliseconds = (times[14] + times[15]) / 2,
                    p95Milliseconds = times[28],
                    totalMilliseconds = times.Sum(),
                    capturesPerSecond = 30000 / times.Sum(),
                    cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    process.PeakWorkingSet64,
                    rawSha256 = hashes.Single(),
                    rawBytesProduced = 30L * Size * Size * 2,
                    diskBytes = 0,
                    backlog = 0,
                    boundary = "Whole capture including independent module initialization and scene/sensor work; shared preloaded catalog and file writes excluded. Host also running unrelated candidate gates; CPU/allocation evidence retained separately from elapsed latency."
                };
                measurements.Add(result);
                Console.WriteLine(JsonSerializer.Serialize(result, Json));
            }
        }
        WriteJson(Path.Combine(output, "renderer-comparison.json"), measurements);
    }

    private static CameraModuleConfig Config(string mode, CameraPixelFormat format = CameraPixelFormat.BayerRggb16,
        DateTimeOffset? celestialStart = null, bool renderDisks = true)
        => new(new ObservatoryLocation(Latitude, Longitude, Elevation, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                illuminationMode = mode,
                fixedSceneUtc = celestialStart,
                renderSolarSystemDisks = renderDisks,
                seed = 1131,
                maximumMagnitude = 6.5,
                maximumResults = 32768,
                magnitudeZeroElectronsPerSecond = MagnitudeZeroRate,
                bortleClass = 3,
                asi676Sensor = new { enabled = format != CameraPixelFormat.Rgb24, blackLevelAdu = 64 },
                shotNoiseEnabled = true,
                vignettingStrength = .15,
                psfSigmaPixels = .85,
                psfRadiusPixels = 3.5
            })),
            new CameraRigConfig(new SensorProfile("POC reduced all-sky sensor", Size, Size, 2,
                    format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    format, format switch
                    {
                        CameraPixelFormat.Mono16 => SensorResponseMode.Monochrome,
                        CameraPixelFormat.Rgb24 => SensorResponseMode.RenderedRgb,
                        _ => SensorResponseMode.BayerRaw
                    }, SensorRecipeVersion: "poc-asi676-reduced-native12-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    Size / 2d, Size / 2d, Size * .49, HorizontalFlip: true, CalibrationVersion: "poc-640-upward-equidistant-180-v2"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(20), 0, 0)),
            CapturePipelineConfig.Empty);

    private static CaptureSetpoint Policy(DateTimeOffset utc)
    {
        var projection = RigProjectionContextFactory.Create(Config("SolarDriven").Rig);
        var altitude = SolarAltitudeClassifier.DirectionAt(Ephemeris, utc, Latitude, Longitude).AltitudeDegrees;
        var night = SkyBrightnessModel.PhotometricBackgroundElectronsPerSecond(3, MagnitudeZeroRate,
            projection.FocalLengthXPixels, projection.FocalLengthYPixels);
        var rate = StellarSkyBackgroundModel.Resolve(night, altitude);
        return Nominal with { Exposure = TimeSpan.FromSeconds(Math.Clamp(2500 / rate, .000001, 20)) };
    }

    private static Task<CameraFrame> Capture(ICelestialCatalog catalog, DateTimeOffset utc, CaptureSetpoint setpoint,
        string mode = "SolarDriven", CameraPixelFormat format = CameraPixelFormat.BayerRggb16, bool renderDisks = true)
        => CaptureConfigured(catalog, Config(mode, format, utc, renderDisks), setpoint);

    private static async Task<CameraFrame> CaptureConfigured(ICelestialCatalog catalog, CameraModuleConfig config, CaptureSetpoint setpoint)
    {
        // A fresh module makes each recorded (time, options, seed) independently reproducible.
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await using var moduleDisposal = module.ConfigureAwait(false);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        return (await module.CaptureAsync(new(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), CaptureMode.Still, setpoint),
            CancellationToken.None).ConfigureAwait(false)).Frame ?? throw new InvalidOperationException("Capture produced no frame.");
    }

    private static async Task Samples(ICelestialCatalog catalog, string output, DateTimeOffset start, DateTimeOffset end)
    {
        var phases = new (string Name, DateTimeOffset Utc)[]
        {
            ("sunrise", start), ("day", LocalDateStart.AddHours(12)),
            ("sunset", Find(SolarEventKind.Sunset, start, end)),
            ("civil-dusk", Find(SolarEventKind.CivilDusk, start, end)),
            ("nautical-dusk", Find(SolarEventKind.NauticalDusk, start, end)),
            ("astronomical-dusk", Find(SolarEventKind.AstronomicalDusk, start, end)),
            ("night", LocalDateStart.AddHours(24)),
            ("astronomical-dawn", Find(SolarEventKind.AstronomicalDawn, start, end)),
            ("nautical-dawn", Find(SolarEventKind.NauticalDawn, start, end)),
            ("civil-dawn", Find(SolarEventKind.CivilDawn, start, end)), ("next-sunrise", end)
        };
        var index = new List<object>();
        foreach (var phase in phases)
        {
            var policy = Policy(phase.Utc);
            foreach (var variant in new[] { "policy", "policy-high-gain", "fixed-short", "long-low-gain", "long-high-gain", "controlled-night" })
            {
                var setpoint = variant switch
                {
                    "fixed-short" => Nominal with { Exposure = TimeSpan.FromMilliseconds(.5) },
                    "long-low-gain" => Nominal,
                    "long-high-gain" => Nominal with { Gain = 180 },
                    "policy-high-gain" => policy with { Gain = 180 },
                    "controlled-night" => Nominal,
                    _ => policy
                };
                var mode = variant == "controlled-night" ? "ControlledNight" : "SolarDriven";
                var frame = await Capture(catalog, phase.Utc, setpoint, mode).ConfigureAwait(false);
                var name = phase.Name + "-" + variant;
                await File.WriteAllBytesAsync(Path.Combine(output, name + ".raw"), frame.PixelData.ToArray()).ConfigureAwait(false);
                var display = Display(frame, 2.2);
                SavePng(Path.Combine(output, name + ".png"), display, Size, Size);
                SavePng(Path.Combine(output, name + "-linear.png"), Display(frame, 1), Size, Size);
                var evidence = Evidence(frame, name, display);
                WriteJson(Path.Combine(output, name + ".json"), evidence);
                index.Add(evidence);
                if (variant == "policy") SavePanorama(output, name, display, frame);
                Console.WriteLine($"sample {name}: exp={setpoint.Exposure.TotalSeconds:R}s gain={setpoint.Gain} sun={frame.Metadata.Extra!["stellarSolarAltitudeDegrees"]} admitted={frame.Metadata.Extra["stellarAdmittedCount"]}");
            }
        }
        WriteJson(Path.Combine(output, "samples.json"), index);
    }

    private static void SavePanorama(string output, string name, byte[] rgb, CameraFrame frame)
    {
        const int width = 1440, height = 360;
        var projection = RigProjectionContextFactory.Create(Config("SolarDriven").Rig);
        var projector = ProjectorFactory.Create(projection);
        var panorama = new byte[width * height * 3];
        var mapped = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var direction = new AltAzPoint(90 - 90d * (y + .5) / height, 360d * (x + .5) / width);
                if (projector.Project(direction) is not { } point) continue;
                var sx = (int)Math.Floor(point.X - .5);
                var sy = (int)Math.Floor(point.Y - .5);
                if (sx < 0 || sy < 0 || sx + 1 >= Size || sy + 1 >= Size ||
                    !projection.ContainsSample(sx + .5, sy + .5) || !projection.ContainsSample(sx + 1.5, sy + 1.5) ||
                    !projection.ContainsSample(sx + 1.5, sy + .5) || !projection.ContainsSample(sx + .5, sy + 1.5)) continue;
                var fx = point.X - .5 - sx;
                var fy = point.Y - .5 - sy;
                for (var c = 0; c < 3; c++)
                    panorama[(y * width + x) * 3 + c] = (byte)Math.Round(
                        rgb[(sy * Size + sx) * 3 + c] * (1 - fx) * (1 - fy) +
                        rgb[(sy * Size + sx + 1) * 3 + c] * fx * (1 - fy) +
                        rgb[((sy + 1) * Size + sx) * 3 + c] * (1 - fx) * fy +
                        rgb[((sy + 1) * Size + sx + 1) * 3 + c] * fx * fy);
                mapped++;
            }
        SavePng(Path.Combine(output, name + "-panorama.png"), panorama, width, height);
        WriteJson(Path.Combine(output, name + "-panorama.json"), new
        {
            algorithm = "poc-shared-projector-equirectangular-bilinear-v1",
            width,
            height,
            projection,
            azimuthDegrees = "left 0 north; 90 east; 180 south; 270 west; right 360 north",
            altitudeDegrees = "top 90 zenith; bottom 0 horizon; pixel centers sampled",
            transfer = "Bilinear resampling of the existing fixed-gamma display; no new stretch",
            missing = "Black where all four source samples are not available; no extrapolation",
            mappedPixels = mapped,
            unavailablePixels = width * height - mapped,
            sourceRawSha256 = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)),
            sourceDisplayPixelsSha256 = Convert.ToHexString(SHA256.HashData(rgb)),
            panoramaPixelsSha256 = Convert.ToHexString(SHA256.HashData(panorama))
        });
    }

    private static async Task Details(ICelestialCatalog catalog, string output)
    {
        var cases = new[]
        {
            ("sun-noon", SolarSystemBody.Sun, LocalDateStart.AddHours(12)),
            ("moon-period-crescent", SolarSystemBody.Moon, LocalDateStart.AddHours(15)),
            ("moon-additional-gibbous", SolarSystemBody.Moon, LocalDateStart.AddDays(7).AddHours(18)),
            ("moon-additional-full", SolarSystemBody.Moon, LocalDateStart.AddDays(14).AddHours(1))
        };
        var evidence = new List<object>();
        foreach (var (name, body, utc) in cases)
        {
            var appearance = SolarDiskEphemeris.Get(body, utc, new(Latitude, Longitude, Elevation));
            var config = Config("SolarDriven", celestialStart: utc);
            config = config with
            {
                Rig = config.Rig with
                {
                    Optics = new OpticsProfile("Perspective", 0, 1.5, 0, LensKind.Rectilinear,
                    HorizontalFlip: true, CalibrationVersion: "poc-disk-detail-1.5-degree-v1"),
                    Orientation = new RigOrientation(appearance.Direction.AltitudeDegrees, appearance.Direction.AzimuthDegrees, 0)
                }
            };
            var projection = RigProjectionContextFactory.Create(config.Rig);
            var radiusPixels = projection.FocalLengthXPixels * Math.Tan(appearance.AngularRadiusDegrees * Math.PI / 180);
            var litArea = Math.PI * radiusPixels * radiusPixels * appearance.IlluminatedFraction;
            var flux = MagnitudeZeroRate * Math.Pow(10, -.4 * appearance.VisualMagnitude);
            var setpoint = Nominal with { Exposure = TimeSpan.FromSeconds(Math.Clamp(1500 * litArea / flux, .000001, 1)) };
            var frame = await CaptureConfigured(catalog, config, setpoint).ConfigureAwait(false);
            var display = Display(frame, 2.2, config);
            SavePng(Path.Combine(output, name + ".png"), display, Size, Size);
            SavePng(Path.Combine(output, name + "-linear.png"), Display(frame, 1, config), Size, Size);
            await File.WriteAllBytesAsync(Path.Combine(output, name + ".raw"), frame.PixelData.ToArray()).ConfigureAwait(false);
            evidence.Add(new
            {
                name,
                appearance,
                fieldOfViewDegrees = 1.5,
                label = "Separate narrow-field VirtualSky capture; not an enlarged disk pasted into the all-sky sequence",
                exposurePolicy = "1500 electrons per illuminated projected disk pixel; clamped 1 microsecond to 1 second to stay within the existing stellar temporal budget",
                source = Evidence(frame, name, display, config)
            });
        }
        WriteJson(Path.Combine(output, "details.json"), evidence);
    }

    private static async Task Day(ICelestialCatalog catalog, string output, DateTimeOffset start, DateTimeOffset end)
    {
        var count = (int)Math.Ceiling((end - start).TotalMinutes);
        var projector = ProjectorFactory.Create(RigProjectionContextFactory.Create(Config("SolarDriven").Rig));
        var path = Enumerable.Range(0, PathRows).Select(row => projector.Project(row <= (PathRows - 1) / 2
            ? new AltAzPoint(row * 180d / (PathRows - 1), 0)
            : new AltAzPoint((PathRows - 1 - row) * 180d / (PathRows - 1), 180))).ToArray();
        WriteJson(Path.Combine(output, "sample-path.json"), path);
        var keogram = new byte[count * PathRows * 3];
        byte[]? trail = null;
        var trailTimes = new List<DateTimeOffset>();
        var rows = new List<object>();
        Directory.CreateDirectory(Path.Combine(output, "sequence"));
        var outageStart = LocalDateStart.AddHours(25.5);
        var outageEnd = outageStart.AddMinutes(30);
        var darkStart = Find(SolarEventKind.AstronomicalDusk, start, end);
        var darkEnd = Find(SolarEventKind.AstronomicalDawn, start, end);
        double integratedSeconds = 0;
        for (var slot = 0; slot < count; slot++)
        {
            var utc = start.AddMinutes(slot);
            var missing = utc < start.AddMinutes(15) ? "leading-unscheduled" :
                utc >= end.AddMinutes(-15) ? "trailing-capture-missing" :
                utc >= outageStart && utc < outageEnd ? "deliberate-30-minute-outage" : null;
            byte[] rgb;
            object? evidence = null;
            var dark = false;
            if (missing is null)
            {
                var frame = await Capture(catalog, utc, Policy(utc)).ConfigureAwait(false);
                rgb = Display(frame, 2.2);
                evidence = Evidence(frame, $"sequence/{slot:D4}", rgb);
                var column = KeogramComposer.Compose([new(Size, Size, Size * 3, CameraPixelFormat.Rgb24, rgb, utc)], new(path));
                for (var row = 0; row < PathRows; row++)
                    column.PixelData.Span.Slice(row * 3, 3).CopyTo(keogram.AsSpan((row * count + slot) * 3, 3));
                dark = utc >= darkStart && utc + frame.Metadata.Exposure <= darkEnd &&
                    double.Parse(frame.Metadata.Extra!["stellarSolarAltitudeDegrees"], CultureInfo.InvariantCulture) <= -18;
                if (dark)
                {
                    trail = trail is null ? rgb.ToArray() : StarTrailComposer.Compose([
                        new(Size, Size, Size * 3, CameraPixelFormat.Rgb24, trail, trailTimes[^1]),
                        new(Size, Size, Size * 3, CameraPixelFormat.Rgb24, rgb, utc)]).PixelData.ToArray();
                    trailTimes.Add(utc);
                    integratedSeconds += frame.Metadata.Exposure.TotalSeconds;
                }
            }
            else
            {
                rgb = new byte[Size * Size * 3];
                for (var row = 0; row < PathRows; row++)
                {
                    var offset = (row * count + slot) * 3;
                    var value = (byte)(((row / 6 + slot / 6) & 1) == 0 ? 45 : 80);
                    keogram[offset] = value; keogram[offset + 1] = value; keogram[offset + 2] = value;
                }
            }
            SavePng(Path.Combine(output, "sequence", $"{slot:D4}.png"), rgb, Size, Size);
            rows.Add(new { slot, utc, localTime = utc.ToOffset(TimeSpan.FromHours(-7)), missing, dark, evidence });
            if (slot % 30 == 0) Console.WriteLine($"slot {slot}/{count} {utc:O} missing={missing ?? "none"} darkSources={trailTimes.Count}");
        }
        SavePng(Path.Combine(output, "keogram-pixels.png"), keogram, count, PathRows);
        if (trail is not null) SavePng(Path.Combine(output, "star-trail.png"), trail, Size, Size);
        WriteJson(Path.Combine(output, "sequence.json"), rows);
        WriteJson(Path.Combine(output, "coverage.json"), new
        {
            start,
            end,
            count,
            cadenceSeconds = 60,
            darkStart,
            darkEnd,
            outageStart,
            outageEnd,
            leadingEnd = start.AddMinutes(15),
            trailingStart = end.AddMinutes(-15),
            darkSourceCount = trailTimes.Count,
            integratedSeconds,
            darkSourceTimes = trailTimes,
            keogramAlgorithm = KeogramComposer.AlgorithmVersion,
            axisAlgorithm = "poc-fixed-planned-period-minute-slots-v1",
            trailAlgorithm = StarTrailComposer.AlgorithmVersion,
            trailCaveat = "Lighten of actual discrete exposures only; minute cadence leaves unexposed intervals, including the deliberate outage. No arcs or missing pixels are synthesized.",
            states = "leading intentionally unscheduled; internal 30-minute outage; trailing missing. No quality exclusions or unelapsed interval in this completed-period run."
        });
    }

    private static object Evidence(CameraFrame frame, string name, byte[] display, CameraModuleConfig? captureConfig = null)
    {
        var projection = RigProjectionContextFactory.Create((captureConfig ?? Config("SolarDriven", frame.PixelFormat)).Rig);
        var samples = new List<int>();
        var saturated = 0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (!projection.ContainsSample(x + .5, y + .5)) continue;
                var offset = (y * Size + x) * 2;
                var value = frame.PixelData.Span[offset] | frame.PixelData.Span[offset + 1] << 8;
                samples.Add(value);
                if (value >= 4095) saturated++;
            }
        samples.Sort();
        return new
        {
            name,
            frame.TimestampUtc,
            sourceUtc = frame.Metadata.Scene!.VirtualExposure!.CelestialStartUtc,
            exposureSeconds = frame.Metadata.Exposure.TotalSeconds,
            frame.Metadata.Gain,
            configuration = captureConfig ?? Config(frame.Metadata.Extra!["skyIlluminationMode"], frame.PixelFormat,
                frame.Metadata.Scene.VirtualExposure.CelestialStartUtc),
            rawSha256 = Convert.ToHexString(SHA256.HashData(frame.PixelData.Span)),
            displayPixelsSha256 = Convert.ToHexString(SHA256.HashData(display)),
            rawMean = samples.Average(),
            p01 = samples[(int)(samples.Count * .01)],
            p50 = samples[samples.Count / 2],
            p99 = samples[(int)(samples.Count * .99)],
            saturationFraction = saturated / (double)samples.Count,
            activeSampleCount = samples.Count,
            linearMean = (samples.Average() - 64) / (4095 - 64),
            frame.Layout,
            frame.Metadata.Extra,
            frame.Metadata.Scene,
            displayBlack = 64,
            displayWhite = 4095,
            displayGamma = 2.2,
            source = "Actual VirtualSky output; no invented sky pixels"
        };
    }

    private static byte[] Display(CameraFrame frame, double gamma, CameraModuleConfig? captureConfig = null)
    {
        var values = new double[Size * Size];
        var valid = new bool[values.Length];
        var projection = RigProjectionContextFactory.Create((captureConfig ?? Config("SolarDriven", frame.PixelFormat)).Rig);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (frame.PixelData.Span[i * 2] | frame.PixelData.Span[i * 2 + 1] << 8) - 64;
            valid[i] = projection.ContainsSample(i % Size + .5, i / Size + .5);
        }
        var linear = LinearBayerReconstruction.Reconstruct(values, valid, Size, Size);
        var rgb = new byte[values.Length * 3];
        for (var i = 0; i < values.Length; i++)
        {
            rgb[i * 3] = Transfer(linear.Red.Span[i], gamma);
            rgb[i * 3 + 1] = Transfer(linear.Green.Span[i], gamma);
            rgb[i * 3 + 2] = Transfer(linear.Blue.Span[i], gamma);
        }
        return rgb;
    }

    private static byte Transfer(double value, double gamma)
        => (byte)Math.Round(255 * Math.Pow(Math.Clamp(value / (4095 - 64), 0, 1), 1 / gamma));

    private static void SavePng(string path, byte[] rgb, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = rgb[i * 3]; rgba[i * 4 + 1] = rgb[i * 3 + 1]; rgba[i * 4 + 2] = rgb[i * 3 + 2]; rgba[i * 4 + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        encoded.SaveTo(file);
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
