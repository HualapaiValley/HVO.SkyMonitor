using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static partial class Program
{
    private static readonly JsonSerializerOptions SatelliteJson = new(Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    private static readonly string[] SatelliteLimitations =
    [
        "Shared synthetic optical profile is not inferred physical brightness or object type.",
        "Engineering fixtures only; no sensitivity, false-positive rate, identity or #530 measured-association qualification.",
        "Multiple-object runs exercise supplied track crowding only; no constellation membership or completeness claim.",
        "640px maximum, accelerated virtual exposures; no five-second full-resolution host cadence or live publication claim.",
        "No catalog star masks; moving stellar residuals can produce confusers.",
        "64 renderer midpoint samples per exposure; input sampling tolerance does not qualify raster integration.",
        "Private truth and labeled diagnostic derivatives are test-only and never detector inputs."
    ];

    // This is an optical stimulus, deliberately separate from the shared orbital contracts.
    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated through strict JSON deserialization.")]
    private sealed record SatelliteLightCurvePoint([property: JsonRequired] double OffsetSeconds, [property: JsonRequired] double MagnitudeDelta);
    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated through strict JSON deserialization.")]
    private sealed record SatelliteOpticalProfile([property: JsonRequired] string SchemaVersion, [property: JsonRequired] bool IsSynthetic,
        [property: JsonRequired] double Magnitude, [property: JsonRequired] double AngularWidthDegrees,
        [property: JsonRequired] double RedWeight, [property: JsonRequired] double GreenWeight, [property: JsonRequired] double BlueWeight,
        [property: JsonRequired] double ExposureSeconds, [property: JsonRequired] int Seed,
        [property: JsonRequired] IReadOnlyList<SatelliteLightCurvePoint> LightCurve);

    private static async Task SatellitePoc(string[] args)
    {
        var canonical = args[0] == "satellite-tracks";
        if (canonical && args.Length is not (11 or 16 or 21 or 26))
            throw new ArgumentException("Supply 1..4 distinct object groups, each with five canonical track files in capture order (5/10/15/20 inputs).");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new ArgumentException("Use a new empty output directory; evidence is immutable.");
        var config = JsonSerializer.Deserialize<CameraModuleConfig>(ReadSatelliteInput(args[3]), Json)
            ?? throw new ArgumentException("Missing CameraModuleConfig.");
        var profile = JsonSerializer.Deserialize<SatelliteOpticalProfile>(ReadSatelliteInput(args[4]), SatelliteJson)
            ?? throw new ArgumentException("Missing synthetic optical profile.");
        ValidateSatelliteProfile(profile);
        var projection = RigProjectionContextFactory.Create(config.Rig);
        // The initial engineering lane stays modest; arbitrary full-resolution qualification belongs to #1395.
        if (projection.WidthPixels > 640 || projection.HeightPixels > 640 ||
            config.Rig.Sensor.PixelFormat != CameraPixelFormat.BayerRggb16 ||
            config.Module.Type != "VirtualSky")
            throw new ArgumentException("This bounded harness requires VirtualSky, RGGB16, and at most 640x640 output.");
        var fontNotice = args[5] + ".license";
        if (!File.Exists(args[5]) || !File.Exists(fontNotice))
            throw new ArgumentException("Supply an explicit font plus adjacent FONT_PATH.license for retained redistribution notices.");
        DateTimeOffset first;
        IReadOnlyList<VirtualTransientSkyTrack> primitives;
        IReadOnlyList<PixelPoint[]>[] predicted;
        OrbitalExposureTrackV1[] tracks = [];
        object provenance;
        if (canonical)
        {
            tracks = args.Skip(6).Select(path => OrbitalPredictionJson.ParseTrack(ReadSatelliteInput(path))).ToArray();
            first = tracks[0].RequestedInterval.StartUtc;
            var geometryError = 0d;
            var frameSegments = Enumerable.Range(0, 5).Select(_ => new List<PixelPoint[]>()).ToArray();
            var all = new List<VirtualTransientSkyTrack>();
            var objects = new HashSet<OrbitalObjectIdentity>();
            for (var group = 0; group < tracks.Length / 5; group++)
            {
                var initial = tracks[group * 5];
                if (!objects.Add(initial.Lineage.ObjectIdentity))
                    throw new ArgumentException("Duplicate object groups are not allowed.");
                ValidateSatelliteGroup(initial, tracks[0]);
                for (var i = 0; i < 5; i++)
                {
                    var track = tracks[group * 5 + i];
                    ValidateSatelliteTrack(track, initial, config, first.AddSeconds(i * profile.ExposureSeconds), profile.ExposureSeconds);
                    var projector = ProjectorFactory.Create(track.Geometry.Projection);
                    foreach (var sample in track.Samples.Where(s => s.Pixel is not null))
                    {
                        var source = projector.Project(sample.Horizontal) ?? throw new ArgumentException("Track projects outside its declared geometry.");
                        // Pixel is already emitted-image geometry. Recompute from horizontal, applying only the remaining transform once.
                        var actual = ProjectedSceneImageTransform.Apply(track.Geometry.ImageTransform, source);
                        geometryError = Math.Max(geometryError, double.Hypot(actual.X - sample.Pixel!.Value.X, actual.Y - sample.Pixel.Value.Y));
                    }
                    frameSegments[i].AddRange(track.Segments.Select(segment => track.Samples.Skip(segment.StartSampleIndex)
                        .Take(segment.SampleCount).Select(s => s.Pixel!.Value).ToArray()));
                    foreach (var segment in track.Segments)
                        AddSatellitePrimitives(all, track.Samples.Skip(segment.StartSampleIndex).Take(segment.SampleCount)
                            .Select(s => SatelliteKeyframe(s.Utc, s.Horizontal, first, profile)).ToArray());
                }
            }
            if (geometryError > 1e-8) throw new ArgumentException($"Track projection/readout mismatch: {geometryError:R} pixels.");
            predicted = frameSegments;
            primitives = all;
            provenance = new
            {
                mode = "canonical-engine-products",
                parser = "OrbitalPredictionJson.ParseTrack (exact canonical bytes and content identity)",
                objectGroupCount = objects.Count,
                productOrder = "Five capture-ordered products per distinct object group; all groups share the same five intervals.",
                projectionMaximumResidualPixels = geometryError,
                products = tracks.Select(t => new { t.ProductSha256, t.Lineage, t.Model, t.Geometry, t.Timing, t.Sampling }),
                qualification = "Input provenance retained; this harness does not independently qualify the propagator."
            };
        }
        else
        {
            using var fixture = JsonDocument.Parse(ReadSatelliteInput(args[6]));
            var root = fixture.RootElement;
            if (root.GetProperty("schemaVersion").GetString() != "satellite-virtual-poc-v1" ||
                !root.GetProperty("model").GetString()!.Contains("improved i", StringComparison.Ordinal))
                throw new ArgumentException("Reference mode requires the retained, explicitly mode-i exploratory fixture.");
            var observer = root.GetProperty("observer");
            if (config.Observatory.LatitudeDegrees != observer.GetProperty("latitudeDegrees").GetDouble() ||
                config.Observatory.LongitudeDegrees != observer.GetProperty("longitudeDegrees").GetDouble() ||
                config.Observatory.ElevationMeters != observer.GetProperty("elevationMeters").GetDouble())
                throw new ArgumentException("Reference observer must match existing module settings.");
            first = root.GetProperty("sequenceStartUtc").GetDateTimeOffset();
            var states = root.GetProperty("exposureSamples").EnumerateArray().ToArray();
            var projector = ProjectorFactory.Create(projection);
            var all = new List<VirtualTransientSkyTrack>();
            predicted = new IReadOnlyList<PixelPoint[]>[5];
            for (var i = 0; i < 5; i++)
            {
                var start = first.AddSeconds(i * profile.ExposureSeconds);
                var end = start.AddSeconds(profile.ExposureSeconds);
                var samples = states.Where(s => s.GetProperty("utc").GetDateTimeOffset() >= start &&
                    s.GetProperty("utc").GetDateTimeOffset() <= end).ToArray();
                if (samples.Length < 2 || samples[0].GetProperty("utc").GetDateTimeOffset() != start ||
                    samples[^1].GetProperty("utc").GetDateTimeOffset() != end)
                    throw new ArgumentException("Reference must supply exact exposure endpoints; no resampling or propagation here.");
                var points = new List<PixelPoint>();
                foreach (var state in samples)
                {
                    var point = projector.Project(new(state.GetProperty("altitudeDegrees").GetDouble(), state.GetProperty("azimuthDegrees").GetDouble()))
                        ?? throw new ArgumentException("Reference leaves the supported aperture; use canonical segmented products instead.");
                    var expected = state.GetProperty("referencePixel");
                    if (double.Hypot(point.X - expected[0].GetDouble(), point.Y - expected[1].GetDouble()) > 1e-8)
                        throw new ArgumentException("Reference fixture does not match configured projection.");
                    points.Add(point);
                }
                predicted[i] = [points.ToArray()];
                AddSatellitePrimitives(all, samples.Select(s => SatelliteKeyframe(s.GetProperty("utc").GetDateTimeOffset(),
                    new(s.GetProperty("altitudeDegrees").GetDouble(), s.GetProperty("azimuthDegrees").GetDouble()), first, profile)).ToArray());
            }
            primitives = all;
            provenance = new
            {
                mode = "independent-reference-mode-i",
                model = root.GetProperty("model").Clone(),
                objectName = root.GetProperty("objectName").Clone(),
                catalogNumber = root.GetProperty("noradCatalogId").Clone(),
                inputGpSha256 = root.GetProperty("inputGpSha256").Clone(),
                referenceInputs = root.GetProperty("referenceInputs").Clone(),
                sampling = root.GetProperty("sampling").Clone(),
                qualification = "Retained Skyfield/python-sgp4 exploratory mode-i reference; not #529 mode-a engine output or qualification."
            };
        }
        var keyframeOffsets = primitives.SelectMany(t => t.Keyframes).Select(k => k.OffsetSeconds).ToHashSet();
        if (profile.LightCurve.Any(k => !keyframeOffsets.Contains(k.OffsetSeconds)))
            throw new ArgumentException("Light-curve knots must coincide with input samples; orbital interpolation belongs to Astronomy.");
        var scenario = new VirtualTransientScenarioDefinition
        {
            ScenarioId = "orbital-harness",
            ScenarioVersion = "2",
            Seed = profile.Seed,
            EpochUtc = first,
            TemporalSampleCount = 64,
            SkyTracks = primitives
        };
        scenario.Validate();
        CameraModuleConfig Configure(bool inject)
        {
            var options = JsonNode.Parse((config.Module.Options ?? throw new ArgumentException("Module options required.")).GetRawText())!.AsObject();
            if (options.ContainsKey("transientScenario") || options.ContainsKey("transientRecurrence"))
                throw new ArgumentException("Settings must contain no preexisting transient stimulus.");
            options.Remove("fixedSceneUtc");
            options["fixedSequenceStartUtc"] = JsonValue.Create(first);
            options["seed"] = profile.Seed;
            if (inject) options["transientScenario"] = JsonSerializer.SerializeToNode(scenario, Json);
            return config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options, Json) } };
        }
        var injectedConfig = Configure(true);
        var controlConfig = Configure(false);
        var snapshot = CatalogSnapshotResolver.Resolve(new(args[2], "hyg-v42-production")
        { ExpectedPackageVersion = "hyg-v4.2-p3-s2-r1" });
        Directory.CreateDirectory(output);
        var privatePath = Path.Combine(output, "private-truth");
        Directory.CreateDirectory(privatePath);
        WriteIdentity(output);
        WriteSatelliteSources(output);
        File.Copy(args[3], Path.Combine(privatePath, "input-settings.json"));
        File.Copy(args[4], Path.Combine(privatePath, "synthetic-profile.json"));
        File.Copy(args[5], Path.Combine(output, "diagnostic-font.ttf"));
        File.Copy(fontNotice, Path.Combine(output, "diagnostic-font.LICENSE"));
        for (var i = 6; i < args.Length; i++) File.Copy(args[i], Path.Combine(privatePath, $"input-{i - 5:00}.json"));
        WriteJson(Path.Combine(privatePath, "injected-config.json"), injectedConfig);
        WriteJson(Path.Combine(privatePath, "control-config.json"), controlConfig);
        WriteJson(Path.Combine(privatePath, "input-provenance.json"), provenance);
        WriteJson(Path.Combine(privatePath, "predicted-segments.json"), predicted);
        var measurements = new List<object>();
        async Task<CameraFrame[]> Sequence(CameraModuleConfig settings, string name)
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var timer = Stopwatch.StartNew();
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var disposal = module.ConfigureAwait(false);
            await module.InitializeAsync(settings, CancellationToken.None).ConfigureAwait(false);
            var frames = new CameraFrame[5];
            for (var i = 0; i < frames.Length; i++)
            {
                var started = first.AddSeconds(i * profile.ExposureSeconds);
                var result = await module.CaptureAsync(new(started, TimeSpan.FromSeconds(profile.ExposureSeconds), CaptureMode.Still,
                    new(TimeSpan.FromSeconds(profile.ExposureSeconds), settings.Rig.Pipeline.NightGain, null, null)), CancellationToken.None).ConfigureAwait(false);
                var frame = result.Frame ?? throw new InvalidOperationException("Capture returned no frame.");
                var exposure = frame.Metadata.Scene?.VirtualExposure ?? throw new InvalidOperationException("No virtual timing evidence.");
                if (exposure.CelestialStartUtc != started || exposure.ScenarioStartUtc != started ||
                    exposure.CelestialEndUtc != started.AddSeconds(profile.ExposureSeconds) ||
                    exposure.ScenarioEndUtc != exposure.CelestialEndUtc || frame.TimestampUtc != started ||
                    frame.Metadata.Exposure != TimeSpan.FromSeconds(profile.ExposureSeconds))
                    throw new InvalidOperationException("Capture logical exposure does not match requested track timing.");
                // The first group's frame index is authoritative; all groups have the same geometry/readout.
                if (canonical && (frame.Layout?.Readout ?? SatelliteReadout(settings)) != tracks[i].Geometry.Readout)
                    throw new InvalidOperationException("Captured readout differs from track facts.");
                ValidateSatelliteLayout(frame);
                frames[i] = frame;
                var label = $"{name}-{i + 1:00}";
                await File.WriteAllBytesAsync(Path.Combine(output, label + ".raw"), frame.PixelData.ToArray()).ConfigureAwait(false);
                SavePng(Path.Combine(output, label + ".png"), SatelliteDisplay(frame), frame.Width, frame.Height);
                WriteJson(Path.Combine(output, label + ".json"), new
                {
                    frame.TimestampUtc,
                    frame.Width,
                    frame.Height,
                    frame.Layout,
                    frame.Metadata.Exposure,
                    frame.Metadata.Gain,
                    rawSha256 = SatelliteHash(frame.PixelData.Span),
                    timing = "Timestamp identifies requested/logical virtual shutter start, not physical sensor timing. Accelerated readout.",
                    display = "Fixed 64..4095 code range; Bayer reconstruction; gamma 2.2; no frame-dependent stretch."
                });
                WriteJson(Path.Combine(privatePath, label + "-simulator.json"), frame.Metadata.Scene);
                Console.WriteLine($"{label}: {SatelliteHash(frame.PixelData.Span)}");
            }
            process.Refresh();
            measurements.Add(new
            {
                name,
                frames = 5,
                elapsedSeconds = timer.Elapsed.TotalSeconds,
                cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                process.PeakWorkingSet64,
                rawBytesProduced = frames.Sum(f => (long)f.PixelData.Length),
                boundary = "Module initialization + five accelerated captures + raw/PNG/JSON I/O. One run; not a cadence qualification."
            });
            return frames;
        }
        var control = await Sequence(controlConfig, "control").ConfigureAwait(false);
        var injected = await Sequence(injectedConfig, "injected").ConfigureAwait(false);
        var repeat = await Sequence(injectedConfig, "repeat").ConfigureAwait(false);
        if (!injected.Zip(repeat).All(p => p.First.PixelData.Span.SequenceEqual(p.Second.PixelData.Span)))
            throw new InvalidOperationException("Repeated raw pixels differ.");
        // These artifacts contain raw pixels, layout, timing and ordinary compatibility only. No scene metadata crosses this boundary.
        var controlDetection = SatelliteDetect(SatelliteArtifacts(control, config), projection, output, "control");
        var injectedDetection = SatelliteDetect(SatelliteArtifacts(injected, config), projection, output, "injected");
        var targetStart = first.AddSeconds(2 * profile.ExposureSeconds);
        var labels = canonical ? tracks.Where(t => t.RequestedInterval.StartUtc == targetStart)
            .Where(static t => t.Segments.Count > 0)
            .Select(static t => (t.Samples[t.Segments[0].StartSampleIndex].Pixel!.Value,
                $"Predicted NORAD {t.Lineage.ObjectIdentity.CatalogNumber}")).ToArray() :
            predicted[2].Take(1).Select(static p => (p[0], "Reference ISS 25544 (mode i)")).ToArray();
        DrawSatelliteDiagnostics(output, predicted[2], injectedDetection.Components, args[5], targetStart,
            profile.ExposureSeconds, canonical ? "canonical product" : "reference mode i", labels);
        WriteJson(Path.Combine(privatePath, "target-comparisons.json"), SatelliteCompare(predicted[2], injectedDetection.Components));
        WriteJson(Path.Combine(output, "validation.json"), new
        {
            schemaVersion = "satellite-harness-validation-v2",
            canonicalInput = canonical,
            inputProvenance = provenance,
            syntheticOpticalProfile = profile,
            repeatedFiveFrameRawPixelsIdentical = true,
            snapshot.DatabaseSha256,
            snapshot.RowCount,
            changedRawPixels = injected.Zip(control).Select(pair => Enumerable.Range(0, pair.First.Width * pair.First.Height)
                .Count(i => pair.First.PixelData.Span[i * 2] != pair.Second.PixelData.Span[i * 2] ||
                    pair.First.PixelData.Span[i * 2 + 1] != pair.Second.PixelData.Span[i * 2 + 1])).ToArray(),
            controlCandidates = controlDetection.Components.Count,
            injectedCandidates = injectedDetection.Components.Count,
            temporalMode = "CausalProvisional: target frame 3, N-2 = frame 1, N-1 = frame 2. No future frames.",
            coldStart = "Frames 1 and 2 produce Missing from the actual Processing temporal factory; no substitute background.",
            detectorBoundary = "ProcessingArtifact raw bytes + ordinary layout/time/compatibility; no orbital identity, path, magnitude, simulator metadata or private truth. Unchanged Processing temporal factory and Imaging extraction.",
            limitations = SatelliteLimitations,
            command = Environment.GetCommandLineArgs(),
            measurements
        });
        WriteSatelliteComparison(output);
        var hashes = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => new
            {
                path = Path.GetRelativePath(output, path),
                bytes = new FileInfo(path).Length,
                sha256 = SatelliteHash(File.ReadAllBytes(path))
            }).ToArray();
        WriteJson(Path.Combine(output, "sha256-manifest.json"), new { schemaVersion = "satellite-harness-files-v1", files = hashes });
        Console.WriteLine($"COMPLETE: causal target=3; control={controlDetection.Components.Count}; injected={injectedDetection.Components.Count}; repeat identical.");
    }

    private static byte[] ReadSatelliteInput(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > OrbitalPredictionJson.MaximumProductBytes)
            throw new ArgumentException("Input exceeds bounded product size.");
        return File.ReadAllBytes(path);
    }

    private static string SatelliteHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void ValidateSatelliteProfile(SatelliteOpticalProfile p)
    {
        if (p.SchemaVersion != "satellite-synthetic-optical-v1" || !p.IsSynthetic ||
            !double.IsFinite(p.Magnitude) || p.Magnitude is < -30 or > 30 ||
            !double.IsFinite(p.AngularWidthDegrees) || p.AngularWidthDegrees is <= 0 or > 2 ||
            !double.IsFinite(p.ExposureSeconds) || p.ExposureSeconds is <= 0 or > 20 ||
            p.LightCurve is null || p.LightCurve.Count > 128 ||
            new[] { p.RedWeight, p.GreenWeight, p.BlueWeight }.Any(v => !double.IsFinite(v) || v is < 0 or > 10) ||
            p.RedWeight + p.GreenWeight + p.BlueWeight <= 0)
            throw new ArgumentException("Invalid explicit synthetic optical profile.");
        var previous = double.NegativeInfinity;
        foreach (var k in p.LightCurve)
        {
            if (k is null || !double.IsFinite(k.OffsetSeconds) || k.OffsetSeconds < 0 || k.OffsetSeconds > 5 * p.ExposureSeconds ||
                k.OffsetSeconds <= previous || !double.IsFinite(k.MagnitudeDelta) ||
                p.Magnitude + k.MagnitudeDelta is < -30 or > 30)
                throw new ArgumentException("Invalid light curve.");
            previous = k.OffsetSeconds;
        }
        if (p.LightCurve.Count != 0 && (p.LightCurve.Count < 2 || p.LightCurve[0].OffsetSeconds != 0 ||
            p.LightCurve[^1].OffsetSeconds != 5 * p.ExposureSeconds))
            throw new ArgumentException("Light curve must span all five exposures or be empty for constant magnitude.");
    }

    private static VirtualTransientSkyKeyframe SatelliteKeyframe(DateTimeOffset utc, AltAzPoint direction,
        DateTimeOffset first, SatelliteOpticalProfile p)
    {
        var offset = (utc - first).TotalSeconds;
        var delta = 0d;
        for (var i = 1; i < p.LightCurve.Count; i++)
        {
            var a = p.LightCurve[i - 1]; var b = p.LightCurve[i];
            if (offset < a.OffsetSeconds || offset > b.OffsetSeconds) continue;
            delta = a.MagnitudeDelta + (b.MagnitudeDelta - a.MagnitudeDelta) *
                (offset - a.OffsetSeconds) / (b.OffsetSeconds - a.OffsetSeconds);
            break;
        }
        return new()
        {
            OffsetSeconds = offset,
            AltitudeDegrees = direction.AltitudeDegrees,
            AzimuthDegrees = direction.AzimuthDegrees,
            Magnitude = p.Magnitude + delta,
            AngularWidthDegrees = p.AngularWidthDegrees,
            RedWeight = p.RedWeight,
            GreenWeight = p.GreenWeight,
            BlueWeight = p.BlueWeight
        };
    }

    private static void AddSatellitePrimitives(List<VirtualTransientSkyTrack> target, VirtualTransientSkyKeyframe[] samples)
    {
        if (samples.Length < 2) throw new ArgumentException("Single-sample segments have no integration support.");
        for (var i = 0; i < samples.Length - 1; i += 127)
            target.Add(new() { PrimitiveId = $"p-{target.Count + 1:000}", Keyframes = samples.Skip(i).Take(128).ToArray() });
    }

    private static void ValidateSatelliteTrack(OrbitalExposureTrackV1 t, OrbitalExposureTrackV1 initial,
        CameraModuleConfig c, DateTimeOffset start, double seconds)
    {
        var geometry = t.Geometry;
        var expectedProjection = geometry.ProjectionCoordinates == OrbitalProjectionCoordinates.NativeSensor
            ? RigProjectionContextFactory.CreateNative(c.Rig) : RigProjectionContextFactory.Create(c.Rig);
        var transform = geometry.ImageTransform;
        var observer = new ObserverLocation(c.Observatory.LatitudeDegrees, c.Observatory.LongitudeDegrees, c.Observatory.ElevationMeters);
        if (t.Status != OrbitalExecutionStatus.Complete || t.CoveredInterval != t.RequestedInterval ||
            t.RequestedInterval.StartUtc != start || t.RequestedInterval.EndUtc != start.AddSeconds(seconds) ||
            geometry.Basis != OrbitalGeometryBasis.ConfiguredRig || geometry.HorizonPolicy != HorizonPolicy.GeometricHorizon ||
            !string.Equals(geometry.RigIdentitySha256, RigProjectionContextFactory.CreateProfileHashSha256(c.Rig), StringComparison.OrdinalIgnoreCase) ||
            geometry.Projection != expectedProjection || geometry.Readout != SatelliteReadout(c) || t.Observer != observer ||
            transform.HorizontalMirror || transform.VerticalMirror || transform.Rotation != ProjectedSceneQuarterRotation.Degrees0 ||
            geometry != initial.Geometry || t.Model != initial.Model ||
            t.Lineage.RecordSha256 != initial.Lineage.RecordSha256 || t.Lineage.ObjectIdentity != initial.Lineage.ObjectIdentity ||
            t.Lineage.SnapshotManifestSha256 != initial.Lineage.SnapshotManifestSha256 ||
            t.Lineage.NormalizedPayloadSha256 != initial.Lineage.NormalizedPayloadSha256 ||
            t.Lineage.SourceDataset != initial.Lineage.SourceDataset || t.Lineage.EpochUtc != initial.Lineage.EpochUtc ||
            t.Lineage.SelectionPolicySha256 != initial.Lineage.SelectionPolicySha256 ||
            t.Timing.ExposureBasis != OrbitalTimingBasis.Estimated || t.Timing.ReadoutCompletedUtc is not null ||
            t.Timing.ReadoutBasis != OrbitalTimingBasis.Unknown || t.Model.Refraction != "none-geometric")
            throw new ArgumentException("Track must be a complete configured-rig product matching settings, source/model and estimated logical exposure; readout unknown, no extra image mirrors/rotation.");
    }

    private static void ValidateSatelliteGroup(OrbitalExposureTrackV1 initial, OrbitalExposureTrackV1 shared)
    {
        // Record, source dataset and epoch belong to each object; shared provenance does not substitute for group validation.
        if (initial.Model != shared.Model || initial.Geometry != shared.Geometry || initial.Observer != shared.Observer ||
            initial.Lineage.SnapshotManifestSha256 != shared.Lineage.SnapshotManifestSha256 ||
            initial.Lineage.NormalizedPayloadSha256 != shared.Lineage.NormalizedPayloadSha256 ||
            initial.Lineage.SelectionPolicySha256 != shared.Lineage.SelectionPolicySha256)
            throw new ArgumentException("Object groups must share model, geometry, observer, snapshot, payload and selection policy.");
    }

    private static FrameReadoutDescriptor SatelliteReadout(CameraModuleConfig config)
        => config.Rig.Readout is { } readout ? SensorReadoutResolver.Resolve(config.Rig.Sensor, readout).Geometry :
            new(config.Rig.Sensor.WidthPixels, config.Rig.Sensor.HeightPixels, 0, 0,
                config.Rig.Sensor.WidthPixels, config.Rig.Sensor.HeightPixels, 1, 1, FrameBinningAlgorithm.IdentityV1, 0, 0);

    private static void ValidateSatelliteLayout(CameraFrame f)
    {
        var layout = f.Layout ?? throw new InvalidOperationException("No emitted layout.");
        if (f.PixelFormat != CameraPixelFormat.BayerRggb16 || layout.ByteOrder != FrameByteOrder.LittleEndian ||
            layout.BlackLevel != 64 || layout.WhiteLevel != 4095 || layout.StrideBytes != f.Width * 2 ||
            f.Width % 2 != 0 || f.Height % 2 != 0 || f.PixelData.Length != f.Width * f.Height * 2)
            throw new ArgumentException("Initial harness supports tight RGGB16 little-endian native12 codes, black=64, white=4095.");
    }

    private static byte[] SatelliteDisplay(CameraFrame frame)
    {
        var values = new double[frame.Width * frame.Height];
        var valid = new bool[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.PixelData.Span[(i * 2)..]);
            values[i] = value - 64;
            valid[i] = value != 0;
        }
        var linear = LinearBayerReconstruction.Reconstruct(values, valid, frame.Width, frame.Height);
        var rgb = new byte[values.Length * 3];
        for (var i = 0; i < values.Length; i++)
        {
            rgb[i * 3] = Transfer(linear.Red.Span[i], 2.2);
            rgb[i * 3 + 1] = Transfer(linear.Green.Span[i], 2.2);
            rgb[i * 3 + 2] = Transfer(linear.Blue.Span[i], 2.2);
        }
        return rgb;
    }

    private static void WriteSatelliteSources(string output, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var name in new[] { "Program.cs", "SatellitePoc.cs", "SatellitePocDetection.cs", "SatellitePocPresentation.cs",
            "SATELLITE-POC.md", "satellite_fixture.py", "satellite-requirements.txt" })
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, name), Path.Combine(output, name));
    }
}
