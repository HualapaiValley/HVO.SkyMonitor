using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IndependentGeometryCorpusUsesActualMeasurementAndSeparateBaseMaps(bool bayer)
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
            captures.Add(await BuildCapture(row.GetProperty("frame").GetInt32(), pixels, bayer));
        }

        var firstOutcome = await Execute(captures.Take(5).ToArray(), 103);
        var nextOutcome = await Execute(captures.Skip(1).ToArray(), 104);
        var first = Read(firstOutcome);
        var next = Read(nextOutcome);
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
            var a = match.MapToReference(sampleOn105)!.Value;
            var b = nextMatch.MapToReference(sampleOn105)!.Value;
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

        if (bayer)
        {
            var red = Channel(firstOutcome, "red");
            var green = Channel(firstOutcome, "green");
            var blue = Channel(firstOutcome, "blue");
            Assert.IsGreaterThan(2 * Signal(blue, sourcePlacement[0]), Signal(red, sourcePlacement[0]));
            Assert.IsGreaterThan(2 * Signal(red, sourcePlacement[2]), Signal(blue, sourcePlacement[2]));
            Assert.AreNotEqual(red.ChecksumSha256, green.ChecksumSha256);
            Assert.AreNotEqual(blue.ChecksumSha256, green.ChecksumSha256);
        }
        else
        {
            Assert.IsGreaterThan(200, Signal(Channel(firstOutcome, "mono"), sourcePlacement[0]));
        }
        TestContext.WriteLine($"{(bayer ? "Bayer" : "Mono")} direct max per-axis error {maxError:F4} px, support {first.ValidPixelCount}/{Width * Height}, checksum {support.ChecksumSha256}");
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
        Assert.IsNull(evidence.Sources[4].MapToReference(new(55.5, 45.5)));
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
        Assert.IsNull(evidence.Sources[4].MapToReference(new(55.5, 45.5)));
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

    public TestContext TestContext { get; set; } = null!;

    private static string Describe(CenteredRegisteredStackEvidence evidence) =>
        string.Join(", ", evidence.Sources.Select(source => $"{source.Offset}={source.Reason}/{source.PairCheck?.Status}({source.PairCheck?.Fit.Count},{source.PairCheck?.Verification.Count})"));

    private static ProcessingProduct Channel(ProcessingOutcome outcome, string channel) =>
        outcome.Products.Single(product => product.Variant == "display-" + channel);

    private static double Signal(ProcessingProduct plane, PixelPoint position)
    {
        var sum = 0.0;
        for (var y = (int)position.Y - 3; y <= (int)position.Y + 3; y++)
        {
            for (var x = (int)position.X - 3; x <= (int)position.X + 3; x++)
            {
                if (Math.Pow(x + .5 - position.X, 2) + Math.Pow(y + .5 - position.Y, 2) <= 9)
                {
                    sum += BinaryPrimitives.ReadUInt16LittleEndian(plane.Payload.Span[((y * Width + x) * 2)..]) - 91;
                }
            }
        }
        return sum;
    }

    private static CenteredRegisteredStackEvidence Read(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode} {outcome.Field}");
        var product = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        return JsonSerializer.Deserialize<CenteredRegisteredStackEvidence>(product.Payload.Span, Json)!;
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
            return new CenteredStackSlot(sequence - center, Time(sequence).AddSeconds(.5),
                capture is null ? CenteredSlotState.Missing : CenteredSlotState.Ready,
                capture?.Pin.Key.CaptureId, capture?.Pin);
        }).ToArray();
        var plan = new CenteredStackPlanV1(Camera, Run, slots[2].CaptureId ?? Guid.NewGuid(),
            slots[2].Exposure?.Linear.ArtifactId, Time(center + 3), "cadence-test-v1", slots);
        var reader = new ExactInputs(captures.Select(item => item.Qualified).ToArray());
        var artifacts = captures.OrderBy(item => item.Pin.CaptureSequence).SelectMany(item => item.Artifacts()).ToArray();
        return (CenteredRegisteredStackRecipe.CreateRequest(plan, "qualified", "display", artifacts),
            new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(reader)]));
    }

    private static async Task<Capture> BuildCapture(int sequence, byte[] pixels, bool bayer)
    {
        var layout = new FrameLayoutDescriptor(Width, Height, Width * 2,
            bayer ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            64, 4095, pixels.Length)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = bayer ? new(Width + 2, Height + 2, 1, 1, Width, Height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1)
                : new(Width, Height, 0, 0, Width, Height, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
        };
        var time = Time(sequence);
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
        var linearProduct = await Produce(new(BuiltInProcessingRecipes.LinearNormalization, default,
            ProcessingInputSelector.Raw("raw"), [raw], "qualified"));
        var linear = Artifact(linearProduct, raw);
        var linearSource = new ExposureMeasurementSourceV1(rawSource.CaptureId, linear.ArtifactId,
            linear.DescriptorIdentitySha256!, linearProduct.ChecksumSha256, linear.RecipeIdentitySha256,
            linearProduct.OutputIdentitySha256, linear.Layout!);
        var orientation = new RigOrientation(90, 180, 0);
        var measurementProduct = await Produce(ExposureMeasurementProducts.Request(
            new(rawSource, linearSource, ExposureCalibrationDisposition.NormalizationOnly, null, Compatible.Orientation, 2.4, orientation),
            raw, linear));
        var measurement = Artifact(measurementProduct, raw);
        var qualified = new LinearExposureInputV1(new(new(Camera, Run, rawSource.CaptureId), rawDescriptor,
            new(layout, orientation, profile.Sha256, "fixture", 5.5)), linear,
            new(LinearExposureDisposition.QualifiedNormalizationOnly,
                raw.ArtifactId, rawSource.DescriptorSha256, rawSource.PayloadSha256, linear.ArtifactId,
                linearSource.DescriptorSha256, linearSource.PayloadSha256, linearProduct.OutputIdentitySha256,
                linearProduct.Recipe, ImmutableArray<Guid>.Empty, new(raw.ArtifactId, 4095), new(linear.ArtifactId, 4095)));
        var mask = raw with
        {
            ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Metadata, Variant = "qualified-mask", Layout = null,
            Payload = Enumerable.Repeat((byte)1, Width * Height).ToArray(), SourceArtifactIds = [raw.ArtifactId, linear.ArtifactId]
        };
        var retained = new CenteredStackQualifiedExposure(qualified, new(2.4, null));
        return new(retained, CenteredStackExposurePin.Create(retained, raw, mask, CenteredStackArtifactPin.From(measurement)), raw, mask, measurement);
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
        public IEnumerable<ProcessingArtifact> Artifacts()
        {
            yield return Raw;
            yield return Qualified.Linear.Linear;
            yield return Mask;
            if (Pin.Measurement is not null)
            {
                yield return Measurement;
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
