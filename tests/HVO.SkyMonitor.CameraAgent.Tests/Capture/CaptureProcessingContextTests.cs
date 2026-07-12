using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CaptureProcessingContextTests
{
    [TestMethod]
    public void PreviewStep_RejectsInvertedPercentilesDuringConstruction()
    {
        var options = new PreviewProcessingStepOptions
        {
            BlackPercentile = 0.9,
            WhitePercentile = 0.1
        };

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            new PreviewCaptureProcessingStep(
                new CaptureProcessingStepMetadata("Preview", "Preview", 0), options));
    }

    [TestMethod]
    public void ReplaceFrame_PreservesRawArtifactAndAddsCalibratedArtifact()
    {
        // Arrange
        var rawFrame = CreateFrame([1, 2, 3, 4]);
        var calibratedFrame = CreateFrame([5, 6, 7, 8]);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(rawFrame));

        // Act
        context.ReplaceFrame(calibratedFrame);

        // Assert
        var artifacts = context.Artifacts;
        Assert.IsNotNull(artifacts);
        Assert.AreSame(rawFrame, artifacts.Raw.Frame);
        Assert.AreSame(calibratedFrame, artifacts[FrameArtifactRole.Calibrated].Frame);
        CollectionAssert.AreEqual(
            new[] { artifacts.Raw.ArtifactId },
            artifacts[FrameArtifactRole.Calibrated].SourceArtifactIds!.ToArray());
    }

    [TestMethod]
    public void WithDerivative_WhenReplacingRaw_ThrowsAndPreservesRawArtifact()
    {
        // Arrange
        var rawFrame = CreateFrame([1, 2, 3, 4]);
        var artifacts = new FrameArtifactSet(rawFrame);

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => artifacts.WithDerivative(FrameArtifactRole.Raw, CreateFrame([5, 6, 7, 8])));

        // Assert
        Assert.AreEqual("role", exception.ParamName);
        Assert.AreSame(rawFrame, artifacts.Raw.Frame);
    }

    [TestMethod]
    public void Constructor_WhenInitialArtifactIsNotRaw_Throws()
    {
        // Arrange
        var calibratedArtifact = new FrameArtifact(
            Guid.NewGuid(),
            FrameArtifactRole.Calibrated,
            CreateFrame([5, 6, 7, 8]));

        // Act
        var exception = Assert.Throws<ArgumentException>(() => new FrameArtifactSet(calibratedArtifact));

        // Assert
        Assert.AreEqual("rawArtifact", exception.ParamName);
    }

    [TestMethod]
    public void ReplaceFrame_WhenSubmissionHasNoFrame_CreatesRawArtifactSet()
    {
        // Arrange
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(frame: null));
        var frame = CreateFrame([5, 6, 7, 8]);

        // Act
        context.ReplaceFrame(frame);

        // Assert
        var artifacts = context.Artifacts;
        Assert.IsNotNull(artifacts);
        Assert.AreSame(frame, artifacts.Raw.Frame);
        Assert.AreSame(artifacts, context.Submission.Result.Artifacts);
    }

    [TestMethod]
    public void Constructor_WhenSubmissionHasArtifacts_ReusesArtifactSet()
    {
        // Arrange
        var rawFrame = CreateFrame([1, 2, 3, 4]);
        var artifacts = new FrameArtifactSet(rawFrame);
        var submission = CreateSubmission(rawFrame) with
        {
            Result = CreateSubmission(rawFrame).Result with { Artifacts = artifacts }
        };

        // Act
        var context = new CaptureProcessingContext(CreateConfig(), submission);

        // Assert
        Assert.AreSame(artifacts, context.Artifacts);
    }

    [TestMethod]
    public void AddDerivative_PreservesRawAndRecordsPreview()
    {
        var raw = CreateFrame([0, 1, 2, 3]);
        var preview = CreateFrame([4, 5, 6, 7]);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));

        context.AddDerivative(FrameArtifactRole.Preview, preview, "preview-v1");

        Assert.AreSame(raw, context.Artifacts!.Raw.Frame);
        Assert.AreSame(preview, context.Artifacts[FrameArtifactRole.Preview].Frame);
        Assert.AreEqual("preview-v1", context.Artifacts[FrameArtifactRole.Preview].RecipeVersion);
    }

    [TestMethod]
    public async Task PreviewStep_CreatesMono8DerivativeAndPreservesMono16Raw()
    {
        var raw = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 1, CameraPixelFormat.Mono16,
            new byte[] { 0, 0, 0, 255 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        var step = new PreviewCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Preview", "Preview", 0), new PreviewProcessingStepOptions());

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreSame(raw, context.Artifacts!.Raw.Frame);
        var preview = context.Artifacts[FrameArtifactRole.Preview].Frame;
        Assert.AreEqual(CameraPixelFormat.Mono8, preview.PixelFormat);
        CollectionAssert.AreEqual(new byte[] { 0, 255 }, preview.PixelData.ToArray());
    }

    [TestMethod]
    public async Task PreviewStep_UsesConfiguredRecipeVersion()
    {
        var raw = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono16,
            new byte[] { 0, 255 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        var step = new PreviewCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Preview", "Preview", 0), new PreviewProcessingStepOptions { RecipeVersion = "custom-preview-v2" });

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("custom-preview-v2", context.Artifacts![FrameArtifactRole.Preview].RecipeVersion);
    }

    [TestMethod]
    public async Task PreviewStep_UsesSourceStrideForPaddedMono16Rows()
    {
        var raw = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono16,
            new byte[] { 0, 1, 0, 2, 99, 99, 0, 3, 0, 4, 99, 99 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0), StrideBytes: 6);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        var step = new PreviewCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Preview", "Preview", 0), new PreviewProcessingStepOptions());

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var preview = context.Artifacts![FrameArtifactRole.Preview].Frame.PixelData.ToArray();
        Assert.AreEqual(0, preview[0]);
        Assert.AreEqual(0, preview[1]);
        Assert.IsGreaterThan(preview[1], preview[2]);
        Assert.AreEqual(byte.MaxValue, preview[3]);
    }

    [TestMethod]
    public async Task PreviewStep_DemosaicsBayerRawWithoutChangingSource()
    {
        var rawBytes = Enumerable.Range(0, 16)
            .SelectMany(index => BitConverter.GetBytes((ushort)(1000 + index * 100)))
            .ToArray();
        var original = rawBytes.ToArray();
        var raw = new CameraFrame(DateTimeOffset.UnixEpoch, 4, 4, CameraPixelFormat.BayerRggb16,
            rawBytes, new FrameMetadata(TimeSpan.FromSeconds(1), 150, 0));
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        var step = new PreviewCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Preview", "Preview", 0), new PreviewProcessingStepOptions());

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(original, raw.PixelData.ToArray());
        var preview = context.Artifacts![FrameArtifactRole.Preview];
        Assert.AreEqual(CameraPixelFormat.Rgb24, preview.Frame.PixelFormat);
        Assert.AreEqual(4 * 4 * 3, preview.Frame.PixelData.Length);
        CollectionAssert.AreEqual(new[] { context.Artifacts.Raw.ArtifactId }, preview.SourceArtifactIds!.ToArray());
    }

    [TestMethod]
    public async Task PreviewStep_CopiesRgb24WithoutChangingSource()
    {
        var rawBytes = new byte[] { 10, 20, 30, 40, 50, 60 };
        var raw = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 1, CameraPixelFormat.Rgb24,
            rawBytes, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        var step = new PreviewCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Preview", "Preview", 0), new PreviewProcessingStepOptions());

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var preview = context.Artifacts![FrameArtifactRole.Preview];
        Assert.AreEqual(CameraPixelFormat.Rgb24, preview.Frame.PixelFormat);
        CollectionAssert.AreEqual(rawBytes, preview.Frame.PixelData.ToArray());
        CollectionAssert.AreEqual(rawBytes, raw.PixelData.ToArray());
    }

    [TestMethod]
    public async Task PreviewStep_RepacksPaddedRgb24Rows()
    {
        var raw = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 2, CameraPixelFormat.Rgb24,
            new byte[] { 1, 2, 3, 99, 4, 5, 6, 99 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0), StrideBytes: 4);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        var step = new PreviewCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Preview", "Preview", 0), new PreviewProcessingStepOptions());

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var preview = context.Artifacts![FrameArtifactRole.Preview].Frame;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, preview.PixelData.ToArray());
        Assert.IsNull(preview.StrideBytes);
    }

    [TestMethod]
    public async Task RollingCombinationStep_EmitsCombinedDerivativeAfterEveryCapture()
    {
        var step = new RollingCombinationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Rolling", "Rolling", 0), new RollingCombinationProcessingStepOptions { WindowSize = 2 });
        var first = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono16,
            new byte[] { 100, 0 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var second = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono16,
            new byte[] { 44, 1 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var firstContext = new CaptureProcessingContext(CreateConfig(), CreateSubmission(first));
        var secondContext = new CaptureProcessingContext(CreateConfig(), CreateSubmission(second));

        await step.ProcessAsync(firstContext, CancellationToken.None).ConfigureAwait(false);
        await step.ProcessAsync(secondContext, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual((ushort)100, BitConverter.ToUInt16(firstContext.Artifacts![FrameArtifactRole.Combined].Frame.PixelData.Span));
        Assert.AreEqual((ushort)200, BitConverter.ToUInt16(secondContext.Artifacts![FrameArtifactRole.Combined].Frame.PixelData.Span));
        Assert.AreSame(second, secondContext.Artifacts.Raw.Frame);
    }

    [TestMethod]
    public async Task RollingCombinationStep_EmitsBayerCombinedDerivative()
    {
        var step = new RollingCombinationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Rolling", "Rolling", 0),
            new RollingCombinationProcessingStepOptions { WindowSize = 2 });
        var first = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.BayerRggb16,
            new byte[] { 100, 0 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var second = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.BayerRggb16,
            new byte[] { 44, 1 }, new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
        var firstContext = new CaptureProcessingContext(CreateConfig(), CreateSubmission(first));
        var secondContext = new CaptureProcessingContext(CreateConfig(), CreateSubmission(second));

        await step.ProcessAsync(firstContext, CancellationToken.None).ConfigureAwait(false);
        await step.ProcessAsync(secondContext, CancellationToken.None).ConfigureAwait(false);

        var combined = secondContext.Artifacts![FrameArtifactRole.Combined].Frame;
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, combined.PixelFormat);
        Assert.AreEqual((ushort)200, BitConverter.ToUInt16(combined.PixelData.Span));
    }

    [TestMethod]
    public async Task AnnotationStep_CreatesAnnotatedPreviewAndPreservesPreview()
    {
        const string sceneId = "annotation-test-scene";
        var raw = CreateFrame([0, 0, 0, 0]) with
        {
            Metadata = new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0, Scene: new SceneProvenance(
                sceneId, "rig-v1", "test", "1", new string('0', 64), "equidistant", "v1", "v1", "v1"))
        };
        var preview = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono8,
            new byte[] { 0, 0, 0, 0 }, raw.Metadata);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        context.AddDerivative(FrameArtifactRole.Preview, preview, "preview-v1");
        var store = new ProjectedSceneStore();
        var utc = DateTimeOffset.UnixEpoch;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 0)
        ]);
        var scene = await new VisibleSceneBuilder(catalog).BuildAsync(new VisibleSceneRequest(
            utc, new ObserverLocation(0, 0, 0),
            new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
            new CatalogQuery(6.5, 10),
            new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('0', 64), "test", "1")))
            .ConfigureAwait(false);
        store.Put(sceneId, scene);
        var step = new AnnotationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Annotation", "Annotation", 0),
            new AnnotationProcessingStepOptions { MarkRadius = 0, DrawLabels = false }, store);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreSame(preview, context.Artifacts![FrameArtifactRole.Preview].Frame);
        Assert.AreEqual((byte)144, context.Artifacts[FrameArtifactRole.AnnotatedPreview].Frame.PixelData.Span[3]);
        CollectionAssert.AreEqual(
            new[] { context.Artifacts[FrameArtifactRole.Preview].ArtifactId },
            context.Artifacts[FrameArtifactRole.AnnotatedPreview].SourceArtifactIds!.ToArray());
    }

    [TestMethod]
    public async Task AnnotationStep_UsesPersistedProjectedObjectsAfterSceneCacheLoss()
    {
        var provenance = new SceneProvenance(
            "persisted-scene", "rig-v1", "test", "1", new string('0', 64),
            "equidistant", "v1", "v1", "v1",
            Objects: [new ProjectedObjectProvenance("star", "Star", 1, 1, 0)]);
        var raw = CreateFrame([0, 0, 0, 0]) with
        {
            Metadata = new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0, Scene: provenance)
        };
        var preview = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono8,
            new byte[] { 0, 0, 0, 0 }, raw.Metadata);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        context.AddDerivative(FrameArtifactRole.Preview, preview, "preview-v1");
        var step = new AnnotationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Annotation", "Annotation", 0),
            new AnnotationProcessingStepOptions { MarkRadius = 0, DrawLabels = false },
            new ProjectedSceneStore());

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual((byte)144,
            context.Artifacts![FrameArtifactRole.AnnotatedPreview].Frame.PixelData.Span[3]);
    }

    [TestMethod]
    public async Task FrameProcessingWorker_WhenDownstreamStepFails_PreservesRawArtifactForLaterSteps()
    {
        // Arrange
        var rawFrame = CreateFrame([1, 2, 3, 4]);
        var channel = new FrameProcessingChannel(capacity: 2);
        var observer = new RawArtifactObserverStep();
        var worker = new FrameProcessingWorker(
            channel,
            [new ThrowingProcessingStep(), observer],
            NullLogger.Instance);

        await channel.WriteAsync(
            new FrameProcessingItem(CreateConfig(), CreateSubmission(rawFrame)),
            CancellationToken.None).ConfigureAwait(false);
        channel.Complete();

        // Act
        await worker.RunAsync(CancellationToken.None).ConfigureAwait(false);

        // Assert
        Assert.AreSame(rawFrame, observer.RawFrame);
    }

    private static CameraFrame CreateFrame(byte[] pixelData)
        => new(
            DateTimeOffset.UnixEpoch,
            Width: 2,
            Height: 2,
            CameraPixelFormat.Mono8,
            pixelData,
            new FrameMetadata(TimeSpan.FromSeconds(1), Gain: 1, TemperatureC: 0));

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)));

    private static CaptureLoopSubmission CreateSubmission(CameraFrame? frame)
        => new(
            new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);

    private sealed class ThrowingProcessingStep : ICaptureProcessingStep
    {
        public string Name => "Throwing";

        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => ValueTask.FromException(new InvalidOperationException("Expected test failure."));
    }

    private sealed class RawArtifactObserverStep : ICaptureProcessingStep
    {
        public string Name => "Observer";

        public int Order => 1;

        public CameraFrame? RawFrame { get; private set; }

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            RawFrame = context.Artifacts?.Raw.Frame;
            return ValueTask.CompletedTask;
        }
    }
}
