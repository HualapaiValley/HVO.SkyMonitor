using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureHostContextTests
{
    [TestMethod]
    public async Task PublishAsync_PhysicalProjectedSceneIsStagedBeforeIngressObservesSubmission()
    {
        var root = CreateRoot();
        try
        {
            using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
            using var staging = new ProjectedSceneStagingStore(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), lifecycle);
            var config = CreatePhysicalProjectedSceneConfig();
            var catalog = new MetadataCatalog();
            var stager = new CaptureProjectedSceneStager(staging, catalog);
            var ingress = new ObservingIngress(staging);
            var submission = CreatePhysicalSubmission(config);
            var context = new CaptureHostContext(
                config, ingress, new RecordingDistributor(), projectedSceneStaging: staging,
                projectedSceneLifecycle: lifecycle, projectedSceneStager: stager);

            await context.PublishAsync(submission, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(ingress.ObservedScene);
            Assert.AreEqual(ProjectedSceneKind.Predicted, ingress.ObservedDocument!.IntendedKind);
            Assert.AreEqual(1, catalog.QueryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PhysicalProjectedSceneRetry_ReusesStableCaptureStageKey()
    {
        var root = CreateRoot();
        try
        {
            using var staging = new ProjectedSceneStagingStore(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            var config = CreatePhysicalProjectedSceneConfig();
            var submission = CreatePhysicalSubmission(config);
            var stager = new CaptureProjectedSceneStager(staging, new MetadataCatalog());

            var first = await stager.StageAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
            var retry = await stager.StageAsync(config, submission, CancellationToken.None).ConfigureAwait(false);

            var firstKey = first.Result.Frame!.Metadata.Scene!.ProjectedSceneStageKey;
            Assert.AreEqual(firstKey, retry.Result.Frame!.Metadata.Scene!.ProjectedSceneStageKey);
            Assert.HasCount(1, Directory.EnumerateFiles(
                Path.Combine(root, "staging", "projected-scenes"), "*.json"));

            var changedConfig = config with
            {
                Rig = config.Rig with { ProfileVersion = string.Concat(config.Rig.ProfileVersion, "-changed") }
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await stager.StageAsync(changedConfig, submission, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.IsNotNull(await staging.ReadAsync(firstKey!, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, true)]
    [DataRow(0, true)]
    public async Task PublishAsync_PhysicalStagerCleanupFollowsPublicationOwnership(
        int publicationStateValue,
        bool retained)
    {
        var root = CreateRoot();
        try
        {
            using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
            using var staging = new ProjectedSceneStagingStore(
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), lifecycle);
            var config = CreatePhysicalProjectedSceneConfig();
            var stager = new CaptureProjectedSceneStager(staging, new MetadataCatalog());
            var ingress = new ObservingThrowingIngress((RawCapturePublicationState)publicationStateValue, staging);
            var context = new CaptureHostContext(
                config, ingress, new RecordingDistributor(), projectedSceneStaging: staging,
                projectedSceneLifecycle: lifecycle, projectedSceneStager: stager);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await context.PublishAsync(CreatePhysicalSubmission(config), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            Assert.IsNotNull(ingress.StageKey);
            Assert.AreEqual(retained,
                await staging.ReadAsync(ingress.StageKey!, CancellationToken.None).ConfigureAwait(false) is not null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishAsync_PhysicalCatalogFailurePreservesRawAndMarksSceneUnavailable()
    {
        var ingress = new CapturingIngress();
        var staging = new RecordingStagingStore();
        var original = CreatePhysicalSubmission(CreatePhysicalProjectedSceneConfig());
        var context = new CaptureHostContext(
            CreatePhysicalProjectedSceneConfig(), ingress, new RecordingDistributor(),
            projectedSceneStaging: staging,
            projectedSceneStager: new CaptureProjectedSceneStager(staging, new ThrowingCatalog()));

        await context.PublishAsync(original, CancellationToken.None).ConfigureAwait(false);

        AssertOptionalStagingFailure(ingress, original, "analysis-unavailable");
        Assert.IsEmpty(staging.DeletedKeys);
    }

    [TestMethod]
    [DataRow("capacity")]
    [DataRow("filesystem")]
    public async Task PublishAsync_PhysicalStageStorageFailurePreservesRawAndCleansPendingOwnership(string failure)
    {
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        var staging = new ThrowingAfterRegistrationStagingStore(
            lifecycle, failure == "capacity" ? new IOException("capacity") : new UnauthorizedAccessException("filesystem"));
        var ingress = new CapturingIngress();
        var config = CreatePhysicalProjectedSceneConfig();
        var original = CreatePhysicalSubmission(config);
        var context = new CaptureHostContext(
            config, ingress, new RecordingDistributor(), projectedSceneStaging: staging,
            projectedSceneLifecycle: lifecycle,
            projectedSceneStager: new CaptureProjectedSceneStager(staging, new MetadataCatalog()));

        await context.PublishAsync(original, CancellationToken.None).ConfigureAwait(false);

        AssertOptionalStagingFailure(ingress, original, "storage-unavailable");
        Assert.AreEqual(0, staging.DeleteCount);
        using var lease = await lifecycle.AcquireReconciliationLeaseAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(lease.PendingStageKeys);
        Assert.IsFalse(staging.StageExists);
    }

    [TestMethod]
    public async Task PublishAsync_PhysicalStageCancellationStillHandsAcquiredRawFrameToIngressOnce()
    {
        using var lifecycle = new ProjectedSceneStageLifecycleCoordinator();
        using var cancellation = new CancellationTokenSource();
        var staging = new CancellingStagingStore(lifecycle, cancellation);
        var ingress = new CapturingIngress();
        var config = CreatePhysicalProjectedSceneConfig();
        var original = CreatePhysicalSubmission(config);
        var context = new CaptureHostContext(
            config, ingress, new RecordingDistributor(), projectedSceneStaging: staging,
            projectedSceneLifecycle: lifecycle,
            projectedSceneStager: new CaptureProjectedSceneStager(staging, new MetadataCatalog()));

        await context.PublishAsync(original, cancellation.Token).ConfigureAwait(false);

        AssertOptionalStagingFailure(ingress, original, "cancelled");
        Assert.IsFalse(ingress.ObservedCancellationRequested);
        using var lease = await lifecycle.AcquireReconciliationLeaseAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(lease.PendingStageKeys);
        Assert.IsFalse(staging.StageExists);
    }

    [TestMethod]
    public async Task PublishAsync_RawIngressFailureRemainsAuthoritativeAfterOptionalStagingFailure()
    {
        var expected = new InvalidOperationException("raw-ingress-authoritative");
        var ingress = new CapturingIngress(expected);
        var staging = new RecordingStagingStore { StageFailure = new IOException("staging-failure") };
        var config = CreatePhysicalProjectedSceneConfig();
        var original = CreatePhysicalSubmission(config);
        var context = new CaptureHostContext(
            config, ingress, new RecordingDistributor(), projectedSceneStaging: staging,
            projectedSceneStager: new CaptureProjectedSceneStager(staging, new MetadataCatalog()));

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await context.PublishAsync(original, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, ingress.CallCount);
        CollectionAssert.AreEqual(original.Result.Frame!.PixelData.ToArray(), ingress.Submission!.Result.Frame!.PixelData.ToArray());
    }

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
            CapturePipelineConfig.Empty,
            AgentId: "agent");

    private static CameraModuleConfig CreatePhysicalProjectedSceneConfig()
    {
        var location = DeploymentLocationSnapshot.Create(
            "location", 1, "test", null, DateTimeOffset.UnixEpoch, null, 0, 0, 0, "UTC");
        return CreateConfig() with
        {
            DeploymentLocation = location,
            Pipeline = new CapturePipelineConfig(
            [
                new CaptureProcessingStepConfig("ProjectedScene", Options: JsonSerializer.SerializeToElement(
                    new ProjectedSceneCaptureProcessingStepOptions()), DependsOn: ["$raw"])
            ])
        };
    }

    private static CaptureLoopSubmission CreatePhysicalSubmission(CameraModuleConfig config)
    {
        var utc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var layout = new FrameLayoutDescriptor(
            2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 16, 16,
            FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, ushort.MaxValue, 8);
        var frame = new CameraFrame(
            utc, 2, 2, CameraPixelFormat.Mono16, new byte[8],
            new FrameMetadata(TimeSpan.FromSeconds(2), 1, 0, "PhysicalCamera"), 4)
        {
            Layout = layout
        };
        var result = new CaptureResult(
            frame, new CaptureSetpoint(TimeSpan.FromSeconds(2), 1, null, null),
            TimeSpan.Zero, CaptureMode.Still, false)
        {
            AcquisitionTiming = new CaptureAcquisitionTiming(utc, utc.AddSeconds(2), utc.AddSeconds(2.1))
        };
        return new CaptureLoopSubmission(
            new CaptureRequest(utc, config.Rig.Pipeline.CaptureInterval, CaptureMode.Still),
            result, utc, config.Rig.Pipeline.CaptureInterval, TimeSpan.Zero);
    }

    private static string CreateRoot()
        => FileSystemTestPaths.CreatePhysicalTemporaryDirectory("skymonitor-tests");

    private static void AssertOptionalStagingFailure(
        CapturingIngress ingress,
        CaptureLoopSubmission original,
        string expectedReason)
    {
        Assert.AreEqual(1, ingress.CallCount);
        Assert.IsNotNull(ingress.Submission);
        var observed = ingress.Submission.Result.Frame!;
        CollectionAssert.AreEqual(original.Result.Frame!.PixelData.ToArray(), observed.PixelData.ToArray());
        Assert.AreEqual(original.Result.Frame.Width, observed.Width);
        Assert.IsNull(observed.Metadata.Scene);
        Assert.AreEqual("Unavailable", observed.Metadata.Extra!["projectedSceneAvailability"]);
        Assert.AreEqual(expectedReason, observed.Metadata.Extra["projectedSceneUnavailableReason"]);
        Assert.IsFalse(observed.Metadata.Extra.Values.Any(static value =>
            value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)));
    }

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

    private sealed class ObservingIngress(IProjectedSceneStagingReader reader) : IRawCaptureIngress
    {
        public SceneProvenance? ObservedScene { get; private set; }
        public StagedProjectedSceneDocument? ObservedDocument { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            ObservedScene = submission.Result.Frame!.Metadata.Scene;
            ObservedDocument = await reader.ReadAsync(ObservedScene!.ProjectedSceneStageKey!, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    private sealed class ObservingThrowingIngress(
        RawCapturePublicationState state,
        IProjectedSceneStagingReader reader) : IRawCaptureIngress
    {
        public string? StageKey { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            StageKey = submission.Result.Frame!.Metadata.Scene!.ProjectedSceneStageKey;
            Assert.IsNotNull(await reader.ReadAsync(StageKey!, cancellationToken).ConfigureAwait(false));
            throw new IOException("ingress-failure");
        }

        public ValueTask<RawCapturePublicationState> GetPublicationStateAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => ValueTask.FromResult(state);
    }

    private sealed class MetadataCatalog : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        public CatalogMetadata Metadata { get; } = new(
            "test", "1", new Uri("https://example.invalid"), new string('A', 64), "test", "1");

        public int QueryCount { get; private set; }

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query)
        {
            QueryCount++;
            return [new CelestialCatalogObject("star", "Star", 0, 0, 1)];
        }

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return ValueTask.FromResult<IReadOnlyList<CelestialCatalogObject>>(
                [new CelestialCatalogObject("star", "Star", 0, 0, 1)]);
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
        internal Exception? StageFailure { get; init; }

        public ValueTask StageAsync(string stageKey, string sceneId, HVO.SkyMonitor.Astronomy.VisibleScene scene,
            CancellationToken cancellationToken) => StageFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(StageFailure);

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

    private sealed class CapturingIngress(Exception? failure = null) : IRawCaptureIngress
    {
        internal int CallCount { get; private set; }
        internal CaptureLoopSubmission? Submission { get; private set; }
        internal bool ObservedCancellationRequested { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Submission = submission;
            ObservedCancellationRequested = cancellationToken.IsCancellationRequested;
            return failure is null
                ? ValueTask.FromResult<RawCaptureReceipt?>(null)
                : ValueTask.FromException<RawCaptureReceipt?>(failure);
        }
    }

    private sealed class ThrowingCatalog : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        public CatalogMetadata Metadata { get; } = new(
            "throwing", "1", new Uri("https://example.invalid"), new string('A', 64), "test", "1");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => throw new InvalidOperationException("catalog");

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<CelestialCatalogObject>>(new InvalidOperationException("catalog"));
    }

    private sealed class ThrowingAfterRegistrationStagingStore(
        ProjectedSceneStageLifecycleCoordinator lifecycle,
        Exception failure) : IProjectedSceneStagingStore
    {
        internal bool StageExists { get; private set; }
        internal int DeleteCount { get; private set; }

        public async ValueTask StageAsync(
            string stageKey, string sceneId, VisibleScene scene, CancellationToken cancellationToken)
        {
            var registered = await lifecycle.RegisterPendingAsync(stageKey, cancellationToken).ConfigureAwait(false);
            try
            {
                StageExists = true;
                throw failure;
            }
            finally
            {
                StageExists = false;
                if (registered) await lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);
            }
        }

        public async ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken)
        {
            DeleteCount++;
            StageExists = false;
            await lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);
        }

        public ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CancellingStagingStore(
        ProjectedSceneStageLifecycleCoordinator lifecycle,
        CancellationTokenSource cancellation) : IProjectedSceneStagingStore
    {
        internal bool StageExists { get; private set; }

        public async ValueTask StageAsync(
            string stageKey, string sceneId, VisibleScene scene, CancellationToken cancellationToken)
        {
            var registered = await lifecycle.RegisterPendingAsync(stageKey, cancellationToken).ConfigureAwait(false);
            try
            {
                StageExists = true;
                await cancellation.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                StageExists = false;
                if (registered) await lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);
            }
        }

        public async ValueTask DeleteAsync(string stageKey, CancellationToken cancellationToken)
        {
            StageExists = false;
            await lifecycle.ResolvePendingAsync(stageKey).ConfigureAwait(false);
        }

        public ValueTask MarkCompletedAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteCompletedAsync(string stageKey, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
