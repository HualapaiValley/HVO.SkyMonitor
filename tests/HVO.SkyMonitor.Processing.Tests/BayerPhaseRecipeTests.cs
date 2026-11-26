using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest discovers public test classes.")]
public sealed class BayerPhaseRecipeTests
{
    private const int Width = 64;
    private const int Height = 48;

    [TestMethod]
    [DataRow(0, 0, BayerPattern.Rggb, BayerRggb16Demosaicer.AlgorithmVersion, LinearBayerReconstruction.AlgorithmVersion)]
    [DataRow(1, 0, BayerPattern.Grbg, BayerRggb16Demosaicer.GrbgAlgorithmVersion, LinearBayerReconstruction.AlgorithmVersion + "-grbg")]
    [DataRow(0, 1, BayerPattern.Gbrg, BayerRggb16Demosaicer.GbrgAlgorithmVersion, LinearBayerReconstruction.AlgorithmVersion + "-gbrg")]
    [DataRow(1, 1, BayerPattern.Bggr, BayerRggb16Demosaicer.BggrAlgorithmVersion, LinearBayerReconstruction.AlgorithmVersion + "-bggr")]
    public async Task BuiltInPreviewAnnotationAndTimeLapseRecipesPreserveAllFourBayerPhasesAsync(
        int originX,
        int originY,
        BayerPattern expectedPattern,
        string expectedDemosaicVersion,
        string expectedFixedLinearVersion)
    {
        var raw = RenderColorPatches(originX, originY);
        var rig = CreateRig(originX, originY);
        var layout = CreateLayout(originX, originY, raw.Length);
        var compatibility = new ProcessingCompatibilityIdentity(
            RigProjectionContextFactory.CreateProfileHashSha256(rig),
            "orientation-v1", "cal-none", "mask-none", "sensor-v1", "setpoint-v1", "profile-v1");
        var rawArtifact = new ProcessingArtifact(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "raw",
            new string('A', 64),
            "application/octet-stream",
            layout,
            raw,
            new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(1),
            compatibility)
        {
            ObservationStartedUtc = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero)
        };

        var executor = new ProcessingRecipeExecutor();
        var selector = ProcessingInputSelector.Raw("raw");

