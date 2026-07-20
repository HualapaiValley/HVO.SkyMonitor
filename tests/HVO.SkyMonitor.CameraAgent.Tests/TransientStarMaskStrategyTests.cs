using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientStarMaskStrategyTests
{
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W1W2PersistentProjectedStarMaskEvidence()
    {
        var results = new List<StrategyMeasurement>();
        foreach (var workload in new[] { Workload.W1, Workload.W2 })
        {
            results.Add(await MeasureAsync(workload).ConfigureAwait(false));
        }

        var root = GetRepositoryRoot();
        var outputDirectory = Path.Combine(root, "TestResults", "issue-115");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "star-mask-strategy.json");
        var evidence = new
        {
            SchemaVersion = "issue-115-star-mask-strategy-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Revision = ReadGit(root, "rev-parse HEAD"),
            DirtyState = ReadGit(root, "status --short"),
            FixtureUtc,
            Window = new { Offsets = new[] { -2, -1, 0, 1, 2 }, CadenceSeconds = 25, ExposureSeconds = 20 },
            Control = "Matched empty-catalog VirtualSky captures derive max(8 * 1.4826 * MAD, absolute residual p99.9999).",
            Strategy = "Union of catalog-projected PSF support over actual N-2..N+2 timestamps in detector coordinates.",
            Results = results
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #115 star-mask evidence: {outputPath}");
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<StrategyMeasurement> MeasureAsync(Workload workload)
    {
        var catalog = CreateCatalog(workload.MaximumResults * 2);
        var starStore = new ProjectedSceneStore();
        var controlStore = new ProjectedSceneStore();
        var starModule = new VirtualSkyCameraModule(TimeProvider.System, catalog, starStore);
        var controlModule = new VirtualSkyCameraModule(TimeProvider.System, new InMemoryCelestialCatalog([]), controlStore);
        var config = CreateConfig(workload);
        await starModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await controlModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var frames = new DetectorFrame[5];
        var controls = new DetectorFrame[5];
        var scenes = new VisibleScene[5];
        for (var index = 0; index < frames.Length; index++)
        {
            var started = FixtureUtc.AddSeconds((index - 2) * 25);
            var request = new CaptureRequest(
                started,
                TimeSpan.FromSeconds(25),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null));
            var starCapture = await starModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var controlCapture = await controlModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            frames[index] = ConvertFrame(starCapture.Frame!);
            controls[index] = ConvertFrame(controlCapture.Frame!);
            var sceneId = starCapture.Frame!.Metadata.Extra!["sceneId"];
            Assert.IsTrue(starStore.TryGet(sceneId, out var scene));
            Assert.IsNotNull(scene);
            scenes[index] = scene;
        }

        var layout = frames[0].Layout;
        var supports = scenes
            .SelectMany(static scene => scene.Objects)
            .Select(item => new Linear16CircularMaskRegion(
                item.Pixel.X * workload.DetectorScale,
                item.Pixel.Y * workload.DetectorScale,
                workload.MaskRadius))
            .ToArray();
        var mask = Linear16MaskOperations.CreateCircularSupportMask(layout.Width, layout.Height, supports);
        var causalSupports = scenes.Take(3)
            .SelectMany(static scene => scene.Objects)
            .Select(item => new Linear16CircularMaskRegion(
                item.Pixel.X * workload.DetectorScale,
                item.Pixel.Y * workload.DetectorScale,
                workload.MaskRadius))
            .ToArray();
        var causalMask = Linear16MaskOperations.CreateCircularSupportMask(layout.Width, layout.Height, causalSupports);
        var empty = Linear16MaskOperations.Empty(layout.Width, layout.Height);
        var starBackground = Background(frames, empty, 0, 1, 3, 4);
        var controlBackground = Background(controls, empty, 0, 1, 3, 4);
        var threshold = CalculateThreshold(workload, controls[2], controlBackground);
        var baseline = Evaluate(workload, frames[2], starBackground, mask, threshold, applyMask: false);
        var masked = Evaluate(workload, frames[2], starBackground, mask, threshold, applyMask: true);
        var extraction = Linear16TransientExtraction.Extract(
            new Linear16Frame(layout.Width, layout.Height, layout.StrideBytes, CameraPixelFormat.Mono16, frames[2].Pixels),
            new Linear16Frame(
                starBackground.Width,
                starBackground.Height,
                starBackground.StrideBytes,
                CameraPixelFormat.Mono16,
                starBackground.PixelData),
            Linear16MaskOperations.Combine([mask, starBackground.NoSupportMask]),
            Linear16MaskOperations.Empty(layout.Width, layout.Height),
            new Linear16TransientExtractionOptions(
                (ushort)threshold,
                1,
                threshold,
                64,
                8,
                64,
                Linear16TransientExtraction.MaximumDetectorPixels,
                0,
                0.9));
        var validPixels = CountValidPixels(workload, layout);
        var maskedPixels = mask.Bits.Span.ToArray().Sum(static value => System.Numerics.BitOperations.PopCount(value));
        var maskedPercent = maskedPixels * 100d / validPixels;
        var countSuppression = PercentSuppressed(baseline.StarAssociatedCount, masked.StarAssociatedCount);
        var energySuppression = PercentSuppressed(baseline.StarAssociatedEnergy, masked.StarAssociatedEnergy);
        var absoluteCountSuppression = PercentSuppressed(
            baseline.StarAssociatedAbsoluteCount,
            masked.StarAssociatedAbsoluteCount);
        var absoluteEnergySuppression = PercentSuppressed(
            baseline.StarAssociatedAbsoluteEnergy,
            masked.StarAssociatedAbsoluteEnergy);
        var motion = MeasureMotion(workload, scenes);
        var causalStarBackground = Background(frames, empty, 0, 1);
        var causalControlBackground = Background(controls, empty, 0, 1);
        var causalThreshold = CalculateThreshold(workload, controls[2], causalControlBackground);
        var causalBaseline = Evaluate(workload, frames[2], causalStarBackground, causalMask, causalThreshold, applyMask: false);
        var causalMasked = Evaluate(workload, frames[2], causalStarBackground, causalMask, causalThreshold, applyMask: true);
        var causalMaskedPixels = causalMask.Bits.Span.ToArray().Sum(static value => System.Numerics.BitOperations.PopCount(value));

        Assert.AreEqual(workload.ExpectedThreshold, threshold);
        Assert.AreEqual(workload.ExpectedBaselineCount, baseline.StarAssociatedCount);
        Assert.AreEqual(workload.ExpectedBaselineEnergy, baseline.StarAssociatedEnergy);
        Assert.AreEqual(workload.ExpectedMaskedPixels, maskedPixels);
        Assert.AreEqual(workload.ExpectedMaskChecksum, Convert.ToHexString(SHA256.HashData(mask.Bits.Span)));
        Assert.AreEqual(workload.ExpectedCausalThreshold, causalThreshold);
        Assert.AreEqual(workload.ExpectedCausalMaskedPixels, causalMaskedPixels);
        Assert.AreEqual(workload.ExpectedCausalMaskChecksum, Convert.ToHexString(SHA256.HashData(causalMask.Bits.Span)));
        Assert.IsGreaterThanOrEqualTo(99, countSuppression);
        Assert.IsGreaterThanOrEqualTo(99, energySuppression);
        Assert.IsGreaterThanOrEqualTo(99, absoluteCountSuppression);
        Assert.IsGreaterThanOrEqualTo(99, absoluteEnergySuppression);
        Assert.IsLessThanOrEqualTo(20, maskedPercent);
        Assert.AreEqual(0, masked.UnassociatedCount);
        Assert.IsFalse(extraction.CandidateLimitExceeded);
        Assert.IsEmpty(extraction.Components);
        Assert.IsLessThanOrEqualTo(maskedPixels, causalMaskedPixels);
        Assert.AreNotEqual(
            Convert.ToHexString(SHA256.HashData(mask.Bits.Span)),
            Convert.ToHexString(SHA256.HashData(causalMask.Bits.Span)));
        Assert.IsGreaterThanOrEqualTo(
            99,
            PercentSuppressed(causalBaseline.StarAssociatedAbsoluteCount, causalMasked.StarAssociatedAbsoluteCount));
        Assert.IsGreaterThanOrEqualTo(
            99,
            PercentSuppressed(causalBaseline.StarAssociatedAbsoluteEnergy, causalMasked.StarAssociatedAbsoluteEnergy));

        return new StrategyMeasurement(
            workload.Id,
            workload.SourceBytes,
            layout.Width,
            layout.Height,
            layout.RequiredByteLength,
            scenes.Select(static scene => scene.Objects.Count).ToArray(),
            threshold,
            motion,
            mask.Bits.Length,
            maskedPixels,
            validPixels,
            maskedPercent,
            Convert.ToHexString(SHA256.HashData(mask.Bits.Span)),
            baseline,
            masked,
            new CausalStrategyMeasurement(
                causalThreshold,
                causalMask.Bits.Length,
                causalMaskedPixels,
                Convert.ToHexString(SHA256.HashData(causalMask.Bits.Span)),
                causalBaseline,
                causalMasked),
            countSuppression,
            energySuppression,
            absoluteCountSuppression,
            absoluteEnergySuppression);
    }

    private static Linear16TemporalBackgroundResult Background(
        IReadOnlyList<DetectorFrame> frames,
        Linear16PixelMask mask,
        params int[] indexes)
    {
        var context = indexes.Select(index => frames[index])
            .Select(frame => new Linear16TemporalFrame(
                new Linear16Frame(
                    frame.Layout.Width,
                    frame.Layout.Height,
                    frame.Layout.StrideBytes,
                    CameraPixelFormat.Mono16,
                    frame.Pixels),
                mask,
                0))
            .ToArray();
        return Linear16TemporalBackground.Compute(context, 0);
    }

    private static int CalculateThreshold(
        Workload workload,
        DetectorFrame target,
        Linear16TemporalBackgroundResult background)
    {
        var histogram = new long[ushort.MaxValue + 1];
        long count = 0;
        for (var y = 0; y < target.Layout.Height; y++)
        {
            for (var x = 0; x < target.Layout.Width; x++)
            {
                if (!IsValid(workload, x, y))
                {
                    continue;
                }
                var targetOffset = y * target.Layout.StrideBytes + x * 2;
                var backgroundOffset = (y * target.Layout.Width + x) * 2;
                var residual = Math.Abs(Read(target.Pixels.AsSpan(), targetOffset) - Read(background.PixelData.Span, backgroundOffset));
                histogram[residual]++;
                count++;
            }
        }
        var medianAbsoluteDeviation = Percentile(histogram, count, 0.5);
        var upperNoise = Percentile(histogram, count, 0.999999);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(8 * 1.4826 * medianAbsoluteDeviation, upperNoise)));
    }

    private static ResidualMetrics Evaluate(
        Workload workload,
        DetectorFrame target,
        Linear16TemporalBackgroundResult background,
        Linear16PixelMask starMask,
        int threshold,
        bool applyMask)
    {
        long count = 0;
        long energy = 0;
        long starCount = 0;
        long starEnergy = 0;
        long unassociatedCount = 0;
        long absoluteCount = 0;
        long absoluteEnergy = 0;
        long starAbsoluteCount = 0;
        long starAbsoluteEnergy = 0;
        long unassociatedAbsoluteCount = 0;
        for (var y = 0; y < target.Layout.Height; y++)
        {
            for (var x = 0; x < target.Layout.Width; x++)
            {
                if (!IsValid(workload, x, y))
                {
                    continue;
                }
                var starAssociated = Linear16MaskOperations.IsExcluded(starMask, x, y);
                if (applyMask && starAssociated)
                {
                    continue;
                }
                var targetOffset = y * target.Layout.StrideBytes + x * 2;
                var backgroundOffset = (y * target.Layout.Width + x) * 2;
                var residual = Read(target.Pixels.AsSpan(), targetOffset) - Read(background.PixelData.Span, backgroundOffset);
                var absoluteResidual = Math.Abs(residual);
                if (absoluteResidual >= threshold)
                {
                    absoluteCount++;
                    absoluteEnergy += absoluteResidual;
                    if (starAssociated)
                    {
                        starAbsoluteCount++;
                        starAbsoluteEnergy += absoluteResidual;
                    }
                    else
                    {
                        unassociatedAbsoluteCount++;
                    }
                }
                if (residual < threshold)
                {
                    continue;
                }
                count++;
                energy += residual;
                if (starAssociated)
                {
                    starCount++;
                    starEnergy += residual;
                }
                else
                {
                    unassociatedCount++;
                }
            }
        }
        return new ResidualMetrics(
            count,
            energy,
            starCount,
            starEnergy,
            unassociatedCount,
            absoluteCount,
            absoluteEnergy,
            starAbsoluteCount,
            starAbsoluteEnergy,
            unassociatedAbsoluteCount);
    }

    private static MotionMeasurement MeasureMotion(Workload workload, VisibleScene[] scenes)
    {
        var values = new List<double>();
        for (var index = 1; index < scenes.Length; index++)
        {
            var previous = scenes[index - 1].Objects.ToDictionary(static item => item.Id, StringComparer.Ordinal);
            foreach (var current in scenes[index].Objects)
            {
                if (previous.TryGetValue(current.Id, out var prior))
                {
                    var dx = (current.Pixel.X - prior.Pixel.X) * workload.DetectorScale;
                    var dy = (current.Pixel.Y - prior.Pixel.Y) * workload.DetectorScale;
                    values.Add(Math.Sqrt(dx * dx + dy * dy));
                }
            }
        }
        values.Sort();
        var p95 = values[(int)Math.Ceiling(values.Count * 0.95) - 1];
        return new MotionMeasurement(values.Count, p95, values[^1], p95 * 4, values[^1] * 4);
    }

    private static InMemoryCelestialCatalog CreateCatalog(int count)
    {
        const double goldenAngle = 137.50776405003785;
        return new InMemoryCelestialCatalog(Enumerable.Range(0, count).Select(index =>
        {
            var fraction = (index + 0.5) / count;
            var declination = Math.Asin(2 * fraction - 1) * 180 / Math.PI;
            var rightAscension = ((index * goldenAngle) % 360) / 15;
            var magnitude = -1 + (index % 75) * 0.1;
            return new CelestialCatalogObject(
                $"star-{index:D5}", $"Star {index:D5}", rightAscension, declination, magnitude, 0.65);
        }));
    }

    private static CameraModuleConfig CreateConfig(Workload workload)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 2025,
            maximumMagnitude = 6.5,
            maximumResults = workload.MaximumResults,
            magnitudeZeroElectronsPerSecond = workload.Id == "W1" ? 300d : 18_000d,
            backgroundElectronsPerSecond = 2d,
            shotNoiseEnabled = true,
            darkCurrentElectronsPerSecond = 0d,
            asi174Sensor = new { enabled = workload.Id == "W1", blackLevelAdu = 64d },
            asi178Sensor = new { enabled = workload.Id == "W2", blackLevelContainerAdu = 64d }
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.5599378, -113.9119818, 520, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    workload.Id,
                    workload.Width,
                    workload.Height,
                    workload.Id == "W1" ? 5.86 : 2.4,
                    workload.Id == "W1" ? SensorColorMode.Mono : SensorColorMode.Color,
                    workload.Format,
                    workload.Id == "W1" ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw,
                    workload.Width * 2,
                    SensorRecipeVersion: $"{workload.Id}-issue-115-star-mask-v1"),
                new OpticsProfile(
                    "EquidistantFisheye",
                    workload.Id == "W1" ? 0 : 1.8,
                    workload.Id == "W1" ? 180 : 185,
                    0,
                    LensKind.Fisheye,
                    workload.Width / 2d,
                    workload.Height / 2d,
                    workload.ImageCircleRadius,
                    workload.FocalLengthPixels,
                    workload.FocalLengthPixels,
                    HorizontalFlip: true,
                    CalibrationVersion: $"{workload.Id}-issue-115-star-mask-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 150, 150)));
    }

    private static DetectorFrame ConvertFrame(CameraFrame frame)
    {
        var sourceLayout = new ImageLayout(
            frame.Width,
            frame.Height,
            frame.PixelFormat,
            frame.StrideBytes ?? checked(frame.Width * ImageLayout.BytesPerPixel(frame.PixelFormat)));
        var converted = Linear16DetectorInputConverter.Convert(sourceLayout, FrameByteOrder.LittleEndian, frame.PixelData);
        return new DetectorFrame(converted.Layout, converted.PixelData.ToArray());
    }

    private static int CountValidPixels(Workload workload, ImageLayout layout)
    {
        var count = 0;
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                if (IsValid(workload, x, y))
                {
                    count++;
                }
            }
        }
        return count;
    }

    private static bool IsValid(Workload workload, int x, int y)
    {
        var centerX = workload.Width / 2d * workload.DetectorScale;
        var centerY = workload.Height / 2d * workload.DetectorScale;
        var radius = workload.ImageCircleRadius * workload.DetectorScale;
        var dx = x + 0.5 - centerX;
        var dy = y + 0.5 - centerY;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static ushort Read(ReadOnlySpan<byte> pixels, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(offset, 2));

    private static int Percentile(long[] histogram, long count, double fraction)
    {
        var target = (long)Math.Ceiling(count * fraction);
        long seen = 0;
        for (var index = 0; index < histogram.Length; index++)
        {
            seen += histogram[index];
            if (seen >= target)
            {
                return index;
            }
        }
        return histogram.Length - 1;
    }

    private static double PercentSuppressed(long baseline, long candidate)
        => baseline == 0 ? 100 : (baseline - candidate) * 100d / baseline;

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ReadGit(string root, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private sealed record DetectorFrame(ImageLayout Layout, byte[] Pixels);
    private sealed record ResidualMetrics(
        long SupraThresholdCount,
        long PositiveResidualEnergy,
        long StarAssociatedCount,
        long StarAssociatedEnergy,
        long UnassociatedCount,
        long AbsoluteSupraThresholdCount,
        long AbsoluteResidualEnergy,
        long StarAssociatedAbsoluteCount,
        long StarAssociatedAbsoluteEnergy,
        long UnassociatedAbsoluteCount);
    private sealed record MotionMeasurement(
        int AdjacentSamples,
        double P95DetectorPixels,
        double MaximumDetectorPixels,
        double EstimatedP95AcrossWindowPixels,
        double EstimatedMaximumAcrossWindowPixels);
    private sealed record StrategyMeasurement(
        string Workload,
        int SourceBytes,
        int DetectorWidth,
        int DetectorHeight,
        int DetectorBytes,
        IReadOnlyList<int> StarsPerScene,
        int NoiseThresholdAdu,
        MotionMeasurement Motion,
        int MaskBytes,
        int MaskedPixels,
        int ValidPixels,
        double ValidAreaMaskedPercent,
        string MaskChecksumSha256,
        ResidualMetrics Baseline,
        ResidualMetrics Masked,
        CausalStrategyMeasurement Causal,
        double StarResidualCountSuppressionPercent,
        double StarResidualEnergySuppressionPercent,
        double StarAbsoluteResidualCountSuppressionPercent,
        double StarAbsoluteResidualEnergySuppressionPercent);
    private sealed record CausalStrategyMeasurement(
        int NoiseThresholdAdu,
        int MaskBytes,
        int MaskedPixels,
        string MaskChecksumSha256,
        ResidualMetrics Baseline,
        ResidualMetrics Masked);

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat Format,
        double ImageCircleRadius,
        double? FocalLengthPixels,
        int MaximumResults,
        double DetectorScale,
        int MaskRadius,
        int ExpectedThreshold,
        long ExpectedBaselineCount,
        long ExpectedBaselineEnergy,
        int ExpectedMaskedPixels,
        string ExpectedMaskChecksum,
        int ExpectedCausalThreshold,
        int ExpectedCausalMaskedPixels,
        string ExpectedCausalMaskChecksum)
    {
        public static Workload W1 { get; } = new(
            "W1", 1936, 1216, CameraPixelFormat.Mono16, 595.84, null, 2000, 1, 5,
            48, 2968, 420380, 206512,
            "51CA89BCEB8158942FC93160E307B2C0A1CFF847753003A1E99D2F253642B769",
            48, 181794, "925E475280923EA287135EDAE4E460EA220C5FB1AE50817C2E4BFE4999976915");
        public static Workload W2 { get; } = new(
            "W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 1187.5, 735.553926, 300, 0.5, 3,
            677, 1365, 15837168, 12494,
            "7A1FFC2770C85D02B4F719ED17D03C43A426C624AAF644A6CCCDA7BFE8BDC243",
            736, 10425, "14A1FDC821467AE2F74846B09524B67574974CE2096434DB613BE85265827AB4");
        public int SourceBytes => checked(Width * Height * 2);
    }
}
