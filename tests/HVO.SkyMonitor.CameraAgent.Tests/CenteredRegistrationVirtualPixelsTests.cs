using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CenteredRegistrationVirtualPixelsTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Run = new("e473de98-45b7-4198-9aae-f22c918424ed");
    private const string LinearVariant = "centered-qualified";

    // Fix the cells, apertures and diagnostic thresholds before scoring. These are
    // VirtualSky intensity diagnostics, not a promise of integrated-flux conservation.
    [TestMethod]
    [DataRow("mono-native", 1, 60, 3)]
    [DataRow("mono-native", 20, 60, 3)]
    [DataRow("mono-native", 20, 120, 3)]
    [DataRow("cfa-native", 1, 30, 5)]
    [DataRow("cfa-native", 20, 60, 3)]
    [DataRow("mono-roi-bin2", 1, 120, 3)]
    [DataRow("mono-mirror", 1, 60, 3)]
    [DataRow("mono-roll", 1, 60, 3)]
    public async Task SensorPixelsCompareRegisteredAndOrdinaryMean(string profileName, int seconds, int cadenceSeconds, int slotCount)
    {
        var token = TestContext.CancellationToken;
        var cadence = TimeSpan.FromSeconds(cadenceSeconds);
        var firstSlot = -(slotCount / 2);
        var catalog = VirtualAstrometryQualificationTests.Snapshot().Catalog;
        var solverCatalog = await catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries, token).ConfigureAwait(false);
        var profile = VirtualAstrometryFixture.Profiles(110201, VirtualAstrometryFixture.Families[0])
            .Single(item => item.Name == profileName);
        var rig = profile.Config.Rig;
        var bayer = rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16;
        var camera = new CameraKey(profile.Config.AgentId!, profile.Name);
        var clock = new ExposureTestTimeProvider(Epoch + firstSlot * cadence, autoAdvance: true);
        var module = new VirtualSkyCameraModule(clock, catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(profile.Config, token).ConfigureAwait(false);
        var exposure = TimeSpan.FromSeconds(seconds);
        var rows = new List<Row>();
        for (var slot = firstSlot; slot <= -firstSlot; slot++)
        {
            var start = Epoch + slot * cadence;
            var request = VirtualAstrometryFixture.Request(start, exposure) with { TargetInterval = cadence };
            var result = await module.CaptureAsync(request, token).ConfigureAwait(false);
            var measured = await PrepareAsync(profile.Config, camera, 103 + slot, request, result, token).ConfigureAwait(false);
            Assert.AreEqual(start, measured.Pin.ExposureStartedUtc);
            rows.Add(await AddPlateAsync(measured, rig, catalog, solverCatalog, token).ConfigureAwait(false));
            clock.Advance(cadence - exposure);
        }

        var slots = Enumerable.Range(-2, 5).Select(slot =>
        {
            var index = slot - firstSlot;
            return index < 0 || index >= rows.Count
                ? new CenteredStackSlot(slot, Epoch + slot * cadence, CenteredSlotState.Missing, null, null)
                : new CenteredStackSlot(slot, rows[index].Pin.ExposureStartedUtc, CenteredSlotState.Ready,
                    rows[index].Pin.Key.CaptureId, rows[index].Pin);
        }).ToArray();
        var center = rows[-firstSlot];
        var close = slots[^1].ExpectedStartUtc.AddSeconds(30);
        var plan = new CenteredStackPlanV1(camera, Run, "virtual-native-110201", center.Pin.Key.CaptureId,
            center.Pin.Linear.ArtifactId, close, close, "fixed-virtual-sensor-series", slots);
        var artifacts = rows.SelectMany(row => new[] { row.Raw, row.Linear, row.Mask, row.Measurement }.Concat(row.Plate)).ToArray();
        var aperture = RigProjectionContextFactory.Create(rig);
        var reader = new CenteredStackRetainedInputReader(new Settlements(rows),
            (_, _) => ValueTask.FromResult<CenteredStackMeasurementGeometry?>(new(rig.Sensor.PixelSizeMicrons, aperture)));
        var executor = new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(reader)]);
        var stackRequest = CenteredRegisteredStackRecipe.CreateRequest(plan, LinearVariant, "registered", artifacts);
        using var process = Process.GetCurrentProcess();
        var stack = await MeasureAsync(process, "registered", () => executor.ExecuteAsync(stackRequest, token)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, stack.Status, $"{stack.ReasonCode}: {stack.Field}");
        var evidence = CenteredRegisteredStackEvidence.Read(stack.Products.Single(product => product.SchemaVersion ==
            CenteredRegisteredStackEvidence.CurrentSchemaVersion));
        var statuses = string.Join(", ", evidence.Sources.Select(source =>
            $"{source.Offset}: {source.Reason} {source.MeasurementReason} ({source.PairCheck?.Status})"));
        var byId = artifacts.ToDictionary(artifact => artifact.ArtifactId);
        var planes = stack.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray();
        evidence.VerifyPlanes(planes, token);
        TestContext.WriteLine($"retained input bytes={artifacts.Sum(artifact => (long)artifact.Payload.Length)}, artifact count={artifacts.Length}; {statuses}");
        var rejectionControl = seconds == 20 && cadenceSeconds == 60;
        if (rejectionControl)
        {
            // The admission-owned measurement/association pipeline is not the #1168
            // measurer. A rejected verified-exposure enrollment is not a long-stack pass.
            // This fixed cell has no valid relative fit at 60 s spacing if the plate is absent.
            TestContext.WriteLine($"20s rejection control: {rows.Count(row => !row.HasVerifiedPlate)}/{rows.Count} plates rejected");
            Assert.IsTrue(evidence.Sources.Any(source => source.Plate is { AssessmentReason: not "accepted" }));
            var rejected = evidence.Sources.Where(source => source.Reason == CenteredRegistrationReason.RegistrationRejected).ToArray();
            Assert.HasCount(bayer ? 2 : 1, rejected, statuses);
            foreach (var source in rejected)
            {
                Assert.IsNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, source.Offset, byId));
            }
            if (bayer)
            {
                Assert.AreEqual(CenteredStackDisposition.SingleExposure, evidence.Disposition);
                return;
            }
            var verified = Assert.ContainsSingle(evidence.Sources.Where(source => source.Offset != 0 &&
                source.Reason == CenteredRegistrationReason.Contributed));
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedPlatePair, verified.Method, statuses);
        }
        else
        {
            Assert.AreEqual(slotCount * exposure.Ticks, evidence.TotalIntegrationTicks, statuses);
            foreach (var source in evidence.Sources.Where(source => source.Offset != 0 && source.CaptureId.HasValue))
            {
                Assert.AreEqual(CenteredRegistrationMethod.VerifiedPlatePair, source.Method, statuses);
            }
        }
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, statuses);
        var contributing = rows.Where(row => evidence.Sources.Any(source =>
            source.CaptureId == row.Pin.Key.CaptureId && source.Reason == CenteredRegistrationReason.Contributed)).ToArray();
        Assert.AreEqual(contributing.Length * exposure.Ticks, evidence.TotalIntegrationTicks, statuses);
        var ordinaryRequest = new ProcessingExecutionRequest(BuiltInProcessingRecipes.RollingMean, default,
            ProcessingInputSelector.Calibrated(LinearVariant), contributing.Select(row => row.Linear).ToArray(), "ordinary");
        var ordinary = await MeasureAsync(process, "ordinary", () => ComparePlanesAsync(ordinaryRequest, bayer, token)).ConfigureAwait(false);
        var channels = bayer ? new[] { "red", "green", "blue" } : ["mono"];
        var displayPlanes = channels.Select(channel => planes.Single(product => product.Variant == $"registered-{channel}")).ToArray();
        Assert.HasCount(channels.Length, displayPlanes.Select(channel => channel.ChecksumSha256).Distinct().ToArray());
        var support = planes.Single(plane => plane.MediaType == CenteredRegisteredStackEvidence.SupportMediaType);
        VerifyCoverage(rig, center, evidence, support);
        if (profileName == "mono-roi-bin2")
        {
            ScoreGrid(rig, center, contributing, evidence, byId);
        }
        else
        {
            Score(catalog.Query(new(5, 2000)), rig, contributing, evidence, displayPlanes, support, ordinary, exposure, byId,
                requireSharpness: !rejectionControl);
        }
    }

    // Binning this crop leaves too few isolated stellar apertures for the fixed photometry sample.
    // Compare the retained map against independent rays on the delivered lattice.
    private void ScoreGrid(CameraRigConfig rig, Row center, Row[] rows, CenteredRegisteredStackEvidence evidence,
        IReadOnlyDictionary<Guid, ProcessingArtifact> retained)
    {
        var layout = center.Raw.Layout!;
        foreach (var row in rows.Where(row => row.Pin.Key != center.Pin.Key))
        {
            var source = evidence.Sources.Single(item => item.CaptureId == row.Pin.Key.CaptureId);
            var map = CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, source.Offset, retained)!;
            var count = 0;
            var maximumError = 0d;
            for (var y = 1; y < 8; y++)
            {
                for (var x = 1; x < 8; x++)
                {
                    var reference = new PixelPoint(x * layout.Width / 8.0, y * layout.Height / 8.0);
                    if (!VirtualAstrometryReference.IsApertureInterior(rig, reference, 8) ||
                        VirtualAstrometryReference.Unproject(rig, reference) is not { } ray)
                    {
                        continue;
                    }
                    var equatorial = VirtualAstrometryReference.FromEnu(ray, center.Midpoint, VirtualAstrometryFixture.Observer);
                    var sourceRay = VirtualAstrometryReference.ToEnu(equatorial, row.Midpoint, VirtualAstrometryFixture.Observer);
                    if (VirtualAstrometryReference.Project(rig, sourceRay) is not { } expectedSource)
                    {
                        continue;
                    }
                    var mapped = map.SourceToReference(expectedSource);
                    Assert.IsNotNull(mapped);
                    maximumError = Math.Max(maximumError, VirtualAstrometryReference.Distance(reference, mapped.Value));
                    count++;
                }
            }
            TestContext.WriteLine($"ROI+bin2 source {source.Offset}: independent grid n={count}, maximum error={maximumError:F3} px");
            Assert.IsGreaterThanOrEqualTo(24, count);
            Assert.IsLessThan(.75, maximumError);
        }
    }

    private void VerifyCoverage(CameraRigConfig rig, Row center, CenteredRegisteredStackEvidence evidence, ProcessingProduct support)
    {
        var layout = center.Raw.Layout!;
        Assert.IsTrue(FrameWhiteThreshold.TryResolveStored(layout, out var white, out var reason), reason);
        var counts = new int[6];
        var outside = 0;
        var clippedCenter = 0;
        var centerBit = 1 << 2;
        var mask = support.Payload.Span;
        var raw = center.Raw.Payload.Span;
        for (var y = 0; y < layout.Height; y++)
        {
            var row = raw.Slice(y * layout.StrideBytes, layout.Width * sizeof(ushort));
            for (var x = 0; x < layout.Width; x++)
            {
                var bits = mask[y * layout.Width + x];
                counts[BitOperations.PopCount((uint)bits)]++;
                if (BinaryPrimitives.ReadUInt16LittleEndian(row[(x * sizeof(ushort))..]) >= white)
                {
                    Assert.AreEqual(0, bits & centerBit, $"clipped center sample ({x},{y})");
                    clippedCenter++;
                }
                // Leave two pixels of clearance for independently evaluated projection and stencil boundaries.
                if (x % 17 == 0 && y % 17 == 0 &&
                    !VirtualAstrometryReference.IsApertureInterior(rig, new(x + .5, y + .5), -2))
                {
                    Assert.AreEqual((byte)0, bits, $"sample outside the reference aperture ({x},{y})");
                    outside++;
                }
            }
        }
        Assert.IsGreaterThan(0, outside);
        Assert.AreEqual(evidence.ValidPixelCount, counts.Skip(2).Sum());
        Assert.IsGreaterThan(0, counts[evidence.Sources.Count(source => source.Reason == CenteredRegistrationReason.Contributed)]);
        TestContext.WriteLine($"Coverage by contribution count 0–5: {string.Join(", ", counts)}; " +
            $"independent exterior probes={outside}, sensor-white center exclusions={clippedCenter}");
    }

    private async ValueTask<T> MeasureAsync<T>(Process process, string name, Func<ValueTask<T>> operation)
    {
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var value = await operation().ConfigureAwait(false);
        process.Refresh();
        TestContext.WriteLine($"{name}: {timer.Elapsed.TotalMilliseconds:F1} ms wall, " +
            $"{(process.TotalProcessorTime - cpu).TotalMilliseconds:F1} ms CPU, " +
            $"{(GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0:F1} MiB allocated, " +
            $"{process.PeakWorkingSet64 / 1048576.0:F1} MiB process peak since testhost start");
        return value;
    }

    private static async ValueTask<ReadOnlyMemory<byte>[]> ComparePlanesAsync(
        ProcessingExecutionRequest request, bool bayer, CancellationToken token)
    {
        var ordinary = await ProduceAsync(request, token).ConfigureAwait(false);
        if (!bayer)
        {
            return [ordinary.Payload];
        }
        // The baseline averages the mosaic at detector coordinates, then reconstructs for each channel's score.
        var layout = ordinary.Layout!;
        var size = layout.Width * layout.Height;
        var values = new double[size];
        var mask = new bool[size];
        Assert.IsTrue(FrameWhiteThreshold.TryResolveStored(layout, out var white, out var reason), reason);
        for (var i = 0; i < size; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16LittleEndian(ordinary.Payload.Span[(i * 2)..]);
            mask[i] = values[i] < white;
        }
        var rgb = LinearBayerReconstruction.Reconstruct(values, mask, layout.Width, layout.Height, cancellationToken: token);
        return [Encode(rgb.Red), Encode(rgb.Green), Encode(rgb.Blue)];

        ReadOnlyMemory<byte> Encode(ReadOnlyMemory<double> channel)
        {
            var plane = new byte[size * 2];
            for (var i = 0; i < size; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(plane.AsSpan(i * 2),
                    (ushort)Math.Round(channel.Span[i], MidpointRounding.AwayFromZero));
            }
            return plane;
        }
    }

    private void Score(IReadOnlyList<CelestialCatalogObject> catalogStars, CameraRigConfig rig, Row[] rows, CenteredRegisteredStackEvidence evidence,
        ProcessingProduct[] planes, ProcessingProduct support, ReadOnlyMemory<byte>[] ordinary, TimeSpan exposure,
        IReadOnlyDictionary<Guid, ProcessingArtifact> retained, bool requireSharpness)
    {
        var positions = rows.Select(row => VirtualAstrometryReference.Trails(rig, row.Midpoint, exposure, catalogStars)
            .ToDictionary(trail => trail.Star.Id, trail => trail.Mid, StringComparer.Ordinal)).ToArray();
        var width = support.Layout!.Width;
        var height = support.Layout.Height;
        var slots = rows.Select(row => evidence.Sources.Single(source => source.CaptureId == row.Pin.Key.CaptureId).Offset).ToArray();
        var centerIndex = Array.IndexOf(slots, 0);
        var expectedSupport = (byte)slots.Sum(offset => 1 << (offset + 2));
        var maps = slots.Select(offset => CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, offset, retained)!).ToArray();
        var placement = new List<double>();
        var candidates = planes.Select(_ => new List<(double WidthRatio, double FluxRatio)>()).ToArray();
        foreach (var star in catalogStars)
        {
            var points = positions.Select(row => row.GetValueOrDefault(star.Id)).ToArray();
            if (star.Magnitude is not (>= 3.4 and <= 5) ||
                points.Any(point => !point.HasValue) || points[centerIndex] is not { } center ||
                !VirtualAstrometryReference.IsApertureInterior(rig, center, 16) ||
                center.X is < 16 || center.X >= width - 16 || center.Y is < 16 || center.Y >= height - 16 ||
                positions[centerIndex].Any(entry => entry.Key != star.Id && entry.Value is { } other &&
                    VirtualAstrometryReference.Distance(center, other) < 26) ||
                !FullAperture(support.Payload, width, center, expectedSupport))
            {
                continue;
            }
            placement.Add(maps.Select((map, index) =>
                VirtualAstrometryReference.Distance(center, map.SourceToReference(points[index]!.Value)!.Value)).Max());
            if (VirtualAstrometryReference.Distance(points[0]!.Value, points[^1]!.Value) < 3)
            {
                continue;
            }
            for (var channel = 0; channel < planes.Length; channel++)
            {
                var mean = Profile(ordinary[channel], width, center);
                var registered = Profile(planes[channel].Payload, width, center);
                if (mean.Flux >= 500 && registered.Flux >= 500)
                {
                    candidates[channel].Add((registered.Width / mean.Width, registered.Flux / mean.Flux));
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(8, placement.Count);
        var mappingError = Median(placement);
        TestContext.WriteLine($"Independent placement n={placement.Count}, median={mappingError:F3} px; valid pixels={evidence.ValidPixelCount}, " +
            $"support sha256={support.ChecksumSha256}");
        Assert.IsLessThan(1.25, mappingError);
        for (var channel = 0; channel < planes.Length; channel++)
        {
            var candidate = candidates[channel];
            if (!requireSharpness && candidate.Count < 8)
            {
                TestContext.WriteLine($"Only {candidate.Count} targets reached the predeclared 3 px displacement; " +
                    "no PSF or aperture-code qualification for this partial window.");
                continue;
            }
            Assert.IsGreaterThanOrEqualTo(8, candidate.Count, planes[channel].Variant);
            var widthRatio = Median(candidate.Select(item => item.WidthRatio));
            var fluxRatio = Median(candidate.Select(item => item.FluxRatio));
            TestContext.WriteLine($"{planes[channel].Variant} diagnostic n={candidate.Count}, PSF RMS width ratio={widthRatio:F3}, " +
                $"aperture-code flux ratio={fluxRatio:F3}");
            Assert.IsLessThan(.95, widthRatio);
            Assert.IsTrue(fluxRatio is >= .80 and <= 1.20);
        }
    }

    private static bool FullAperture(ReadOnlyMemory<byte> support, int width, PixelPoint center, byte expectedSupport)
    {
        for (var y = (int)center.Y - 10; y <= (int)center.Y + 10; y++)
        {
            for (var x = (int)center.X - 10; x <= (int)center.X + 10; x++)
            {
                if (support.Span[y * width + x] != expectedSupport)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static (double Flux, double Width) Profile(ReadOnlyMemory<byte> pixels, int width, PixelPoint center)
    {
        var background = 0d;
        var count = 0;
        for (var y = (int)center.Y - 13; y <= (int)center.Y + 13; y++)
        {
            for (var x = (int)center.X - 13; x <= (int)center.X + 13; x++)
            {
                if (RadiusSquared(x, y) is >= 121 and <= 169)
                {
                    background += Code(x, y);
                    count++;
                }
            }
        }
        background /= count;
        var flux = 0d;
        var secondMoment = 0d;
        for (var y = (int)center.Y - 10; y <= (int)center.Y + 10; y++)
        {
            for (var x = (int)center.X - 10; x <= (int)center.X + 10; x++)
            {
                var radius = RadiusSquared(x, y);
                if (radius <= 100)
                {
                    var signal = Code(x, y) - background;
                    flux += signal;
                    secondMoment += signal * radius;
                }
            }
        }
        return (flux, Math.Sqrt(Math.Max(0, secondMoment) / Math.Max(1, flux)));

        double RadiusSquared(int x, int y) => Math.Pow(x + .5 - center.X, 2) + Math.Pow(y + .5 - center.Y, 2);
        ushort Code(int x, int y) => BinaryPrimitives.ReadUInt16LittleEndian(pixels.Span[((y * width + x) * 2)..]);
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }

    // This harness borrows real sensor output but deliberately has no capture scheduler,
    // database, or published view. Truth enters only Score, after producing the result.
    private static async Task<Row> PrepareAsync(CameraModuleConfig config, CameraKey camera, int sequence,
        CaptureRequest request, CaptureResult captured, CancellationToken token)
    {
        var frame = captured.Frame ?? throw new InvalidOperationException("VirtualSky did not produce an exposure.");
        var submission = new CaptureLoopSubmission(request, captured, request.RequestedStartUtc, request.TargetInterval, request.TargetInterval);
        var ids = RawCaptureDescriptorFactory.CreateStableIds(config, submission);
        var descriptor = RawCaptureDescriptorFactory.Create(config, submission, new(camera.AgentId, sequence, ids.CaptureId, ids.ArtifactId),
            ProcessingIdentity.ComputePayloadSha256(frame.PixelData), request.RequestedStartUtc.AddMinutes(1));
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(config, new(ids.ArtifactId, FrameArtifactRole.Raw, frame),
            descriptor.Artifact.Variant, captured.AcquisitionTiming, descriptor);
        var normalized = await ProduceAsync(new(BuiltInProcessingRecipes.LinearNormalization, default,
            ProcessingInputSelector.Raw(raw.Variant), [raw], LinearVariant), token).ConfigureAwait(false);
        var linearId = ProcessingIdentity.CreateArtifactId(normalized.OutputIdentitySha256);
        var linearDescriptor = DerivativeDescriptorFactory.Create(descriptor, linearId, "registration-fixture-linear", normalized);
        var linear = FromProduct(raw, normalized) with
        {
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(linearDescriptor)
        };
        var key = new ExposureKey(camera, Run, ids.CaptureId);
        var rig = config.Rig;
        var geometry = new ExposureGeometryV1(descriptor.Layout, rig.Orientation, descriptor.Profiles.Rig.Sha256, rig.Optics.CalibrationVersion, null);
        var admitted = new ExposureInputV1(key, descriptor, geometry);
        Assert.IsTrue(FrameWhiteThreshold.TryResolveStored(descriptor.Layout, out var white, out var reason), reason);
        Assert.AreEqual(raw.Layout!.PixelFormat == CameraPixelFormat.Mono16 ? 4095 : ushort.MaxValue, white);
        var provenance = new LinearInputProvenanceV1(LinearExposureDisposition.QualifiedNormalizationOnly,
            raw.ArtifactId, raw.DescriptorIdentitySha256!, descriptor.Artifact.ChecksumSha256,
            linear.ArtifactId, linear.DescriptorIdentitySha256!, normalized.ChecksumSha256, normalized.OutputIdentitySha256,
            normalized.Recipe, ImmutableArray<Guid>.Empty, new(raw.ArtifactId, white), new(linear.ArtifactId, white));
        var product = new ExposureProductRef(key, new(Run, "Calibration", "exposure-linear-v1", LinearVariant),
            linear.ArtifactId, provenance.OutputIdentitySha256, provenance.LinearPayloadSha256, null);
        var settled = new LinearExposureLookup(admitted, provenance.Disposition, sequence, product, provenance, "linear.explicit-no-correction");
        var input = new LinearExposureInputV1(admitted, linear, provenance);
        var aperture = RigProjectionContextFactory.Create(rig);
        var measurement = FromProduct(raw, await ProduceAsync(ExposureMeasurementBindings.Request(settled, raw, input, aperture,
            rig.Sensor.PixelSizeMicrons), token).ConfigureAwait(false));
        var binding = ExposureMeasurementBindings.FromAdmitted(settled, aperture, rig.Sensor.PixelSizeMicrons);
        var bytes = new byte[descriptor.Layout.Width * descriptor.Layout.Height];
        bytes.AsSpan().Fill(1); // no additional calibration mask on the normalization-only path
        var mask = CenteredStackValidityMask.Create(raw, linear, bytes);
        var pin = CenteredStackExposurePin.Create(new(input, binding), raw, mask, CenteredStackArtifactPin.From(measurement));
        return new(pin, raw, linear, mask, measurement, settled);
    }

    private static ProcessingArtifact FromProduct(ProcessingArtifact raw, ProcessingProduct product) => raw with
    {
        ArtifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
        Role = product.Role,
        Variant = product.Variant,
        RecipeIdentitySha256 = product.Recipe.IdentitySha256,
        RecipeDescriptor = product.Recipe.Descriptor,
        MediaType = product.MediaType,
        Layout = product.Layout,
        Payload = product.Payload,
        SourceArtifactIds = product.SourceArtifactIds,
        ProductKind = product.Kind,
        SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256,
        DescriptorIdentitySha256 = null
    };

    private static async Task<Row> AddPlateAsync(Row row, CameraRigConfig rig, SqliteCelestialCatalog catalog,
        AstrometricCatalogData solverCatalog, CancellationToken token)
    {
        // Independent prediction from the catalog, observation and declared rig,
        // never a scene artifact exported by the VirtualSky renderer.
        var raw = row.Raw;
        var projection = RigProjectionContextFactory.Create(rig);
        var predicted = await new VisibleSceneBuilder(catalog).BuildAsync(new VisibleSceneRequest(
            row.Midpoint, VirtualAstrometryFixture.Observer, projection, new(5, 2000), solverCatalog.Metadata,
            projectionVersion: rig.Optics.CalibrationVersion), token).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, predicted,
            ProjectedSceneImageTransformV1.Identity(raw.Layout!.Width, raw.Layout.Height),
            new(row.Pin.Key.CaptureId, raw.ArtifactId, raw.DescriptorIdentitySha256!), rig.Optics.CalibrationVersion, predicted.Request.ProjectionVersion);
        var sceneArtifact = FromProduct(raw, await ProduceAsync(
            ExposureCatalogPredictionProducts.Request(raw, scene), token).ConfigureAwait(false));
        var expected = ExposureMeasurementBindings.FromAdmitted(row.Settlement, projection, rig.Sensor.PixelSizeMicrons);
        var associated = ExposureAssociationProducts.Associate(raw, row.Measurement, expected, sceneArtifact,
            ExposureAssociationProducts.Variant, cancellationToken: token);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, associated.Status, associated.ReasonCode);
        var association = FromProduct(raw, Assert.ContainsSingle(associated.Products));
        var snapshot = new AstrometryCatalogSnapshotV1(solverCatalog.Metadata, solverCatalog.Stars, solverCatalog.Provenance!,
            solverCatalog.IsCompleteForRequestedMagnitude, solverCatalog.CompletenessMagnitudeLimit, solverCatalog.CoordinateModel);
        var context = RetainedAstrometryProducts.CreateContext(raw, association, sceneArtifact, snapshot, AstrometricClockFacts.NotSupplied);
        var contextArtifact = FromProduct(raw, RetainedAstrometryProducts.CreateContextProduct(context, raw, association, sceneArtifact, "context"));
        var solution = await ProduceAsync(RetainedAstrometryProducts.Request(raw, association, sceneArtifact, contextArtifact,
            new(VirtualAstrometryFixture.SolverOptions), "solve"), token).ConfigureAwait(false);
        var assessment = MeasuredAstrometryJson.Parse(solution.Payload).Assessment;
        var solved = FromProduct(raw, solution);
        return row with
        {
            Plate = [association, sceneArtifact, contextArtifact, solved],
            HasVerifiedPlate = assessment.HasMeasuredMapping,
            Pin = row.Pin with
            {
                Plate = new(CenteredStackArtifactPin.From(association), CenteredStackArtifactPin.From(sceneArtifact),
                    CenteredStackArtifactPin.From(contextArtifact), CenteredStackArtifactPin.From(solved))
            }
        };
    }

    private static async Task<ProcessingProduct> ProduceAsync(ProcessingExecutionRequest request, CancellationToken token)
    {
        var result = await new ProcessingRecipeExecutor().ExecuteAsync(request, token).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, $"{result.ReasonCode}: {result.Field}");
        return Assert.ContainsSingle(result.Products);
    }

    private sealed record Row(CenteredStackExposurePin Pin, ProcessingArtifact Raw, ProcessingArtifact Linear,
        ProcessingArtifact Mask, ProcessingArtifact Measurement, LinearExposureLookup Settlement)
    {
        internal DateTimeOffset Midpoint => Pin.ExposureStartedUtc.AddTicks((Pin.ExposureEndedUtc - Pin.ExposureStartedUtc).Ticks / 2);
        internal ProcessingArtifact[] Plate { get; init; } = [];
        internal bool HasVerifiedPlate { get; init; }
    }

    private sealed class Settlements(IReadOnlyList<Row> rows) : IExposureProductReader
    {
        public ValueTask<LinearExposureLookup?> ReadLinearAsync(ExposureKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(rows.SingleOrDefault(row => row.Pin.Key == key)?.Settlement);
        }

        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ExposureProductLookup?> ReadProductAsync(ExposureKey key, ExposureProductSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ExposureProductLookup>> ReadProductsAfterAsync(CameraKey camera, long afterRevision, int maximumCount,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public TestContext TestContext { get; set; } = null!;
}
