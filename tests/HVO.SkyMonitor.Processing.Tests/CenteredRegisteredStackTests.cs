using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "MSTest handles its async tests.")]
public sealed class CenteredRegisteredStackTests
{
    private const int Width = 160;
    private const int Height = 128;
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero);
    private static readonly CameraKey Camera = new("centered-test-agent", "camera-1");
    private static readonly Guid Run = new("076548f8-a029-4765-b16a-e18841b70bf1");
    private static readonly ProcessingCompatibilityIdentity Compatible = new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
    private static readonly RecipeIdentityDescriptor RawRecipe = RecipeIdentityDescriptor.Create("capture", "1.0.0", "test",
        JsonSerializer.SerializeToElement(new { }));

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task IndependentGeometryCorpusUsesActualMeasurementAndSeparateBaseMaps(bool bayer, bool calibrate)
    {
        var root = Environment.GetEnvironmentVariable("HVO_REGISTRATION_GEOMETRY");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set HVO_REGISTRATION_GEOMETRY to the independently supplied geometry fixture.");
        }
        using var oracle = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root!, "geometry-oracle.json")));
        var sourcePlacement = oracle.RootElement.GetProperty("sourcesAt103").EnumerateArray()
            .Select(row => new PixelPoint(row[0].GetDouble(), row[1].GetDouble())).ToArray();
        var captures = new List<Capture>();
        foreach (var row in oracle.RootElement.GetProperty("frames").EnumerateArray())
        {
            var name = row.GetProperty("file").GetString()!;
            if (name.StartsWith("bayer-", StringComparison.Ordinal) != bayer)
            {
                continue;
            }
            var pixels = await File.ReadAllBytesAsync(Path.Combine(root!, name));
            Assert.AreEqual(row.GetProperty("sha256").GetString()!.ToUpperInvariant(), ProcessingIdentity.ComputePayloadSha256(pixels));
            captures.Add(await BuildCapture(row.GetProperty("frame").GetInt32(), pixels, bayer, calibrate: calibrate));
        }

        var firstOutcome = await Execute(captures.Take(5).ToArray(), 103);
        var nextOutcome = await Execute(captures.Skip(1).ToArray(), 104);
        var first = Read(firstOutcome);
        var next = Read(nextOutcome);
        var planes = firstOutcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray();
        first.VerifyPlanes(planes);
        Assert.ThrowsExactly<InvalidDataException>(() => first.VerifyPlanes(planes.Skip(1).ToArray()));
        var stored = new byte[planes[0].Payload.Length];
        var altered = planes.ToArray();
        altered[0] = altered[0] with { Payload = stored };
        Assert.ThrowsExactly<InvalidDataException>(() => first.VerifyPlanes(altered));
        Assert.AreEqual(CenteredStackDisposition.Registered, first.Disposition);
        Assert.AreEqual(CenteredStackDisposition.Registered, next.Disposition);
        Assert.AreEqual(5, first.Sources.Count(source => source.Reason == CenteredRegistrationReason.Contributed), Describe(first));
        Assert.AreEqual(5, next.Sources.Count(source => source.Reason == CenteredRegistrationReason.Contributed), Describe(next));
        var match = first.Sources.Single(source => source.CaptureId == captures[4].Pin.Key.CaptureId);
        var nextMatch = next.Sources.Single(source => source.CaptureId == captures[4].Pin.Key.CaptureId);
        Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, match.Method);
        var firstExpected = oracle.RootElement.GetProperty("knownSource105ToCenter103");
        var nextExpected = oracle.RootElement.GetProperty("knownSource105ToCenter104");
        var dxFirst = firstExpected[0].GetDouble();
        var dyFirst = firstExpected[1].GetDouble();
        var dxNext = nextExpected[0].GetDouble();
        var dyNext = nextExpected[1].GetDouble();
        var maxError = 0.0;
        foreach (var heldPosition in sourcePlacement)
        {
            var sampleOn105 = new PixelPoint(heldPosition.X - dxFirst, heldPosition.Y - dyFirst);
            var a = CenteredRegisteredStackRecipe.ReconstructSourceMap(first, match.Offset, new Dictionary<Guid, ProcessingArtifact>())!
                .SourceToReference(sampleOn105)!.Value;
            var b = CenteredRegisteredStackRecipe.ReconstructSourceMap(next, nextMatch.Offset, new Dictionary<Guid, ProcessingArtifact>())!
                .SourceToReference(sampleOn105)!.Value;
            var tolerance = bayer ? .65 : .30;
            Assert.AreEqual(heldPosition.X, a.X, tolerance);
            Assert.AreEqual(heldPosition.Y, a.Y, tolerance);
            Assert.AreEqual(sampleOn105.X + dxNext, b.X, tolerance);
            Assert.AreEqual(sampleOn105.Y + dyNext, b.Y, tolerance);
            Assert.IsGreaterThan(2, Math.Abs(a.X - b.X));
            maxError = Math.Max(maxError, new[] { Math.Abs(heldPosition.X - a.X), Math.Abs(heldPosition.Y - a.Y),
                Math.Abs(sampleOn105.X + dxNext - b.X), Math.Abs(sampleOn105.Y + dyNext - b.Y) }.Max());
        }

        var support = firstOutcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.SupportMediaType);
        Assert.AreEqual((byte)31, support.Payload.Span[50 * Width + 50]);
        Assert.AreEqual(5 * TimeSpan.TicksPerSecond, first.IntegrationTicksAt(31));
        Assert.AreEqual(5 * TimeSpan.TicksPerSecond, first.TotalIntegrationTicks);
        Assert.AreEqual(Start.AddSeconds(6.5), first.ReferenceMidpointUtc);
        var repeated = await Execute(captures.Take(5).ToArray(), 103);
        CollectionAssert.AreEqual(firstOutcome.Products.Select(product => product.ChecksumSha256).ToArray(),
            repeated.Products.Select(product => product.ChecksumSha256).ToArray());
        CollectionAssert.AreEqual(firstOutcome.Products.Select(product => product.OutputIdentitySha256).ToArray(),
            repeated.Products.Select(product => product.OutputIdentitySha256).ToArray());

        var background = calibrate ? Math.Round((91d - 64) * ushort.MaxValue / (4095 - 64)) : 91;
        if (bayer)
        {
            var red = Channel(firstOutcome, "red");
            var green = Channel(firstOutcome, "green");
            var blue = Channel(firstOutcome, "blue");
            Assert.IsGreaterThan(2 * Signal(blue, sourcePlacement[0], background), Signal(red, sourcePlacement[0], background));
            Assert.IsGreaterThan(2 * Signal(red, sourcePlacement[2], background), Signal(blue, sourcePlacement[2], background));
            Assert.AreNotEqual(red.ChecksumSha256, green.ChecksumSha256);
            Assert.AreNotEqual(blue.ChecksumSha256, green.ChecksumSha256);
        }
        else
        {
            Assert.IsGreaterThan(200, Signal(Channel(firstOutcome, "mono"), sourcePlacement[0], background));
        }
        TestContext.WriteLine($"{(bayer ? "Bayer" : "Mono")} {(calibrate ? "reference-calibrated" : "normalization-only")} direct max per-axis error {maxError:F4} px, support {first.ValidPixelCount}/{Width * Height}, checksum {support.ChecksumSha256}");
    }

    [TestMethod]
    public async Task ExcludedMeasurementCannotContributeAndMissingCenterNeverBorrowsNeighbor()
    {
        var goodPixels = Generate((x, y) => (ushort)(91 + 2500 * Math.Exp(-((x - 55) * (x - 55) + (y - 45) * (y - 45)) / 4d)));
        var blankPixels = Generate(static (_, _) => 91);
        var center = await BuildCapture(103, goodPixels, false);
        var blank = await BuildCapture(105, blankPixels, false);
        var outcome = await Execute([center, blank], 103);
        var evidence = Read(outcome);
        Assert.AreEqual(CenteredStackDisposition.SingleExposure, evidence.Disposition);
        Assert.AreEqual(CenteredRegistrationReason.MeasurementEmpty, evidence.Sources[4].Reason);
        Assert.IsNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, 2, new Dictionary<Guid, ProcessingArtifact>()));
        Assert.AreEqual(TimeSpan.TicksPerSecond, evidence.TotalIntegrationTicks);
        Assert.HasCount(1, outcome.Products);

        var lost = Read(await Execute([blank], 103));
        Assert.AreEqual(CenteredStackDisposition.NoReference, lost.Disposition);
        Assert.AreEqual(CenteredRegistrationReason.ReferenceUnavailable, lost.Sources[4].Reason);
        Assert.AreEqual(0, lost.TotalIntegrationTicks);
        Assert.IsEmpty(lost.Outputs);
    }

    [TestMethod]
    public async Task RehashedMeasurementFromAnotherCaptureFailsIndependentBinding()
    {
        var pixels = Generate(static (_, _) => 91);
        var center = await BuildCapture(103, pixels, false);
        var later = await BuildCapture(105, pixels, false);
        var swapped = center.Measurement with { CaptureId = later.Pin.Key.CaptureId };
        var tampered = later with
        {
            Pin = later.Pin with { Measurement = CenteredStackArtifactPin.From(swapped) },
            Measurement = swapped
        };
        // Each artifact id may be pinned once. Omit the reference product to isolate the rehashed endpoint mutation.
        var request = new[] { center with { Pin = center.Pin with { Measurement = null } }, tampered };
        var evidence = Read(await Execute(request, 103));
        Assert.AreEqual(CenteredRegistrationReason.InvalidMeasurement, evidence.Sources[4].Reason);
        Assert.IsNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, 2, new Dictionary<Guid, ProcessingArtifact>()));
    }

    [TestMethod]
    public async Task OrderedPlanRejectsFilteringToReferenceAndPropagatesCancellation()
    {
        var pixels = Generate(static (_, _) => 91);
        var captures = new[] { await BuildCapture(103, pixels, false), await BuildCapture(105, pixels, false) };
        var (request, executor) = Request(captures, 103);
        var filtered = await executor.ExecuteAsync(request with { InputArtifactId = captures[0].Pin.Linear.ArtifactId });
        Assert.AreEqual(CenteredRegisteredStackRecipe.InvalidPlan, filtered.ReasonCode);
        var reordered = await executor.ExecuteAsync(request with { Inputs = request.Inputs.Reverse().ToArray() });
        Assert.AreEqual(CenteredRegisteredStackRecipe.InvalidPlan, reordered.ReasonCode);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await executor.ExecuteAsync(request, cancel.Token));
    }

    [TestMethod]
    public async Task PinnedInputLossIsRetryableAndMetadataDriftProducesNoProducts()
    {
        var pixels = Generate(static (_, _) => 91);
        var center = await BuildCapture(103, pixels, false, measurementReason: "qualified-fixture");
        var source = await BuildCapture(105, pixels, false, measurementReason: "qualified-fixture");
        var (request, executor) = Request([center, source], 103);
        var missing = await executor.ExecuteAsync(request with
        {
            Inputs = request.Inputs.Where(artifact => artifact.ArtifactId != source.Measurement.ArtifactId).ToArray()
        });
        Assert.AreEqual(ProcessingOutcomeStatus.RetryableFailure, missing.Status);
        Assert.AreEqual(CenteredRegisteredStackRecipe.RetainedInputsUnavailable, missing.ReasonCode);
        Assert.IsEmpty(missing.Products);
        var tampered = await executor.ExecuteAsync(request with
        {
            Inputs = request.Inputs.Select(artifact => artifact.ArtifactId == source.Raw.ArtifactId
                ? artifact with { Compatibility = artifact.Compatibility with { Rig = "other" } } : artifact).ToArray()
        });
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, tampered.ReasonCode);
        Assert.IsEmpty(tampered.Products);
        var restored = Read(await executor.ExecuteAsync(request));
        Assert.AreEqual(CenteredRegistrationReason.MeasurementEmpty, restored.Sources[4].Reason);
    }

    [TestMethod]
    public async Task RetainedPlatesAndRelativeEndpointsShareOneWindowAndReopen()
    {
        var centerFixture = await MeasuredAstrometryFixture.CreateAtAsync(0);
        var firstFixture = await MeasuredAstrometryFixture.CreateAtAsync(-2);
        var lastFixture = await MeasuredAstrometryFixture.CreateAtAsync(4);
        var center = await FromPlateFixture(centerFixture, 103, includePlate: true);
        var first = await FromPlateFixture(firstFixture, 102, includePlate: true);
        var last = await FromPlateFixture(lastFixture, 105, includePlate: false);
        var evidence = Read(await Execute([first, center, last], 103));
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, Describe(evidence));
        Assert.AreEqual(CenteredRegistrationMethod.VerifiedPlatePair, evidence.Sources[1].Method, Describe(evidence));
        Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, evidence.Sources[4].Method, Describe(evidence));
        var retained = first.Artifacts().Concat(center.Artifacts()).ToDictionary(item => item.ArtifactId);
        var map = CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, -1, retained)!;
        var independentCheck = evidence.Sources[1].PairCheck!.Verification;
        Assert.IsGreaterThanOrEqualTo(8, independentCheck.Count);
        var source = ExposureMeasurementProducts.Read(first.Measurement,
            MeasurementBinding(first)).Sources.Detections[independentCheck[0].SourceIndex].Pixel;
        var reference = ExposureMeasurementProducts.Read(center.Measurement,
            MeasurementBinding(center)).Sources.Detections[independentCheck[0].ReferenceIndex].Pixel;
        var mapped = map.SourceToReference(source)!.Value;
        Assert.AreEqual(reference.X, mapped.X, .85);
        Assert.AreEqual(reference.Y, mapped.Y, .85);
        Assert.IsNotNull(evidence.Sources[1].Plate?.UncertaintyReason);
        retained.Remove(first.Pin.Plate!.Context.ArtifactId);
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, -1, retained));

        var (associations, scene, context) = (center.PlateArtifacts[0], center.PlateArtifacts[1], center.PlateArtifacts[2]);
        var rejected = await Produce(RetainedAstrometryProducts.Request(center.Raw, associations, scene, context,
            MeasuredAstrometryFixture.Options with
            {
                Solver = MeasuredAstrometryFixture.Options.Solver! with { ColdBudgetMilliseconds = .000001 }
            }, "solve"));
        Assert.IsFalse(MeasuredAstrometryJson.Parse(rejected.Payload).Assessment.HasMeasuredMapping);
        var rejectedArtifact = Artifact(rejected, center.Raw);
        var rejectedCenter = center with
        {
            Pin = center.Pin with { Plate = center.Pin.Plate! with { Solution = CenteredStackArtifactPin.From(rejectedArtifact) } },
            PlateArtifacts = [associations, scene, context, rejectedArtifact]
        };
        var fallback = Read(await Execute([first, rejectedCenter, last], 103));
        Assert.AreEqual(CenteredStackDisposition.Registered, fallback.Disposition, Describe(fallback));
        Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, fallback.Sources[1].Method, Describe(fallback));
        Assert.IsNotNull(fallback.Sources[2].Plate?.AssessmentReason);
    }

    public TestContext TestContext { get; set; } = null!;

    private static string Describe(CenteredRegisteredStackEvidence evidence) =>
        string.Join(", ", evidence.Sources.Select(source => $"{source.Offset}={source.Reason}/{source.PairCheck?.Status}({source.PairCheck?.Fit.Count},{source.PairCheck?.Verification.Count})"));

    private static ProcessingProduct Channel(ProcessingOutcome outcome, string channel) =>
        outcome.Products.Single(product => product.Variant == "display-" + channel);

    private static double Signal(ProcessingProduct plane, PixelPoint position, double background)
    {
        var sum = 0.0;
        for (var y = (int)position.Y - 3; y <= (int)position.Y + 3; y++)
        {
            for (var x = (int)position.X - 3; x <= (int)position.X + 3; x++)
            {
                if (Math.Pow(x + .5 - position.X, 2) + Math.Pow(y + .5 - position.Y, 2) <= 9)
                {
                    sum += BinaryPrimitives.ReadUInt16LittleEndian(plane.Payload.Span[((y * Width + x) * 2)..]) - background;
                }
            }
        }
        return sum;
    }

    private static CenteredRegisteredStackEvidence Read(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode} {outcome.Field}");
        var product = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        return CenteredRegisteredStackEvidence.Read(product);
    }

    private static async Task<ProcessingOutcome> Execute(Capture[] captures, int center)
    {
        var (request, executor) = Request(captures, center);
        return await executor.ExecuteAsync(request);
    }

    private static (ProcessingExecutionRequest Request, ProcessingRecipeExecutor Executor) Request(Capture[] captures, int center)
    {
        var slots = Enumerable.Range(center - 2, 5).Select(sequence =>
        {
            var capture = captures.SingleOrDefault(item => item.Pin.CaptureSequence == sequence);
            return new CenteredStackSlot(sequence - center, Time(sequence),
                capture is null ? CenteredSlotState.Missing : CenteredSlotState.Ready,
                capture?.Pin.Key.CaptureId, capture?.Pin);
        }).ToArray();
        var plan = new CenteredStackPlanV1(Camera, Run, "test-generation", slots[2].CaptureId,
            slots[2].Exposure?.Linear.ArtifactId, Time(center + 3), Time(center + 3), "cadence-test-closure", slots);
        var reader = new ExactInputs(captures.Select(item => item.Qualified).ToArray());
        var artifacts = captures.OrderBy(item => item.Pin.CaptureSequence).SelectMany(item => item.Artifacts()).ToArray();
        return (CenteredRegisteredStackRecipe.CreateRequest(plan, "qualified", "display", artifacts),
            new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(reader)]));
    }

    private static async Task<Capture> BuildCapture(int sequence, byte[] pixels, bool bayer,
        int width = Width, int height = Height, DateTimeOffset? observation = null, string? measurementReason = null,
        bool calibrate = false)
    {
        var layout = new FrameLayoutDescriptor(width, height, width * 2,
            bayer ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            64, 4095, pixels.Length)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = bayer ? new(width + 2, height + 2, 1, 1, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1)
                : new(width, height, 0, 0, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
        };
        var time = observation ?? Time(sequence);
        var end = time.AddSeconds(1);
        var integration = end - time;
        var profile = new ProfileIdentityDescriptor("fixed", "1", new string('A', 64));
        var rawDescriptor = new ReconstructionDescriptor(new(Camera.AgentId, "rig-1", sequence, Guid.NewGuid()),
            new(time, time, end, end, end), new(integration, integration, 10, 10, 0, 0, null, -10),
            new(profile, profile, profile, profile, profile), layout,
            new(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-1", "raw", end, [], RawRecipe,
                "application/x-hvo-frame", ProcessingIdentity.ComputePayloadSha256(pixels)));
        var rawSource = ExposureMeasurementSourceV1.FromDescriptor(rawDescriptor);
        Assert.IsTrue(rawDescriptor.Validate().IsValid, rawDescriptor.Validate().FieldPath);
        var raw = new ProcessingArtifact(rawSource.ArtifactId, FrameArtifactRole.Raw, "raw", rawSource.RecipeSha256,
            rawDescriptor.Artifact.MediaType, layout, pixels, end, integration, Compatible, sequence, [],
            time, end, new(10, 0, -10))
        {
            CaptureId = rawSource.CaptureId, DescriptorIdentitySha256 = rawSource.DescriptorSha256, RecipeDescriptor = RawRecipe
        };
        var reference = calibrate ? ReferenceCalibration(raw) : null;
        var linearProduct = await Produce(reference ?? new(BuiltInProcessingRecipes.LinearNormalization, default,
            ProcessingInputSelector.Raw("raw"), [raw], "qualified"));
        var linear = Artifact(linearProduct, raw);
        var linearSource = new ExposureMeasurementSourceV1(rawSource.CaptureId, linear.ArtifactId,
            linear.DescriptorIdentitySha256!, linearProduct.ChecksumSha256, linear.RecipeIdentitySha256,
            linearProduct.OutputIdentitySha256, linear.Layout!);
        var orientation = new RigOrientation(90, 180, 0);
        var measurementProduct = await Produce(ExposureMeasurementProducts.Request(
            new(rawSource, linearSource, calibrate ? ExposureCalibrationDisposition.ReferenceCalibrated : ExposureCalibrationDisposition.NormalizationOnly,
                measurementReason, Compatible.Orientation, 2.4, orientation),
            raw, linear));
        var measurement = Artifact(measurementProduct, raw);
        var qualified = new LinearExposureInputV1(new(new(Camera, Run, rawSource.CaptureId), rawDescriptor,
            new(layout, orientation, profile.Sha256, "fixture", 5.5)), linear,
            new(calibrate ? LinearExposureDisposition.Calibrated : LinearExposureDisposition.QualifiedNormalizationOnly,
                raw.ArtifactId, rawSource.DescriptorSha256, rawSource.PayloadSha256, linear.ArtifactId,
                linearSource.DescriptorSha256, linearSource.PayloadSha256, linearProduct.OutputIdentitySha256,
                linearProduct.Recipe, linear.SourceArtifactIds!.Skip(1).ToImmutableArray(), new(raw.ArtifactId, 4095),
                new(linear.ArtifactId, calibrate ? ushort.MaxValue : 4095))
            {
                CalibrationBundleIdentitySha256 = reference?.AuxiliaryInputs?[0].IdentitySha256,
                CalibrationPayloadHashes = reference is null ? [] :
                    reference.Inputs.Skip(1).Select(item => ProcessingIdentity.ComputePayloadSha256(item.Payload)).ToImmutableArray()
            });
        var mask = CenteredStackValidityMask.Create(raw, linear, Enumerable.Repeat((byte)1, layout.Width * layout.Height).ToArray());
        var retained = new CenteredStackQualifiedExposure(qualified, new(2.4, null), measurementReason);
        return new(retained, CenteredStackExposurePin.Create(retained, raw, mask, CenteredStackArtifactPin.From(measurement)), raw, mask, measurement);
    }

    private static async Task<Capture> FromPlateFixture(MeasuredAstrometryFixture fixture, int sequence, bool includePlate)
    {
        var capture = await BuildCapture(sequence, fixture.Raw.Payload.ToArray(), false, width: 512, height: 512,
            observation: fixture.Raw.ObservationStartedUtc!.Value);
        if (!includePlate)
        {
            return capture;
        }
        var raw = capture.Raw;
        var scene = fixture.Scene with { Source = new(raw.CaptureId!.Value, raw.ArtifactId, raw.DescriptorIdentitySha256!) };
        scene = scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };
        var sceneInput = MeasuredStellarAssociationFixtures.SceneAuxiliary(scene);
        var sceneArtifact = Artifact(await Produce(new(BuiltInProcessingRecipes.ProjectedScene, default,
            ProcessingInputSelector.Raw(raw.Variant), [raw], "projection", AuxiliaryInputs: [sceneInput])), raw);
        var associations = Artifact(await Produce(new(BuiltInProcessingRecipes.MeasuredStellarAssociations, default,
            ProcessingInputSelector.Raw(raw.Variant), [raw], "association", AuxiliaryInputs: [sceneInput])), raw);
        var context = RetainedAstrometryProducts.CreateContext(raw, associations, sceneArtifact,
            new(fixture.Context.CatalogMetadata, fixture.Context.CatalogStars, fixture.Context.CatalogPackage,
                fixture.Context.CatalogComplete, fixture.Context.CompletenessMagnitudeLimit),
            AstrometricClockFacts.NotSupplied);
        var contextArtifact = Artifact(RetainedAstrometryProducts.CreateContextProduct(context, raw, associations, sceneArtifact, "context"), raw);
        var solution = await Produce(RetainedAstrometryProducts.Request(raw, associations, sceneArtifact, contextArtifact,
            MeasuredAstrometryFixture.Options, "solve"));
        Assert.IsTrue(MeasuredAstrometryJson.Parse(solution.Payload).Assessment.HasMeasuredMapping);
        var solutionArtifact = Artifact(solution, raw);
        var pins = new CenteredStackPlatePins(CenteredStackArtifactPin.From(associations), CenteredStackArtifactPin.From(sceneArtifact),
            CenteredStackArtifactPin.From(contextArtifact), CenteredStackArtifactPin.From(solutionArtifact));
        return capture with
        {
            Pin = capture.Pin with { Plate = pins },
            PlateArtifacts = [associations, sceneArtifact, contextArtifact, solutionArtifact]
        };
    }

    private static ExposureMeasurementInputV1 MeasurementBinding(Capture capture)
    {
        var data = capture.Qualified.Linear;
        var provenance = data.Provenance;
        return new(ExposureMeasurementSourceV1.FromDescriptor(data.Exposure.Raw),
            new(data.Exposure.Key.CaptureId, provenance.LinearArtifactId, provenance.LinearDescriptorSha256,
                provenance.LinearPayloadSha256, provenance.Recipe.IdentitySha256, provenance.OutputIdentitySha256, data.Linear.Layout!),
            ExposureCalibrationDisposition.NormalizationOnly, null, capture.Raw.Compatibility.Orientation,
            2.4, data.Exposure.Geometry.Orientation);
    }

    private static ProcessingExecutionRequest ReferenceCalibration(ProcessingArtifact raw)
    {
        var layout = CalibrationMasterBuilder.CreateNormalizedLayout(raw.Layout!);
        var references = CalibrationReferenceKinds.All.Select((kind, index) =>
        {
            var pixels = new byte[(int)layout.ByteLength];
            if (kind == CalibrationReferenceKinds.Flat)
            {
                for (var offset = 0; offset < pixels.Length; offset += 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset, 2), 30000);
                }
            }
            return raw with
            {
                ArtifactId = new Guid(index + 1, 0, 0, new byte[8]),
                Variant = kind,
                Layout = layout,
                Payload = pixels
            };
        }).ToArray();
        var profile = new ReferenceCalibrationProfileV1(ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            "uniform-reference", "1", "independently known fixture references", Start.AddDays(-1), null,
            layout.Width, layout.Height, layout.PixelFormat, 30000, 9, 11, -11, -9,
            references.Select(reference => new CalibrationReferenceDescriptorV1(reference.Variant, reference.ArtifactId,
                ProcessingIdentity.ComputePayloadSha256(reference.Payload), reference.Integration,
                reference.Conditions!.Gain, reference.Conditions.TemperatureC)).ToArray());
        return new(BuiltInProcessingRecipes.ReferenceCalibration, default, ProcessingInputSelector.Raw(raw.Variant),
            [raw, .. references], "qualified", AuxiliaryInputs:
            [new("calibration-profile", ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: ReferenceCalibrationProfileV1.CurrentSchemaVersion,
                IdentitySha256: ReferenceCalibrationProfileJson.ComputeIdentitySha256(profile), Payload: ReferenceCalibrationProfileJson.Serialize(profile)),
             .. references.Select(reference => new ProcessingAuxiliaryInput(reference.Variant + "-reference",
                 ProcessingAuxiliaryInputKind.Artifact, ProcessingInputSelector.Raw(reference.Variant), ArtifactId: reference.ArtifactId))]);
    }

    private static DateTimeOffset Time(int sequence) => Start.AddSeconds(2 * (sequence - 100));

    private static ProcessingArtifact Artifact(ProcessingProduct product, ProcessingArtifact raw) =>
        raw with
        {
            ArtifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), Role = product.Role,
            Variant = product.Variant, RecipeIdentitySha256 = product.Recipe.IdentitySha256,
            MediaType = product.MediaType, Layout = product.Layout, Payload = product.Payload,
            SourceArtifactIds = product.SourceArtifactIds, ProductKind = product.Kind, SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256, RecipeDescriptor = product.Recipe.Descriptor,
            DescriptorIdentitySha256 = MeasuredAstrometryFixture.Hash(product.OutputIdentitySha256)
        };

    private static async Task<ProcessingProduct> Produce(ProcessingExecutionRequest request)
    {
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode} {outcome.Field}");
        return outcome.Products.Single();
    }

    private static byte[] Generate(Func<int, int, ushort> sample)
    {
        var bytes = new byte[Width * Height * 2];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * Width + x) * 2), sample(x, y));
            }
        }
        return bytes;
    }

    private sealed record Capture(CenteredStackQualifiedExposure Qualified, CenteredStackExposurePin Pin,
        ProcessingArtifact Raw, ProcessingArtifact Mask, ProcessingArtifact Measurement)
    {
        public ProcessingArtifact[] PlateArtifacts { get; init; } = [];
        public IEnumerable<ProcessingArtifact> Artifacts()
        {
            yield return Raw;
            yield return Qualified.Linear.Linear;
            yield return Mask;
            if (Pin.Measurement is not null)
            {
                yield return Measurement;
            }
            foreach (var artifact in PlateArtifacts)
            {
                yield return artifact;
            }
        }
    }

    private sealed class ExactInputs(CenteredStackQualifiedExposure[] inputs) : ICenteredStackInputReader
    {
        public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, Guid linearArtifactId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(inputs.SingleOrDefault(item => item.Linear.Exposure.Key == key &&
                item.Linear.Linear.ArtifactId == linearArtifactId));
        }
    }
}
