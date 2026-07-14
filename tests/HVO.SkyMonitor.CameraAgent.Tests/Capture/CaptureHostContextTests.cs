using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureHostContextTests
{
    [TestMethod]
    public async Task PublishAsync_CommitsBeforeQueueingAndDoesNotRetainFrameInWakeUpItem()
    {
        var payload = new byte[8];
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            new StoredFrameReference(
                manifest.RelativeArtifactPath,
                "/tmp/committed.bin",
                manifest.Descriptor.Timing.ExposureStartedUtc,
                FrameArtifactRole.Raw));
        var ingress = new RecordingIngress(receipt);
        var channel = new FrameProcessingChannel(2);
        var config = CreateConfig();
        var frame = new CameraFrame(
            manifest.Descriptor.Timing.ExposureStartedUtc,
            2,
            2,
            CameraPixelFormat.Mono16,
            payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN),
            4);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            frame.TimestampUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
        var context = new CaptureHostContext(config, channel, ingress);

        await context.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);
        channel.Complete();
        FrameProcessingItem? queued = null;
        await foreach (var item in channel.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            queued = item;
        }

        Assert.IsTrue(ingress.CompletedBeforeReturn);
        Assert.IsNotNull(queued);
        Assert.IsNull(queued.Submission.Result.Frame);
        Assert.IsNull(queued.Submission.Result.Artifacts);
        Assert.AreSame(receipt, queued.RawCapture);
        Assert.AreSame(frame, submission.Result.Frame);
        Assert.IsTrue(ingress.LastWakeupQueued);
    }

    [TestMethod]
    public async Task PublishAsync_WhenWakeUpChannelIsFull_ReturnsAfterDurableCommitWithoutBlocking()
    {
        var payload = new byte[8];
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            new StoredFrameReference(manifest.RelativeArtifactPath, "/tmp/committed.bin", manifest.Descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw));
        var ingress = new RecordingIngress(receipt);
        var channel = new FrameProcessingChannel(2);
        var config = CreateConfig();
        var frame = new CameraFrame(
            manifest.Descriptor.Timing.ExposureStartedUtc, 2, 2, CameraPixelFormat.Mono16, payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN), 4);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            frame.TimestampUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        await channel.WriteAsync(new FrameProcessingItem(config, submission), CancellationToken.None).ConfigureAwait(false);
        await channel.WriteAsync(new FrameProcessingItem(config, submission), CancellationToken.None).ConfigureAwait(false);
        var context = new CaptureHostContext(config, channel, ingress);

        await context.PublishAsync(submission, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        Assert.IsTrue(ingress.CompletedBeforeReturn);
        Assert.AreEqual(2, channel.CurrentDepth);
        Assert.AreEqual(2L, channel.AcceptedCount);
        Assert.IsFalse(ingress.LastWakeupQueued);
    }

    private static CameraModuleConfig CreateConfig()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            AgentId: "agent");

    private sealed class RecordingIngress(RawCaptureReceipt receipt) : IRawCaptureIngress, IRawIngressWakeupReporter
    {
        public bool CompletedBeforeReturn { get; private set; }

        public bool? LastWakeupQueued { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            CompletedBeforeReturn = true;
            return ValueTask.FromResult<RawCaptureReceipt?>(receipt);
        }

        public void ReportWakeup(bool queued) => LastWakeupQueued = queued;
    }
}
