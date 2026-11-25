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
