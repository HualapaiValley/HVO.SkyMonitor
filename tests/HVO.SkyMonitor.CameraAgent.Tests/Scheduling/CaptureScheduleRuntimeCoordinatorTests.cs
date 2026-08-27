using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureScheduleRuntimeCoordinatorTests
{
    [TestMethod]
    public async Task OperatorState_AfterFileConfigurationReload_ExposesReloadedProfileSha256()
    {
        using var fixture = await RuntimeFixture.CreateAsync(initializeRuntime: false).ConfigureAwait(false);

        var initial = await fixture.Store.InitializeAsync(
            fixture.Configuration, CancellationToken.None).ConfigureAwait(false);
        var reloaded = fixture.Configuration with { Schedule = AlwaysOpenDefinition("reloaded") };
        var durable = await fixture.Store.InitializeAsync(reloaded, CancellationToken.None).ConfigureAwait(false);
        var effective = initial.ActiveRevision.Profile.ApplyTo(reloaded);
        _ = await fixture.Runtime.InitializeAsync(effective, CancellationToken.None).ConfigureAwait(false);
        var state = await fixture.Runtime.GetOperatorStateAsync(CancellationToken.None).ConfigureAwait(false);
        var expected = LocalCaptureProfileContract.ComputeSha256(
            LocalCaptureProfileDefinition.CreateForConfiguration(
                reloaded,
                reloaded.Schedule!));

        Assert.AreEqual(expected, state.FileConfigurationProfileSha256);
        Assert.AreEqual(expected, durable.PendingRevision!.ProfileSha256);
        Assert.AreEqual(durable.PendingRevision.RevisionId, state.PendingRevision!.RevisionId);
    }

    [TestMethod]
    public async Task Grant_BindsRevisionExpansionProfileAndLocationBeforeModuleCall()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);

        var grant = await fixture.Runtime.WaitForGrantAsync(CancellationToken.None).ConfigureAwait(false);
        using var lease = await fixture.Admission.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        var confirmed = await fixture.Runtime.ConfirmGrantAsync(
            grant, "admission-1", CancellationToken.None).ConfigureAwait(false);
        lease.MarkNoPublicationRequired();

        Assert.IsNotNull(confirmed);
        Assert.AreEqual(fixture.Runtime.Snapshot!.Revision.RevisionId, confirmed.Revision.RevisionId);
        Assert.AreEqual(fixture.Location.LocationId, confirmed.Evidence.DeploymentLocationId);
        Assert.AreEqual(fixture.Location.Version, confirmed.Evidence.DeploymentLocationVersion);
        Assert.AreEqual(confirmed.Revision.ScheduleSha256, confirmed.Evidence.ScheduleRevisionSha256);
        Assert.AreEqual(confirmed.Revision.ProfileSha256, confirmed.Evidence.LocalProfileSha256);
        Assert.AreEqual(confirmed.Profile.Id, confirmed.Evidence.SetpointProfileId);
        Assert.IsTrue(CaptureScheduleContract.ValidateAdmissionEvidence(confirmed.Evidence).IsValid);
    }

    [TestMethod]
    public async Task WaitForGrant_AfterManualPause_ReportsAdmissionInterruption()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        _ = await fixture.Runtime.WaitForGrantAsync(CancellationToken.None).ConfigureAwait(false);
        var version = fixture.Admission.Snapshot.Version;
        _ = await fixture.Admission.PauseAsync(
            "pause-schedule", version, "owner", "test", CancellationToken.None).ConfigureAwait(false);

        var pendingGrant = fixture.Runtime.WaitForGrantAsync(CancellationToken.None);
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(pendingGrant.IsCompleted);
        _ = await fixture.Admission.ResumeAsync(
            "resume-schedule", fixture.Admission.Snapshot.Version, "owner", "test", CancellationToken.None)
            .ConfigureAwait(false);

        var resumedGrant = await pendingGrant.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Assert.IsTrue(resumedGrant.AdmissionWasInterrupted);
    }

    [TestMethod]
    public async Task ConfirmGrant_WhenPauseAndResumeOccurredAfterGrant_ReportsAdmissionInterruption()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var grant = await fixture.Runtime.WaitForGrantAsync(CancellationToken.None).ConfigureAwait(false);
        _ = await fixture.Admission.PauseAsync(
            "pause-after-grant",
            fixture.Admission.Snapshot.Version,
            "owner",
            "test",
            CancellationToken.None).ConfigureAwait(false);
        _ = await fixture.Admission.ResumeAsync(
            "resume-after-grant",
            fixture.Admission.Snapshot.Version,
            "owner",
            "test",
            CancellationToken.None).ConfigureAwait(false);

        var confirmed = await fixture.Runtime.ConfirmGrantAsync(
            grant, "admission-after-pause", CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(confirmed);
        Assert.IsTrue(confirmed.AdmissionWasInterrupted);
    }

    [TestMethod]
    public async Task Stage_WhenModulePreflightRejects_DoesNotChangeDurableState()
    {
        using var fixture = await RuntimeFixture.CreateAsync(new RejectingModuleValidator()).ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

        _ = await Assert.ThrowsAsync<CaptureProfileCompatibilityException>(() => fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.Create(
                fixture.Configuration, AlwaysOpenDefinition("invalid", gain: 2)),
            "stage-invalid",
            initial.Version,
            "owner",
            null,
            CancellationToken.None)).ConfigureAwait(false);

        var current = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(initial.Version, current.Version);
        Assert.IsNull(current.PendingRevision);
        Assert.AreEqual(initial.ActiveRevision.RevisionId, current.ActiveRevision.RevisionId);
    }

    [TestMethod]
    public async Task Activation_WaitsForPublicationBoundaryAndCancelsOldRevision()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var targetProfile = LocalCaptureProfileDefinition.Create(
            fixture.Configuration,
            AlwaysOpenDefinition("replacement", gain: 5));
        var staged = await fixture.Store.StageAsync(
            targetProfile,
            "stage-runtime",
            initial.Version,
            "owner",
            "replacement",
            CancellationToken.None).ConfigureAwait(false);
        var revisionChanged = fixture.Runtime.RevisionChanged;
        var lease = await fixture.Admission.EnterAsync(CancellationToken.None).ConfigureAwait(false);

        var activation = fixture.Runtime.ActivateAsync(
            staged.PendingRevision!.RevisionId,
            "activate-runtime",
            staged.Version,
            "owner",
            "approved",
            CancellationToken.None);
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(activation.IsCompleted);

        lease.MarkPublished();
        lease.Dispose();
        var activated = await activation.ConfigureAwait(false);

        Assert.AreEqual(staged.PendingRevision.RevisionId, activated.ActiveRevision.RevisionId);
        Assert.AreEqual(staged.PendingRevision.RevisionId, fixture.Runtime.Snapshot!.Revision.RevisionId);
        Assert.IsTrue(revisionChanged.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Runner_PublishesExactScheduleEvidenceAndScheduledSetpoint()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var module = new SingleCaptureModule(fixture.TimeProvider);
        var host = new RecordingHostContext(fixture.Runtime.Snapshot!.Configuration, cancellation);
        var runner = new CameraModuleRunner(
            module,
            host,
            fixture.TimeProvider,
            NullLogger.Instance,
            fixture.Admission,
            scheduleRuntimeCoordinator: fixture.Runtime);

        await runner.RunAsync(cancellation.Token).ConfigureAwait(false);

        var submission = host.Submission;
        Assert.IsNotNull(submission);
        Assert.IsNotNull(submission.CycleEvidence?.ScheduleAdmission);
        Assert.AreEqual("initial", submission.CycleEvidence.ScheduleAdmission.SetpointProfileId);
        Assert.AreEqual(TimeSpan.FromSeconds(1), submission.Request.RequestedSetpoint?.Exposure);
        Assert.AreEqual(1d, submission.Request.RequestedSetpoint?.Gain);
        Assert.IsTrue(submission.CycleEvidence.ScheduleAdmission.DecisionUtc <=
            submission.CycleEvidence.ModuleCallStartedUtc);
    }

    [TestMethod]
    public async Task LateActivationReplay_DoesNotReplaceNewerRuntimeRevision()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var first = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.Create(
                fixture.Configuration, AlwaysOpenDefinition("first", gain: 2)),
            "stage-first", initial.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);
        var firstReceipt = await fixture.Runtime.ActivateAsync(
            first.PendingRevision!.RevisionId,
            "activate-first",
            first.Version,
            "owner",
            null,
            CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.Create(
                fixture.Configuration, AlwaysOpenDefinition("second", gain: 3)),
            "stage-second", firstReceipt.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);
        var secondReceipt = await fixture.Runtime.ActivateAsync(
            second.PendingRevision!.RevisionId,
            "activate-second",
            second.Version,
            "owner",
            null,
            CancellationToken.None).ConfigureAwait(false);
        var secondRevisionId = fixture.Runtime.Snapshot!.Revision.RevisionId;

        var replay = await fixture.Runtime.ActivateAsync(
            first.PendingRevision.RevisionId,
            "activate-first",
            first.Version,
            "owner",
            null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(firstReceipt.Version, replay.Version);
        Assert.AreEqual(secondReceipt.ActiveRevision.RevisionId, secondRevisionId);
        Assert.AreEqual(secondRevisionId, fixture.Runtime.Snapshot!.Revision.RevisionId);
        Assert.AreEqual(secondRevisionId,
            (await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
    }

    [TestMethod]
    public async Task LateStageReplay_DoesNotReplaceNewerPendingRuntimeRevision()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var first = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.Create(
                fixture.Configuration, AlwaysOpenDefinition("first-stage", gain: 2)),
            "stage-replay-first", initial.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.Create(
                fixture.Configuration, AlwaysOpenDefinition("second-stage", gain: 3)),
            "stage-replay-second", first.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);

        var replay = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.Create(
                fixture.Configuration, AlwaysOpenDefinition("first-stage", gain: 2)),
            "stage-replay-first", initial.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(first.Version, replay.Version);
        Assert.AreEqual(second.PendingRevision!.RevisionId, fixture.Runtime.Snapshot!.PendingRevisionId);
        Assert.AreEqual(
            second.PendingRevision.RevisionId,
            (await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision!.RevisionId);
    }

    private static CaptureScheduleDefinition AlwaysOpenDefinition(string profileId, double gain = 1)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                profileId,
                TimeSpan.FromSeconds(1),
                gain,
                TimeSpan.FromSeconds(2))],
            Enum.GetValues<DayOfWeek>()
                .Select(day => new CaptureWeeklyScheduleWindow(
                    $"always-open-{day.ToString().ToUpperInvariant()}",
                    day,
                    new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                    new CaptureScheduleBoundary(
                        CaptureScheduleBoundaryKind.FixedLocalTime,
                        TimeOnly.MinValue,
                        DayOffset: 1),
                    profileId))
                .ToArray());

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _root;
        private readonly CaptureControlTelemetry _telemetry;

        private RuntimeFixture(
            string root,
            CaptureControlTelemetry telemetry,
            SqliteCaptureScheduleStore store,
            CaptureAdmissionCoordinator admission,
            CaptureScheduleRuntimeCoordinator runtime,
            CameraModuleConfig configuration,
            DeploymentLocationSnapshot location,
            TimeProvider timeProvider)
        {
            _root = root;
            _telemetry = telemetry;
            Store = store;
            Admission = admission;
            Runtime = runtime;
            Configuration = configuration;
            Location = location;
            TimeProvider = timeProvider;
        }

        internal SqliteCaptureScheduleStore Store { get; }

        internal CaptureAdmissionCoordinator Admission { get; }

        internal CaptureScheduleRuntimeCoordinator Runtime { get; }

        internal CameraModuleConfig Configuration { get; }

        internal DeploymentLocationSnapshot Location { get; }

        internal TimeProvider TimeProvider { get; }

        internal static async Task<RuntimeFixture> CreateAsync(
            ICameraModuleConfigurationValidator? moduleConfigurationValidator = null,
            bool initializeRuntime = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "hvo-schedule-runtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var now = new DateTimeOffset(2025, 1, 13, 1, 0, 0, TimeSpan.Zero);
            var timeProvider = new FixedTimeProvider(now);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var rawIngress = new JournalInitializer(root);
            var store = new SqliteCaptureScheduleStore(rawIngress, options, timeProvider);
            var telemetry = new CaptureControlTelemetry();
            var admission = new CaptureAdmissionCoordinator(
                rawIngress, options, timeProvider, telemetry);
            var rawState = new RawIngressState(timeProvider);
            rawState.Set(RawIngressAvailability.Accepting, "accepting");
            var laneState = new CaptureLaneState(timeProvider, options);
            laneState.Update([]);
            var location = DeploymentLocationSnapshot.Create(
                "test-location", 2, "test", null, DateTimeOffset.UnixEpoch, null,
                35, -114, 1000, "UTC");
            var configuration = new CameraModuleConfig(
                new ObservatoryLocation(35, -114, 1000, "UTC"),
                new CameraModuleDescriptor("test"),
                new CameraRigConfig(
                    new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                    new OpticsProfile("Perspective", 50, 10, 0),
                    new RigOrientation(90, 0, 0),
                    new PipelineExposureProfile(
                        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                    new CameraControlPolicy
                    {
                        ExposureControl = AutomaticControlOwnership.Disabled,
                        GainControl = AutomaticControlOwnership.Disabled
                    }),
                CapturePipelineConfig.Empty,
                AgentId: "test-agent")
            {
                DeploymentLocation = location,
                Schedule = AlwaysOpenDefinition("initial")
            };
            var runtime = new CaptureScheduleRuntimeCoordinator(
                store,
                admission,
                rawState,
                laneState,
                new EmptyPipelineFactory(),
                timeProvider,
                moduleConfigurationValidator: moduleConfigurationValidator);
            await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            if (initializeRuntime)
            {
                await runtime.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            }
            return new RuntimeFixture(
                root, telemetry, store, admission, runtime, configuration, location, timeProvider);
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Admission.Dispose();
            Store.Dispose();
            _telemetry.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RejectingModuleValidator : ICameraModuleConfigurationValidator
    {
        public void Validate(CameraModuleConfig configuration)
        {
            if (configuration.Schedule?.SetpointProfiles.Any(profile => profile.Id == "invalid") == true)
            {
                throw new ArgumentException("Unsupported test module.", nameof(configuration));
            }
        }
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
            => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class SingleCaptureModule(TimeProvider timeProvider) : ICameraModule
    {
        public string Id => "single";

        public string DisplayName => "Single";

        public string ModuleType => "test";

        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            var now = timeProvider.GetUtcNow();
            var setpoint = request.RequestedSetpoint!;
            var frame = new CameraFrame(
                now,
                2,
                2,
                CameraPixelFormat.Mono16,
                new byte[8],
                new FrameMetadata(setpoint.Exposure, setpoint.Gain, 0));
            return Task.FromResult(new CaptureResult(
                frame,
                setpoint,
                TimeSpan.Zero,
                CaptureMode.Still,
                false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(now, now, now)
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHostContext(
        CameraModuleConfig configuration,
        CancellationTokenSource cancellation) : ICaptureHostContext
    {
        public CameraModuleConfig Configuration { get; } = configuration;

        internal CaptureLoopSubmission? Submission { get; private set; }

        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            Submission = submission;
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            var journal = new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 5);
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
