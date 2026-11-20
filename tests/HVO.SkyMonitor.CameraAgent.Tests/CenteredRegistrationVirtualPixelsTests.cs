using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
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
    private static readonly int[] CadenceOffsets = [-60, 0, 60];
    private const string LinearVariant = "centered-qualified";

    // Fix the cells, apertures and diagnostic thresholds before scoring. These are
    // VirtualSky intensity diagnostics, not a promise of integrated-flux conservation.
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(20, false)]
    [DataRow(1, true)]
    [DataRow(20, true)]
    public async Task NativeSensorPixelsCompareRegisteredAndOrdinaryMean(int seconds, bool bayer)
    {
        var token = TestContext.CancellationToken;
        var catalog = VirtualAstrometryQualificationTests.Snapshot().Catalog;
        var solverCatalog = await catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries, token).ConfigureAwait(false);
        var profile = VirtualAstrometryFixture.Profiles(110201).Single(item => item.Name == (bayer ? "cfa-native" : "mono-native"));
        var rig = profile.Config.Rig;
        var camera = new CameraKey(profile.Config.AgentId!, profile.Name);
        var clock = new ExposureTestTimeProvider(Epoch.AddSeconds(CadenceOffsets[0]), autoAdvance: true);
        var module = new VirtualSkyCameraModule(clock, catalog, new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(profile.Config, token).ConfigureAwait(false);
        var exposure = TimeSpan.FromSeconds(seconds);
        var rows = new List<Row>();
        foreach (var offset in CadenceOffsets)
        {
            var request = VirtualAstrometryFixture.Request(Epoch.AddSeconds(offset), exposure);
            var result = await module.CaptureAsync(request, token).ConfigureAwait(false);
            var measured = await PrepareAsync(profile.Config, camera, rows.Count + 102, request, result, token).ConfigureAwait(false);
            rows.Add(await AddPlateAsync(measured, rig, catalog, solverCatalog, token).ConfigureAwait(false));
            clock.Advance(TimeSpan.FromSeconds(60) - exposure);
        }

        var slots = new CenteredStackSlot[5];
        slots[0] = new(-2, Epoch.AddSeconds(-120), CenteredSlotState.Missing, null, null);
        slots[4] = new(2, Epoch.AddSeconds(120), CenteredSlotState.Missing, null, null);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            slots[i + 1] = new(i - 1, row.Pin.ExposureStartedUtc, CenteredSlotState.Ready, row.Pin.Key.CaptureId, row.Pin);
        }
        var center = rows[1];
        var plan = new CenteredStackPlanV1(camera, Run, "virtual-native-110201", center.Pin.Key.CaptureId,
            center.Pin.Linear.ArtifactId, Epoch.AddSeconds(150), Epoch.AddSeconds(150), "fixed-three-visible-slots", slots);
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
        var rejectedPlates = rows.Count(row => !row.HasVerifiedPlate);
        if (seconds == 20 && rejectedPlates > 0)
        {
            // The admission-owned measurement/association pipeline is not the #1168
            // measurer. A rejected verified-exposure enrollment is not a long-stack pass.
            // This fixed cell has no valid relative fit at 60 s spacing if the plate is absent.
            TestContext.WriteLine($"20s rejection control: {rejectedPlates}/3 plates rejected; {statuses}");
            Assert.IsTrue(evidence.Sources.Any(source => source.Plate is { AssessmentReason: not "accepted" }));
            Assert.IsTrue(evidence.Sources.Any(source => source.Reason == CenteredRegistrationReason.RegistrationRejected));
            foreach (var source in evidence.Sources.Where(source => source.Reason == CenteredRegistrationReason.RegistrationRejected))
            {
                Assert.IsNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, source.Offset, byId));
            }
            if (evidence.Disposition != CenteredStackDisposition.Registered)
            {
                Assert.AreEqual(CenteredStackDisposition.SingleExposure, evidence.Disposition);
                return;
            }
        }
        else
        {
            Assert.AreEqual(3 * exposure.Ticks, evidence.TotalIntegrationTicks, statuses);
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedPlatePair, evidence.Sources[1].Method);
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedPlatePair, evidence.Sources[3].Method);
        }
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, statuses);
        var contributing = rows.Where((_, index) => evidence.Sources[index + 1].Reason == CenteredRegistrationReason.Contributed).ToArray();
        Assert.AreEqual(contributing.Length * exposure.Ticks, evidence.TotalIntegrationTicks, statuses);
        var ordinaryRequest = new ProcessingExecutionRequest(BuiltInProcessingRecipes.RollingMean, default,
            ProcessingInputSelector.Calibrated(LinearVariant), contributing.Select(row => row.Linear).ToArray(), "ordinary");
        var ordinary = await MeasureAsync(process, "ordinary", () => ComparePlaneAsync(ordinaryRequest, bayer, token)).ConfigureAwait(false);
        var plane = planes.Single(product => product.Variant == (bayer ? "registered-green" : "registered-mono"));
        if (bayer)
        {
            Assert.HasCount(3, planes[..^1].Select(channel => channel.ChecksumSha256).Distinct().ToArray());
        }
        var support = planes.Single(plane => plane.MediaType == CenteredRegisteredStackEvidence.SupportMediaType);
        Assert.AreEqual((byte)0, support.Payload.Span[0]); // outside the declared optical aperture
        Score(catalog.Query(new(5, 2000)), rig, contributing, evidence, plane, support, ordinary, exposure, byId);
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

    private static async ValueTask<ReadOnlyMemory<byte>> ComparePlaneAsync(
        ProcessingExecutionRequest request, bool bayer, CancellationToken token)
    {
        var ordinary = await ProduceAsync(request, token).ConfigureAwait(false);
        if (!bayer)
        {
            return ordinary.Payload;
        }
        // The baseline averages the mosaic at detector coordinates, then reconstructs for the same green-channel score.
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
        var green = new byte[size * 2];
        for (var i = 0; i < size; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(green.AsSpan(i * 2), (ushort)Math.Round(rgb.Green.Span[i], MidpointRounding.AwayFromZero));
        }
        return green;
    }

    private void Score(IReadOnlyList<CelestialCatalogObject> catalogStars, CameraRigConfig rig, Row[] rows, CenteredRegisteredStackEvidence evidence,
        ProcessingProduct plane, ProcessingProduct support, ReadOnlyMemory<byte> ordinary, TimeSpan exposure,
        IReadOnlyDictionary<Guid, ProcessingArtifact> retained)
    {
        var positions = rows.Select(row => VirtualAstrometryReference.Trails(rig, row.Midpoint, exposure, catalogStars)
            .ToDictionary(trail => trail.Star.Id, trail => trail.Mid, StringComparer.Ordinal)).ToArray();
        var width = plane.Layout!.Width;
        var height = plane.Layout.Height;
        var slots = rows.Select(row => evidence.Sources.Single(source => source.CaptureId == row.Pin.Key.CaptureId).Offset).ToArray();
        var centerIndex = Array.IndexOf(slots, 0);
        var expectedSupport = (byte)slots.Sum(offset => 1 << (offset + 2));
        var maps = slots.Select(offset => CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, offset, retained)!).ToArray();
        var candidate = new List<(double WidthRatio, double FluxRatio, double MappingError)>();
        foreach (var star in catalogStars)
        {
            var points = positions.Select(row => row.GetValueOrDefault(star.Id)).ToArray();
            if (star.Magnitude is not (>= 3.4 and <= 5) ||
                points.Any(point => !point.HasValue) || points[centerIndex] is not { } center ||
                !VirtualAstrometryReference.IsApertureInterior(rig, center, 16) ||
                center.X is < 16 || center.X >= width - 16 || center.Y is < 16 || center.Y >= height - 16 ||
                VirtualAstrometryReference.Distance(points[0]!.Value, points[^1]!.Value) < 3 ||
                positions[centerIndex].Any(entry => entry.Key != star.Id && entry.Value is { } other &&
                    VirtualAstrometryReference.Distance(center, other) < 26) ||
                !FullAperture(support.Payload, width, center, expectedSupport))
            {
                continue;
            }
            var mean = Profile(ordinary, width, center);
            var registered = Profile(plane.Payload, width, center);
            if (mean.Flux < 500 || registered.Flux < 500)
            {
                continue;
            }
            var error = maps.Select((map, index) =>
                VirtualAstrometryReference.Distance(center, map.SourceToReference(points[index]!.Value)!.Value)).Max();
            candidate.Add((registered.Width / mean.Width, registered.Flux / mean.Flux, error));
        }

        Assert.IsGreaterThanOrEqualTo(8, candidate.Count);
        var widthRatio = Median(candidate.Select(item => item.WidthRatio));
        var fluxRatio = Median(candidate.Select(item => item.FluxRatio));
        var mappingError = Median(candidate.Select(item => item.MappingError));
        TestContext.WriteLine($"Independent actual-pixel scorer n={candidate.Count}, mapping median={mappingError:F3} px, " +
            $"PSF RMS width ratio={widthRatio:F3}, aperture-code flux ratio={fluxRatio:F3}, valid pixels={evidence.ValidPixelCount}, " +
            $"support sha256={support.ChecksumSha256}");
        Assert.IsLessThan(1.25, mappingError);
        Assert.IsLessThan(.95, widthRatio);
        Assert.IsTrue(fluxRatio is >= .80 and <= 1.20);
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
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var sceneArtifact = FromProduct(raw, await ProduceAsync(new(BuiltInProcessingRecipes.ProjectedScene, default,
            ProcessingInputSelector.Raw(raw.Variant), [raw], "predicted",
            AuxiliaryInputs: [new("scene", ProcessingAuxiliaryInputKind.CanonicalJson,
                SchemaVersion: scene.SchemaVersion, IdentitySha256: scene.SceneIdentitySha256, Payload: sceneBytes)
                { ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes) }]), token).ConfigureAwait(false));
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