        // 1. EncodedPreview (JPEG) preserves RGB targets, algorithm contract, and trail source-pixel compatibility.
        var encodedReq = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new { outputEncoding = "Jpeg", jpegQuality = 90 }),
            selector,
            [rawArtifact],
            "preview",
            InputArtifactId: rawArtifact.ArtifactId);
        var encodedOutcome = await executor.ExecuteAsync(encodedReq).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, encodedOutcome.Status, encodedOutcome.ReasonCode);
        var encodedProduct = encodedOutcome.Products[0];
        var encodedContract = BuiltInProcessingRecipes.CreateProductContract(encodedReq, encodedProduct.Recipe);
        Assert.IsTrue(encodedProduct.Algorithms.SequenceEqual(encodedContract.Algorithms));
        Assert.AreEqual(expectedDemosaicVersion,
            encodedProduct.Algorithms.Single(a => a.Name == "rggb-demosaic").Version);
        var decodedEncoded = JpegImageCodec.DecodeJpeg(encodedProduct.Payload);
        AssertColorTargets(decodedEncoded, $"EncodedPreview-{expectedPattern}");

        // EncodedPreview (Packed) is recognized by TransientTrailPresentation.PreservesSourcePixels across all 4 phases.
        var packedReq = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new { outputEncoding = "Packed" }),
            selector,
            [rawArtifact],
            "preview-packed",
            InputArtifactId: rawArtifact.ArtifactId);
        var packedOutcome = await executor.ExecuteAsync(packedReq).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, packedOutcome.Status, packedOutcome.ReasonCode);
        var packedProduct = packedOutcome.Products[0];
        Assert.IsTrue(TransientTrailPresentation.PreservesSourcePixels(
            packedProduct.Role, packedProduct.Recipe.Descriptor, packedProduct.Algorithms));

        // 2. FixedPreview preserves RGB targets, phase-distinct linear reconstruction version, and trail compatibility.
        var fixedReq = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.FixedPreview,
            JsonSerializer.SerializeToElement(new FixedDisplayTransferOptions(64, 4095, 2.2)),
            selector,
            [rawArtifact],
            "fixed-preview",
            InputArtifactId: rawArtifact.ArtifactId);
        var fixedOutcome = await executor.ExecuteAsync(fixedReq).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, fixedOutcome.Status, fixedOutcome.ReasonCode);
        var fixedProduct = fixedOutcome.Products[0];
        var fixedContract = BuiltInProcessingRecipes.CreateProductContract(fixedReq, fixedProduct.Recipe);
        Assert.IsTrue(fixedProduct.Algorithms.SequenceEqual(fixedContract.Algorithms));
        Assert.AreEqual(expectedFixedLinearVersion,
            fixedProduct.Algorithms.Single(a => a.Name == "linear-rggb-reconstruction").Version);
        Assert.IsTrue(TransientTrailPresentation.PreservesSourcePixels(
            fixedProduct.Role, fixedProduct.Recipe.Descriptor, fixedProduct.Algorithms));
        var fixedDecoded = new DecodedImage(
            Width, Height, CameraPixelFormat.Rgb24, Width * 3, fixedProduct.Payload, "packed", "test");
        AssertColorTargets(fixedDecoded, $"FixedPreview-{expectedPattern}");

        // 3. Raw-input Annotation preserves RGB targets and phase-distinct algorithm contract.
        var annotationReq = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            JsonSerializer.SerializeToElement(new
            {
                drawLabels = false,
                drawImageCircle = false,
                drawCardinalDirections = false,
                jpegQuality = 90
            }),
            selector,
            [rawArtifact],
            "annotated",
            Annotation: new ProcessingAnnotationInput(
                [],
                [],
                new PreviewTransform(1, 1),
                null,
                new string('C', 64)),
            InputArtifactId: rawArtifact.ArtifactId);
        var annotationOutcome = await executor.ExecuteAsync(annotationReq).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, annotationOutcome.Status, annotationOutcome.ReasonCode);
        var annotationProduct = annotationOutcome.Products[0];
        var annotationContract = BuiltInProcessingRecipes.CreateProductContract(annotationReq, annotationProduct.Recipe);
        Assert.IsTrue(annotationProduct.Algorithms.SequenceEqual(annotationContract.Algorithms));
        Assert.AreEqual(expectedDemosaicVersion,
            annotationProduct.Algorithms.Single(a => a.Name == "rggb-demosaic").Version);
        var decodedAnnotated = JpegImageCodec.DecodeJpeg(annotationProduct.Payload);
        AssertColorTargets(decodedAnnotated, $"Annotation-{expectedPattern}");

        // 4. TimeLapseFrameRenderer preserves RGB targets on both daytime and nighttime paths.
        var timeLapseOptions = new TimeLapseFrameOptions(
            CardinalDirections: false, CornerMetadata: false, ImageCircle: false, JpegQuality: 90);
        var nightFrame = TimeLapseFrameRenderer.Render([rawArtifact], rig, timeLapseOptions, daytime: false, Width, Height);
        var dayFrame = TimeLapseFrameRenderer.Render([rawArtifact], rig, timeLapseOptions, daytime: true, Width, Height);
        AssertColorTargets(JpegImageCodec.DecodeJpeg(nightFrame.Jpeg), $"TimeLapse-Night-{expectedPattern}");
        AssertColorTargets(JpegImageCodec.DecodeJpeg(dayFrame.Jpeg), $"TimeLapse-Day-{expectedPattern}");

        // For non-(0,0) phases, TimeLapseFrameRenderer rendering identity must not alias the (0,0) RGGB identity.
        if (expectedPattern != BayerPattern.Rggb)
        {
            var rggbRig = CreateRig(0, 0);
            var rggbLayout = CreateLayout(0, 0, raw.Length);
            var rggbCompat = compatibility with { Rig = RigProjectionContextFactory.CreateProfileHashSha256(rggbRig) };
            var rggbArtifact = rawArtifact with { Layout = rggbLayout, Compatibility = rggbCompat };
            var rggbNight = TimeLapseFrameRenderer.Render([rggbArtifact], rggbRig, timeLapseOptions, daytime: false, Width, Height);
            Assert.AreNotEqual(rggbNight.RenderingIdentitySha256, nightFrame.RenderingIdentitySha256);
        }
    }

    [TestMethod]
    public async Task ShiftedBayerPhasesProduceDistinctRecipeAndOutputIdentitiesWhilePinningHistoricalRggbAsync()
    {
        var fixedArtifactId = Guid.Parse("19281928-1928-4928-8928-192819281928");
        var createdUtc = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var rig = CreateRig(0, 0);
        var compatibility = new ProcessingCompatibilityIdentity(
            RigProjectionContextFactory.CreateProfileHashSha256(rig),
            "orientation-v1", "cal-none", "mask-none", "sensor-v1", "setpoint-v1", "profile-v1");
        var sharedRaw = RenderColorPatches(0, 0);
        var selector = ProcessingInputSelector.Raw("raw");
        var executor = new ProcessingRecipeExecutor();

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            BuiltInProcessingRecipes.CreateExecutionIdentity((ProcessingExecutionRequest)null!));

        (int X, int Y, BayerPattern Pattern)[] phases =
        [
            (0, 0, BayerPattern.Rggb),
            (1, 0, BayerPattern.Grbg),
            (0, 1, BayerPattern.Gbrg),
            (1, 1, BayerPattern.Bggr)
        ];

        FrameLayoutDescriptor LegacyRggbLayout() =>
            new(Width, Height, Width * 2, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, 12, 16,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, sharedRaw.Length)
            {
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample
            };

        FrameLayoutDescriptor Mono16Layout() =>
            new(Width, Height, Width * 2, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 12, 16,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 64, 4095, sharedRaw.Length)
            {
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                LevelCodeSpace = FrameLevelCodeSpace.NativeSample
            };

        ProcessingArtifact ArtifactFor(FrameLayoutDescriptor layout) => new(
            fixedArtifactId,
            FrameArtifactRole.Raw,
            "raw",
            new string('A', 64),
            "application/octet-stream",
            layout,
            sharedRaw,
            createdUtc,
            TimeSpan.FromSeconds(1),
            compatibility)
        {
            ObservationStartedUtc = createdUtc
        };

        var encodedOptions = JsonSerializer.SerializeToElement(new { outputEncoding = "Jpeg", jpegQuality = 90 });
        var fixedOptions = JsonSerializer.SerializeToElement(new FixedDisplayTransferOptions(64, 4095, 2.2));
        var annotationOptions = JsonSerializer.SerializeToElement(new
        {
            drawLabels = false,
            drawImageCircle = false,
            drawCardinalDirections = false,
            jpegQuality = 90
        });
        var annotationInput = new ProcessingAnnotationInput(
            [],
            [],
            new PreviewTransform(1, 1),
            null,
            new string('C', 64));

        // Verify EncodedPreview, FixedPreview, and raw-input Annotation bind non-(0,0) CFA phase into both
        // Recipe.IdentitySha256 and OutputIdentitySha256 while preserving historical (0,0) RGGB and legacy keys.
        var recipeCases = new (string RecipeName, JsonElement Options, string Variant, ProcessingAnnotationInput? Annotation, string GoldenRggbRecipe, string GoldenRggbOutput)[]
        {
            (BuiltInProcessingRecipes.EncodedPreview, encodedOptions, "preview", null,
                "CEBC4D6E215D8F95B07425B2A8A830D74DE7F617A5A5F1073355F0A641EAA65D",
                "7478AAA909DC3BFC4FC3F32DB2AF4C5F77C9691CAB8B013F414C8BE1653A655A"),
            (BuiltInProcessingRecipes.FixedPreview, fixedOptions, "fixed-preview", null,
                "15A1782B2ABA9DD9CDFBA461BBA4CC6FF3D0D8BCDBC9217C266C6F4A90D7DB81",
                "CCD5E9D7CC90B35EE2D8BEB39053C2CA4D398E57618AB95408AF5B2CBD5C18E5"),
            (BuiltInProcessingRecipes.Annotation, annotationOptions, "annotated", annotationInput,
                "5BDEA97715F86F24C95F9ACF1E4E2A02A4A922248FF7E42B7DB4C10B4D314D53",
                "7CFAF2AB0C281FB257EB79E3CC3C20DA4927D68AC14820496D11DCA12CAA98C3")
        };

        foreach (var (recipeName, options, variant, annotation, goldenRggbRecipe, goldenRggbOutput) in recipeCases)
        {
            var historicalIdentity = BuiltInProcessingRecipes.CreateExecutionIdentity(
                recipeName, options, selector, annotation);
            var recipeIds = new Dictionary<BayerPattern, string>();
            var outputIds = new Dictionary<BayerPattern, string>();
            var artifactIds = new Dictionary<BayerPattern, Guid>();
            byte[]? rggbPayload = null;

            foreach (var (ox, oy, pattern) in phases)
            {
                var req = new ProcessingExecutionRequest(
                    recipeName, options, selector, [ArtifactFor(CreateLayout(ox, oy, sharedRaw.Length))],
                    variant, Annotation: annotation, InputArtifactId: fixedArtifactId);
                var outcome = await executor.ExecuteAsync(req).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{recipeName}-{pattern}: {outcome.ReasonCode}");
                var product = outcome.Products[0];

                // Replay contract agreement: CreateExecutionIdentity(request) and descriptor/output hashing must match!
                var replayExpected = BuiltInProcessingRecipes.CreateExecutionIdentity(req);
                Assert.AreEqual(replayExpected.IdentitySha256, product.Recipe.IdentitySha256, $"{recipeName}-{pattern}: replay recipe identity");
                Assert.AreEqual(
                    ProcessingIdentity.CreateRecipeIdentity(product.Recipe.Descriptor).IdentitySha256,
                    product.Recipe.IdentitySha256,
                    $"{recipeName}-{pattern}: descriptor recipe identity");
                Assert.AreEqual(
                    ProcessingIdentity.CreateOutputIdentity(product.Role, product.Variant, product.Recipe.IdentitySha256, product.SourceArtifactIds),
                    product.OutputIdentitySha256,
                    $"{recipeName}-{pattern}: output identity");

                if (pattern == BayerPattern.Rggb)
                {
                    Assert.AreEqual(historicalIdentity.IdentitySha256, product.Recipe.IdentitySha256,
                        $"{recipeName}: (0,0) RGGB must preserve historical recipe identity.");
                    Assert.IsFalse(product.Recipe.Descriptor.Options.TryGetProperty("deliveredCfa", out _),
                        $"{recipeName}: (0,0) RGGB descriptor must omit deliveredCfa.");
                    Assert.AreEqual(goldenRggbRecipe, product.Recipe.IdentitySha256, $"{recipeName}: golden RGGB recipe");
                    Assert.AreEqual(goldenRggbOutput, product.OutputIdentitySha256, $"{recipeName}: golden RGGB output");
                    rggbPayload = product.Payload.ToArray();
                }
                else
                {
                    Assert.AreNotEqual(historicalIdentity.IdentitySha256, product.Recipe.IdentitySha256,
                        $"{recipeName}-{pattern}: shifted phase must not alias historical RGGB recipe identity.");
                    Assert.AreEqual(pattern.ToString(), product.Recipe.Descriptor.Options.GetProperty("deliveredCfa").GetString(),
                        $"{recipeName}-{pattern}: descriptor must record deliveredCfa.");
                }

                recipeIds[pattern] = product.Recipe.IdentitySha256;
                outputIds[pattern] = product.OutputIdentitySha256;
                artifactIds[pattern] = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            }

            Assert.AreEqual(4, recipeIds.Values.Distinct(StringComparer.Ordinal).Count(),
                $"{recipeName}: all 4 CFA phases must produce mutually distinct Recipe.IdentitySha256 values.");
            Assert.AreEqual(4, outputIds.Values.Distinct(StringComparer.Ordinal).Count(),
                $"{recipeName}: all 4 CFA phases must produce mutually distinct OutputIdentitySha256 values.");
            Assert.AreEqual(4, artifactIds.Values.Distinct().Count(),
                $"{recipeName}: all 4 CFA phases must produce mutually distinct durable artifact IDs.");

            // Legacy layout (Readout = null) must match (0,0) RGGB recipe identity, output identity, and payload bytes!
            var legacyReq = new ProcessingExecutionRequest(
                recipeName, options, selector, [ArtifactFor(LegacyRggbLayout())],
                variant, Annotation: annotation, InputArtifactId: fixedArtifactId);
            var legacyOutcome = await executor.ExecuteAsync(legacyReq).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, legacyOutcome.Status, legacyOutcome.ReasonCode);
            var legacyProduct = legacyOutcome.Products[0];
            Assert.AreEqual(recipeIds[BayerPattern.Rggb], legacyProduct.Recipe.IdentitySha256, $"{recipeName}: legacy recipe");
            Assert.AreEqual(outputIds[BayerPattern.Rggb], legacyProduct.OutputIdentitySha256, $"{recipeName}: legacy output");
            CollectionAssert.AreEqual(rggbPayload!, legacyProduct.Payload.ToArray(), $"{recipeName}: legacy bytes");

            // Mono16 input must keep historical recipe identity without deliveredCfa.
            var monoReq = new ProcessingExecutionRequest(
                recipeName, options, selector, [ArtifactFor(Mono16Layout())],
                variant, Annotation: annotation, InputArtifactId: fixedArtifactId);
            var monoOutcome = await executor.ExecuteAsync(monoReq).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, monoOutcome.Status, monoOutcome.ReasonCode);
            Assert.AreEqual(historicalIdentity.IdentitySha256, monoOutcome.Products[0].Recipe.IdentitySha256, $"{recipeName}: mono16 recipe");
            Assert.IsFalse(monoOutcome.Products[0].Recipe.Descriptor.Options.TryGetProperty("deliveredCfa", out _), $"{recipeName}: mono16 deliveredCfa");
        }

        // Non-demosaicing recipes (e.g. LinearNormalization) must not bind deliveredCfa even on odd-origin BGGR.
        var linearReq = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.LinearNormalization,
            JsonSerializer.SerializeToElement(new { mode = "None" }),
            selector,
            [ArtifactFor(CreateLayout(1, 1, sharedRaw.Length))],
            "linear",
            InputArtifactId: fixedArtifactId);
        var linearOutcome = await executor.ExecuteAsync(linearReq).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, linearOutcome.Status, linearOutcome.ReasonCode);
        var linearHistorical = BuiltInProcessingRecipes.CreateExecutionIdentity(
            linearReq.RecipeName, linearReq.Options, linearReq.Input);
        Assert.AreEqual(linearHistorical.IdentitySha256, linearOutcome.Products[0].Recipe.IdentitySha256);
        Assert.AreEqual(linearHistorical.IdentitySha256, BuiltInProcessingRecipes.CreateExecutionIdentity(linearReq).IdentitySha256);
    }

    [TestMethod]
    public void TimeLapseFrameRendererPinsRggbIdentityAndDistinguishesDeliveredCfaWithSameRig()
    {
        const string goldenRggbRenderingIdentity = "6BC0FB08FEF12D88CD586763A59CF3DD58A287E912CE1558EF2F54013A61F618";
        var fixedArtifactId = Guid.Parse("19281928-1928-4928-8928-192819280001");
        var createdUtc = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var sharedRig = CreateRig(0, 0);
        var sharedCompat = new ProcessingCompatibilityIdentity(
            RigProjectionContextFactory.CreateProfileHashSha256(sharedRig),
            "orientation-v1", "cal-none", "mask-none", "sensor-v1", "setpoint-v1", "profile-v1");
        var sharedRaw = RenderColorPatches(0, 0);
        var options = new TimeLapseFrameOptions(
            CardinalDirections: false, CornerMetadata: false, ImageCircle: false, JpegQuality: 90);

        ProcessingArtifact ArtifactFor(FrameLayoutDescriptor layout) => new(
            fixedArtifactId,
            FrameArtifactRole.Raw,
            "raw",
            new string('A', 64),
            "application/octet-stream",
            layout,
            sharedRaw,
            createdUtc,
            TimeSpan.FromSeconds(1),
            sharedCompat)
        {
            ObservationStartedUtc = createdUtc
        };

        // Hold rig, compatibility, artifact ID, raw bytes, timestamp, and options completely fixed;
        // vary ONLY layout.Readout.CfaOriginX/Y so RenderingIdentitySha256 separation depends strictly on deliveredCfa.
        var rggb = TimeLapseFrameRenderer.Render([ArtifactFor(CreateLayout(0, 0, sharedRaw.Length))], sharedRig, options, daytime: false, Width, Height);
        var grbg = TimeLapseFrameRenderer.Render([ArtifactFor(CreateLayout(1, 0, sharedRaw.Length))], sharedRig, options, daytime: false, Width, Height);
        var gbrg = TimeLapseFrameRenderer.Render([ArtifactFor(CreateLayout(0, 1, sharedRaw.Length))], sharedRig, options, daytime: false, Width, Height);
        var bggr = TimeLapseFrameRenderer.Render([ArtifactFor(CreateLayout(1, 1, sharedRaw.Length))], sharedRig, options, daytime: false, Width, Height);

        var legacyLayout = new FrameLayoutDescriptor(
            Width, Height, Width * 2, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, 12, 16,
            FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, sharedRaw.Length)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };
        var legacy = TimeLapseFrameRenderer.Render([ArtifactFor(legacyLayout)], sharedRig, options, daytime: false, Width, Height);

        Assert.AreEqual(goldenRggbRenderingIdentity, rggb.RenderingIdentitySha256,
            "Pinned (0,0) RGGB time-lapse rendering identity must remain unchanged.");
        Assert.AreEqual(rggb.RenderingIdentitySha256, legacy.RenderingIdentitySha256,
            "Legacy null-readout Bayer layout must preserve the exact (0,0) RGGB rendering identity.");
        string[] allIdentities =
        [
            rggb.RenderingIdentitySha256,
            grbg.RenderingIdentitySha256,
            gbrg.RenderingIdentitySha256,
            bggr.RenderingIdentitySha256
        ];
        Assert.AreEqual(4, allIdentities.Distinct(StringComparer.Ordinal).Count(),
            "All 4 delivered Bayer CFA phases with identical rig and bytes must produce distinct RenderingIdentitySha256 values.");
    }

    private static byte[] RenderColorPatches(int originX, int originY)
    {
        var raw = new byte[Width * Height * 2];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var channel = ((y + originY) & 1, (x + originX) & 1) switch
                {
                    (0, 0) => 0,
                    (1, 1) => 2,
                    _ => 1
                };
                ushort code = 120;
                if (x is >= 8 and <= 20 && y is >= 14 and <= 34)
                {
                    code = channel == 0 ? (ushort)3600 : (ushort)120;
                }
                else if (x is >= 26 and <= 38 && y is >= 14 and <= 34)
                {
                    code = channel == 1 ? (ushort)3600 : (ushort)120;
                }
                else if (x is >= 44 and <= 56 && y is >= 14 and <= 34)
                {
                    code = channel == 2 ? (ushort)3600 : (ushort)120;
                }
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((y * Width + x) * 2), code);
            }
        }
        return raw;
    }

    private static FrameLayoutDescriptor CreateLayout(int originX, int originY, int byteLength) =>
        new(Width, Height, Width * 2, CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, 12, 16,
            FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 64, 4095, byteLength)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                Width + originX * 2, Height + originY * 2, originX, originY, Width, Height, 1, 1,
                FrameBinningAlgorithm.IdentityV1, originX, originY)
        };

    private static CameraRigConfig CreateRig(int originX, int originY) =>
        new(
            new SensorProfile("PhaseTest", Width + originX * 2, Height + originY * 2, 2.4,
                SensorColorMode.Color, CameraPixelFormat.BayerRggb16, SensorResponseMode.BayerRaw,
                Width * 2, SampleByteOrder.LittleEndian),
            new OpticsProfile("Perspective", 8.0, 90.0, 0.0)
            {
                LensKind = LensKind.Rectilinear,
                PrincipalPointX = (Width + originX * 2) / 2.0,
                PrincipalPointY = (Height + originY * 2) / 2.0,
                CalibrationVersion = "phase-test-v1"
            },
            new RigOrientation(90.0, 0.0, 0.0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1))
        {
            Readout = new SensorReadoutProfile(
                new SensorCrop(originX, originY, Width, Height), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.BayerRggb16, 12, 16, FrameSamplePacking.ByteAligned,
                FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample, 64, 4095,
                Width * 2, SampleByteOrder.LittleEndian, ColorFilterArrayPattern.Rggb, originX, originY)
        };

    private static void AssertColorTargets(DecodedImage image, string label)
    {
        Assert.AreEqual(Width, image.Width, label);
        Assert.AreEqual(Height, image.Height, label);
        Assert.AreEqual(CameraPixelFormat.Rgb24, image.PixelFormat, label);
        var red = Sample(image, 14, 24);
        var green = Sample(image, 32, 24);
        var blue = Sample(image, 50, 24);
        Assert.IsGreaterThan(red.Blue * 2.0, red.Red, $"{label}: red target {red}");
        Assert.IsGreaterThan(red.Green + 30, red.Red, $"{label}: red target {red}");
        Assert.IsGreaterThan(green.Red + 30, green.Green, $"{label}: green target {green}");
        Assert.IsGreaterThan(green.Blue + 30, green.Green, $"{label}: green target {green}");
        Assert.IsGreaterThan(blue.Red * 2.0, blue.Blue, $"{label}: blue target {blue}");
        Assert.IsGreaterThan(blue.Green + 30, blue.Blue, $"{label}: blue target {blue}");

        static (double Red, double Green, double Blue) Sample(DecodedImage decoded, int cx, int cy)
        {
            double r = 0, g = 0, b = 0;
            var count = 0;
            var span = decoded.PixelData.Span;
            for (var y = cy - 2; y <= cy + 2; y++)
            {
                for (var x = cx - 2; x <= cx + 2; x++)
                {
                    var o = y * decoded.StrideBytes + x * 3;
                    r += span[o];
                    g += span[o + 1];
                    b += span[o + 2];
                    count++;
                }
            }
            return (r / count, g / count, b / count);
        }
    }
}
