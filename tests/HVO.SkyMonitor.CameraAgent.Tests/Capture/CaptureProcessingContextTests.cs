using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CaptureProcessingContextTests
{
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
    public async Task AnnotationStep_CreatesAnnotatedPreviewAndPreservesPreview()
    {
        var raw = CreateFrame([0, 0, 0, 0]);
        var preview = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono8,
            new byte[] { 0, 0, 0, 0 }, raw.Metadata);
        var context = new CaptureProcessingContext(CreateConfig(), CreateSubmission(raw));
        context.AddDerivative(FrameArtifactRole.Preview, preview, "preview-v1");
        var step = new AnnotationCaptureProcessingStep(
            new CaptureProcessingStepMetadata("Annotation", "Annotation", 0), new AnnotationProcessingStepOptions { FocalLengthPixels = 1 });

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.AreSame(preview, context.Artifacts![FrameArtifactRole.Preview].Frame);
        Assert.AreEqual(byte.MaxValue, context.Artifacts[FrameArtifactRole.AnnotatedPreview].Frame.PixelData.Span[3]);
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
