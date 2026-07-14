using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
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
        var distributor = new RecordingDistributor();
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
        var context = new CaptureHostContext(config, ingress, distributor);

        await context.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(ingress.CompletedBeforeReturn);
        Assert.AreSame(frame, submission.Result.Frame);
        Assert.AreEqual(1, distributor.NotificationCount);
    }

    [TestMethod]
    public async Task PublishAsync_NotifiesDistributorAfterDurableCommitWithoutBlocking()
    {
        var payload = new byte[8];
        var manifest = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var receipt = new RawCaptureReceipt(
            RawIngressOutcome.Committed,
            manifest,
            new StoredFrameReference(manifest.RelativeArtifactPath, "/tmp/committed.bin", manifest.Descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw));
        var ingress = new RecordingIngress(receipt);
        var distributor = new RecordingDistributor();
        var config = CreateConfig();
        var frame = new CameraFrame(
            manifest.Descriptor.Timing.ExposureStartedUtc, 2, 2, CameraPixelFormat.Mono16, payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN), 4);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            frame.TimestampUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var context = new CaptureHostContext(config, ingress, distributor);

        await context.PublishAsync(submission, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        Assert.IsTrue(ingress.CompletedBeforeReturn);
        Assert.AreEqual(1, distributor.NotificationCount);
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

    private sealed class RecordingIngress(RawCaptureReceipt receipt) : IRawCaptureIngress
    {
        public bool CompletedBeforeReturn { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            CompletedBeforeReturn = true;
            return ValueTask.FromResult<RawCaptureReceipt?>(receipt);
        }
    }

    private sealed class RecordingDistributor : ICaptureDistributor
    {
        public int NotificationCount { get; private set; }

        public void NotifyCommittedCapture() => NotificationCount++;

        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
