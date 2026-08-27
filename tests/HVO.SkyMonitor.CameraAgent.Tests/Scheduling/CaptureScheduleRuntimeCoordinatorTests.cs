using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
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
            LocalCaptureProfileDefinition.CreateV2(
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
    public async Task StageAndPreview_RejectLegacyAndMalformedCurrentProfiles()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var legacy = LocalCaptureProfileDefinition.Create(
            fixture.Configuration,
            AlwaysOpenDefinition("legacy"));
        var malformedCurrentProfiles = new[]
        {
            LocalCaptureProfileDefinition.CreateV2(
                fixture.Configuration with
                {
                    Rig = fixture.Configuration.Rig with { ControlPolicy = new CameraControlPolicy() }
                },
                AlwaysOpenDefinition("unspecified")),
            LocalCaptureProfileDefinition.CreateV2(
                fixture.Configuration with
                {
                    Rig = fixture.Configuration.Rig with
                    {
                        ControlPolicy = fixture.Configuration.Rig.ControlPolicy! with
                        {
                            AutoExposure = CameraFeatureDirective.Enabled,
                            AutoGain = CameraFeatureDirective.Disabled
                        }
                    }
                },
                AlwaysOpenDefinition("legacy-directives")),
            LocalCaptureProfileDefinition.CreateV2(
                fixture.Configuration,
                AlwaysOpenDefinition("legacy-schedule") with
                {
                    WeeklyWindows = [],
                    LegacyAlwaysOpen = true,
                    LegacySetpointProfileId = "legacy-schedule"
                })
        };

        _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.Runtime.Preview(legacy, 1));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Runtime.StageAsync(
            legacy, "stage-v1", initial.Version, "owner", null, CancellationToken.None)).ConfigureAwait(false);
        foreach (var (malformedCurrent, index) in malformedCurrentProfiles.Select((profile, index) => (profile, index)))
        {
            _ = Assert.ThrowsExactly<ArgumentException>(() => fixture.Runtime.Preview(malformedCurrent, 1));
            _ = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Runtime.StageAsync(
                malformedCurrent,
                $"stage-malformed-v2-{index}",
                initial.Version,
                "owner",
                null,
                CancellationToken.None)).ConfigureAwait(false);
        }

        var unchanged = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(initial.Version, unchanged.Version);
        Assert.IsNull(unchanged.PendingRevision);
    }

    [TestMethod]
    public async Task Activation_WaitsForPublicationBoundaryAndCancelsOldRevision()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var targetProfile = LocalCaptureProfileDefinition.CreateV2(
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
    public async Task Runner_PersistedV1GrantRetainsSolarDefaultsAndTransitions()
    {
        using var fixture = await RuntimeFixture.CreateAsync(legacyHostMetered: true).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var module = new TransitionCaptureModule(fixture.TimeProvider);
        var host = new MultiCaptureHostContext(
            fixture.Runtime.Snapshot!.Configuration,
            cancellation,
            captureCount: 2);
        var ephemeris = new DeclinationSequenceEphemeris(30, 0, -30);
        var runner = new CameraModuleRunner(
            module,
            host,
            fixture.TimeProvider,
            NullLogger.Instance,
            fixture.Admission,
            ephemeris,
            scheduleRuntimeCoordinator: fixture.Runtime);

        await runner.RunAsync(cancellation.Token).ConfigureAwait(false);

        Assert.HasCount(2, module.Requests);
        var firstSetpoint = module.Requests[0].RequestedSetpoint;
        var secondSetpoint = module.Requests[1].RequestedSetpoint;
        Assert.IsNotNull(firstSetpoint);
        Assert.IsNotNull(secondSetpoint);
        Assert.AreEqual(TimeSpan.FromSeconds(2), firstSetpoint.Exposure);
        Assert.AreEqual(20d, firstSetpoint.Gain);
        Assert.AreEqual(TimeSpan.FromSeconds(6), secondSetpoint.Exposure);
        Assert.AreEqual(50d, secondSetpoint.Gain);
        Assert.AreEqual(3, ephemeris.RequestCount);
        Assert.IsTrue(host.Submissions.All(static submission =>
            submission.CycleEvidence!.ScheduleAdmission!.Reason ==
            CaptureScheduleAdmissionReason.LegacyCompatibility));
        Assert.AreEqual(
            CaptureControlDecisionReason.SolarRegimeChanged,
            host.Submissions[0].CycleEvidence!.Decision.Reason);
        Assert.AreEqual(
            CaptureControlDecisionReason.SolarRegimeChanged,
            host.Submissions[1].CycleEvidence!.Decision.Reason);
    }

    [TestMethod]
    [DataRow(LocalCaptureProfileDefinition.LegacySchemaVersion)]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion)]
    public async Task PersistedLegacyProfile_GrantedIngressEvidenceUsesNormalizedEffectiveIdentity(
        string schemaVersion)
    {
        using var fixture = await RuntimeFixture.CreateAsync(
            persistedProfileSchemaVersion: schemaVersion).ConfigureAwait(false);
        var runtime = fixture.Runtime.Snapshot!;
        var expectedSha256 = LocalCaptureProfileContract.ComputeEffectiveSha256(runtime.Configuration);
        var expectedPipelineSchema = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
            ? CapturePipelineSchemaVersions.LegacyV1
            : CapturePipelineSchemaVersions.ExplicitV2;
        var expectedDependencyPolicy = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
            ? CapturePipelineDependencyPolicy.LegacyInference
            : CapturePipelineDependencyPolicy.RejectEnabledDependent;

        Assert.AreEqual(schemaVersion, runtime.Revision.Profile.SchemaVersion);
        Assert.AreEqual(expectedPipelineSchema, runtime.Configuration.Pipeline.SchemaVersion);
        Assert.AreEqual(expectedDependencyPolicy, runtime.Configuration.Pipeline.DependencyPolicy);
        Assert.AreEqual(expectedDependencyPolicy, runtime.Revision.Profile.DependencyPolicy);
        Assert.AreEqual(expectedSha256, runtime.Revision.ProfileSha256);
        var operatorState = await fixture.Runtime.GetOperatorStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expectedSha256, operatorState.ActiveRevision.ProfileSha256);

        var accessor = new CameraAgentConfigurationAccessor();
        accessor.SetConfiguration(runtime.Configuration);
        var fleet = new FleetStatusCollector(
            accessor,
            new RawIngressState(fixture.TimeProvider),
            new CaptureLaneState(fixture.TimeProvider, fixture.HostOptions),
            new CaptureProcessingState(),
            new ArtifactOutboxState(),
            new StoragePressureState(),
            new CaptureTelemetrySink(),
            new FleetRuntimeState(fixture.TimeProvider),
            fixture.TimeProvider,
            fixture.Runtime);
        var fleetReport = await fleet.CollectAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            fixture.TimeProvider.GetUtcNow().AddMinutes(-1),
            new FleetStatusOutboxSnapshot(0, 0, 0, 0, 0, 0, 0, null, fixture.TimeProvider.GetUtcNow()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expectedSha256, fleetReport.Configuration.ActiveLocalProfileSha256);

        var grant = await fixture.Runtime.WaitForGrantAsync(CancellationToken.None).ConfigureAwait(false);
        using var admission = await fixture.Admission.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        var confirmed = await fixture.Runtime.ConfirmGrantAsync(
            grant, $"ingress-{schemaVersion}", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(confirmed);
        var ingressUtc = fixture.TimeProvider.GetUtcNow() + confirmed.Profile.Exposure + TimeSpan.FromSeconds(2);
        using var ingress = CreateRawIngress(fixture, ingressUtc);
        var receipt = await ingress.AcceptAsync(
            runtime.Configuration,
            CreateGrantedSubmission(ingressUtc, confirmed),
            CancellationToken.None).ConfigureAwait(false);
        admission.MarkPublished();

        Assert.IsNotNull(receipt);
        Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
        Assert.AreEqual(
            expectedSha256,
            receipt.Manifest.Descriptor.CycleEvidence!.ScheduleAdmission!.LocalProfileSha256);
    }

    [TestMethod]
    public async Task LateActivationReplay_DoesNotReplaceNewerRuntimeRevision()
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var first = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.CreateV2(
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
            LocalCaptureProfileDefinition.CreateV2(
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
            LocalCaptureProfileDefinition.CreateV2(
                fixture.Configuration, AlwaysOpenDefinition("first-stage", gain: 2)),
            "stage-replay-first", initial.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);
        var second = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.CreateV2(
                fixture.Configuration, AlwaysOpenDefinition("second-stage", gain: 3)),
            "stage-replay-second", first.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);

        var replay = await fixture.Runtime.StageAsync(
            LocalCaptureProfileDefinition.CreateV2(
                fixture.Configuration, AlwaysOpenDefinition("first-stage", gain: 2)),
            "stage-replay-first", initial.Version, "owner", null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(first.Version, replay.Version);
        Assert.AreEqual(second.PendingRevision!.RevisionId, fixture.Runtime.Snapshot!.PendingRevisionId);
        Assert.AreEqual(
            second.PendingRevision.RevisionId,
            (await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).PendingRevision!.RevisionId);
    }

    [TestMethod]
    [DataRow(LocalCaptureProfileDefinition.LegacySchemaVersion)]
    [DataRow(LocalCaptureProfileDefinition.CurrentSchemaVersion)]
    public async Task RestoredOperatorProfile_PreUpgradeStageFromBasisReplaysBeforeStrictValidation(
        string schemaVersion)
    {
        using var fixture = await RuntimeFixture.CreateAsync().ConfigureAwait(false);
        var initial = await fixture.Store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var basisRevisionId = initial.ActiveRevision.RevisionId;
        const string idempotencyKey = "operator-pre-upgrade-stage";
        const string actor = "owner";
        const string reason = "pre-upgrade";
        long? expectedVersion = initial.Version;
        var schedule = AlwaysOpenDefinition("upgrade");
        var staged = await fixture.Runtime.StageFromBasisAsync(
            LocalCaptureProfileDefinition.CreateV2(fixture.Configuration, schedule),
            basisRevisionId,
            idempotencyKey,
            expectedVersion,
            actor,
            reason,
            CancellationToken.None).ConfigureAwait(false);
        var legacyConfiguration = fixture.Configuration with
        {
            Rig = fixture.Configuration.Rig with
            {
                ControlPolicy = new CameraControlPolicy
                {
                    AutoExposure = CameraFeatureDirective.Enabled,
                    AutoGain = CameraFeatureDirective.Disabled
                }
            }
        };
        var persistedProfile = schemaVersion == LocalCaptureProfileDefinition.LegacySchemaVersion
            ? LocalCaptureProfileDefinition.Create(legacyConfiguration, schedule)
            : LocalCaptureProfileDefinition.CreateV2(legacyConfiguration, schedule);
        var profileJson = System.Text.Encoding.UTF8.GetBytes(
            CaptureContractJson.SerializeToElement(persistedProfile).GetRawText());
        var commandSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            CommandKind = "stage",
            Payload = new { Profile = persistedProfile, BasisRevisionId = basisRevisionId },
            expectedVersion,
            actor,
            reason
        });
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.Root, "journal", "raw-ingress.db")}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE capture_schedule_revisions
                SET profile_json = $json, profile_sha256 = $profile_sha, schedule_sha256 = $schedule_sha
                WHERE revision_id = $revision;
                UPDATE capture_schedule_commands
                SET payload_sha256 = $command_sha
                WHERE idempotency_key = $operation;
                """;
            command.Parameters.AddWithValue("$json", profileJson);
            command.Parameters.AddWithValue(
                "$profile_sha",
                LocalCaptureProfileContract.ComputePersistedRevisionSha256(persistedProfile));
            command.Parameters.AddWithValue("$schedule_sha", CaptureScheduleContract.ComputeSha256(schedule));
            command.Parameters.AddWithValue("$revision", staged.PendingRevision!.RevisionId);
            command.Parameters.AddWithValue("$command_sha", commandSha256);
            command.Parameters.AddWithValue("$operation", idempotencyKey);
            Assert.AreEqual(2, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var basis = await fixture.Store.GetRevisionAsync(basisRevisionId, CancellationToken.None).ConfigureAwait(false);
        var restored = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
            CameraAgentScheduleOperatorProjection.Sanitize(persistedProfile),
            basis.Profile);

        var replay = await fixture.Runtime.StageFromBasisAsync(
            restored,
            basisRevisionId,
            idempotencyKey,
            expectedVersion,
            actor,
            reason,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(staged.Version, replay.Version);
        Assert.AreEqual(schemaVersion, replay.PendingRevision!.Profile.SchemaVersion);
        var mismatch = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
            CameraAgentScheduleOperatorProjection.Sanitize(
                persistedProfile with { Schedule = AlwaysOpenDefinition("mismatch") }),
            basis.Profile);
        _ = await Assert.ThrowsAsync<CaptureScheduleStoreConflictException>(() =>
            fixture.Runtime.StageFromBasisAsync(
                mismatch,
                basisRevisionId,
                idempotencyKey,
                expectedVersion,
                actor,
                reason,
                CancellationToken.None)).ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Runtime.StageFromBasisAsync(
            restored,
            basisRevisionId,
            $"{idempotencyKey}-new",
            staged.Version,
            actor,
            reason,
            CancellationToken.None)).ConfigureAwait(false);
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

        internal string Root => _root;

        internal IOptions<CameraAgentHostOptions> HostOptions { get; private init; } = null!;

        internal static async Task<RuntimeFixture> CreateAsync(
            ICameraModuleConfigurationValidator? moduleConfigurationValidator = null,
            bool initializeRuntime = true,
            bool legacyHostMetered = false,
            string? persistedProfileSchemaVersion = null)
        {
            legacyHostMetered |= persistedProfileSchemaVersion is not null;
            var root = Path.Combine(Path.GetTempPath(), "hvo-schedule-runtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var now = new DateTimeOffset(2025, 1, 13, 1, 0, 0, TimeSpan.Zero);
            var timeProvider = new FixedTimeProvider(now);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
            var rawIngress = new JournalInitializer(root);
            var location = DeploymentLocationSnapshot.Create(
                "test-location", 2, "test", null, DateTimeOffset.UnixEpoch, null,
                legacyHostMetered ? 90 : 35,
                legacyHostMetered ? 0 : -114,
                1000,
                "UTC");
            var pipeline = legacyHostMetered
                ? new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(10),
                    20,
                    80,
                    new ExposureEnvelope(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(16),
                        1,
                        100,
                        new ExposureDefaults(TimeSpan.FromSeconds(2), 20),
                        new ExposureDefaults(TimeSpan.FromSeconds(10), 80),
                        0.5,
                        TwilightDefaults: new ExposureDefaults(TimeSpan.FromSeconds(6), 50)),
                    CadenceMode: CaptureCadenceMode.Continuous)
                : new PipelineExposureProfile(
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1);
            var controlPolicy = legacyHostMetered
                ? new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.HostMetered,
                    GainControl = AutomaticControlOwnership.HostMetered,
                    Metering = new CaptureMeteringPolicy
                    {
                        XStride = 1,
                        YStride = 1,
                        UseImageCircle = false
                    },
                    SolarRegimes = new CaptureSolarRegimePolicy
                    {
                        DayAltitudeThresholdDegrees = 20,
                        NightAltitudeThresholdDegrees = -20
                    }
                }
                : new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                };
            var persistedSchedule = legacyHostMetered
                ? new CaptureScheduleDefinition(
                    "capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile(
                        "legacy-night",
                        TimeSpan.FromSeconds(10),
                        80,
                        TimeSpan.FromSeconds(1),
                        CaptureCadenceMode.Continuous)],
                    [],
                    LegacyAlwaysOpen: true,
                    LegacySetpointProfileId: "legacy-night")
                : AlwaysOpenDefinition("initial");
            var schedule = AlwaysOpenDefinition("initial");
            var configuration = new CameraModuleConfig(
                new ObservatoryLocation(
                    legacyHostMetered ? 90 : 35,
                    legacyHostMetered ? 0 : -114,
                    1000,
                    "UTC"),
                new CameraModuleDescriptor("test"),
                new CameraRigConfig(
                    new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                    new OpticsProfile("Perspective", 50, 10, 0),
                    new RigOrientation(90, 0, 0),
                    pipeline,
                    controlPolicy),
                CapturePipelineConfig.Empty,
                AgentId: "test-agent")
            {
                DeploymentLocation = location,
                Schedule = schedule
            };
            if (legacyHostMetered)
            {
                using var seedStore = new SqliteCaptureScheduleStore(rawIngress, options, timeProvider);
                var initial = await seedStore.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                var persistedConfiguration = configuration with
                {
                    Rig = configuration.Rig with
                    {
                        ControlPolicy = controlPolicy with
                        {
                            ExposureControl = AutomaticControlOwnership.Unspecified,
                            GainControl = AutomaticControlOwnership.Unspecified,
                            AutoExposure = CameraFeatureDirective.Enabled,
                            AutoGain = CameraFeatureDirective.Enabled
                        }
                    }
                };
                var profile = persistedProfileSchemaVersion == LocalCaptureProfileDefinition.CurrentSchemaVersion
                    ? LocalCaptureProfileDefinition.CreateV2(persistedConfiguration, persistedSchedule)
                    : LocalCaptureProfileDefinition.Create(persistedConfiguration, persistedSchedule);
                var profileJson = System.Text.Encoding.UTF8.GetBytes(
                    CaptureContractJson.SerializeToElement(profile).GetRawText());
                using var connection = new SqliteConnection(
                    $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE capture_schedule_revisions
                    SET profile_json = $json, profile_sha256 = $profile_sha, schedule_sha256 = $schedule_sha
                    WHERE revision_id = $id;
                    """;
                command.Parameters.AddWithValue("$json", profileJson);
                command.Parameters.AddWithValue(
                    "$profile_sha",
                    LocalCaptureProfileContract.ComputePersistedRevisionSha256(profile));
                command.Parameters.AddWithValue("$schedule_sha", CaptureScheduleContract.ComputeSha256(profile.Schedule));
                command.Parameters.AddWithValue("$id", initial.ActiveRevision.RevisionId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var store = new SqliteCaptureScheduleStore(rawIngress, options, timeProvider);
            var telemetry = new CaptureControlTelemetry();
            var admission = new CaptureAdmissionCoordinator(
                rawIngress, options, timeProvider, telemetry);
            var rawState = new RawIngressState(timeProvider);
            rawState.Set(RawIngressAvailability.Accepting, "accepting");
            var laneState = new CaptureLaneState(timeProvider, options);
            laneState.Update([]);
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
                root, telemetry, store, admission, runtime, configuration, location, timeProvider)
            {
                HostOptions = options
            };
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

    private static RawCaptureIngress CreateRawIngress(RuntimeFixture fixture, DateTimeOffset ingressUtc)
    {
        var timeProvider = new FixedTimeProvider(ingressUtc);
        var state = new RawIngressState(timeProvider);
        return new RawCaptureIngress(
            fixture.HostOptions,
            new UnlimitedCapacityProvider(),
            state,
            timeProvider,
            new RawIngressTelemetry(state),
            NullLogger<RawCaptureIngress>.Instance,
            new NullRawIngressFaultInjector());
    }

    private static CaptureLoopSubmission CreateGrantedSubmission(
        DateTimeOffset ingressUtc,
        CaptureScheduleGrant grant)
    {
        var exposure = grant.Profile.Exposure;
        var gain = grant.Profile.Gain;
        var exposureStartedUtc = ingressUtc - exposure - TimeSpan.FromSeconds(1);
        var exposureEndedUtc = ingressUtc.AddSeconds(-1);
        var frame = new CameraFrame(
            exposureEndedUtc,
            2,
            2,
            CameraPixelFormat.Mono16,
            new byte[8],
            new FrameMetadata(exposure, gain, double.NaN, "schedule-evidence"),
            4);
        var setpoint = new CaptureSetpoint(exposure, gain, null, null);
        var result = new CaptureResult(
            frame,
            setpoint,
            TimeSpan.Zero,
            CaptureMode.Still,
            false)
        {
            AcquisitionTiming = new CaptureAcquisitionTiming(
                exposureStartedUtc,
                exposureEndedUtc,
                exposureEndedUtc)
        };
        return new CaptureLoopSubmission(
            new CaptureRequest(exposureStartedUtc.AddSeconds(-1), exposure, CaptureMode.Still, setpoint),
            result,
            exposureStartedUtc.AddMilliseconds(-100),
            TimeSpan.Zero,
            TimeSpan.Zero)
        {
            CycleEvidence = new CaptureCycleEvidence(
                CaptureCadenceMode.Continuous,
                CaptureStartReason.Initial,
                AutomaticControlOwnership.HostMetered,
                AutomaticControlOwnership.HostMetered,
                CaptureSolarRegime.Night,
                exposureStartedUtc.AddMilliseconds(-100),
                null,
                new CaptureMeteringEvidence(
                    exposureEndedUtc.AddMilliseconds(10),
                    exposureEndedUtc.AddMilliseconds(20),
                    0,
                    0,
                    0,
                    0,
                    null,
                    CaptureMeteringOutcome.NoFrame),
                new CaptureControlDecisionEvidence(
                    exposureEndedUtc.AddMilliseconds(100),
                    exposureEndedUtc.AddMilliseconds(200),
                    exposure,
                    gain,
                    exposure,
                    gain,
                    CaptureControlDecisionReason.NoSample),
                exposureEndedUtc.AddMilliseconds(300))
            {
                ScheduleAdmission = grant.Evidence
            }
        };
    }

    private sealed class UnlimitedCapacityProvider : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => new(long.MaxValue, long.MaxValue);
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

    private sealed class TransitionCaptureModule(TimeProvider timeProvider) : ICameraModule, ICameraSetpointController
    {
        internal List<CaptureRequest> Requests { get; } = [];

        public string Id => "transition";

        public string DisplayName => "Transition";

        public string ModuleType => "test";

        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
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

        public ValueTask<DateTimeOffset> ApplySetpointAsync(
            CaptureSetpoint setpoint,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(timeProvider.GetUtcNow());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeclinationSequenceEphemeris(params double[] declinations) : IPlanetEphemeris
    {
        public string ModelVersion => "declination-sequence-test-v1";

        internal int RequestCount { get; private set; }

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            Assert.AreEqual(SolarSystemBody.Sun, body);
            Assert.IsTrue(RequestCount < declinations.Length);
            return new SolarSystemPosition(new EquatorialPoint(0, declinations[RequestCount++]), -26.74);
        }
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

    private sealed class MultiCaptureHostContext(
        CameraModuleConfig configuration,
        CancellationTokenSource cancellation,
        int captureCount) : ICaptureHostContext
    {
        public CameraModuleConfig Configuration { get; } = configuration;

        internal List<CaptureLoopSubmission> Submissions { get; } = [];

        public async ValueTask PublishAsync(
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            if (Submissions.Count == captureCount)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
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
