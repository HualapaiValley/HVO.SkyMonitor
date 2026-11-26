using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using static HVO.SkyMonitor.Processing.Tests.MeasuredStellarAssociationFixtures;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// #526 candidate-bound SNR accuracy against non-circular synthetic ground truth and deterministic detector, matcher,
/// and presentation resource/latency/candidate-count qualification across full-frame and transformed ROI scenes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class MeasuredAssociationQualificationTests
{
    private static readonly DateTimeOffset ExposureMidpointUtc = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 520);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task SyntheticCandidateBoundSnrErrorMatchesGroundTruthAcrossMono16AndBayerRggb16(CameraPixelFormat format)
    {
        const int width = 256;
        const int height = 256;
        const double background = 1000.0;
        const double noiseSigma = 6.0;
        const double psfSigma = 1.25;

        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var jupiterPos = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(jupiterPos.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, width / 2d, height / 2d, 500, 500, width, height,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var projector = ProjectorFactory.Create(projection);

        var starSpecs = new (string Id, double Dx, double Dy, double Mag, double Amplitude)[]
        {
            ("star:q1", -64, -48, 1.0, 2200),
            ("star:q2", 58, -42, 1.5, 1400),
            ("star:q3", -54, 52, 2.0, 900),
            ("star:q4", 62, 56, 2.4, 650)
        };
        const double jupiterAmplitude = 2800;

        var catalog = new List<CelestialCatalogObject>(starSpecs.Length);
        foreach (var (id, dx, dy, mag, _) in starSpecs)
        {
            var altAz = projector.Unproject(new(width / 2d + dx, height / 2d + dy))!.Value;
            var eq = EquatorialPrecession.PrecessToJ2000(
                CoordinateTransforms.HorizontalToEquatorial(altAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
                ExposureMidpointUtc);
            catalog.Add(new(id, id, eq.RightAscensionHours, eq.DeclinationDegrees, mag));
        }

        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(catalog), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, projection, new CatalogQuery(6.5, 100),
                new CatalogMetadata("qual", "1", new Uri("https://example.test/qual"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(width, height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");

        var amplitudesById = starSpecs.ToDictionary(static s => s.Id, static s => s.Amplitude, StringComparer.Ordinal);
        amplitudesById["solar-system:Jupiter"] = jupiterAmplitude;
        var sources = scene.Objects.Select(o => (o.Id, o.Pixel, Amplitude: amplitudesById[o.Id])).ToArray();

        // Build exact non-circular synthetic ground truth from noiseless PSF + independent noise-only calibration frame.
        var validMask = new bool[width * height];
        Array.Fill(validMask, true);
        var noiseOnly = CreateGaussianNoiseField(width, height, background, noiseSigma, seed: 526901);
        double effectiveNoiseSigma;
        if (format == CameraPixelFormat.BayerRggb16)
        {
            var noiseLum = LinearBayerReconstruction.Reconstruct(noiseOnly, validMask, width, height, BayerPattern.Rggb)
                .ToLuminance().Pixels.Span;
            effectiveNoiseSigma = SampleStdDev(noiseLum, width, height, margin: 8);
        }
        else
        {
            effectiveNoiseSigma = noiseSigma;
        }

        var measurementOptions = MeasuredStellarAssociationRecipe.MeasurementOptions(format);
        var segThreshold = Math.Max(
            measurementOptions.SegmentationSigma * effectiveNoiseSigma,
            measurementOptions.MinimumPeakAboveBackground * measurementOptions.SegmentationSigma / measurementOptions.PeakSigma);
        var growRadius = Math.Max(2, (int)Math.Ceiling(2 * measurementOptions.WindowSigmaPixels));

        var noiseless = new double[width * height];
        Array.Fill(noiseless, background);
        foreach (var (_, pixel, amplitude) in sources)
            AddNormalizedGaussian(noiseless, width, height, pixel, psfSigma, amplitude);
        var noiselessEval = format == CameraPixelFormat.BayerRggb16
            ? LinearBayerReconstruction.Reconstruct(noiseless, validMask, width, height, BayerPattern.Rggb).ToLuminance().Pixels.ToArray()
            : noiseless;

        var truthById = new Dictionary<string, (double Flux, double Snr, PixelPoint Pixel)>(StringComparer.Ordinal);
        foreach (var (id, pixel, _) in sources)
        {
            var (truthFlux, apertureCount) = MeasureNoiselessAperture(noiselessEval, width, height, pixel, background, segThreshold, growRadius);
            var truthSnr = truthFlux / (effectiveNoiseSigma * Math.Sqrt(apertureCount));
            truthById[id] = (truthFlux, truthSnr, pixel);
        }

        // Render noisy linear frame with deterministic Box-Muller Gaussian noise and run full measured association recipe.
        var noisy = CreateGaussianNoiseField(width, height, background, noiseSigma, seed: 526902);
        foreach (var (_, pixel, amplitude) in sources)
            AddNormalizedGaussian(noisy, width, height, pixel, psfSigma, amplitude);
        var raw = RawArtifact(noisy, format, width, height);
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(raw, scene)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;

        Assert.HasCount(starSpecs.Length, evidence.Associations);
        Assert.IsNotNull(evidence.PointSourcePlanets);
        var planet = Assert.ContainsSingle(evidence.PointSourcePlanets.Associations);
        Assert.IsEmpty(evidence.UnassociatedDetections);

        var maxSnrRelErr = 0.0;
        var maxFluxRelErr = 0.0;
        var maxCentroidErr = 0.0;
        var snrTolerance = format == CameraPixelFormat.Mono16 ? 0.12 : 0.20;
        var fluxTolerance = format == CameraPixelFormat.Mono16 ? 0.08 : 0.12;
        var centroidTolerance = format == CameraPixelFormat.Mono16 ? 0.15 : 0.30;

        foreach (var (id, measuredX, measuredY, flux, snr, eligible) in evidence.Associations
            .Select(static a => (a.CatalogId, a.MeasuredX, a.MeasuredY, a.Flux, a.SignalToNoise, a.LabelEligible))
            .Append((planet.ObjectId, planet.MeasuredX, planet.MeasuredY, planet.Flux, planet.SignalToNoise, planet.LabelEligible)))
        {
            Assert.IsTrue(eligible, $"{id} must be label-eligible.");
            Assert.IsNotNull(snr, $"{id} must report SNR.");
            var truth = truthById[id];
            var centroidErr = double.Hypot(measuredX - truth.Pixel.X, measuredY - truth.Pixel.Y);
            var fluxRelErr = Math.Abs(flux - truth.Flux) / truth.Flux;
            var snrRelErr = Math.Abs(snr.Value - truth.Snr) / truth.Snr;
            maxCentroidErr = Math.Max(maxCentroidErr, centroidErr);
            maxFluxRelErr = Math.Max(maxFluxRelErr, fluxRelErr);
            maxSnrRelErr = Math.Max(maxSnrRelErr, snrRelErr);

            TestContext.WriteLine(
                $"[{format}] {id}: centroidErr={centroidErr:F4}px, flux={flux:F1} (truth={truth.Flux:F1}, relErr={fluxRelErr:P2}), " +
                $"snr={snr.Value:F2} (truth={truth.Snr:F2}, relErr={snrRelErr:P2})");
            Assert.IsLessThanOrEqualTo(centroidTolerance, centroidErr, $"{format} {id} centroid error {centroidErr:F4}px exceeded {centroidTolerance}px.");
            Assert.IsLessThanOrEqualTo(fluxTolerance, fluxRelErr, $"{format} {id} flux relative error {fluxRelErr:P2} exceeded {fluxTolerance:P0}.");
            Assert.IsLessThanOrEqualTo(snrTolerance, snrRelErr, $"{format} {id} SNR relative error {snrRelErr:P2} exceeded {snrTolerance:P0}.");
        }

        TestContext.WriteLine(
            $"[{format}] summary: maxCentroidErr={maxCentroidErr:F4}px, maxFluxRelErr={maxFluxRelErr:P2}, maxSnrRelErr={maxSnrRelErr:P2}, " +
            $"medianNoiseSigma={evidence.Measurement.MedianNoiseSigma:F3} (truth={effectiveNoiseSigma:F3})");
    }

    [TestMethod]
    [DataRow("full-frame-mono16", CameraPixelFormat.Mono16, 1024, 1024, false, 36, 4)]
    [DataRow("full-frame-bayer16", CameraPixelFormat.BayerRggb16, 1024, 1024, false, 36, 4)]
    [DataRow("roi-transformed-mono16", CameraPixelFormat.Mono16, 512, 512, true, 12, 2)]
    public async Task FullFrameAndRoiQualificationRecordsAndBoundsDetectorMatcherAndPresentationResources(
        string profileName,
        CameraPixelFormat format,
        int sensorWidth,
        int sensorHeight,
        bool transformedRoi,
        int catalogStarCount,
        int unclaimedCount)
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var jupiterPos = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(jupiterPos.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, sensorWidth / 2d, sensorHeight / 2d,
            sensorWidth * 1.5, sensorHeight * 1.5, sensorWidth, sensorHeight, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees, BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var projector = ProjectorFactory.Create(projection);
        var transform = transformedRoi
            ? new ProjectedSceneImageTransformV1(
                ProjectedSceneImageTransformV1.CurrentSchemaVersion,
                sensorWidth, sensorHeight, CropX: 64, CropY: 64, CropWidth: 384, CropHeight: 384,
                BinX: 2, BinY: 2, HorizontalMirror: true, VerticalMirror: false,
                Rotation: ProjectedSceneQuarterRotation.Degrees90, OutputWidthPixels: 192, OutputHeightPixels: 192)
            : ProjectedSceneImageTransformV1.Identity(sensorWidth, sensorHeight);
        var outWidth = transform.OutputWidthPixels;
        var outHeight = transform.OutputHeightPixels;

        // Place catalog stars on a well-separated grid inside the emitted image, avoiding Jupiter at center.
        var cols = (int)Math.Ceiling(Math.Sqrt(catalogStarCount + unclaimedCount + 1));
        var rows = (int)Math.Ceiling((catalogStarCount + unclaimedCount + 1) / (double)cols);
        var catalog = new List<CelestialCatalogObject>(catalogStarCount);
        var unclaimedPixels = new List<PixelPoint>(unclaimedCount);
        var placed = 0;
        for (var r = 0; r < rows && placed < catalogStarCount + unclaimedCount; r++)
        {
            for (var c = 0; c < cols && placed < catalogStarCount + unclaimedCount; c++)
            {
                var ox = 24 + (outWidth - 48) * (c + 0.5) / cols;
                var oy = 24 + (outHeight - 48) * (r + 0.5) / rows;
                if (double.Hypot(ox - outWidth / 2d, oy - outHeight / 2d) < 20)
                    continue;
                if (catalog.Count < catalogStarCount)
                {
                    var native = ProjectedSceneImageTransform.Inverse(transform, new(ox, oy));
                    var altAz = projector.Unproject(native)!.Value;
                    var eq = EquatorialPrecession.PrecessToJ2000(
                        CoordinateTransforms.HorizontalToEquatorial(altAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
                        ExposureMidpointUtc);
                    catalog.Add(new($"star:q{catalog.Count:D2}", $"Qual {catalog.Count:D2}", eq.RightAscensionHours, eq.DeclinationDegrees, 1.5 + (catalog.Count % 4) * 0.5));
                }
                else
                {
                    unclaimedPixels.Add(new(ox, oy));
                }
                placed++;
            }
        }

        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(catalog), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, projection, new CatalogQuery(6.5, 500),
                new CatalogMetadata("qual", "1", new Uri("https://example.test/qual"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, transform,
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");

        // Render all stars except the last 2 (so expected-not-detected also runs non-trivially), plus Jupiter + unclaimed.
        var renderedStars = scene.Objects.Where(static o => o.Kind == CelestialObjectKind.Star).SkipLast(2).ToArray();
        var jupiterObj = scene.Objects.Single(static o => o.Id == "solar-system:Jupiter");
        var samples = CreateGaussianNoiseField(outWidth, outHeight, 1000.0, 5.0, seed: 526910 + outWidth);
        foreach (var obj in renderedStars)
            AddNormalizedGaussian(samples, outWidth, outHeight, obj.Pixel, 1.25, 2200);
        AddNormalizedGaussian(samples, outWidth, outHeight, jupiterObj.Pixel, 1.25, 3500);
        foreach (var pixel in unclaimedPixels)
            AddNormalizedGaussian(samples, outWidth, outHeight, pixel, 1.25, 1800);

        var raw = RawArtifact(samples, format, outWidth, outHeight);
        var aperture = new ProjectedSceneAperture(scene);
        var validMask = aperture.CreateSampleMask();
        var saturatedMask = new bool[samples.Length];
        var measOpts = MeasuredStellarAssociationRecipe.MeasurementOptions(format);

        // Stage 1: Detector / Measurer timing, CPU, allocations, candidate count.
        var proc = Process.GetCurrentProcess();
        var detCpuStart = proc.TotalProcessorTime;
        var detAllocStart = GC.GetAllocatedBytesForCurrentThread();
        var detSw = Stopwatch.StartNew();
        StellarMeasurementResult measurement;
        if (format == CameraPixelFormat.BayerRggb16)
        {
            var lum = LinearBayerReconstruction.Reconstruct(samples, validMask, outWidth, outHeight, BayerPattern.Rggb).ToLuminance();
            measurement = StellarSourceMeasurer.Measure(lum.Pixels.Span, lum.ValidMask.Span, saturatedMask, outWidth, outHeight, measOpts);
        }
        else
        {
            measurement = StellarSourceMeasurer.Measure(samples, validMask, saturatedMask, outWidth, outHeight, measOpts);
        }
        detSw.Stop();
        var detAllocBytes = GC.GetAllocatedBytesForCurrentThread() - detAllocStart;
        var detCpuMs = (proc.TotalProcessorTime - detCpuStart).TotalMilliseconds;

        // Stage 2: Matcher + canonical serialization timing, CPU, allocations, payload size.
        var settings = new MeasuredStellarAssociationSettingsV1();
        var labelPolicy = new StellarLabelPolicySettingsV1();
        var matchCpuStart = proc.TotalProcessorTime;
        var matchAllocStart = GC.GetAllocatedBytesForCurrentThread();
        var matchSw = Stopwatch.StartNew();
        var entries = MeasuredStellarAssociator.Associate(scene, measurement, settings, labelPolicy);
        var evidence = MeasuredStellarAssociationRecipe.CreateEvidence(raw, raw.Layout!, CaptureId, DescriptorIdentity, scene,
            new(StellarSourceMeasurer.AlgorithmVersion, measurement.SettingsIdentitySha256.ToUpperInvariant(),
                format == CameraPixelFormat.BayerRggb16 ? "bayer-rggb" : "mono-linear-v1",
                measurement.Status, measurement.MedianBackground, measurement.MedianNoiseSigma,
                measurement.CandidateCount, measurement.Detections.Count, measurement.Exclusions.Count),
            settings, labelPolicy, entries);
        var evidenceBytes = MeasuredStellarAssociationJson.Serialize(evidence);
        matchSw.Stop();
        var matchAllocBytes = GC.GetAllocatedBytesForCurrentThread() - matchAllocStart;
        var matchCpuMs = (proc.TotalProcessorTime - matchCpuStart).TotalMilliseconds;

        // Stage 3: Presentation layers (measured + expected-not-detected) timing, CPU, allocations, payload sizes.
        var presCpuStart = proc.TotalProcessorTime;
        var presAllocStart = GC.GetAllocatedBytesForCurrentThread();
        var presSw = Stopwatch.StartNew();
        var measuredPayload = MeasuredAssociationPresentation.CreateMeasuredPayload(evidence, scene);
        var expectedPayload = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(evidence, scene);
        var measuredBytes = PresentationLayerPayloadJson.Serialize(measuredPayload);
        var expectedBytes = PresentationLayerPayloadJson.Serialize(expectedPayload);
        presSw.Stop();
        var presAllocBytes = GC.GetAllocatedBytesForCurrentThread() - presAllocStart;
        var presCpuMs = (proc.TotalProcessorTime - presCpuStart).TotalMilliseconds;

        TestContext.WriteLine(
            $"[{profileName} {outWidth}x{outHeight}] " +
            $"detector: {detSw.ElapsedMilliseconds}ms (cpu={detCpuMs:F1}ms, alloc={detAllocBytes / 1024d:F1}KiB, candidates={measurement.CandidateCount}, detections={measurement.Detections.Count}, exclusions={measurement.Exclusions.Count}); " +
            $"matcher: {matchSw.ElapsedMilliseconds}ms (cpu={matchCpuMs:F1}ms, alloc={matchAllocBytes / 1024d:F1}KiB, stars={evidence.Associations.Count}/{catalog.Count}, planet={evidence.PointSourcePlanets!.Associations.Count}, unclaimed={evidence.UnassociatedDetections.Count}, payload={evidenceBytes.Length}B); " +
            $"presentation: {presSw.ElapsedMilliseconds}ms (cpu={presCpuMs:F1}ms, alloc={presAllocBytes / 1024d:F1}KiB, measured={measuredBytes.Length}B/{measuredPayload.Markers.Count}m/{measuredPayload.TextBlocks.Count}t, expected={expectedBytes.Length}B/{expectedPayload.Markers.Count}m/{expectedPayload.TextBlocks.Count}t)");

        // Accuracy & candidate-count assertions.
        var expectedSources = renderedStars.Length + 1 + unclaimedPixels.Count;
        Assert.AreEqual(StellarMeasurementStatus.Completed, measurement.Status);
        Assert.IsLessThanOrEqualTo(StellarDetector.MaximumSupportedCandidates, measurement.CandidateCount);
        Assert.IsGreaterThanOrEqualTo(expectedSources, measurement.CandidateCount);
        Assert.IsLessThanOrEqualTo(expectedSources + 8, measurement.CandidateCount, "Noise floor must not flood candidate counts.");
        Assert.HasCount(renderedStars.Length, evidence.Associations);
        Assert.HasCount(2, evidence.UnmatchedPredictions);
        Assert.ContainsSingle(evidence.PointSourcePlanets.Associations);
        Assert.HasCount(unclaimedPixels.Count, evidence.UnassociatedDetections);
        Assert.HasCount(2, expectedPayload.Markers);

        // Deterministic CI-safe latency, allocation, and payload compactness bounds.
        Assert.IsLessThan(10_000, detSw.ElapsedMilliseconds, $"{profileName} detector wall-clock exceeded bound.");
        Assert.IsLessThan(2_000, matchSw.ElapsedMilliseconds, $"{profileName} matcher wall-clock exceeded bound.");
        Assert.IsLessThan(2_000, presSw.ElapsedMilliseconds, $"{profileName} presentation wall-clock exceeded bound.");
        Assert.IsLessThan(256L * 1024 * 1024, detAllocBytes, $"{profileName} detector allocation exceeded bound.");
        Assert.IsLessThan(32L * 1024 * 1024, matchAllocBytes, $"{profileName} matcher allocation exceeded bound.");
        Assert.IsLessThan(32L * 1024 * 1024, presAllocBytes, $"{profileName} presentation allocation exceeded bound.");
        Assert.IsLessThan(64 * 1024, evidenceBytes.Length, $"{profileName} analytical association payload must stay compact.");
        Assert.IsLessThan(128 * 1024, measuredBytes.Length + expectedBytes.Length, $"{profileName} presentation payloads must stay compact.");
    }

    private static double[] CreateGaussianNoiseField(int width, int height, double background, double sigma, int seed)
    {
        var state = unchecked((ulong)seed + 0x9E3779B97F4A7C15UL);
        double NextUniform()
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (z >> 11) * (1.0 / (1UL << 53));
            }
        }

        var samples = new double[width * height];
        for (var i = 0; i < samples.Length; i += 2)
        {
            var u1 = Math.Max(1e-12, 1.0 - NextUniform());
            var u2 = NextUniform();
            var mag = sigma * Math.Sqrt(-2.0 * Math.Log(u1));
            var theta = 2.0 * Math.PI * u2;
            samples[i] = background + mag * Math.Cos(theta);
            if (i + 1 < samples.Length)
                samples[i + 1] = background + mag * Math.Sin(theta);
        }
        return samples;
    }

    private static void AddNormalizedGaussian(double[] samples, int width, int height, PixelPoint pixel, double sigma, double amplitude)
    {
        var radius = (int)Math.Ceiling(6 * sigma);
        var minX = Math.Max(0, (int)Math.Floor(pixel.X) - radius);
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(pixel.X) + radius);
        var minY = Math.Max(0, (int)Math.Floor(pixel.Y) - radius);
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(pixel.Y) + radius);
        var inv2Sigma2 = 1.0 / (2.0 * sigma * sigma);
        for (var y = minY; y <= maxY; y++)
        {
            var dy = y + 0.5 - pixel.Y;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x + 0.5 - pixel.X;
                samples[y * width + x] += amplitude * Math.Exp(-(dx * dx + dy * dy) * inv2Sigma2);
            }
        }
    }

    private static double SampleStdDev(ReadOnlySpan<double> samples, int width, int height, int margin)
    {
        double sum = 0;
        var count = 0;
        for (var y = margin; y < height - margin; y++)
            for (var x = margin; x < width - margin; x++)
            {
                sum += samples[y * width + x];
                count++;
            }
        var mean = sum / count;
        double sumSq = 0;
        for (var y = margin; y < height - margin; y++)
            for (var x = margin; x < width - margin; x++)
            {
                var d = samples[y * width + x] - mean;
                sumSq += d * d;
            }
        return Math.Sqrt(sumSq / (count - 1));
    }

    private static (double Flux, int AperturePixels) MeasureNoiselessAperture(
        double[] pixels, int width, int height, PixelPoint center, double background, double segThreshold, int growRadius)
    {
        var box = 16;
        var x0 = Math.Max(0, (int)Math.Floor(center.X) - box);
        var y0 = Math.Max(0, (int)Math.Floor(center.Y) - box);
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(center.X) + box);
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(center.Y) + box);
        var w = x1 - x0 + 1;
        var h = y1 - y0 + 1;
        var own = new bool[w * h];
        for (var wy = 0; wy < h; wy++)
            for (var wx = 0; wx < w; wx++)
                own[wy * w + wx] = pixels[(y0 + wy) * width + (x0 + wx)] - background > segThreshold;

        var grown = new bool[w * h];
        for (var wy = 0; wy < h; wy++)
        {
            for (var wx = 0; wx < w; wx++)
            {
                var hit = false;
                for (var dy = -growRadius; dy <= growRadius && !hit; dy++)
                {
                    var ny = wy + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (var dx = -growRadius; dx <= growRadius; dx++)
                    {
                        var nx = wx + dx;
                        if (nx >= 0 && nx < w && own[ny * w + nx])
                        {
                            hit = true;
                            break;
                        }
                    }
                }
                grown[wy * w + wx] = hit;
            }
        }

        double flux = 0;
        var count = 0;
        for (var wy = 0; wy < h; wy++)
        {
            for (var wx = 0; wx < w; wx++)
            {
                if (!grown[wy * w + wx]) continue;
                flux += pixels[(y0 + wy) * width + (x0 + wx)] - background;
                count++;
            }
        }
        return (flux, count);
    }
}
