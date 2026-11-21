using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
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
    [TestCategory("Manual")]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task IndependentGeometryCorpusUsesActualMeasurementAndSeparateBaseMaps(bool bayer, bool calibrate)
    {
        var root = Environment.GetEnvironmentVariable("HVO_REGISTRATION_GEOMETRY");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "Set HVO_REGISTRATION_GEOMETRY to the independently supplied geometry fixture.");
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
        var contributorIds = captures.Take(5).Select(capture => capture.Pin.Linear.ArtifactId).ToArray();
        Assert.IsTrue(planes.All(plane => plane.SourceArtifactIds.SequenceEqual(contributorIds)));
        first.VerifyPlanes(planes);
        Assert.ThrowsExactly<InvalidDataException>(() => first.VerifyPlanes(planes.Skip(1).ToArray()));
        var stored = new byte[planes[0].Payload.Length];
        var altered = planes.ToArray();
        altered[0] = altered[0] with { Payload = stored };
        Assert.ThrowsExactly<InvalidDataException>(() => first.VerifyPlanes(altered));
        altered[0] = planes[0] with { Layout = planes[0].Layout! with { ByteOrder = FrameByteOrder.BigEndian } };
        Assert.ThrowsExactly<InvalidDataException>(() => first.VerifyPlanes(altered));
        altered[0] = planes[0] with
        {
            Recipe = planes[0].Recipe with { Descriptor = planes[0].Recipe.Descriptor with { SemanticVersion = "99.0.0" } }
        };
        Assert.ThrowsExactly<InvalidDataException>(() => first.VerifyPlanes(altered));
        var retained = firstOutcome.Products.Single(product => product.SchemaVersion == CenteredRegisteredStackEvidence.CurrentSchemaVersion);
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(
            RewriteEvidence(retained, json => { json["width"] = Height; json["height"] = Width; })));
        if (bayer)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(RewriteEvidence(retained, json =>
            {
                var outputs = json["outputs"]!.AsArray();
                var red = outputs[0]!.DeepClone();
                var blue = outputs[2]!.DeepClone();
                red["channel"] = "blue";
                blue["channel"] = "red";
                outputs[0] = blue;
                outputs[2] = red;
            })));
        }
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
    [TestCategory("Unit")]
    public async Task BayerRecipePublishesExactLinearChannelsAndExcludesClippedReconstructionSupport()
    {
        var reference = await BuildCapture(103, Generate(Field), bayer: true);
        var neighbor = await BuildCapture(105, Generate((x, y) =>
            x is >= 69 and <= 73 && y is >= 59 and <= 63 ? (ushort)4095 : Field(x, y)), bayer: true);
        var result = await Execute([reference, neighbor], 103);
        var evidence = Read(result);
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, Describe(evidence));
        evidence.VerifyPlanes(result.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray());

        // The odd/odd crop is BGGR. The flat target has exact, different codes in each lane.
        Assert.AreEqual((ushort)216, CodeAt(Channel(result, "red"), 78, 61));
        Assert.AreEqual((ushort)132, CodeAt(Channel(result, "green"), 78, 61));
        Assert.AreEqual((ushort)88, CodeAt(Channel(result, "blue"), 78, 61));
        var support = Channel(result, "support").Payload;
        Assert.AreEqual((byte)20, support.Span[61 * Width + 78]);
        Assert.AreEqual((byte)4, support.Span[61 * Width + 71]);
        Assert.AreEqual((byte)0, support.Span[0]);
        Assert.AreEqual((ushort)0, CodeAt(Channel(result, "red"), 71, 61));

        static ushort Field(int x, int y) => StarField(x, y, (x & 1, y & 1) switch
        {
            (0, 0) => 88,
            (1, 1) => 216,
            _ => 132
        });
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ReferenceCalibratedRecipeRejectsRawAndCorrectedWhiteIndependently()
    {
        var reference = await BuildCapture(103, Generate(static (x, y) => StarField(x, y, 91)),
            bayer: false, calibrate: true, clippingControls: true);
        var neighbor = await BuildCapture(105, Generate(static (x, y) => y is >= 59 and <= 63 ? x switch
        {
            14 when y == 61 => (ushort)3999,
            >= 24 and <= 28 => (ushort)4000,
            >= 36 and <= 40 => (ushort)4001,
            >= 48 and <= 52 => (ushort)3000,
            _ => StarField(x, y, 91)
        } : StarField(x, y, 91)), bayer: false, calibrate: true, clippingControls: true);
        var corrected = neighbor.Qualified.Linear.Linear.Payload;
        Assert.IsLessThan(ushort.MaxValue, ReadCode(corrected, 26, 61), "Raw white must be below corrected white.");
        Assert.IsLessThan(ushort.MaxValue, ReadCode(corrected, 38, 61), "Above raw white must be below corrected white.");
        Assert.AreEqual(ushort.MaxValue, ReadCode(corrected, 50, 61), "Below raw white must reach corrected white.");

        var result = await Execute([reference, neighbor], 103);
        var evidence = Read(result);
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, Describe(evidence));
        var support = Channel(result, "support").Payload;
        Assert.AreEqual((byte)20, support.Span[61 * Width + 14]);
        Assert.AreEqual((byte)4, support.Span[61 * Width + 26]);
        Assert.AreEqual((byte)4, support.Span[61 * Width + 38]);
        Assert.AreEqual((byte)4, support.Span[61 * Width + 50]);
        Assert.AreEqual((byte)20, support.Span[61 * Width + 62]);
        Assert.AreEqual((ushort)0, CodeAt(Channel(result, "mono"), 26, 61));
        Assert.AreEqual(2 * TimeSpan.TicksPerSecond, evidence.IntegrationTicksAt(20));
        Assert.AreEqual(TimeSpan.TicksPerSecond, evidence.IntegrationTicksAt(4));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ReferenceApertureClipsMappedNeighborsButAllowsSupportAtClippedCenterPixel()
    {
        var aperture = new ProjectionContext(ProjectionModel.EquidistantFisheye, 80, 64, 50, 50, Width, Height,
            ProjectionAperture.Circular, ImageCircleRadiusPixels: 77);
        const int insideX = 151, insideY = 35;
        const int outsideX = 153, outsideY = 34;
        (double X, double Y)[] stars =
        [
            (50, 34), (78, 30), (109, 35), (121, 57), (44, 66), (76, 52),
            (99, 59), (110, 85), (72, 96), (50, 91), (90, 91), (66, 73)
        ];
        var reference = await BuildCapture(103, Generate((x, y) =>
            (x, y) == (insideX, insideY) ? (ushort)4095 : StarField(x, y, 91, stars)), false, aperture: aperture);
        var first = await BuildCapture(104, Generate((x, y) => StarField(x + 5, y, 81, stars)), false, aperture: aperture);
        var second = await BuildCapture(105, Generate((x, y) => StarField(x + 8, y, 101, stars)), false, aperture: aperture);
        var outcome = await Execute([reference, first, second], 103);
        var evidence = Read(outcome);
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, Describe(evidence));
        var support = Channel(outcome, "support").Payload.ToArray();
        var mono = Channel(outcome, "mono");
        var outside = Enumerable.Range(0, support.Length)
            .Where(index => Math.Pow(index % Width + .5 - 80, 2) + Math.Pow(index / Width + .5 - 64, 2) > 77 * 77)
            .ToArray();
        TestContext.WriteLine($"F2 exterior display-valid={outside.Count(i => CenteredRegisteredStackEvidence.ContributorsAt(support[i]) >= 2)}, " +
            $"exterior support={outside.Count(i => support[i] != 0)}, " +
            $"signed={support[outsideY * Width + outsideX]}/{CodeAt(mono, outsideX, outsideY)}, " +
            $"inside={support[insideY * Width + insideX]}/{CodeAt(mono, insideX, insideY)}; {Describe(evidence)}.");
        foreach (var (offset, shift) in new[] { (1, 5), (2, 8) })
        {
            Assert.AreEqual(CenteredRegistrationMethod.VerifiedCentroidSimilarity, evidence.Sources[offset + 2].Method);
            var map = CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, offset, new Dictionary<Guid, ProcessingArtifact>());
            Assert.AreEqual(shift, map!.SourceToReference(new(80.5, 64.5))!.Value.X - 80.5, .05);
        }

        Assert.IsTrue(outside.Contains(outsideY * Width + outsideX));
        Assert.IsFalse(outside.Contains(insideY * Width + insideX));
        Assert.AreEqual((byte)0, support[outsideY * Width + outsideX]);
        Assert.AreEqual((ushort)0, CodeAt(mono, outsideX, outsideY));
        Assert.IsTrue(outside.All(index => support[index] == 0 && CodeAt(mono, index % Width, index / Width) == 0));
        Assert.AreEqual((byte)24, support[insideY * Width + insideX]);
        Assert.AreEqual((ushort)91, CodeAt(mono, insideX, insideY));
        evidence.VerifyPlanes(outcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RehashedRelativeMemberRequiresAnInvertibleVerifiedMap(bool singular)
    {
        var outcome = await ShiftedMonoWindow();
        var retained = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        var evidence = CenteredRegisteredStackEvidence.Read(retained);
        var planes = outcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray();
        evidence.VerifyPlanes(planes);
        var emptyInputs = new Dictionary<Guid, ProcessingArtifact>();
        var member = evidence.Sources[3];
        var genuine = CenteredRegisteredStackRecipe.ReconstructSourceMap(evidence, 1, emptyInputs);
        Assert.IsNotNull(genuine!.ReferenceToSource(new(80, 64)));

        var rewritten = RewriteEvidence(retained, json =>
        {
            var source = json["sources"]![3]!;
            if (singular)
            {
                source["similarity"]!["a"] = 0;
                source["similarity"]!["b"] = 0;
            }
            else
            {
                source["pairCheck"] = null;
            }
        });
        var malformed = evidence.Sources.ToArray();
        malformed[3] = singular ? member with { Similarity = new ImageSimilarity(0, 0, 0, 0) }
            : member with { PairCheck = null };
        var read = Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(rewritten));
        var reconstruct = Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackRecipe.ReconstructSourceMap(
            evidence with { Sources = malformed }, 1, emptyInputs));
        TestContext.WriteLine($"F3 singular={singular}: Read rejected ({read.Message}); reconstruction rejected ({reconstruct.Message}).");
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("fit", .49, .52)]
    [DataRow("verification", .54, .56)]
    public async Task RetainedRelativeReportMustMeetItsOwnRmsLimit(string group, double withinLimit, double aboveLimit)
    {
        var outcome = await ShiftedMonoWindow();
        var retained = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        var planes = outcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray();
        var good = CenteredRegisteredStackEvidence.Read(WithResiduals(withinLimit));
        good.VerifyPlanes(planes);
        Assert.IsNotNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(good, 1, new Dictionary<Guid, ProcessingArtifact>()));
        TestContext.WriteLine($"Relative {group} RMS {withinLimit:F2} reopened and verified.");
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(WithResiduals(.80)));
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(WithResiduals(aboveLimit)));
        TestContext.WriteLine($"Relative {group} RMS 0.80 and {aboveLimit:F2} rejected.");

        ProcessingProduct WithResiduals(double residual) =>
            RewriteEvidence(retained, json =>
            {
                foreach (var match in json["sources"]![3]!["pairCheck"]![group]!.AsArray())
                {
                    match!["residualPixels"] = residual;
                }
            });
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ProducerVerifiedRelativeReportAtRmsCutoffReopens(bool fitCutoff)
    {
        var source = FieldStars.Select((star, index) => new StellarDetection(index, new(star.X + .5, star.Y + .5),
            200 - index, 100, 1, 1, 0, 0)).ToArray();
        var reference = source.ToArray();
        // These fixed, mixed perturbations put one producer sum exactly at its own inclusive cutoff.
        (int Index, double X, double Y)[] perturbed = fitCutoff
            ?
            [
                (0, 27.12647647177526, 21.11454430113261), (2, 82.98285055420278, 16.80980361654045),
                (3, 110.06554714256012, 27.792475173583714), (5, 139.98171119402394, 46.95588612884335),
                (6, 108.26321978992249, 81.79928985825158), (8, 91.995608516104, 107.45648657870537),
                (9, 61.94679110424578, 91.45075439091725), (11, 16.88217200666814, 84.95832191566922)
            ]
            :
            [
                (1, 57.36933930186058, 24.197421336838236), (4, 132.46346708060676, 20.13062106912187),
                (7, 130.80327878118757, 98.59617187042227), (10, 35.360226884971816, 105.06623700197864)
            ];
        foreach (var (index, x, y) in perturbed)
        {
            reference[index] = reference[index] with { Pixel = new(x, y) };
        }

        var fit = DirectCentroidRegistration.Fit(source, reference, Width, Height);
        Assert.AreEqual(DirectRegistrationStatus.Verified, fit.Check.Status);
        Assert.HasCount(8, fit.Check.Fit);
        Assert.HasCount(4, fit.Check.Verification);
        var fittedMap = fit.Map ?? throw new AssertFailedException("The verified fit did not return a map.");
        var matches = fitCutoff ? fit.Check.Fit : fit.Check.Verification;
        var limit = fitCutoff ? 8 * .50 * .50 : 4 * .55 * .55;
        var reSquared = matches.Sum(match => match.ResidualPixels * match.ResidualPixels);
        Assert.AreEqual(Math.BitIncrement(limit), reSquared);

        var outcome = await ShiftedMonoWindow();
        var retained = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        var rewritten = RewriteEvidence(retained, json =>
        {
            var contributor = json["sources"]![3]!;
            var report = JsonSerializer.SerializeToNode(fit.Check, JsonSerializerOptions.Web)!;
            report["status"] = fit.Check.Status.ToString();
            contributor["pairCheck"] = report;
            contributor["similarity"] = JsonSerializer.SerializeToNode(fittedMap, JsonSerializerOptions.Web);
        });
        TestContext.WriteLine($"Relative {(fitCutoff ? "fit" : "holdout")} producer {fit.Check.Status}, " +
            $"limit={limit:R}, sum re-squared={reSquared:R}.");
        var reopened = CenteredRegisteredStackEvidence.Read(rewritten);
        reopened.VerifyPlanes(outcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray());
        var map = CenteredRegisteredStackRecipe.ReconstructSourceMap(reopened, 1, new Dictionary<Guid, ProcessingArtifact>());
        Assert.AreEqual(fittedMap.SourceToReference(new(80, 64)), map!.SourceToReference(new(80, 64)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PlaneReaderChecksAllSupportBitsIncludingSinglySupportedPixels()
    {
        var outcome = await ShiftedMonoWindow();
        var retained = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        var planes = outcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray();
        var evidence = CenteredRegisteredStackEvidence.Read(retained);
        evidence.VerifyPlanes(planes);
        var support = Channel(outcome, "support").Payload.ToArray();
        var bit = 1 << 3;
        var actual = support.Count(value => (value & bit) != 0);
        var alone = support.Count(value => value == bit);
        Assert.IsGreaterThan(0, alone);
        Assert.AreEqual(evidence.Sources[3].SupportedPixels, actual);
        TestContext.WriteLine($"F4 actual +1 support={actual}; singly supported={alone}; display-valid={evidence.ValidPixelCount}.");
        foreach (var forged in new[] { 1, actual - alone })
        {
            var rewritten = RewriteEvidence(retained, json => json["sources"]![3]!["supportedPixels"] = forged);
            var reopened = CenteredRegisteredStackEvidence.Read(rewritten);
            var error = Assert.ThrowsExactly<InvalidDataException>(() => reopened.VerifyPlanes(planes));
            TestContext.WriteLine($"F4 forged +1 count {forged} rejected by plane verifier ({error.Message}).");
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task WriterRejectsHistoricalAndUnauthenticatedExecutionIdentities()
    {
        var center = await BuildCapture(103, Generate((x, y) => StarField(x, y, 91)), false);
        var neighbor = await BuildCapture(104, Generate((x, y) => StarField(x + 5, y, 81)), false);
        var (request, executor) = Request([center, neighbor], 103);
        var produced = await executor.ExecuteAsync(request);
        Read(produced).VerifyPlanes(produced.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray());
        var current = produced.Products[0].Recipe;
        var writer = new CenteredRegisteredStackRecipe(new ExactInputs([center.Qualified, neighbor.Qualified]));

        var oldOptions = JsonNode.Parse(current.Descriptor.Options.GetRawText())!;
        oldOptions["parameters"]!["directCentroidFit"] = "direct-centroid-similarity-v3";
        oldOptions["parameters"]!.AsObject().Remove("referenceFootprint");
        var old = ProcessingIdentity.CreateRecipeIdentity(
            writer.Definition with { ImplementationVersion = "centered-direct-registration-v1" },
            JsonSerializer.SerializeToElement(oldOptions));
        var unbound = ProcessingIdentity.CreateRecipeIdentity(writer.Definition, writer.NormalizeOptions(default));
        var altered = current with { Descriptor = current.Descriptor with { OptionsSha256 = new string('0', 64) } };
        var replay = await writer.ExecuteAsync(request, current, TestContext.CancellationToken);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, replay.Status);
        Assert.AreEqual(produced.Products[0].OutputIdentitySha256, replay.Products[0].OutputIdentitySha256);

        var historical = await writer.ExecuteAsync(request, old, TestContext.CancellationToken);
        var unboundResult = await writer.ExecuteAsync(request, unbound, TestContext.CancellationToken);
        var alteredResult = await writer.ExecuteAsync(request, altered, TestContext.CancellationToken);
        TestContext.WriteLine($"Writer identity: current={replay.Status}; historical={historical.Status}/{historical.Products.Count}; " +
            $"unbound={unboundResult.Status}/{unboundResult.Products.Count}; altered={alteredResult.Status}/{alteredResult.Products.Count}.");
        foreach (var result in new[] { historical, unboundResult, alteredResult })
        {
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, result.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, result.ReasonCode);
            Assert.IsEmpty(result.Products);
        }
    }

    private static async Task<ProcessingOutcome> ShiftedMonoWindow()
    {
        var reference = await BuildCapture(103, Generate((x, y) =>
            x < 16 ? (ushort)4095 : StarField(x, y, 91)), false);
        var neighbor = await BuildCapture(104, Generate((x, y) => StarField(x + 5, y, 81)), false);
        return await Execute([reference, neighbor], 103);
    }

    [TestMethod]
    [TestCategory("Unit")]
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

        var masked = await BuildCapture(103, goodPixels, false, collapsedDescriptor: true, blankMask: true);
        var maskedOutcome = Read(await Execute([masked, blank], 103));
        Assert.AreEqual(CenteredStackDisposition.SingleExposure, maskedOutcome.Disposition);
        Assert.AreEqual(CenteredRegistrationReason.NoValidSamples, maskedOutcome.Sources[2].Reason);
        Assert.AreEqual(Start.AddSeconds(6.5), maskedOutcome.ReferenceMidpointUtc);
        Assert.AreEqual(masked.Pin.Linear.ArtifactId, maskedOutcome.Plan.ReferenceLinearArtifactId);
        Assert.IsEmpty(maskedOutcome.Outputs);

        var lost = Read(await Execute([blank], 103));
        Assert.AreEqual(CenteredStackDisposition.NoReference, lost.Disposition);
        Assert.AreEqual(CenteredRegistrationReason.ReferenceUnavailable, lost.Sources[4].Reason);
        Assert.AreEqual(0, lost.TotalIntegrationTicks);
        Assert.IsEmpty(lost.Outputs);
    }

    [TestMethod]
    [TestCategory("Unit")]
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
    [TestCategory("Unit")]
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
    [TestCategory("Unit")]
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
        var missingProvenance = new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(new ExactInputs([center.Qualified]))]);
        var retry = await missingProvenance.ExecuteAsync(request);
        Assert.AreEqual(ProcessingOutcomeStatus.RetryableFailure, retry.Status);
        Assert.IsEmpty(retry.Products);

        var changedGeometry = source.Qualified with
        {
            MeasurementInput = source.Qualified.MeasurementInput with { NativePixelPitchMicrometers = 3.4 }
        };
        var different = new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(new ExactInputs([center.Qualified, changedGeometry]))]);
        var inconsistent = await different.ExecuteAsync(request);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, inconsistent.ReasonCode);
        Assert.IsEmpty(inconsistent.Products);
        var tampered = await executor.ExecuteAsync(request with
        {
            Inputs = request.Inputs.Select(artifact => artifact.ArtifactId == source.Raw.ArtifactId
                ? artifact with { Compatibility = artifact.Compatibility with { Rig = "other" } } : artifact).ToArray()
        });
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, tampered.ReasonCode);
        Assert.IsEmpty(tampered.Products);
        var missingProducer = await executor.ExecuteAsync(request with
        {
            Inputs = request.Inputs.Select(artifact => artifact.ArtifactId == source.Measurement.ArtifactId
                ? artifact with { RecipeDescriptor = null } : artifact).ToArray()
        });
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, missingProducer.ReasonCode);
        Assert.IsEmpty(missingProducer.Products);
        var restored = Read(await executor.ExecuteAsync(request));
        Assert.AreEqual(CenteredRegistrationReason.MeasurementEmpty, restored.Sources[4].Reason);
        var retained = (await executor.ExecuteAsync(request)).Products.Single();
        var json = JsonNode.Parse(retained.Payload.Span)!;
        json["referenceMidpointUtc"] = "2001-01-01T00:00:00+00:00";
        var rewritten = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(json)));
        var checksum = ProcessingIdentity.ComputePayloadSha256(rewritten);
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(retained with
        {
            Payload = rewritten,
            ChecksumSha256 = checksum,
            ContentIdentitySha256 = checksum
        }));
        Guid[] wrongParents = [retained.SourceArtifactIds[0]];
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(retained with
        {
            SourceArtifactIds = wrongParents,
            OutputIdentitySha256 = ProcessingIdentity.CreateOutputIdentity(retained.Role, retained.Variant,
                retained.Recipe.IdentitySha256, wrongParents)
        }));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task MetadataOnlyClosureMatchesReopenedPinsAndRejectsLegacyMissingEnvelope()
    {
        const int height = Height + 1;
        var center = await BuildCapture(103, new byte[Width * height * 2], bayer: false, height: height,
            measurementReason: "qualified-committed");
        var linear = center.Qualified.Linear.Linear;
        var rawHeader = CenteredStackArtifactHeader.FromArtifact(center.Raw);
        var linearHeader = CenteredStackArtifactHeader.FromArtifact(linear);
        Assert.AreEqual(center.Pin.Raw, CenteredStackArtifactPin.FromHeader(rawHeader, center.Pin.Raw.PayloadSha256));
        Assert.AreEqual(center.Pin.Linear, CenteredStackArtifactPin.FromHeader(linearHeader, center.Pin.Linear.PayloadSha256));
        Assert.AreNotEqual(center.Pin.Linear,
            CenteredStackArtifactPin.FromHeader(linearHeader with { RecipeDescriptor = null }, center.Pin.Linear.PayloadSha256));
        Assert.AreNotEqual(center.Pin.Linear, CenteredStackArtifactPin.From(linear with { Payload = new byte[] { 1 } }));
        var maskPin = CenteredStackValidityMask.PinAllValid(rawHeader, linearHeader);
        Assert.AreEqual(center.Pin.ValidityMask, maskPin);
        var mask = CenteredStackValidityMask.OpenAllValid(rawHeader, linearHeader, maskPin, linear.CreatedUtc);
        Assert.AreEqual(maskPin, CenteredStackArtifactPin.From(mask));
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredStackValidityMask.OpenAllValid(rawHeader,
            linearHeader with { Compatibility = linearHeader.Compatibility with { Rig = "changed" } }, maskPin, linear.CreatedUtc));

        var settled = Settlement(center.Qualified.Linear, measurementReason: "qualified-committed");
        var expected = CenteredStackRetainedInputReader.BindCommittedMeasurement(settled, linear.Layout!, new(2.4, null));
        Assert.AreEqual("qualified-committed", expected.CalibrationReason);
        Assert.AreEqual(center.Pin, CenteredStackExposurePin.FromCommitted(settled.Exposure, settled.Provenance!, expected,
            center.Pin.Raw, center.Pin.Linear, maskPin, center.Pin.Measurement));

        var (request, executor) = Request([center], 103);
        var auxiliary = request.AuxiliaryInputs!.Single();
        var plan = JsonNode.Parse(auxiliary.Payload.Span)!;
        Assert.IsTrue(plan["slots"]![2]!["exposure"]!["raw"]!.AsObject().Remove("envelopeSha256"));
        var legacy = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(plan)));
        var rejected = await executor.ExecuteAsync(request with
        {
            AuxiliaryInputs = [auxiliary with { Payload = legacy, IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(legacy) }]
        });
        Assert.AreEqual(CenteredRegisteredStackRecipe.InvalidPlan, rejected.ReasonCode);
        Assert.IsEmpty(rejected.Products);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RetainedPlatesAndRelativeEndpointsShareOneWindowAndReopen()
    {
        var centerFixture = await MeasuredAstrometryFixture.CreateAtAsync(0);
        var firstFixture = await MeasuredAstrometryFixture.CreateAtAsync(-2);
        var lastFixture = await MeasuredAstrometryFixture.CreateAtAsync(4);
        var center = await FromPlateFixture(centerFixture, 103, includePlate: true);
        var first = await FromPlateFixture(firstFixture, 102, includePlate: true);
        var last = await FromPlateFixture(lastFixture, 105, includePlate: false);
        var outcome = await Execute([first, center, last], 103);
        var evidence = Read(outcome);
        Assert.AreEqual(CenteredStackDisposition.Registered, evidence.Disposition, Describe(evidence));
        var planes = outcome.Products.Where(product => product.Kind == ProcessingProductKind.PixelData).ToArray();
        evidence.VerifyPlanes(planes);
        var contributors = new[] { first.Pin.Linear.ArtifactId, center.Pin.Linear.ArtifactId, last.Pin.Linear.ArtifactId };
        foreach (var plane in planes)
        {
            CollectionAssert.AreEqual(contributors, plane.SourceArtifactIds.ToArray(), Describe(evidence));
        }
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
        var retainedEvidence = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        var probes = FieldStars.Select((star, index) => new StellarDetection(index, new(star.X + .5, star.Y + .5),
            200 - index, 100, 1, 1, 0, 0)).ToArray();
        var within = CheckPlate(.54);
        Assert.AreEqual(DirectRegistrationStatus.Verified, within.Status);
        var verifiedPlate = CenteredRegisteredStackEvidence.Read(WithPlateCheck(within));
        verifiedPlate.VerifyPlanes(planes);
        Assert.IsNotNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(verifiedPlate, -1, retained));
        var excessive = CheckPlate(.80);
        Assert.AreEqual(DirectRegistrationStatus.ResidualExceeded, excessive.Status);
        Assert.HasCount(12, excessive.Verification);
        TestContext.WriteLine("Plate RMS 0.54 reopened and verified. The producer returned ResidualExceeded for twelve 0.80 px matches.");
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(
            WithPlateCheck(excessive with { Status = DirectRegistrationStatus.Verified })));
        var justAbove = CheckPlate(.56);
        Assert.AreEqual(DirectRegistrationStatus.ResidualExceeded, justAbove.Status);
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredRegisteredStackEvidence.Read(
            WithPlateCheck(justAbove with { Status = DirectRegistrationStatus.Verified })));
        TestContext.WriteLine("Plate RMS 0.80 and 0.56 rejected after changing only the producer's status to Verified.");

        // Keep the reviewer's original transform and the producer's unchanged report.
        var cutoffMap = new ImageSimilarity(1, 0, 0.0026785000000000003, 0.5499934778138283);
        var cutoff = DirectCentroidRegistration.VerifyPlatePair(cutoffMap, probes, probes, Width, Height);
        Assert.AreEqual(DirectRegistrationStatus.Verified, cutoff.Status);
        Assert.HasCount(12, cutoff.Verification);
        const double limit = 12 * .55 * .55;
        var originalSum = 0.0;
        foreach (var probe in probes)
        {
            var projected = cutoffMap.SourceToReference(probe.Pixel)!.Value;
            var dx = projected.X - probe.Pixel.X;
            var dy = projected.Y - probe.Pixel.Y;
            originalSum += dx * dx + dy * dy;
        }
        Assert.AreEqual(limit, originalSum);
        var reSquared = cutoff.Verification.Sum(match => match.ResidualPixels * match.ResidualPixels);
        Assert.AreEqual(Math.BitIncrement(limit), reSquared);
        TestContext.WriteLine($"Plate producer {cutoff.Status}, sum={originalSum:R}, limit={limit:R}, sum re-squared={reSquared:R}.");
        var reopenedCutoff = CenteredRegisteredStackEvidence.Read(WithPlateCheck(cutoff));
        reopenedCutoff.VerifyPlanes(planes);
        Assert.IsNotNull(CenteredRegisteredStackRecipe.ReconstructSourceMap(reopenedCutoff, -1, retained));

        DirectRegistrationCheck CheckPlate(double shift) =>
            DirectCentroidRegistration.VerifyPlatePair(new ImageSimilarity(1, 0, shift, 0), probes, probes, Width, Height);

        ProcessingProduct WithPlateCheck(DirectRegistrationCheck check) =>
            RewriteEvidence(retainedEvidence, json =>
            {
                var report = json["sources"]![1]!["pairCheck"]!;
                report["status"] = check.Status.ToString();
                report["verification"] = JsonSerializer.SerializeToNode(check.Verification, JsonSerializerOptions.Web);
            });

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

    private static ushort CodeAt(ProcessingProduct plane, int x, int y) => ReadCode(plane.Payload, x, y);

    private static ushort ReadCode(ReadOnlyMemory<byte> pixels, int x, int y) =>
        BinaryPrimitives.ReadUInt16LittleEndian(pixels.Span[((y * Width + x) * 2)..]);

    private static readonly (double X, double Y)[] FieldStars =
    [
        (27, 20), (57, 24), (83, 16), (110, 28), (132, 19), (140, 47),
        (108, 82), (131, 98), (91, 107), (62, 91), (35, 105), (17, 85)
    ];

    private static ushort StarField(int x, int y, int background, (double X, double Y)[]? stars = null)
    {
        stars ??= FieldStars;
        var code = (double)background;
        for (var i = 0; i < stars.Length; i++)
        {
            var (sx, sy) = stars[i];
            code += (1800 - 73 * i) * Math.Exp(-((x - sx) * (x - sx) + (y - sy) * (y - sy)) / 3.2);
        }
        return (ushort)Math.Round(code, MidpointRounding.AwayFromZero);
    }

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

    private static ProcessingProduct RewriteEvidence(ProcessingProduct product, Action<JsonNode> rewrite)
    {
        var node = JsonNode.Parse(product.Payload.Span)!;
        rewrite(node);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(node)));
        var checksum = ProcessingIdentity.ComputePayloadSha256(bytes);
        return product with { Payload = bytes, ChecksumSha256 = checksum, ContentIdentitySha256 = checksum };
    }

    private static CenteredRegisteredStackEvidence Read(ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode} {outcome.Field}");
        var product = outcome.Products.Single(product => product.MediaType == CenteredRegisteredStackEvidence.MediaType);
        using var document = JsonDocument.Parse(product.Payload);
        Assert.AreEqual(JsonValueKind.String, document.RootElement.GetProperty("disposition").ValueKind);
        Assert.AreEqual(JsonValueKind.String, document.RootElement.GetProperty("plan").GetProperty("slots")[0].GetProperty("state").ValueKind);
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
        var qualified = captures.Select(item => item.Qualified).ToArray();
        var reader = new CenteredStackRetainedInputReader(new ProductBook(qualified), (exposure, token) =>
        {
            token.ThrowIfCancellationRequested();
            var binding = qualified.SingleOrDefault(item => item.Linear.Exposure.Key == exposure.Key)?.MeasurementInput;
            return ValueTask.FromResult<CenteredStackMeasurementGeometry?>(binding is null ? null :
                new(binding.NativePixelPitchMicrometers, binding.ApertureProjection));
        });
        var artifacts = captures.OrderBy(item => item.Pin.CaptureSequence).SelectMany(item => item.Artifacts()).ToArray();
        return (CenteredRegisteredStackRecipe.CreateRequest(plan, "qualified", "display", artifacts),
            new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(reader)]));
    }

    private static async Task<Capture> BuildCapture(int sequence, byte[] pixels, bool bayer,
        int width = Width, int height = Height, DateTimeOffset? observation = null, string? measurementReason = null,
        bool calibrate = false, bool collapsedDescriptor = false, bool blankMask = false, bool clippingControls = false,
        ProjectionContext? aperture = null)
    {
        var sensorWhite = clippingControls ? 4000 : 4095;
        var layout = new FrameLayoutDescriptor(width, height, width * 2,
            bayer ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            64, sensorWhite, pixels.Length)
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
            new(time, time, collapsedDescriptor ? time : end, end, end), new(integration, integration, 10, 10, 0, 0, null, -10),
            new(profile, profile, profile, profile, profile), layout,
            new(Guid.NewGuid(), FrameArtifactRole.Raw, "camera-1", "raw", end, [], RawRecipe,
                "application/x-hvo-frame", ProcessingIdentity.ComputePayloadSha256(pixels)));
        var rawSource = ExposureMeasurementSourceV1.FromDescriptor(rawDescriptor);
        Assert.IsTrue(rawDescriptor.Validate().IsValid, rawDescriptor.Validate().FieldPath);
        var raw = new ProcessingArtifact(rawSource.ArtifactId, FrameArtifactRole.Raw, "raw", rawSource.RecipeSha256,
            rawDescriptor.Artifact.MediaType, layout, pixels, end, integration, Compatible, sequence, [],
            time, end, new(10, 0, -10))
        {
            CaptureId = rawSource.CaptureId,
            DescriptorIdentitySha256 = rawSource.DescriptorSha256,
            RecipeDescriptor = RawRecipe
        };
        var reference = calibrate ? ReferenceCalibration(raw, clippingControls) : null;
        var linearProduct = await Produce(reference ?? new(BuiltInProcessingRecipes.LinearNormalization, default,
            ProcessingInputSelector.Raw("raw"), [raw], "qualified"));
        var linear = Artifact(linearProduct, raw);
        var linearSource = new ExposureMeasurementSourceV1(rawSource.CaptureId, linear.ArtifactId,
            linear.DescriptorIdentitySha256!, linearProduct.ChecksumSha256, linear.RecipeIdentitySha256,
            linearProduct.OutputIdentitySha256, linear.Layout!);
        var orientation = new RigOrientation(90, 180, 0);
        var qualified = new LinearExposureInputV1(new(new(Camera, Run, rawSource.CaptureId), rawDescriptor,
            new(layout, orientation, profile.Sha256, "fixture", 5.5)), linear,
            new(calibrate ? LinearExposureDisposition.Calibrated : LinearExposureDisposition.QualifiedNormalizationOnly,
                raw.ArtifactId, rawSource.DescriptorSha256, rawSource.PayloadSha256, linear.ArtifactId,
                linearSource.DescriptorSha256, linearSource.PayloadSha256, linearProduct.OutputIdentitySha256,
                 linearProduct.Recipe, linear.SourceArtifactIds!.Skip(1).ToImmutableArray(), new(raw.ArtifactId, sensorWhite),
                 new(linear.ArtifactId, calibrate ? ushort.MaxValue : sensorWhite))
            {
                CalibrationBundleIdentitySha256 = reference?.AuxiliaryInputs?[0].IdentitySha256,
                CalibrationPayloadHashes = reference is null ? [] :
                    reference.Inputs.Skip(1).Select(item => ProcessingIdentity.ComputePayloadSha256(item.Payload)).ToImmutableArray()
            });
        var mask = CenteredStackValidityMask.Create(raw, linear,
            Enumerable.Repeat(blankMask ? (byte)0 : (byte)1, layout.Width * layout.Height).ToArray());
        var settlement = Settlement(qualified, measurementReason);
        var expected = ExposureMeasurementBindings.FromAdmitted(settlement, aperture, nativePixelPitchMicrometers: 2.4);
        var measurement = Artifact(await Produce(ExposureMeasurementBindings.Request(settlement, raw, qualified, aperture, nativePixelPitchMicrometers: 2.4)), raw);
        var retained = new CenteredStackQualifiedExposure(qualified, expected);
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

    private static ExposureMeasurementInputV1 MeasurementBinding(Capture capture) => capture.Qualified.MeasurementInput;

    private static ProcessingExecutionRequest ReferenceCalibration(ProcessingArtifact raw, bool clippingControls)
    {
        var layout = CalibrationMasterBuilder.CreateNormalizedLayout(raw.Layout!);
        var references = CalibrationReferenceKinds.All.Select((kind, index) =>
        {
            var pixels = new byte[(int)layout.ByteLength];
            if (kind == CalibrationReferenceKinds.Flat)
            {
                for (var offset = 0; offset < pixels.Length; offset += 2)
                {
                    var pixel = offset / 2;
                    var onLowFlat = clippingControls && pixel % layout.Width is >= 48 and <= 52 &&
                        pixel / layout.Width is >= 59 and <= 63;
                    BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset, 2), onLowFlat ? (ushort)10000 : (ushort)30000);
                }
            }
            else if (clippingControls && kind == CalibrationReferenceKinds.Bias)
            {
                for (var offset = 0; offset < pixels.Length; offset += 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset, 2), 512);
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
            layout.Width, layout.Height, layout.PixelFormat, clippingControls ? (ushort)29488 : (ushort)30000, 9, 11, -11, -9,
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
            ArtifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            Role = product.Role,
            Variant = product.Variant,
            RecipeIdentitySha256 = product.Recipe.IdentitySha256,
            MediaType = product.MediaType,
            Layout = product.Layout,
            Payload = product.Payload,
            SourceArtifactIds = product.SourceArtifactIds,
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256,
            RecipeDescriptor = product.Recipe.Descriptor,
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

    private static LinearExposureLookup Settlement(LinearExposureInputV1 qualified, string? measurementReason)
    {
        var key = qualified.Exposure.Key;
        var provenance = qualified.Provenance;
        var product = new ExposureProductRef(key, new(Run, "Calibration", "test-linear", qualified.Linear.Variant),
            provenance.LinearArtifactId, provenance.OutputIdentitySha256, provenance.LinearPayloadSha256, null);
        return new(qualified.Exposure, provenance.Disposition, 1, product, provenance, measurementReason);
    }

    private sealed class ProductBook(CenteredStackQualifiedExposure[] inputs) : IExposureProductReader
    {
        public ValueTask<LinearExposureLookup?> ReadLinearAsync(ExposureKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = inputs.SingleOrDefault(item => item.Linear.Exposure.Key == key);
            return ValueTask.FromResult(input is null ? null : Settlement(input.Linear, input.MeasurementInput.CalibrationReason));
        }

        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ExposureProductLookup?> ReadProductAsync(ExposureKey key, ExposureProductSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ExposureProductLookup>> ReadProductsAfterAsync(CameraKey camera, long afterRevision,
            int maximumCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ExactInputs(CenteredStackQualifiedExposure[] inputs) : ICenteredStackInputReader
    {
        public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, ProcessingArtifact linear, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(inputs.SingleOrDefault(item => item.Linear.Exposure.Key == key &&
                item.Linear.Linear.ArtifactId == linear.ArtifactId));
        }
    }
}
