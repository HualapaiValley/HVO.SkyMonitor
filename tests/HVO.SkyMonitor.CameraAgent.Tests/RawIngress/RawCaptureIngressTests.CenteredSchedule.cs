using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Modules.Zwo;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task CenteredScheduleRetiresOldExpansionsAndReopensAdmissionAndReplay()
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var start = Timestamp(8).AddMinutes(5);
        var clock = new ExposureTestTimeProvider(start);
        var config = CenteredContinuousConfiguration(start);
        var schedule = config.Schedule!;
        config = config with
        {
            Schedule = schedule with
            {
                WeeklyWindows = Enum.GetValues<DayOfWeek>().Select(day =>
                    schedule.WeeklyWindows[0] with { Id = $"morning-{day}", Day = day }).ToArray()
            }
        };
        try
        {
            CenteredRawEvidence original;
            CaptureScheduleRuntimeSnapshot first;
            CaptureScheduleRuntimeSnapshot? day40 = null;
            CaptureScheduleRuntimeSnapshot previous;
            CaptureScheduleRuntimeSnapshot latest;
            long expansions;
            using (var setup = new CenteredFixture(root, timeProvider: clock))
            using (var telemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry))
            using (var store = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock))
            {
                var lanes = new CaptureLaneState(clock, setup.Options);
                lanes.Update([]);
                using var runtime = new CaptureScheduleRuntimeCoordinator(store, admission, setup.RawState, lanes, setup.Factory, clock);
                await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
                await admission.InitializeAsync(token).ConfigureAwait(false);
                first = await runtime.InitializeAsync(config, token).ConfigureAwait(false);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var module = new CenteredRunnerModule((_, request, _) => Task.FromResult(CompleteScheduledCapture(clock, request)));
                await using var moduleScope = module.ConfigureAwait(false);
                var host = new CaptureHostContext(config, setup.Ingress, new CenteredTraceNotifier { Committed = stop.Cancel });
                await new CameraModuleRunner(module, host, clock, NullLogger.Instance, admission,
                    new AstronomyEnginePlanetEphemeris(), telemetry, scheduleRuntimeCoordinator: runtime)
                    .RunAsync(stop.Token).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var lane = setup.Policy.Definitions.Single(item => item.Name == "standard");
                var work = await setup.Ingress.ClaimAsync(lane, "live", config, token).ConfigureAwait(false);
                var raw = (await setup.Ingress.ReadCurrentAsync(new(config.AgentId!, config.CenteredExposure!.CameraId), token)
                    .ConfigureAwait(false)).AcquisitionHead!.Exposure;
                original = (await setup.Ingress.ReadCenteredRawAsync(raw, token).ConfigureAwait(false))!;
                Assert.IsNotNull(work);
                using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
                var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, storage, setup.Telemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance);
                using var handler = new StandardCaptureLaneHandler(setup.Factory, persistence, setup.Telemetry,
                    NullLogger<StandardCaptureLaneHandler>.Instance, setup.Ingress, setup.Options, timeProvider: clock);
                var result = await handler.HandleAsync(work.Context, token).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await setup.Ingress.CompleteAsync(work, token).ConfigureAwait(false);

                for (var day = 1; day <= 50; day++)
                {
                    clock.Advance(TimeSpan.FromDays(1));
                    await runtime.GetOperatorStateAsync(token).ConfigureAwait(false);
                    if (day is 10 or 25)
                        await SwitchAsync(runtime, store, day).ConfigureAwait(false);
                    if (day == 40)
                        day40 = runtime.Snapshot;
                    if (day is 10 or 25 or 40 or 50)
                        _ = await ReadExpansionCountAsync(day).ConfigureAwait(false);
                }
                previous = runtime.Snapshot!;
                await SwitchAsync(runtime, store, 50).ConfigureAwait(false);
                latest = runtime.Snapshot!;
                expansions = await ReadExpansionCountAsync(50).ConfigureAwait(false);
                Assert.IsLessThanOrEqualTo(68L, expansions, "Keep at most 64 recent and four active or pending expansions.");
                Assert.IsNull(await ReadAdmissionAsync(store, first, start).ConfigureAwait(false));
                using var journal = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.AreEqual(0L, await ScalarLongAsync(journal, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
            }

            using var reopened = new CenteredFixture(root, timeProvider: clock);
            using var restartedTelemetry = new CaptureControlTelemetry();
            using var restartedAdmission = new CaptureAdmissionCoordinator(reopened.Ingress, reopened.Options, clock, restartedTelemetry);
            using var restartedStore = new SqliteCaptureScheduleStore(reopened.Ingress, reopened.Options, clock);
            await restartedAdmission.InitializeAsync(token).ConfigureAwait(false);
            var reopenedLanes = new CaptureLaneState(clock, reopened.Options);
            reopenedLanes.Update([]);
            using var restarted = new CaptureScheduleRuntimeCoordinator(restartedStore, restartedAdmission, reopened.RawState,
                reopenedLanes, reopened.Factory, clock);
            var current = await restarted.InitializeAsync(config, token).ConfigureAwait(false);
            Assert.AreEqual(latest.Revision.RevisionId, current.Revision.RevisionId);
            Assert.AreEqual(latest.AdmissionPolicy.ExpansionSha256, current.AdmissionPolicy.ExpansionSha256);
            Assert.IsLessThanOrEqualTo(expansions, await ReadExpansionCountAsync(50).ConfigureAwait(false));
            Assert.AreEqual(previous.AdmissionPolicy.ExpansionSha256,
                (await ReadAdmissionAsync(restartedStore, previous, clock.GetUtcNow()).ConfigureAwait(false))?.ExpansionSha256);
            Assert.IsNotNull(day40);
            Assert.AreEqual(day40.AdmissionPolicy.ExpansionSha256,
                (await ReadAdmissionAsync(restartedStore, day40, start.AddDays(40)).ConfigureAwait(false))?.ExpansionSha256);

            var state = await restartedStore.GetSnapshotAsync(token).ConfigureAwait(false);
            await restarted.RollbackAsync(previous.Revision.RevisionId, "rollback-recent", state.Version, "owner", null, token)
                .ConfigureAwait(false);
            var grant = await restarted.WaitForGrantAsync(token).ConfigureAwait(false);
            Assert.AreEqual(previous.AdmissionPolicy.ExpansionSha256, grant.Evidence.ExpansionSha256);
            Assert.AreEqual(previous.Revision.RevisionId, grant.Evidence.ScheduleRevisionId);

            var historical = original.Manifest.Descriptor;
            await reopened.GraphOperations.SubmitReplayAsync(
                new(historical.Capture.CaptureId, original.Manifest.CaptureGraphRevisionId!, historical.Artifact.ArtifactId),
                "retired-expansion-replay", "owner", token).ConfigureAwait(false);
            var replay = await reopened.Store.ClaimReplayAsync("replay", token).ConfigureAwait(false);
            Assert.IsNotNull(replay);
            Assert.AreEqual(original.ManifestSha256, replay.RawCapture.CommittedManifestSha256);
            var replayed = replay.RawCapture.Manifest.Descriptor.CenteredAcquisition!.ScheduleAdmission!;
            Assert.AreEqual(historical.CenteredAcquisition!.ScheduleAdmission, replayed);
            Assert.AreEqual(first.AdmissionPolicy.ExpansionSha256, replayed.ExpansionSha256);
            using var replayStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var replayPersistence = new CaptureProcessingPersistence(reopened.Options, reopened.Store, replayStorage,
                reopened.Telemetry, NullLogger<CaptureProcessingPersistence>.Instance);
            var execution = new ProcessingExecutionContext(replay.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                replay.Execution.GraphRevisionId, replay.Execution.LocalPlanIdentitySha256, false, replay.WorkId,
                replay.LeaseToken, replay.LeaseOwner, replay.Execution.DeadlineUtc);
            var graph = reopened.Factory.CreateRetainedGraph(replay.Configuration);
            try
            {
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(
                    new(replay.Configuration, replay.Submission, replay.RawCapture, replay.WorkId, replay.LeaseToken, execution),
                    graph, replayPersistence, reopened.Telemetry, replay.Execution.AttemptCount, NullLogger.Instance, token,
                    durableAttempt: replay.ClaimCount, timeProvider: clock).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var completed = await reopened.Store.CompleteReplayAsync(replay, result, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status);
            }
            finally { graph.DisposeSteps(); }
        }
        finally { DeleteRoot(root); }

        async Task SwitchAsync(CaptureScheduleRuntimeCoordinator runtime, SqliteCaptureScheduleStore store, int day)
        {
            var changed = config.Schedule! with
            {
                SetpointProfiles = [config.Schedule.SetpointProfiles[0] with { Gain = day }]
            };
            var state = await store.GetSnapshotAsync(token).ConfigureAwait(false);
            var staged = await runtime.StageAsync(LocalCaptureProfileDefinition.CreateV2(config, changed),
                $"stage-{day}", state.Version, "owner", null, token).ConfigureAwait(false);
            await runtime.ActivateAsync(staged.PendingRevision!.RevisionId, $"activate-{day}", staged.Version, "owner", null, token)
                .ConfigureAwait(false);
        }

        async Task<long> ReadExpansionCountAsync(int day)
        {
            using var journal = await OpenJournalAsync(root).ConfigureAwait(false);
            var count = await ScalarLongAsync(journal, "SELECT COUNT(*) FROM capture_schedule_expansions;").ConfigureAwait(false);
            var intervals = await ScalarLongAsync(journal, "SELECT COUNT(*) FROM capture_schedule_intervals;").ConfigureAwait(false);
            var pages = await ScalarLongAsync(journal, "PRAGMA page_count;").ConfigureAwait(false);
            var free = await ScalarLongAsync(journal, "PRAGMA freelist_count;").ConfigureAwait(false);
            var pageSize = await ScalarLongAsync(journal, "PRAGMA page_size;").ConfigureAwait(false);
            Console.WriteLine($"Day {day}: {count} expansions, {intervals} intervals, {(pages - free) * pageSize} bytes in live journal pages.");
            return count;
        }

        Task<CaptureSchedulePreview?> ReadAdmissionAsync(SqliteCaptureScheduleStore store,
            CaptureScheduleRuntimeSnapshot snapshot, DateTimeOffset utc)
            => store.TryReadPreviewAsync(snapshot.Revision, snapshot.Location, utc,
                CaptureScheduleIntervalExpander.AdmissionAlgorithmVersion, snapshot.AdmissionPolicy.PreviewStartUtc, token);
    }

    [TestMethod]
    public async Task CenteredZwoOpaqueInputRecordsUnavailableMeteringAndKeepsRequestedControls()
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var token = deadline.Token;
        try
        {
            var start = Timestamp(8).AddMinutes(5);
            var clock = new ExposureTestTimeProvider(start);
            var centered = CenteredContinuousConfiguration(start);
            var zwo = ZwoAsiCameraModuleTests.CreateCenteredTransportTestConfig();
            var config = zwo with
            {
                AgentId = centered.AgentId,
                DeploymentLocation = centered.DeploymentLocation,
                CenteredExposure = centered.CenteredExposure,
                Schedule = centered.Schedule,
                Pipeline = centered.Pipeline with { Steps = [] },
                Rig = zwo.Rig with
                {
                    Pipeline = zwo.Rig.Pipeline with
                    {
                        Envelope = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), 0, 82,
                            new(TimeSpan.FromMilliseconds(10), 5), new(TimeSpan.FromMilliseconds(100), 40), 0.65)
                    },
                    ControlPolicy = new()
                    {
                        ExposureControl = AutomaticControlOwnership.HostMetered,
                        GainControl = AutomaticControlOwnership.HostMetered,
                        Metering = new() { UseImageCircle = false }
                    }
                }
            };
            using var setup = new CenteredFixture(root, timeProvider: clock);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry);
            using var store = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock);
            var lanes = new CaptureLaneState(clock, setup.Options);
            lanes.Update([]);
            using var runtime = new CaptureScheduleRuntimeCoordinator(store, admission, setup.RawState, lanes, setup.Factory, clock);
            await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
            await admission.InitializeAsync(token).ConfigureAwait(false);
            config = (await runtime.InitializeAsync(config, token).ConfigureAwait(false)).Configuration;

            var camera = new CameraKey(config.AgentId!, config.CenteredExposure!.CameraId);
            var owner = (await setup.Ingress.FenceCenteredOwnerAsync(camera.AgentId, camera.CameraId,
                CaptureContractJson.ComputeCanonicalJsonSha256(config), token).ConfigureAwait(false))!;
            var native = new ZwoAsiCameraModuleTests.FakeAsiNativeApi
            {
                ExposureStatuses = new([AsiExposureStatus.Idle, AsiExposureStatus.Idle, AsiExposureStatus.Success,
                    AsiExposureStatus.Idle, AsiExposureStatus.Success]),
                ExposureStatusObserved = () => clock.Advance(TimeSpan.FromMilliseconds(10))
            };
            var module = ZwoAsiCameraModuleTests.Module(native, clock);
            await using var moduleScope = module.ConfigureAwait(false);
            string? deviceIdentity = null;
            await ((ICameraBoundExposureCapture)module).InitializeBoundAsync(config, async (verified, ct) =>
            {
                await setup.Ingress.FenceCenteredDeviceAsync(owner, verified, ct).ConfigureAwait(false);
                deviceIdentity = verified;
            }, token).ConfigureAwait(false);
            await setup.Ingress.ActivateCenteredOwnerAsync(owner, token).ConfigureAwait(false);

            var commits = 0;
            var host = new CaptureHostContext(config, setup.Ingress, new CenteredTraceNotifier
            {
                Committed = () => { if (++commits == 2) stop.Cancel(); }
            }, centeredDeviceIdentity: deviceIdentity, centeredOwner: owner);
            await new CameraModuleRunner(module, host, clock, NullLogger.Instance, admission,
                new AstronomyEnginePlanetEphemeris(), telemetry, scheduleRuntimeCoordinator: runtime)
                .RunAsync(stop.Token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(2, commits);
            Assert.AreEqual(2, native.Calls.Count(call => call == "Set:Exposure:10000"));
            Assert.AreEqual(2, native.Calls.Count(call => call == "Set:Gain:5"));

            var head = (await setup.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false)).AcquisitionHead!;
            var raw = (await setup.Ingress.ReadCenteredRawAsync(head.Exposure, token).ConfigureAwait(false))!;
            var layout = raw.Manifest.Descriptor.Layout;
            Assert.AreEqual(FrameStoredCodeTransform.OpaqueContainerV1, layout.StoredCodeTransform);
            Assert.AreEqual(65535d, layout.WhiteLevel);
            Assert.IsFalse(FrameWhiteThreshold.TryResolveStored(layout, out _, out var reason));
            Assert.AreEqual(FrameWhiteThreshold.OpaqueAdcReason, reason);
            var requested = raw.Manifest.Descriptor.CenteredAcquisition!.Module!.RequestedSetpoint;
            Assert.IsNotNull(requested);
            Assert.AreEqual(new CaptureSetpoint(TimeSpan.FromMilliseconds(10), 5, null, 15), requested);
            Assert.AreEqual(requested.Exposure, raw.Manifest.Descriptor.Controls.EffectiveExposure);
            Assert.AreEqual(requested.Gain, raw.Manifest.Descriptor.Controls.EffectiveGain);

            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            using var check = db.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM centered_exposure_attempts target
                JOIN centered_exposure_attempts source ON source.run_id = target.run_id
                  AND source.ordinal = 1 AND source.capture_id = target.source_capture_id
                WHERE target.ordinal = 2 AND target.status = 'observed' AND target.reason = $reason
                  AND target.metering_json IS NULL AND target.requested_json = source.requested_json;
                """;
            check.Parameters.AddWithValue("$reason", FrameWhiteThreshold.OpaqueAdcReason);
            Assert.AreEqual(1L, await check.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    public async Task CenteredContinuousScheduleReopensSameWindowWithConfirmedAdmissionVersion(
        bool pauseAndResume, bool missedBlackout, bool crossMidnight)
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            var firstUtc = crossMidnight ? Timestamp(23).AddMinutes(55) : Timestamp(8).AddMinutes(5);
            var clock = new ExposureTestTimeProvider(firstUtc);
            var config = CenteredContinuousConfiguration(clock.GetUtcNow());
            var blackoutStart = crossMidnight ? Timestamp(12).AddDays(1) : firstUtc.AddSeconds(1);
            var blackoutEnd = blackoutStart + (crossMidnight ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(2));
            if (crossMidnight)
                config = config with
                {
                    Schedule = config.Schedule! with
                    {
                        WeeklyWindows = [new("same-window", firstUtc.DayOfWeek,
                            new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(20, 0)),
                            new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(4, 0),
                                DayOffset: 1, Offset: TimeSpan.FromDays(1)), "continuous")],
                        Blackouts = [new("missed", blackoutStart, blackoutEnd)]
                    }
                };
            else if (missedBlackout)
                config = config with
                {
                    Schedule = config.Schedule! with
                    {
                        Blackouts = [new("missed", blackoutStart, blackoutEnd)]
                    }
                };
            var freshTarget = missedBlackout || pauseAndResume;
            var captures = new List<(ExposureAttemptKey Key, CaptureRequest Request, CaptureResult Result)>();
            var operatorStates = new List<CaptureScheduleOperatorState>();
            ExposureKey first;
            long version;
            using (var setup = new CenteredFixture(root, timeProvider: clock))
            using (var telemetry = new CaptureControlTelemetry())
            using (var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry))
            {
                await RunOneAsync(setup, telemetry, admission).ConfigureAwait(false);
                var runtime = await setup.Ingress.ReadCurrentAsync(captures[0].Key.Camera, token).ConfigureAwait(false);
                first = runtime.AcquisitionHead!.Exposure;
                version = admission.Snapshot.Version;
                if (pauseAndResume)
                {
                    await admission.PauseAsync("pause-between-grants", version, "fixture", null, token).ConfigureAwait(false);
                    await admission.ResumeAsync("resume-same-window", admission.Snapshot.Version, "fixture", null, token)
                        .ConfigureAwait(false);
                    Assert.AreNotEqual(version, admission.Snapshot.Version);
                }
                else
                {
                    var noOp = await admission.ResumeAsync("resume-no-op", version, "fixture", null, token).ConfigureAwait(false);
                    Assert.IsFalse(noOp.Changed);
                    Assert.AreEqual(version, noOp.Version);
                }
            }

            if (crossMidnight || missedBlackout)
            {
                var resumeUtc = crossMidnight
                    ? missedBlackout ? Timestamp(2).AddDays(2) : Timestamp(0).AddDays(1).AddMinutes(5)
                    : blackoutEnd;
                clock.Advance(resumeUtc - clock.GetUtcNow());
            }
            using var restarted = new CenteredFixture(root, timeProvider: clock);
            using var afterTelemetry = new CaptureControlTelemetry();
            using var afterAdmission = new CaptureAdmissionCoordinator(restarted.Ingress, restarted.Options, clock, afterTelemetry);
            var historical = await restarted.Ingress.ReadCenteredRawAsync(first, token).ConfigureAwait(false);
            Assert.IsNotNull(historical);
            Assert.AreEqual(version, historical.Run.AdmissionStateVersion);
            var recorded = historical.Manifest.Descriptor.CenteredAcquisition!;
            Assert.AreEqual(TimeSpan.Zero, recorded.EffectiveInterval);
            Assert.AreEqual(TimeSpan.Zero, recorded.Module!.RequestedInterval);
            Assert.AreEqual(15d, recorded.Module.RequestedSetpoint!.TargetFps);
            Assert.AreEqual(7d, recorded.Module.ReportedSetpoint.TargetFps);
            Assert.IsNull(historical.Manifest.Descriptor.CycleEvidence);
            var gallery = new SqliteCameraAgentGallery(restarted.Options, restarted.Store);
            var detail = (await gallery.GetCaptureAsync(first.CaptureId, token).ConfigureAwait(false))?.Detail;
            Assert.IsNotNull(detail);
            Assert.AreEqual("continuous", detail.Schedule!.SetpointProfileId);
            Assert.AreEqual(recorded.ScheduleAdmission!.ScheduleRevisionId, detail.Schedule.RevisionId);
            Assert.AreEqual(CaptureCadenceMode.Continuous, detail.CaptureProfile!.CadenceMode);
            Assert.AreEqual(TimeSpan.Zero, detail.CaptureProfile.EffectiveInterval);
            if (pauseAndResume)
            {
                var authenticated = await ReadAuthenticatedGalleryAsync(gallery, first.CaptureId, token).ConfigureAwait(false);
                Assert.AreEqual(detail.Schedule, authenticated.Schedule);
                Assert.AreEqual(detail.CaptureProfile, authenticated.CaptureProfile);
            }
            var facts = FrameFactsRetainedContext.Create(new(historical.Manifest.Descriptor));
            Assert.AreEqual(FrameFactsValue.FromText("continuous"), facts.Tokens.Get("acquisition.scheduleProfile").Value);
            Assert.AreEqual(FrameFactsValue.FromText("Continuous"), facts.Tokens.Get("acquisition.cadenceMode").Value);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(historical.Manifest.Descriptor),
                facts.Provenance["acquisition.cadenceMode"].SourceIdentity);

            await RunOneAsync(restarted, afterTelemetry, afterAdmission).ConfigureAwait(false);
            Assert.HasCount(2, captures);
            Assert.AreEqual(captures[0].Request.RequestedSetpoint, captures[1].Request.RequestedSetpoint);
            Assert.AreEqual(!freshTarget, captures[0].Key.RunId == captures[1].Key.RunId);
            Assert.AreEqual(freshTarget ? 1L : 2L, captures[1].Key.Ordinal);
            Assert.AreNotEqual(captures[0].Key.AttemptId, captures[1].Key.AttemptId);
            var subsequent = await restarted.Ingress.ReadCurrentAsync(first.Camera, token).ConfigureAwait(false);
            var second = await restarted.Ingress.ReadCenteredRawAsync(subsequent.AcquisitionHead!.Exposure, token).ConfigureAwait(false);
            Assert.AreEqual(recorded.ScheduleAdmission.IntervalId, second!.Manifest.Descriptor.RetainedScheduleAdmission!.IntervalId);
            Assert.AreEqual(afterAdmission.Snapshot.Version, second.Run.AdmissionStateVersion);
            var secondSchedule = second.Manifest.Descriptor.RetainedScheduleAdmission!;
            Assert.AreEqual(recorded.ScheduleAdmission.EffectiveEndUtc, operatorStates[0].Decision.NextTransitionUtc);
            Assert.AreEqual(secondSchedule.EffectiveEndUtc, operatorStates[1].Decision.NextTransitionUtc);
            Assert.AreEqual(CaptureScheduleIntervalExpander.AlgorithmVersion, operatorStates[1].Preview.ExpansionAlgorithmVersion);
            if (crossMidnight)
            {
                Assert.AreNotEqual(operatorStates[0].Preview.PreviewStartUtc, operatorStates[1].Preview.PreviewStartUtc);
                Assert.AreEqual(missedBlackout ? Timestamp(4).AddDays(2) : blackoutStart, secondSchedule.EffectiveEndUtc);
                Assert.AreEqual(blackoutStart, recorded.ScheduleAdmission.EffectiveEndUtc);
            }
            else
            {
                Assert.AreEqual(recorded.ScheduleAdmission.ExpansionSha256, secondSchedule.ExpansionSha256);
            }
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            var retirement = missedBlackout ? "schedule-grant-changed" : "schedule-admission-interrupted";
            Assert.AreEqual(freshTarget ? 1L : 0L, await ScalarLongAsync(db, $"""
                SELECT COUNT(*) FROM centered_exposure_attempts
                WHERE ordinal = 2 AND status = 'expired' AND reason = '{retirement}'
                  AND source_capture_id IS NOT NULL AND capture_id IS NULL;
                """).ConfigureAwait(false));
            if (missedBlackout)
            {
                Assert.AreEqual(blackoutStart, recorded.ScheduleAdmission.EffectiveEndUtc);
                Assert.AreEqual(blackoutEnd, second.Manifest.Descriptor.RetainedScheduleAdmission.EffectiveStartUtc);
            }

            foreach (var cadence in new[] { CaptureCadenceMode.Continuous, CaptureCadenceMode.MinimumStartInterval })
            {
                var invalidInterval = cadence == CaptureCadenceMode.Continuous ? TimeSpan.FromTicks(-1) : TimeSpan.Zero;
                var invalid = historical.Manifest.Descriptor with
                {
                    CenteredAcquisition = recorded with { CadenceMode = cadence, EffectiveInterval = invalidInterval }
                };
                Assert.IsFalse(invalid.Validate().IsValid);
                invalid = invalid with
                {
                    CenteredAcquisition = recorded with
                    {
                        CadenceMode = cadence,
                        EffectiveInterval = TimeSpan.FromSeconds(1),
                        Module = recorded.Module with { RequestedInterval = invalidInterval }
                    }
                };
                Assert.IsFalse(invalid.Validate().IsValid);
            }

            async Task RunOneAsync(CenteredFixture setup, CaptureControlTelemetry telemetry, CaptureAdmissionCoordinator admission)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
                await admission.InitializeAsync(token).ConfigureAwait(false);
                using var schedule = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock);
                var lanes = new CaptureLaneState(clock, setup.Options);
                lanes.Update([]);
                using var runtime = new CaptureScheduleRuntimeCoordinator(schedule, admission, setup.RawState, lanes, setup.Factory, clock);
                await runtime.InitializeAsync(config, token).ConfigureAwait(false);
                var snapshot = runtime.Snapshot!;
                var persisted = await schedule.TryReadPreviewAsync(snapshot.Revision, snapshot.Location, clock.GetUtcNow(),
                    CaptureScheduleIntervalExpander.AdmissionAlgorithmVersion, snapshot.AdmissionPolicy.PreviewStartUtc, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(snapshot.AdmissionPolicy.ExpansionSha256, persisted?.ExpansionSha256);
                operatorStates.Add(await runtime.GetOperatorStateAsync(token).ConfigureAwait(false));
                var notifier = new CenteredTraceNotifier { Committed = stop.Cancel };
                var host = new CaptureHostContext(config, setup.Ingress, notifier);
                var module = new CenteredRunnerModule((attempt, request, _) =>
                {
                    var result = CompleteScheduledCapture(clock, request);
                    captures.Add((attempt, request, result));
                    return Task.FromResult(result);
                });
                await using var moduleScope = module.ConfigureAwait(false);
                await new CameraModuleRunner(module, host, clock, NullLogger.Instance, admission,
                    new AstronomyEnginePlanetEphemeris(), telemetry, scheduleRuntimeCoordinator: runtime)
                    .RunAsync(stop.Token).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredInnerBlackoutRefusesExactDispatchedAttemptAndReopensFresh()
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var token = deadline.Token;
        try
        {
            var now = Timestamp(8).AddMinutes(5);
            var clock = new ExposureTestTimeProvider(now);
            var blackoutStart = now.AddSeconds(1);
            var blackoutEnd = now.AddSeconds(3);
            var baseConfig = CenteredContinuousConfiguration(now);
            var config = baseConfig with
            {
                Schedule = baseConfig.Schedule! with { Blackouts = [new("inner", blackoutStart, blackoutEnd)] }
            };
            using var setup = new CenteredFixture(root, timeProvider: clock);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry);
            await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
            await admission.InitializeAsync(token).ConfigureAwait(false);
            using var schedule = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock);
            var lanes = new CaptureLaneState(clock, setup.Options);
            lanes.Update([]);
            using var runtime = new CaptureScheduleRuntimeCoordinator(schedule, admission, setup.RawState, lanes, setup.Factory, clock);
            await runtime.InitializeAsync(config, token).ConfigureAwait(false);
            var attempts = new List<ExposureAttemptKey>();
            var starts = 0;
            var module = new CenteredRunnerModule((attempt, request, _) =>
            {
                attempts.Add(attempt);
                if (attempts.Count == 2)
                {
                    Assert.AreEqual(blackoutStart, request.BoundStartBeforeUtc);
                    clock.Advance(blackoutStart - clock.GetUtcNow());
                }
                var captured = CompleteScheduledCapture(clock, request);
                starts++;
                return Task.FromResult(captured);
            });
            await using var moduleScope = module.ConfigureAwait(false);
            var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commits = 0;
            var host = new CaptureHostContext(config, setup.Ingress, new CenteredTraceNotifier
            {
                Committed = () =>
                {
                    if (++commits == 1) firstCommitted.TrySetResult();
                    else stop.Cancel();
                }
            });
            var runner = new CameraModuleRunner(module, host, clock, NullLogger.Instance, admission,
                new AstronomyEnginePlanetEphemeris(), telemetry, scheduleRuntimeCoordinator: runtime);
            var running = runner.RunAsync(stop.Token);
            try
            {
                await firstCommitted.Task.WaitAsync(token).ConfigureAwait(false);
                await clock.TimerCreated.Task.WaitAsync(token).ConfigureAwait(false);
                Assert.AreEqual(blackoutStart, clock.GetUtcNow());
                using var db = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.AreEqual(1L, await ScalarLongAsync(db, """
                    SELECT COUNT(*) FROM centered_exposure_attempts
                    WHERE ordinal = 2 AND status = 'expired' AND reason = 'schedule.start-window-closed'
                      AND source_capture_id IS NOT NULL AND capture_id IS NULL;
                    """).ConfigureAwait(false));
                Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                clock.Advance(blackoutEnd - clock.GetUtcNow());
                await running.WaitAsync(token).ConfigureAwait(false);
                Assert.AreEqual(2, starts);
                Assert.HasCount(3, attempts);
                Assert.AreEqual(attempts[0].RunId, attempts[1].RunId);
                Assert.AreNotEqual(attempts[1].RunId, attempts[2].RunId);
                Assert.AreEqual(1L, attempts[2].Ordinal);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await running.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredWorkingZwoFencesTwoColdLegacyStartupsUntilIdle()
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            var center = CenteredContinuousConfiguration(Timestamp(8)) with
            {
                CenteredExposure = new("physical", CenteredExposureProfile.CurrentExecutionVersion)
            };
            var device = ZwoAsiCameraModuleTests.CreateCenteredTransportTestConfig();
            var legacy = device with
            {
                AgentId = center.AgentId,
                DeploymentLocation = center.DeploymentLocation,
                Rig = device.Rig with { ControlPolicy = center.Rig.ControlPolicy }
            };
            var physical = legacy with { CenteredExposure = center.CenteredExposure, Pipeline = center.Pipeline };
            ExposureAttemptKey uncertain;
            using (var setup = new CenteredFixture(root))
            {
                await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
                var transition = await setup.Ingress.FenceCenteredOwnerAsync(physical.AgentId!, "physical",
                    CaptureContractJson.ComputeCanonicalJsonSha256(physical), token).ConfigureAwait(false);
                var native = new ZwoAsiCameraModuleTests.FakeAsiNativeApi { ExposureStatuses = new([AsiExposureStatus.Idle]) };
                var module = ZwoAsiCameraModuleTests.Module(native);
                await using var moduleScope = module.ConfigureAwait(false);
                string? identity = null;
                await ((ICameraBoundExposureCapture)module).InitializeBoundAsync(physical, async (verified, ct) =>
                {
                    identity = verified;
                    await setup.Ingress.FenceCenteredDeviceAsync(transition!, verified, ct).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                await setup.Ingress.ActivateCenteredOwnerAsync(transition!, token).ConfigureAwait(false);
                var reservation = await setup.Ingress.ReserveCenteredAsync(physical, CenteredTestResources, null, token, identity)
                    .ConfigureAwait(false);
                uncertain = reservation.Pending.Key;
                await setup.Ingress.DispatchCenteredAsync(uncertain, CenteredTestSettings, null, "fixture",
                    token, owner: transition).ConfigureAwait(false);
            }

            for (var restart = 0; restart < 2; restart++)
            {
                var working = new ZwoAsiCameraModuleTests.FakeAsiNativeApi { RepeatWorkingStatus = true };
                await RunLegacyStartupAsync(working, shouldCapture: false).ConfigureAwait(false);
                Assert.IsFalse(working.Calls.Any(call => call.StartsWith("Set:", StringComparison.Ordinal) ||
                    call.StartsWith("Roi:", StringComparison.Ordinal) || call.StartsWith("Position:", StringComparison.Ordinal) ||
                    call is "StartExposure" or "StopExposure"));
            }

            var idle = new ZwoAsiCameraModuleTests.FakeAsiNativeApi
            {
                ExposureStatuses = new([AsiExposureStatus.Idle, AsiExposureStatus.Success])
            };
            await RunLegacyStartupAsync(idle, shouldCapture: true).ConfigureAwait(false);
            Assert.IsTrue(idle.Calls.Any(call => call.StartsWith("Set:Exposure:", StringComparison.Ordinal)));
            Assert.IsTrue(idle.Calls.Any(call => call.StartsWith("Set:Gain:", StringComparison.Ordinal)));
            Assert.AreEqual(1, idle.Calls.Count(call => call == "StartExposure"));
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(db, $"""
                SELECT COUNT(*) FROM centered_exposure_attempts WHERE attempt_id = '{uncertain.AttemptId:N}'
                  AND status = 'expired' AND capture_id IS NULL AND reason = 'profile-replaced';
                """).ConfigureAwait(false));

            async Task RunLegacyStartupAsync(ZwoAsiCameraModuleTests.FakeAsiNativeApi native, bool shouldCapture)
            {
                using var setup = new CenteredFixture(root);
                using var telemetry = new CaptureControlTelemetry();
                using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, TimeProvider.System, telemetry);
                var fleet = new CaptureRuntimeState(TimeProvider.System);
                var accessor = new CameraAgentConfigurationAccessor();
                accessor.SetConfiguration(legacy);
                var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var logger = new CenteredCaptureFailureLogger();
                using var service = new CameraCaptureService(accessor, new CenteredModuleFactory(ZwoAsiCameraModuleTests.Module(native)),
                    setup.Ingress, new CenteredTraceNotifier { Committed = () => committed.TrySetResult() },
                    TimeProvider.System, new AstronomyEnginePlanetEphemeris(), telemetry, admission, fleet,
                    new CenteredReadyLifetime(new CancellationToken(canceled: true)), logger);
                await service.StartAsync(token).ConfigureAwait(false);
                try
                {
                    var observed = await Task.WhenAny(committed.Task, logger.Failure.Task).WaitAsync(token).ConfigureAwait(false);
                    if (shouldCapture)
                    {
                        var failure = logger.Failure.Task.IsCompletedSuccessfully
                            ? (await logger.Failure.Task.ConfigureAwait(false)).ToString()
                            : null;
                        Assert.AreSame(committed.Task, observed, failure);
                    }
                    else
                    {
                        Assert.AreSame(logger.Failure.Task, observed);
                        var failure = await logger.Failure.Task.ConfigureAwait(false);
                        StringAssert.Contains(failure.Message, "prior ASI exposure", StringComparison.Ordinal);
                        Assert.AreEqual(nameof(InvalidOperationException), fleet.Snapshot.Capture.Reason);
                    }
                }
                finally { await service.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            }
        }
        finally { DeleteRoot(root); }
    }

    private sealed class CenteredCaptureFailureLogger : ILogger<CameraCaptureService>
    {
        internal TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Failure.TrySetResult(exception);
            else if (logLevel >= LogLevel.Error)
                Failure.TrySetResult(new InvalidOperationException(formatter(state, exception)));
        }
    }

    private static async Task<CameraAgentGalleryCaptureDetail> ReadAuthenticatedGalleryAsync(
        SqliteCameraAgentGallery gallery, Guid captureId, CancellationToken cancellationToken)
    {
        const string email = "centered@fixture.test";
        var manager = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.SetupGet(item => item.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(item => item.FindByIdAsync("fixture-owner")).ReturnsAsync(new ApplicationUser
        {
            Id = "fixture-owner",
            NormalizedEmail = email.ToUpperInvariant(),
            IsSiteOwner = true,
            SecurityStamp = "fixture-stamp"
        });
        manager.Setup(item => item.GetSecurityStampAsync(It.IsAny<ApplicationUser>())).ReturnsAsync("fixture-stamp");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton(manager.Object);
        builder.Services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        builder.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = email }));
        builder.Services.AddCameraAgentAuthorization();
        builder.Services.AddAuthentication("centered-fixture").AddScheme<AuthenticationSchemeOptions, CenteredGalleryAuthentication>(
            "centered-fixture", _ => { });
        builder.Services.AddSingleton<ICameraAgentGallery>(gallery);
        builder.Services.AddSingleton(Mock.Of<ICameraAgentArchive>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentCapturePresentationProjector>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentCurrentImagePresentationService>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentLayeredPresentationService>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentPresentationMaterializer>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentRetainedAstrometryService>());
        var app = builder.Build();
        await using var appScope = app.ConfigureAwait(false);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCameraAgentGalleryEndpoints();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var uri = new Uri($"/api/v1/operations/gallery/{captureId:D}", UriKind.Relative);
        using var rejected = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        client.DefaultRequestHeaders.Add("X-Centered-Fixture-Owner", "1");
        return (await client.GetFromJsonAsync<CameraAgentGalleryCapture>(uri, cancellationToken).ConfigureAwait(false))!.Detail!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The authentication scheme constructs the fixture handler through dependency injection.")]
    private sealed class CenteredGalleryAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Centered-Fixture-Owner"] != "1") return Task.FromResult(AuthenticateResult.NoResult());
            var owner = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "fixture-owner"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
                new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "fixture-stamp")
            ], IdentityConstants.ApplicationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(owner, Scheme.Name)));
        }
    }

    private static CameraModuleConfig CenteredContinuousConfiguration(DateTimeOffset utc)
    {
        var config = CenteredConfiguration("camera");
        return config with
        {
            Rig = config.Rig with
            {
                ControlPolicy = new()
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }
            },
            Schedule = new("capture-schedule-v1",
                [new("continuous", TimeSpan.FromMilliseconds(10), 5, TimeSpan.Zero, CaptureCadenceMode.Continuous, 15)],
                [new("same-window", utc.DayOfWeek,
                    new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(8, 0)),
                    new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(10, 0)), "continuous")])
        };
    }

    private static CaptureResult CompleteScheduledCapture(ExposureTestTimeProvider clock, CaptureRequest request)
    {
        var started = clock.GetUtcNow().AddMilliseconds(1);
        request.RequireOpenBoundStart(started);
        var ended = started + request.RequestedSetpoint!.Exposure;
        var readout = ended.AddMilliseconds(1);
        var result = CenteredTraceFrame(101, request);
        clock.Advance(readout - clock.GetUtcNow());
        return result with
        {
            Frame = result.Frame! with { TimestampUtc = started },
            AcquisitionTiming = new(started, ended, readout),
            NextSetpoint = result.NextSetpoint with { TargetFps = 7 }
        };
    }
}
