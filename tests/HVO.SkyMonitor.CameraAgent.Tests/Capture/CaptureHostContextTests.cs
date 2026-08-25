using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

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
                 FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
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
            new StoredFrameReference(manifest.RelativeArtifactPath, "/tmp/committed.bin", manifest.Descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
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

    [TestMethod]
    public async Task PublishAsync_IngressFailureDeletesExplicitCaptureStageAndPreservesOriginalException()
    {
        var stageKey = new string('A', 64);
        var expected = new InvalidOperationException("original-ingress-failure");
        var ingress = new ThrowingIngress(expected, RawCapturePublicationState.DefinitelyNotCommitted);
        var staging = new RecordingStagingStore();
        var frame = new CameraFrame(
            DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono16, new byte[8],
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN, Scene: new SceneProvenance(
                new string('B', 64), "rig", "catalog", "1", new string('C', 64), "projection",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey)), 4);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false), frame.TimestampUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var context = new CaptureHostContext(CreateConfig(), ingress, new RecordingDistributor(),
            projectedSceneStaging: staging);

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await context.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(new[] { stageKey }, staging.DeletedKeys.ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PublishAsync_AmbiguousOrCommittedFailureRetainsStage(bool committed)
    {
        var state = committed ? RawCapturePublicationState.Committed : RawCapturePublicationState.Unknown;
        var stageKey = new string('D', 64);
        var expected = new InvalidOperationException("postcommit-failure");
        var ingress = new ThrowingIngress(expected, state);
        var staging = new RecordingStagingStore();
        var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono16, new byte[8],
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN, Scene: new SceneProvenance(
                new string('E', 64), "rig", "catalog", "1", new string('F', 64), "projection",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey)), 4);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false), frame.TimestampUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var context = new CaptureHostContext(CreateConfig(), ingress, new RecordingDistributor(),
            projectedSceneStaging: staging);

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await context.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreSame(expected, actual);
        Assert.HasCount(0, staging.DeletedKeys);
    }

    [TestMethod]
    public async Task PublishAsync_PublicationStateQueryFailureRetainsStageAndPreservesOriginalException()
    {
        var stageKey = new string('1', 64);
        var expected = new InvalidOperationException("original-publication-failure");
        var ingress = new ThrowingIngress(expected, RawCapturePublicationState.Unknown, throwStateQuery: true);
        var staging = new RecordingStagingStore();
        var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, CameraPixelFormat.Mono16, new byte[8],
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN, Scene: new SceneProvenance(
                new string('2', 64), "rig", "catalog", "1", new string('3', 64), "projection",
                "projection-v1", "astronomy-v1", "sensor-v1",
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: stageKey)), 4);
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero, CaptureMode.Still, false), frame.TimestampUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var context = new CaptureHostContext(CreateConfig(), ingress, new RecordingDistributor(),
            projectedSceneStaging: staging);

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await context.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreSame(expected, actual);
        Assert.HasCount(0, staging.DeletedKeys);
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

    private sealed class ThrowingIngress(
        Exception exception,
        RawCapturePublicationState state,
        bool throwStateQuery = false) : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.FromException<RawCaptureReceipt?>(exception);

        ValueTask<RawCapturePublicationState> IRawCaptureIngress.GetPublicationStateAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => throwStateQuery
                ? ValueTask.FromException<RawCapturePublicationState>(new IOException("state-query-failure"))
                : ValueTask.FromResult(state);
    }

    private sealed class RecordingStagingStore : IProjectedSceneStagingStore
    {
        internal List<string> DeletedKeys { get; } = [];

        public ValueTask StageAsync(string stageKey, string sceneId, HVO.SkyMonitor.Astronomy.VisibleScene scene,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(stageKey);
            return ValueTask.CompletedTask;
        }

        public ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
