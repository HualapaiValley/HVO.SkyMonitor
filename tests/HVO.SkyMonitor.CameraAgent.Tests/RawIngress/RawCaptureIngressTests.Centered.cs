extern alias InstallerRuntime;

using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using Installer = InstallerRuntime::HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Endpoints;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    private static readonly bool[] WhiteClassification = [false, true, true];

    [TestMethod]
    public async Task CenteredRuntimeSeparatesJournalHeadFromLocalOccupancyAcrossRestart()
    {
        var root = CreateRoot();
        try
        {
            var config = CenteredConfiguration("camera");
            var camera = new CameraKey(config.AgentId!, "camera");
            Guid runId;
            using (var first = new CenteredFixture(root))
            {
                var starting = await first.Ingress.ReadCurrentAsync(camera, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CenteredRuntimeEvidenceBasis.Unavailable, starting.Basis);
                Assert.IsNull(starting.BufferOccupancy);
                Assert.IsNull(starting.Holds);
                var admitted = await first.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
                var current = await first.Ingress.ReadCurrentAsync(camera, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CenteredRuntimeEvidenceBasis.ActiveAcquisition, current.Basis);
                Assert.AreEqual(1, current.BufferOccupancy);
                Assert.AreEqual(16, current.BufferCapacity);
                Assert.AreEqual(1, current.Holds!.PendingMeteringCount);
                Assert.AreEqual(admitted.Manifest.Descriptor.Capture.CaptureId, current.AcquisitionHead!.Exposure.CaptureId);
                Assert.AreEqual(Timestamp(1), current.AcquisitionHead.ExposureStartedUtc);
                Assert.IsTrue(current.ObservedUtc >= current.AcquisitionHead.CommittedUtc);
                runId = current.RunId!.Value;
            }

            using var restarted = new CenteredFixture(root);
            await restarted.Ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var retained = await restarted.Ingress.ReadRunAsync(camera, runId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CenteredRuntimeEvidenceBasis.RetainedJournal, retained.Basis);
            Assert.IsNotNull(retained.AcquisitionHead);
            Assert.IsNull(retained.BufferOccupancy);
            Assert.AreEqual(1, retained.Holds!.PendingMeteringCount);
            await restarted.Ingress.ReserveCenteredAsync(config, CenteredTestResources, null, CancellationToken.None)
                .ConfigureAwait(false);
            var resumedBeforeFirstFrame = await restarted.Ingress.ReadCurrentAsync(camera, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(CenteredRuntimeEvidenceBasis.RetainedJournal, resumedBeforeFirstFrame.Basis);
            Assert.IsNull(resumedBeforeFirstFrame.BufferOccupancy);
            await restarted.Ingress.ReconcileCenteredControlAsync(config with { CenteredExposure = null },
                CancellationToken.None, camera).ConfigureAwait(false);
            await restarted.Ingress.ReserveCenteredAsync(config, CenteredTestResources, null, CancellationToken.None)
                .ConfigureAwait(false);
            var switched = await restarted.Ingress.ReadCurrentAsync(camera, CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(runId, switched.RunId);
            Assert.AreEqual(CenteredRuntimeEvidenceBasis.ActiveAcquisition, switched.Basis);
            Assert.AreEqual(0, switched.BufferOccupancy);
            Assert.IsNull(switched.AcquisitionHead);
        }
        finally { DeleteRoot(root); }
    }
    private static readonly CaptureSetpoint CenteredTestSettings = new(TimeSpan.FromMilliseconds(10), 5, null, null);
    private static readonly CenteredSceneResources CenteredTestResources = new(null, null, null, null, "fixture");

    [TestMethod]
    public async Task CenteredRunnerRecovers103AndNeverApplies104DecisionTo105()
    {
        var root = CreateRoot();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var left = CenteredConfiguration("left");
            left = left with
            {
                Rig = left.Rig with
                {
                    ControlPolicy = new()
                    {
                        ExposureControl = AutomaticControlOwnership.HostMetered,
                        GainControl = AutomaticControlOwnership.Disabled
                    },
                    Pipeline = left.Rig.Pipeline with
                    {
                        DayExposure = CenteredTestSettings.Exposure,
                        NightExposure = CenteredTestSettings.Exposure,
                        DayGain = CenteredTestSettings.Gain,
                        NightGain = CenteredTestSettings.Gain,
                        CadenceMode = CaptureCadenceMode.Continuous
                    }
                }
            };
            var right = CenteredConfiguration("right");
            var camera = new CameraKey(left.AgentId!, "left");
            ExposureKey raw103;
            ExposureAttemptKey target104;
            ExposureAttemptKey rightTarget;
            var requestUtc = Timestamp(1);
            var clock = new FixedTimeProvider(requestUtc);

            using (var initial = new CenteredFixture(root, new CenteredTraceFault()))
            {
                var other = await initial.AdmitAsync(right, requestUtc).ConfigureAwait(false);
                rightTarget = other.Manifest.CenteredExposure!.Target;
                var slot = 100;
                var first = new CenteredRunnerModule(async (attempt, request, token) =>
                {
                    slot++;
                    Assert.AreEqual(slot - 100L, attempt.Ordinal);
                    await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint!, slot != 101, token).ConfigureAwait(false);
                    return CenteredTraceFrame(slot, request, effectiveGain: slot == 103 ? 7 : null);
                });
                await using var firstModule = first.ConfigureAwait(false);
                var host = new CaptureHostContext(left, initial.Ingress, new CenteredTraceNotifier());
                var runner = new CameraModuleRunner(first, host, clock, NullLogger.Instance, new AstronomyEnginePlanetEphemeris());
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(() => runner.RunAsync(stop.Token))
                    .ConfigureAwait(false);
                Assert.AreEqual(103, slot);
            }

            using (var restarted = new CenteredFixture(root))
            {
                await restarted.Ingress.InitializeAsync(stop.Token).ConfigureAwait(false);
                await restarted.Ingress.ReconcileCenteredControlAsync(left, stop.Token).ConfigureAwait(false);
                var head = await restarted.Ingress.ReadCurrentAsync(camera, stop.Token).ConfigureAwait(false);
                raw103 = head.AcquisitionHead!.Exposure;
                var retained = await restarted.Ingress.ReadCenteredRawAsync(raw103, stop.Token).ConfigureAwait(false);
                target104 = retained!.Manifest.CenteredExposure!.Target;
                Assert.AreEqual(4L, target104.Ordinal);
                Assert.AreEqual(requestUtc, retained.Manifest.Descriptor.Timing.RequestedStartUtc);
                Assert.AreEqual(requestUtc.AddMilliseconds(3), retained.Manifest.Descriptor.Timing.ExposureStartedUtc);
                Assert.AreEqual(7d, retained.Manifest.Descriptor.Controls.EffectiveGain);
                var uncertain = new CenteredRunnerModule(async (attempt, request, token) =>
                {
                    Assert.AreEqual(target104, attempt);
                    Assert.AreEqual(7d, request.RequestedSetpoint!.Gain);
                    await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint, true, token).ConfigureAwait(false);
                    throw new TimeoutException("Device changed one control before its acknowledgment was lost.");
                });
                await using var uncertainModule = uncertain.ConfigureAwait(false);
                var host = new CaptureHostContext(left, restarted.Ingress, new CenteredTraceNotifier());
                var runner = new CameraModuleRunner(uncertain, host, clock, NullLogger.Instance, new AstronomyEnginePlanetEphemeris());
                await Assert.ThrowsExactlyAsync<TimeoutException>(() => runner.RunAsync(stop.Token)).ConfigureAwait(false);
            }

            using var fresh = new CenteredFixture(root);
            await fresh.Ingress.InitializeAsync(stop.Token).ConfigureAwait(false);
            await fresh.Ingress.ReconcileCenteredControlAsync(left, stop.Token).ConfigureAwait(false);
            var otherPending = await fresh.Ingress.ReserveCenteredAsync(right, CenteredTestResources, null, stop.Token)
                .ConfigureAwait(false);
            Assert.AreEqual(rightTarget, otherPending.Pending.Key);
            var read = new CenteredExposureProductReader(fresh.Ingress, fresh.Store);
            var persistence = new CaptureProcessingPersistence(fresh.Options, fresh.Store, fresh.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using var handler = new StandardCaptureLaneHandler(fresh.Factory, persistence, fresh.Telemetry,
                NullLogger<StandardCaptureLaneHandler>.Instance, fresh.Ingress, fresh.Options);
            var nextSlot = 104;
            var last = new CenteredTraceNotifier();
            last.Committed = () =>
            {
                if (nextSlot == 106) stop.Cancel();
            };
            var resumed = new CenteredRunnerModule(async (attempt, request, token) =>
            {
                nextSlot++;
                Assert.AreNotEqual(target104.RunId, attempt.RunId);
                Assert.AreEqual(CenteredTestSettings, request.RequestedSetpoint);
                await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint!, nextSlot == 106, token).ConfigureAwait(false);
                if (nextSlot == 105)
                {
                    var lane = fresh.Policy.Definitions.Single(definition => definition.Name == "standard");
                    for (var index = 0; index < 4; index++)
                    {
                        var lease = await fresh.Ingress.ClaimAsync(lane, "trace-worker", left, token).ConfigureAwait(false);
                        Assert.IsNotNull(lease);
                        var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
                        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                        await fresh.Ingress.CompleteAsync(lease, token).ConfigureAwait(false);
                    }
                    Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly,
                        (await read.ReadLinearAsync(raw103, token).ConfigureAwait(false))!.Disposition);
                }
                return CenteredTraceFrame(nextSlot, request);
            });
            await using var resumedModule = resumed.ConfigureAwait(false);
            var resumedHost = new CaptureHostContext(left, fresh.Ingress, last);
            await new CameraModuleRunner(resumed, resumedHost, clock, NullLogger.Instance, new AstronomyEnginePlanetEphemeris())
                .RunAsync(stop.Token).ConfigureAwait(false);
            Assert.AreEqual(106, nextSlot);
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(6L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(db, """
                SELECT COUNT(*) FROM centered_exposure_attempts
                WHERE ordinal=4 AND status='expired' AND reason='target-progress-unknown' AND capture_id IS NULL;
                """).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow("profile-replaced", false)]
    [DataRow("device-changed", false)]
    [DataRow("device-changed", true)]
    [DataRow("attachment-incomplete", false)]
    public async Task CenteredColdLegacyAndReplacementRetirePendingTarget(string retirementReason, bool unverifiedAttachment)
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            var config = CenteredConfiguration("selected");
            config = config with
            {
                Rig = config.Rig with
                {
                    ControlPolicy = new CameraControlPolicy
                    {
                        ExposureControl = AutomaticControlOwnership.Disabled,
                        GainControl = AutomaticControlOwnership.Disabled
                    },
                    Pipeline = config.Rig.Pipeline with
                    {
                        CaptureInterval = TimeSpan.FromMinutes(1),
                        DayExposure = CenteredTestSettings.Exposure,
                        NightExposure = CenteredTestSettings.Exposure,
                        DayGain = CenteredTestSettings.Gain,
                        NightGain = CenteredTestSettings.Gain,
                        CadenceMode = CaptureCadenceMode.MinimumStartInterval
                    }
                }
            };
            var other = CenteredConfiguration("unrelated");
            ExposureAttemptKey target;
            ExposureAttemptKey unrelated;
            ExposureKey archived;
            var start = Timestamp(1);
            using (var first = new CenteredFixture(root))
            {
                var raw = await first.AdmitAsync(other, start).ConfigureAwait(false);
                unrelated = raw.Manifest.CenteredExposure!.Target;
                var module = new CenteredRunnerModule((_, request, _) =>
                    Task.FromResult(CenteredTraceFrame(103, request)), deviceIdentity: "test-device-1");
                await CaptureOnceAsync(first, config, unverifiedAttachment ? new UnverifiedCenteredModule(module) : module,
                    start, token).ConfigureAwait(false);
                var camera = new CameraKey(config.AgentId!, "selected");
                var head = await first.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false);
                archived = head.AcquisitionHead!.Exposure;
                target = (await first.Ingress.ReadCenteredRawAsync(archived, token).ConfigureAwait(false))!.Manifest.CenteredExposure!.Target;
            }

            if (retirementReason == "profile-replaced")
            {
                using var legacy = new CenteredFixture(root);
                var module = new CenteredRunnerModule((_, _, _) =>
                    throw new AssertFailedException("Legacy startup must use normal capture."),
                    captureLegacy: (request, _) => Task.FromResult(CenteredTraceFrame(104, request)));
                await CaptureOnceAsync(legacy, config with { CenteredExposure = null }, module, start.AddSeconds(1), token)
                    .ConfigureAwait(false);
            }
            if (retirementReason == "attachment-incomplete")
            {
                using var interrupted = new CenteredFixture(root);
                await interrupted.Ingress.InitializeAsync(token).ConfigureAwait(false);
                var transition = await interrupted.Ingress.FenceCenteredOwnerAsync(config.AgentId!, "selected",
                    CaptureContractJson.ComputeCanonicalJsonSha256(config), token).ConfigureAwait(false);
                Assert.IsNotNull(transition);
                await interrupted.Ingress.FenceCenteredDeviceAsync(transition, "test-device-1", token).ConfigureAwait(false);
            }

            using var restarted = new CenteredFixture(root);
            (ExposureAttemptKey Attempt, CaptureSetpoint? Settings) actual = default;
            var replacement = new CenteredRunnerModule((attempt, request, _) =>
            {
                actual = (attempt, request.RequestedSetpoint);
                return Task.FromResult(CenteredTraceFrame(105, request));
            }, deviceIdentity: retirementReason == "device-changed" ? "test-device-2" : "test-device-1");
            Assert.AreEqual("centered-trace", replacement.Id);
            await CaptureOnceAsync(restarted, config, unverifiedAttachment ? new UnverifiedCenteredModule(replacement) : replacement,
                start.AddSeconds(2), token).ConfigureAwait(false);
            Assert.IsNotNull(actual.Attempt);
            Assert.AreNotEqual(target.RunId, actual.Attempt.RunId);
            Assert.AreNotEqual(target.AttemptId, actual.Attempt.AttemptId);
            Assert.AreEqual(1L, actual.Attempt.Ordinal);
            Assert.AreEqual(CenteredTestSettings, actual.Settings);
            Assert.IsNotNull(await restarted.Ingress.ReadCenteredRawAsync(archived, token).ConfigureAwait(false));
            Assert.AreEqual(unrelated, (await restarted.Ingress.ReserveCenteredAsync(other, CenteredTestResources, null, token)
                .ConfigureAwait(false)).Pending.Key);
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            using var status = db.CreateCommand();
            status.CommandText = "SELECT reason FROM centered_exposure_attempts WHERE attempt_id = $target AND status = 'expired';";
            status.Parameters.AddWithValue("$target", target.AttemptId.ToString("N"));
            Assert.AreEqual(retirementReason, await status.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredConfirmedScheduleWindowAndResumeSelectFreshSettings(bool opaque)
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var token = deadline.Token;
        try
        {
            var start = Timestamp(9).AddSeconds(-1);
            var clock = new ExposureTestTimeProvider(start);
            using var setup = new CenteredFixture(root, timeProvider: clock);
            using var telemetry = new CaptureControlTelemetry();
            using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry);
            using var schedule = new SqliteCaptureScheduleStore(setup.Ingress, setup.Options, clock);
            var lanes = new CaptureLaneState(clock, setup.Options);
            lanes.Update([]);
            using var runtime = new CaptureScheduleRuntimeCoordinator(schedule, admission, setup.RawState,
                lanes, setup.Factory, clock);
            var config = CenteredConfiguration("camera");
            config = config with
            {
                Rig = config.Rig with
                {
                    ControlPolicy = new CameraControlPolicy
                    {
                        ExposureControl = AutomaticControlOwnership.Disabled,
                        GainControl = AutomaticControlOwnership.Disabled
                    }
                },
                Schedule = new CaptureScheduleDefinition("capture-schedule-v1",
                    [new("earlier", TimeSpan.FromMilliseconds(10), 5, TimeSpan.FromSeconds(30)),
                     new("later", TimeSpan.FromMilliseconds(500), 11, TimeSpan.FromSeconds(45))],
                    [new("early-window", start.DayOfWeek, Boundary(8), Boundary(9), "earlier"),
                     new("late-window", start.DayOfWeek, Boundary(9), Boundary(10), "later")])
            };
            await admission.InitializeAsync(token).ConfigureAwait(false);
            await runtime.InitializeAsync(config, token).ConfigureAwait(false);
            var firstCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commits = 0;
            var distributor = new CenteredTraceNotifier
            {
                Committed = () =>
            {
                if (Interlocked.Increment(ref commits) == 1) firstCommit.TrySetResult();
                else
                {
                    secondCommit.TrySetResult();
                    stop.Cancel();
                }
            }
            };
            var host = new CaptureHostContext(config, setup.Ingress, distributor);
            var shots = new List<(ExposureAttemptKey Attempt, CaptureRequest Request)>();
            var module = new CenteredRunnerModule((attempt, request, _) =>
            {
                shots.Add((attempt, request));
                var result = CenteredTraceFrame(100 + shots.Count, request);
                if (opaque)
                {
                    var frame = result.Frame!;
                    result = result with
                    {
                        Frame = frame with
                        {
                            Layout = frame.Layout! with
                            {
                                StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1,
                                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
                                WhiteLevel = ushort.MaxValue
                            }
                        }
                    };
                }
                var actualStart = clock.GetUtcNow().AddMilliseconds(1);
                var actualEnd = actualStart + request.RequestedSetpoint!.Exposure;
                var readout = actualEnd.AddMilliseconds(1);
                result = result with
                {
                    Frame = result.Frame! with { TimestampUtc = actualStart },
                    AcquisitionTiming = new(actualStart, actualEnd, readout)
                };
                var toReadout = readout - clock.GetUtcNow();
                if (toReadout > TimeSpan.Zero) clock.Advance(toReadout);
                return Task.FromResult(result);
            });
            await using var moduleScope = module.ConfigureAwait(false);
            var fleet = new FleetRuntimeState(clock);
            var runner = new CameraModuleRunner(module, host, clock, NullLogger.Instance, admission,
                new AstronomyEnginePlanetEphemeris(), telemetry, fleet, runtime);
            var running = runner.RunAsync(stop.Token);
            try
            {
                await Task.WhenAny(firstCommit.Task, running).WaitAsync(token).ConfigureAwait(false);
                if (running.IsFaulted) await running.ConfigureAwait(false);
                Assert.IsTrue(firstCommit.Task.IsCompleted);
                await admission.PauseAsync("centered-pause", admission.Snapshot.Version, "fixture", null, token)
                    .ConfigureAwait(false);
                Assert.AreEqual(CaptureAdmissionState.Paused, admission.Snapshot.State);
                clock.Advance(TimeSpan.FromSeconds(2));
                using var db = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                await admission.ResumeAsync("centered-resume", admission.Snapshot.Version, "fixture", null, token)
                    .ConfigureAwait(false);
                for (var tick = 0; tick < 20 && !secondCommit.Task.IsCompleted && !running.IsCompleted; tick++)
                {
                    clock.Advance(TimeSpan.FromSeconds(2));
                    await Task.WhenAny(secondCommit.Task, running, Task.Delay(25, token)).ConfigureAwait(false);
                }
                await Task.WhenAny(secondCommit.Task, running).WaitAsync(token).ConfigureAwait(false);
                if (running.IsFaulted) await running.ConfigureAwait(false);
                Assert.IsTrue(secondCommit.Task.IsCompleted);
                Assert.HasCount(2, shots);
                Assert.AreEqual(CenteredTestSettings, shots[0].Request.RequestedSetpoint);
                Assert.AreEqual(new CaptureSetpoint(TimeSpan.FromMilliseconds(500), 11, null, null), shots[1].Request.RequestedSetpoint);
                Assert.AreNotEqual(shots[0].Attempt.RunId, shots[1].Attempt.RunId);

                var camera = shots[1].Attempt.Camera;
                var current = await setup.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false);
                var raw = await setup.Ingress.ReadCenteredRawAsync(current.AcquisitionHead!.Exposure, token).ConfigureAwait(false);
                var evidence = raw!.Manifest.Descriptor.CenteredAcquisition!;
                Assert.AreEqual("later", evidence.ScheduleAdmission!.SetpointProfileId);
                Assert.AreEqual(TimeSpan.FromSeconds(45), evidence.EffectiveInterval);
                Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval, evidence.CadenceMode);
                Assert.AreEqual(evidence.ScheduleAdmission.ScheduleRevisionSha256, raw.Run.Schedule!.Revision);
                Assert.AreEqual(11d, raw.Manifest.Descriptor.Controls.EffectiveGain);
                await running.ConfigureAwait(false);
                Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(502), evidence.ModuleDuration);
                Assert.IsGreaterThan(raw.Manifest.Descriptor.Timing.RequestedStartUtc, raw.Manifest.Descriptor.Timing.ExposureStartedUtc);
                Assert.IsTrue(fleet.Snapshot.Timings.Single(timing => timing.Segment == FleetTimingSegment.Ingress)
                    .MaximumMilliseconds > 0);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await running.ConfigureAwait(false);
            }
        }
        finally { DeleteRoot(root); }

        static CaptureScheduleBoundary Boundary(int hour) => new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(hour, 0));
    }

    private static async Task CaptureOnceAsync(CenteredFixture setup, CameraModuleConfig config, ICameraModule module,
        DateTimeOffset utc, CancellationToken cancellationToken)
    {
        var accessor = new CameraAgentConfigurationAccessor();
        accessor.SetConfiguration(config);
        var clock = new FixedTimeProvider(utc);
        using var telemetry = new CaptureControlTelemetry();
        using var admission = new CaptureAdmissionCoordinator(setup.Ingress, setup.Options, clock, telemetry);
        using var ownership = new CameraModuleOwnership();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var distributor = new CenteredTraceNotifier { Committed = () => done.TrySetResult() };
        using var service = new CameraCaptureService(accessor, new CenteredModuleFactory(module), setup.Ingress,
            distributor, clock, new AstronomyEnginePlanetEphemeris(), telemetry, admission, new FleetRuntimeState(clock),
            new CenteredReadyLifetime(new CancellationToken(canceled: true)), NullLogger<CameraCaptureService>.Instance, moduleOwnership: ownership);
        await service.StartAsync(cancellationToken).ConfigureAwait(false);
        try { await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
    }

    private sealed class CenteredModuleFactory(ICameraModule module) : ICameraModuleFactory
    {
        public ICameraModule Create(string moduleType) => module;
    }

    private sealed class CenteredReadyLifetime(CancellationToken started) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => started;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static CaptureResult CenteredTraceFrame(int slot, CaptureRequest request, double? effectiveGain = null)
    {
        var observed = request.RequestedStartUtc.AddMilliseconds(slot - 100);
        var fixture = CenteredSubmission(observed).Result;
        var frame = fixture.Frame!;
        var settings = request.RequestedSetpoint!;
        var ended = observed + settings.Exposure;
        return fixture with
        {
            Frame = frame with { Metadata = frame.Metadata with { Gain = effectiveGain ?? settings.Gain, Exposure = settings.Exposure } },
            AcquisitionTiming = new(observed, ended, ended.AddMilliseconds(1))
        };
    }

    private static async Task AssertDispatchedAsync(
        string root, ExposureAttemptKey attempt, CaptureSetpoint settings, bool metered, CancellationToken token)
    {
        using var db = await OpenJournalAsync(root).ConfigureAwait(false);
        using var read = db.CreateCommand();
        read.CommandText = """
            SELECT requested_json, metering_json FROM centered_exposure_attempts
            WHERE attempt_id = $id AND status = 'dispatched' AND capture_id IS NULL;
            """;
        read.Parameters.AddWithValue("$id", attempt.AttemptId.ToString("N"));
        using var data = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
        Assert.IsTrue(await data.ReadAsync(token).ConfigureAwait(false));
        Assert.AreEqual(settings, JsonSerializer.Deserialize<CaptureSetpoint>((byte[])data[0]));
        Assert.AreEqual(metered, !await data.IsDBNullAsync(1, token).ConfigureAwait(false));
    }

    private sealed class CenteredTraceFault : IRawIngressFaultInjector
    {
        private int _commits;
        public bool IsEnabled(RawIngressFaultPoint point) => point == RawIngressFaultPoint.AfterJournalCommit;
        public void Inject(RawIngressFaultPoint point)
        {
            if (point == RawIngressFaultPoint.AfterJournalCommit && ++_commits == 4)
                throw new InjectedRawIngressFaultException();
        }
    }

    private sealed class CenteredTraceNotifier : ICaptureDistributor
    {
        internal Action? Committed { get; set; }
        public void NotifyCommittedCapture() => Committed?.Invoke();
        public ValueTask ProcessEphemeralAsync(
            CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class CenteredRunnerModule(
        Func<ExposureAttemptKey, CaptureRequest, CancellationToken, Task<CaptureResult>> capture,
        string deviceIdentity = "centered-trace-device",
        Func<CaptureRequest, CancellationToken, Task<CaptureResult>>? captureLegacy = null)
        : ICameraModule, ICameraBoundExposureCapture
    {
        public CenteredRunnerModule()
            : this(static (_, _, _) => throw new AssertFailedException(
                "The processing fixture supplies retained pixels and does not start a shutter."))
        { }

        public string Id => "centered-trace";
        public string DisplayName => Id;
        public string ModuleType => Id;
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task InitializeBoundAsync(CameraModuleConfig configuration,
            Func<string, CancellationToken, ValueTask> fenceAttachment, CancellationToken cancellationToken)
            => await fenceAttachment(deviceIdentity, cancellationToken).ConfigureAwait(false);

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => captureLegacy?.Invoke(request, cancellationToken) ??
                throw new AssertFailedException("Centered acquisition must use the bound module entry point.");
        public bool EnforcesBoundStartLimit => true;

        public Task<CaptureResult> CaptureBoundAsync(
            ExposureAttemptKey attempt, CaptureRequest request, CancellationToken cancellationToken)
            => capture(attempt, request, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnverifiedCenteredModule(CenteredRunnerModule module) : ICameraModule, ICameraBoundExposureCapture
    {
        public string Id => module.Id;
        public string DisplayName => module.DisplayName;
        public string ModuleType => module.ModuleType;
        public CameraModuleCapabilities Capabilities => module.Capabilities;
        public Task InitializeAsync(CameraModuleConfig configuration, CancellationToken cancellationToken)
            => module.InitializeAsync(configuration, cancellationToken);
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => module.CaptureAsync(request, cancellationToken);
        public Task<CaptureResult> CaptureBoundAsync(
            ExposureAttemptKey attempt, CaptureRequest request, CancellationToken cancellationToken)
            => module.CaptureBoundAsync(attempt, request, cancellationToken);
        public ValueTask DisposeAsync() => module.DisposeAsync();
    }

    [TestMethod]
    public async Task CenteredInterleavedSameAgentModelAndTimestampsAreDistinctAndIdempotent()
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            var left = CenteredConfiguration("left");
            var right = CenteredConfiguration("right");
            var submission = CenteredSubmission(Timestamp(3));
            var a = await setup.DispatchAsync(left).ConfigureAwait(false);
            var b = await setup.DispatchAsync(right).ConfigureAwait(false);
            var first = await setup.Ingress.AcceptCenteredAsync(left, submission, a, CancellationToken.None).ConfigureAwait(false);
            var second = await setup.Ingress.AcceptCenteredAsync(right, submission, b, CancellationToken.None).ConfigureAwait(false);

            Assert.AreNotEqual(first.Manifest.Descriptor.Capture.CaptureId, second.Manifest.Descriptor.Capture.CaptureId);
            Assert.AreNotEqual(first.Manifest.Descriptor.Artifact.ArtifactId, second.Manifest.Descriptor.Artifact.ArtifactId);
            Assert.AreEqual(first.Manifest.Descriptor.Artifact.SourceId, second.Manifest.Descriptor.Artifact.SourceId);
            Assert.AreEqual(first.Manifest.Descriptor.Timing.ExposureStartedUtc, second.Manifest.Descriptor.Timing.ExposureStartedUtc);
            Assert.AreNotEqual(RawCaptureDescriptorFactory.CreateStableIds(left with { CenteredExposure = null }, submission).CaptureId,
                first.Manifest.Descriptor.Capture.CaptureId);

            var againB = await setup.Ingress.AcceptCenteredAsync(right, submission, b, CancellationToken.None).ConfigureAwait(false);
            var againA = await setup.Ingress.AcceptCenteredAsync(left, submission, a, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(RawIngressOutcome.Existing, againB.Outcome);
            Assert.AreEqual(RawIngressOutcome.Existing, againA.Outcome);
            Assert.AreEqual(first.CommittedManifestSha256, againA.CommittedManifestSha256);
            Assert.AreEqual(second.CommittedManifestSha256, againB.CommittedManifestSha256);

            var nextA = await setup.DispatchAsync(left).ConfigureAwait(false);
            Assert.AreEqual(first.Manifest.CenteredExposure!.Target, nextA.Pending.Key);
            await setup.Ingress.ReconcileCenteredControlAsync(left, CancellationToken.None).ConfigureAwait(false);
            var nextB = await setup.Ingress.ReserveCenteredAsync(right, CenteredTestResources, null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(second.Manifest.CenteredExposure!.Target, nextB.Pending.Key);
            Assert.AreEqual(second.Manifest.Descriptor.Capture.CaptureId, nextB.Pending.SourceCaptureId);
            await setup.Ingress.ReconcileCenteredControlAsync(left with { CenteredExposure = null },
                CancellationToken.None, a.Pending.Key.Camera).ConfigureAwait(false);
            Assert.AreEqual(nextB.Pending.Key,
                (await setup.Ingress.ReserveCenteredAsync(right, CenteredTestResources, null, CancellationToken.None)
                    .ConfigureAwait(false)).Pending.Key);

            var freshA = await setup.Ingress.ReserveCenteredAsync(left, CenteredTestResources, null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreNotEqual(nextA.Pending.Key.RunId, freshA.Pending.Key.RunId);
            Assert.IsNull(freshA.Pending.SourceCaptureId);
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(2L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM centered_exposure_attempts WHERE status = 'expired';")
                .ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow((int)RawIngressFaultPoint.BeforeIdentityReservation, false)]
    [DataRow((int)RawIngressFaultPoint.SidecarDirectorySynced, true)]
    [DataRow((int)RawIngressFaultPoint.AfterJournalCommit, true)]
    public async Task CenteredAdmissionFaultRecoversOnlyProvenCaptureAndExactNextTarget(int crashPoint, bool recoverable)
    {
        var root = CreateRoot();
        try
        {
            var config = CenteredConfiguration("camera");
            var submission = CenteredSubmission(Timestamp(3));
            ExposureAttemptKey source;
            using (var initial = new CenteredFixture(root, new OneShotFaultInjector((RawIngressFaultPoint)crashPoint)))
            {
                var reservation = await initial.DispatchAsync(config).ConfigureAwait(false);
                source = reservation.Pending.Key;
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(async () =>
                    await initial.Ingress.AcceptCenteredAsync(config, submission, reservation, CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }

            using var resumed = new CenteredFixture(root);
            await resumed.Ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await resumed.Ingress.ReconcileCenteredControlAsync(config, CancellationToken.None).ConfigureAwait(false);
            var next = await resumed.Ingress.ReserveCenteredAsync(config, CenteredTestResources, null, CancellationToken.None)
                .ConfigureAwait(false);
            if (recoverable)
            {
                var ids = RawCaptureDescriptorFactory.CreateStableIds(config, submission);
                var raw = await resumed.Ingress.ReadCenteredRawAsync(new(source.Camera, source.RunId, ids.CaptureId),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(raw);
                Assert.AreEqual(ids.CaptureId, next.Pending.SourceCaptureId);
                Assert.AreEqual(raw.Manifest.CenteredExposure!.Target, next.Pending.Key);
                Assert.AreEqual(source, raw.Manifest.CenteredExposure!.Source);
                var frame = await resumed.Ingress.ReadCenteredFrameAsync(raw, CancellationToken.None).ConfigureAwait(false);
                CollectionAssert.AreEqual(submission.Result.Frame!.PixelData.ToArray(), frame.PixelData.ToArray());
            }
            else
            {
                Assert.IsNull(next.Pending.SourceCaptureId);
                Assert.AreNotEqual(source.RunId, next.Pending.Key.RunId);
            }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredAdmissionPressureRetiresUnadmittedTargetBeforeFreshDispatch()
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            var config = CenteredConfiguration("pressure");
            var rejectedSubmission = CenteredSubmission(Timestamp(2));
            ExposureAttemptKey rejected;
            using (var full = new CenteredFixture(root, requiredMaximumPendingCount: 1))
            {
                var first = await full.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
                var target = await full.DispatchAsync(config).ConfigureAwait(false);
                rejected = target.Pending.Key;
                Assert.AreEqual(first.Manifest.CenteredExposure!.Target, rejected);
                await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(async () =>
                    await full.Ingress.AcceptCenteredAsync(config, rejectedSubmission, target, token).ConfigureAwait(false))
                    .ConfigureAwait(false);
                using var db = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM raw_capture_assignments;").ConfigureAwait(false));
            }

            using var recovered = new CenteredFixture(root, requiredMaximumPendingCount: 1);
            await recovered.Ingress.InitializeAsync(token).ConfigureAwait(false);
            await recovered.Ingress.ReconcileCenteredControlAsync(config, token).ConfigureAwait(false);
            var rejectedCapture = RawCaptureDescriptorFactory.CreateStableIds(config, rejectedSubmission).CaptureId;
            Assert.IsNull(await recovered.Ingress.ReadCenteredRawAsync(new(rejected.Camera, rejected.RunId, rejectedCapture), token)
                .ConfigureAwait(false));
            var lane = recovered.Policy.Definitions.Single(definition => definition.Name == "standard");
            var old = await recovered.Ingress.ClaimAsync(lane, "pressure-fixture", config, token).ConfigureAwait(false);
            Assert.IsNotNull(old);
            await recovered.Ingress.CompleteAsync(old, token).ConfigureAwait(false);
            var fresh = await recovered.DispatchAsync(config).ConfigureAwait(false);
            Assert.AreNotEqual(rejected.RunId, fresh.Pending.Key.RunId);
            Assert.IsNull(fresh.Pending.SourceCaptureId);
            var committed = await recovered.Ingress.AcceptCenteredAsync(config, CenteredSubmission(Timestamp(3)), fresh, token)
                .ConfigureAwait(false);
            Assert.AreEqual(RawIngressOutcome.Committed, committed.Outcome);
            Assert.AreEqual(fresh.Pending.Key, committed.Manifest.CenteredExposure!.Source);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("zero-fps")]
    [DataRow("negative-fps")]
    [DataRow("nonfinite-fps")]
    [DataRow("zero-interval")]
    [DataRow("valid")]
    [DataRow("orphan-zero-fps")]
    public async Task CenteredPredecessorLaneRequiresValidRetainedDispatch(string evidence)
    {
        var root = CreateRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        try
        {
            using var setup = new CenteredFixture(root, new OneShotFaultInjector(RawIngressFaultPoint.SidecarDirectorySynced));
            var config = CenteredConfiguration("camera");
            var baseline = CenteredSubmission(Timestamp(1));
            var expected = baseline.Request.RequestedSetpoint! with { TargetFps = 15 };
            var submission = baseline with { Request = baseline.Request with { RequestedSetpoint = expected } };
            var reservation = await setup.Ingress.ReserveCenteredAsync(config, CenteredTestResources, null, token).ConfigureAwait(false);
            await setup.Ingress.DispatchCenteredAsync(reservation.Pending.Key, submission.Request.RequestedSetpoint!,
                null, "fixture", token).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(async () =>
                await setup.Ingress.AcceptCenteredAsync(config, submission, reservation, token).ConfigureAwait(false))
                .ConfigureAwait(false);
            var identity = RawCaptureDescriptorFactory.CreateStableIds(config, submission);
            var started = RawCaptureDescriptorFactory.ResolveExposureStartedUtc(submission, submission.Result.Frame!);
            var files = RawIngressFileStore.GetPaths(root, started, identity.ArtifactId);
            var sidecar = files.SidecarAbsolutePath;
            var historical = await WritePredecessorSidecarAsync(sidecar,
                await File.ReadAllBytesAsync(sidecar, token).ConfigureAwait(false), token)
                .ConfigureAwait(false);
            var pixels = await File.ReadAllBytesAsync(files.PayloadAbsolutePath, token).ConfigureAwait(false);
            if (evidence == "orphan-zero-fps")
            {
                using var journal = await OpenJournalAsync(root).ConfigureAwait(false);
                using var corruptOrphan = journal.CreateCommand();
                corruptOrphan.CommandText = "UPDATE centered_exposure_attempts SET requested_json = $bad WHERE attempt_id = $attempt;";
                corruptOrphan.Parameters.AddWithValue("$bad", JsonSerializer.SerializeToUtf8Bytes(expected with { TargetFps = 0 }));
                corruptOrphan.Parameters.AddWithValue("$attempt", reservation.Pending.Key.AttemptId.ToString("N"));
                Assert.AreEqual(1, await corruptOrphan.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
                Assert.AreEqual(0L, await ScalarLongAsync(journal, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarLongAsync(journal, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false));
                var retainedSidecar = Directory.GetFiles(root, Path.GetFileName(sidecar), SearchOption.AllDirectories).Single();
                var retainedPayload = Directory.GetFiles(root, Path.GetFileName(files.PayloadAbsolutePath), SearchOption.AllDirectories).Single();
                CollectionAssert.AreEqual(historical, await File.ReadAllBytesAsync(retainedSidecar, token).ConfigureAwait(false));
                CollectionAssert.AreEqual(pixels, await File.ReadAllBytesAsync(retainedPayload, token).ConfigureAwait(false));
                return;
            }
            await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            var oldContext = await ScalarBytesAsync(db, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false);
            var oldDigest = await ScalarStringAsync(db, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false);
            var envelope = CaptureLaneEnvelopeSerializer.Deserialize(oldContext, oldDigest);
            var predecessor = envelope with
            {
                Submission = envelope.Submission with
                {
                    Request = envelope.Submission.Request with { RequestedSetpoint = expected with { TargetFps = null } }
                }
            };
            var (predecessorContext, predecessorDigest) = CaptureLaneEnvelopeSerializer.Serialize(
                predecessor.Configuration, predecessor.Submission);
            using var old = db.CreateCommand();
            old.CommandText = "UPDATE capture_lane_contexts SET context_json = $context, context_sha256 = $digest;";
            old.Parameters.AddWithValue("$context", predecessorContext);
            old.Parameters.AddWithValue("$digest", predecessorDigest);
            Assert.AreEqual(1, await old.ExecuteNonQueryAsync(token).ConfigureAwait(false));

            using var corrupt = db.CreateCommand();
            corrupt.CommandText = "UPDATE centered_exposure_attempts SET requested_json = $bad WHERE capture_id = $capture;";
            corrupt.Parameters.AddWithValue("$bad", evidence switch
            {
                "missing" => (object)DBNull.Value,
                "malformed" => "{"u8.ToArray(),
                "zero-fps" => JsonSerializer.SerializeToUtf8Bytes(expected with { TargetFps = 0 }),
                "negative-fps" => JsonSerializer.SerializeToUtf8Bytes(expected with { TargetFps = -1 }),
                "nonfinite-fps" => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expected)
                    .Replace("\"TargetFps\":15", "\"TargetFps\":1e309", StringComparison.Ordinal)),
                "zero-interval" => JsonSerializer.SerializeToUtf8Bytes(expected with { NextIntervalOverride = TimeSpan.Zero }),
                _ => JsonSerializer.SerializeToUtf8Bytes(expected)
            });
            corrupt.Parameters.AddWithValue("$capture", identity.CaptureId.ToString("N"));
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync(token).ConfigureAwait(false));
            await setup.Ingress.InitializeLanesAsync(token).ConfigureAwait(false);
            var lane = setup.Policy.Definitions.Single(item => item.Name == "standard");
            var claimed = await setup.Ingress.ClaimAsync(lane, "predecessor-dispatch", config, token).ConfigureAwait(false);
            if (evidence == "valid")
            {
                Assert.IsNotNull(claimed);
                Assert.AreEqual(expected, claimed.Context.Submission.Request.RequestedSetpoint);
            }
            else
            {
                Assert.IsNull(claimed);
            }
            var quarantined = evidence == "valid" ? 0L : 1L;
            Assert.AreEqual(quarantined, await ScalarLongAsync(db, """
                SELECT COUNT(*) FROM raw_captures WHERE state = 'quarantined' AND failure_reason = 'lane-evidence-invalid';
                """).ConfigureAwait(false));
            Assert.AreEqual(quarantined, await ScalarLongAsync(db, """
                SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND failure_reason = 'evidence-invalid';
                """).ConfigureAwait(false));
            CollectionAssert.AreEqual(historical, await File.ReadAllBytesAsync(sidecar, token).ConfigureAwait(false));
            CollectionAssert.AreEqual(pixels, await File.ReadAllBytesAsync(files.PayloadAbsolutePath, token).ConfigureAwait(false));
            CollectionAssert.AreEqual(predecessorContext,
                await ScalarBytesAsync(db, "SELECT context_json FROM capture_lane_contexts;").ConfigureAwait(false));
            Assert.AreEqual(predecessorDigest,
                await ScalarStringAsync(db, "SELECT context_sha256 FROM capture_lane_contexts;").ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    private static async Task<byte[]> WritePredecessorSidecarAsync(
        string sidecarPath, byte[] current, CancellationToken cancellationToken)
    {
        var raw = CaptureContractJson.ParseManifest(current).Document!.Manifest;
        var acquisition = raw.Descriptor.CenteredAcquisition!;
        var historical = CaptureContractJson.Serialize(raw with
        {
            Descriptor = raw.Descriptor with
            {
                CenteredAcquisition = acquisition with
                {
                    Module = acquisition.Module! with { RequestedSetpoint = null }
                }
            }
        });
        await File.WriteAllBytesAsync(sidecarPath, historical, cancellationToken).ConfigureAwait(false);
        return historical;
    }

    private static string LegacyArchiveIdentity(JsonObject receipt)
    {
        var staged = receipt.DeepClone().AsObject();
        staged["storageNode"] = string.Empty;
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(staged));
    }

    private static async Task SetArchiveEvidenceAsync(SqliteConnection journal, string identity, CancellationToken token)
    {
        using var oldWriter = journal.CreateCommand();
        oldWriter.CommandText = """
            UPDATE processing_node_inputs SET identity_sha256 = $identity
            WHERE node_id = 'ArchiveStorage' AND kind = 'CanonicalContext' AND name = 'centered-archive';
            """;
        oldWriter.Parameters.AddWithValue("$identity", identity);
        Assert.AreEqual(1, await oldWriter.ExecuteNonQueryAsync(token).ConfigureAwait(false));
    }

    private static async Task<JsonObject> ReadLegacyArchiveReceiptAsync(
        string receiptPath, string root, CancellationToken cancellationToken)
    {
        var receipt = JsonNode.Parse(await File.ReadAllBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false))!.AsObject();
        receipt.Remove("schemaVersion");
        receipt["root"] = StorageRootIdentity.Key(root);
        receipt["storageNode"] = "ArchiveStorage";
        return receipt;
    }


    [TestMethod]
    public async Task CenteredLinearProductPublishesBeforeLaneAckAndSurvivesHotEviction()
    {
        var root = CreateRoot();
        try
        {
            var clock = new ExposureTestTimeProvider(DateTimeOffset.UtcNow);
            using var setup = new CenteredFixture(root, timeProvider: clock);
            var config = CenteredConfiguration("camera");
            var first = await setup.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
            var key = new ExposureKey(first.Manifest.CenteredExposure!.Source.Camera,
                first.Manifest.CenteredExposure.Source.RunId, first.Manifest.Descriptor.Capture.CaptureId);
            var reader = new CenteredExposureProductReader(setup.Ingress, setup.Store);
            var initial = await reader.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LinearExposureDisposition.Pending, initial!.Disposition);

            var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, setup.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using var standard = new StandardCaptureLaneHandler(setup.Factory, persistence, setup.Telemetry,
                NullLogger<StandardCaptureLaneHandler>.Instance, setup.Ingress, setup.Options);
            var lease = await setup.Ingress.ClaimAsync(setup.Policy.Definitions.Single(lane => lane.Name == "standard"),
                "centered-test", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);

            for (var i = 2; i <= CenteredRecentExposureIndex.CapacityPerCamera + 1; i++)
                await setup.AdmitAsync(config, Timestamp(i)).ConfigureAwait(false);
            CollectionAssert.DoesNotContain(setup.Ingress.RecentCenteredExposures.Read(key.Camera).ToArray(), key);

            var cursor = setup.Changes.Version;
            using var wakeupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var wakeup = setup.Changes.WaitForChangeAsync(cursor, wakeupTimeout.Token);
            Assert.IsFalse(wakeup.IsCompleted);
            var processing = Task.Run(async () =>
                await standard.HandleAsync(lease.Context, wakeupTimeout.Token).ConfigureAwait(false));
            for (var sample = 0; sample < 32 && !processing.IsCompleted; sample++)
            {
                var observation = await reader.ReadLinearAsync(key, wakeupTimeout.Token).ConfigureAwait(false);
                Assert.IsNotNull(observation);
                if (observation.Disposition == LinearExposureDisposition.Pending)
                {
                    Assert.IsNull(observation.Revision);
                    Assert.IsNull(observation.Product);
                }
                else
                {
                    Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, observation.Disposition);
                    Assert.IsNotNull(observation.Product);
                }
                await Task.Yield();
            }
            var result = await processing.ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            await wakeup.ConfigureAwait(false);
            var settled = await reader.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(settled);
            Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, settled.Disposition, settled.Reason);
            Assert.AreEqual(first.Manifest.Descriptor.Artifact.ArtifactId, settled.Provenance!.RawArtifactId);
            Assert.AreEqual(44810d, settled.Provenance.RawWhiteThreshold.StoredWhiteLevel);
            Assert.IsNotNull(settled.Product);
            Assert.AreEqual(lease.Context.Execution!.ExecutionId, settled.Product.Slot.ExecutionId);
            var changes = await reader.ReadProductsAfterAsync(key.Camera, 0, 10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, changes);
            Assert.AreEqual(settled.Product, changes[0].Product);

            var graph = setup.Factory.CreateRetainedGraph(config);
            try
            {
                var node = graph.Nodes.Single(node => node.Id == settled.Product.Slot.Producer);
                var evidence = await setup.Store.ReadNodeAsync(key.CaptureId, node.Id, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(evidence);
                var settlements = CenteredExposureProductReader.CreateSettlements(first.Manifest, node, evidence.Status,
                    evidence.Reason, evidence.Outputs, evidence.Inputs!);
                Assert.HasCount(1, settlements);
                Task WriteAsync(CenteredProductSettlement settlement)
                    => setup.Store.WriteNodeAsync(key.CaptureId, node, evidence.Status, evidence.Reason, evidence.Attempt,
                        evidence.ProcessingProfileIdentitySha256, evidence.StartedUtc, evidence.CompletedUtc, evidence.Duration,
                        evidence.Outcome, evidence.Inputs!, lease.WorkId, lease.LeaseToken, evidence.Outputs,
                        lease.Context.Execution, ProcessingNodeExecutionRoute.InProcess, CancellationToken.None, [settlement]).AsTask();
                var versionAfterSettling = setup.Changes.Version;
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var equal = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    await WriteAsync(settlements[0]).ConfigureAwait(false);
                });
                var conflicting = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                        WriteAsync(settlements[0] with { PayloadSha256 = new string('A', 64) })).ConfigureAwait(false);
                });
                start.SetResult();
                await Task.WhenAll(equal, conflicting).ConfigureAwait(false);
                Assert.AreEqual(settled.Product, (await reader.ReadLinearAsync(key, CancellationToken.None)
                    .ConfigureAwait(false))!.Product);
                Assert.AreEqual(versionAfterSettling, setup.Changes.Version);
            }
            finally { graph.DisposeSteps(); }

            var custody = new CenteredExposureInputCustody(setup.Ingress, reader, setup.Store, persistence, clock);
            var holdA = await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            var holdB = await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(holdA);
            Assert.IsNotNull(holdB);
            await using var scopeA = holdA.ConfigureAwait(false);
            await using var scopeB = holdB.ConfigureAwait(false);
            Assert.AreEqual(settled.Provenance.LinearPayloadSha256, PayloadChecksum.ComputeSha256(holdA.Input.Linear.Payload.Span));
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(2L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM centered_exposure_holds;").ConfigureAwait(false));
            await holdA.DisposeAsync().ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM centered_exposure_holds;").ConfigureAwait(false));
            await setup.Ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);

            var peak = new List<CenteredExposureInputLease>();
            try
            {
                for (var i = 0; i < 63; i++)
                {
                    var hold = await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                        .ConfigureAwait(false);
                    Assert.IsNotNull(hold);
                    peak.Add(hold);
                }
                Assert.IsNull(await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false));
                Assert.AreEqual(64L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM centered_exposure_holds;").ConfigureAwait(false));
                clock.Advance(TimeSpan.FromMinutes(2));
                var recoveredHold = await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(recoveredHold);
                await using var recoveredScope = recoveredHold.ConfigureAwait(false);
                Assert.AreEqual(1L, await ScalarLongAsync(db, "SELECT COUNT(*) FROM centered_exposure_holds;").ConfigureAwait(false));
            }
            finally
            {
                foreach (var hold in peak) await hold.DisposeAsync().ConfigureAwait(false);
            }

            var saved = await setup.Store.ReadOutputByArtifactIdAsync(settled.Product.ArtifactId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(saved);
            var operation = new ProcessingLifecycleOperation("delete:centered-test", "delete", settled.Product.OutputIdentitySha256,
                saved.PayloadRelativePath, saved.SidecarRelativePath, "processing-deletion-tombstones/centered-test",
                "retention-expired", 0);
            var firstReader = await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(firstReader);
            await using var firstReaderScope = firstReader.ConfigureAwait(false);
            Assert.IsFalse(await setup.Store.PlanLifecycleOperationAsync(operation, CancellationToken.None).ConfigureAwait(false));
            await new DerivedProductReconciler(root, setup.Store).RunAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(Path.Combine(root, saved.PayloadRelativePath)));
            Assert.IsTrue(File.Exists(Path.Combine(root, saved.SidecarRelativePath)));
            await firstReader.DisposeAsync().ConfigureAwait(false);

            Assert.IsTrue(await setup.Store.PlanLifecycleOperationAsync(operation, CancellationToken.None).ConfigureAwait(false));
            Assert.IsTrue(File.Exists(Path.Combine(root, saved.PayloadRelativePath)));
            Assert.IsNull(await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false));
            await new DerivedProductReconciler(root, setup.Store).RunAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(await setup.Store.ReadOutputByArtifactIdAsync(settled.Product.ArtifactId, CancellationToken.None)
                .ConfigureAwait(false));
            Assert.IsFalse(File.Exists(Path.Combine(root, saved.PayloadRelativePath)));
            Assert.IsFalse(File.Exists(Path.Combine(root, saved.SidecarRelativePath)));
            var expired = await reader.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LinearExposureDisposition.Expired, expired!.Disposition);
            Assert.AreEqual("retention-expired", expired.Reason);
            Assert.AreEqual(settled.Product, expired.Product);
            Assert.AreEqual(settled.Revision, expired.Revision);
            Assert.AreEqual(LinearExposureDisposition.QualifiedNormalizationOnly, expired.Provenance!.Disposition);
            Assert.AreEqual(settled.Provenance.RawArtifactId, expired.Provenance.RawArtifactId);
            Assert.AreEqual(settled.Provenance.RawDescriptorSha256, expired.Provenance.RawDescriptorSha256);
            Assert.AreEqual(settled.Provenance.RawPayloadSha256, expired.Provenance.RawPayloadSha256);
            Assert.AreEqual(settled.Provenance.LinearDescriptorSha256, expired.Provenance.LinearDescriptorSha256);
            Assert.AreEqual(settled.Provenance.LinearPayloadSha256, expired.Provenance.LinearPayloadSha256);
            Assert.AreEqual(settled.Provenance.OutputIdentitySha256, expired.Provenance.OutputIdentitySha256);
            Assert.IsNull(await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredLinearReaderKeepsAdmissionSlotAcrossManualReplay()
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            var config = CenteredConfiguration("camera");
            var admitted = await setup.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
            var binding = admitted.Manifest.CenteredExposure!;
            var key = new ExposureKey(binding.Source.Camera, binding.Source.RunId, admitted.Manifest.Descriptor.Capture.CaptureId);
            var reader = new CenteredExposureProductReader(setup.Ingress, setup.Store);
            var requested = await setup.GraphOperations.SubmitReplayAsync(
                new(key.CaptureId, admitted.Manifest.CaptureGraphRevisionId!, admitted.Manifest.Descriptor.Artifact.ArtifactId),
                "manual-replay", "centered-test", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Pending, requested.Execution.Status);
            Assert.AreEqual(LinearExposureDisposition.Pending,
                (await reader.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false))!.Disposition);
            Assert.IsNull(await setup.Store.ClaimReplayAsync("centered-manual", CancellationToken.None).ConfigureAwait(false));

            var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, setup.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using var standard = new StandardCaptureLaneHandler(setup.Factory, persistence, setup.Telemetry,
                NullLogger<StandardCaptureLaneHandler>.Instance, setup.Ingress, setup.Options);
            var live = await setup.Ingress.ClaimAsync(setup.Policy.Definitions.Single(lane => lane.Name == "standard"),
                "centered-live", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(live);
            var result = await standard.HandleAsync(live.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var original = await reader.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(original?.Product);
            Assert.AreEqual(live.Context.Execution!.ExecutionId, original.Product.Slot.ExecutionId);
            await setup.Ingress.CompleteAsync(live, CancellationToken.None).ConfigureAwait(false);

            var replay = await setup.Store.ClaimReplayAsync("centered-manual", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(replay);
            var replayExecution = new ProcessingExecutionContext(replay.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                replay.Execution.GraphRevisionId, replay.Execution.LocalPlanIdentitySha256, false, replay.WorkId,
                replay.LeaseToken, replay.LeaseOwner, replay.Execution.DeadlineUtc);
            var graph = setup.Factory.CreateRetainedGraph(replay.Configuration);
            try
            {
                var replayResult = await FrameProcessingWorker.ProcessGraphItemAsync(
                    new(replay.Configuration, replay.Submission, replay.RawCapture, replay.WorkId, replay.LeaseToken, replayExecution),
                    graph, persistence, setup.Telemetry, replay.Execution.AttemptCount, NullLogger.Instance,
                    CancellationToken.None, durableAttempt: replay.ClaimCount).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replayResult.Outcome, replayResult.Reason);
                var completedReplay = await setup.Store.CompleteReplayAsync(replay, replayResult, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completedReplay.Status);
            }
            finally { graph.DisposeSteps(); }

            var after = await reader.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(original.Product, after!.Product);
            Assert.AreEqual(original.Revision, after.Revision);
            Assert.AreEqual(original.Provenance!.OutputIdentitySha256, after.Provenance!.OutputIdentitySha256);
            Assert.IsNull(await reader.ReadProductAsync(key, original.Product.Slot with
            {
                ExecutionId = replay.Execution.ExecutionId
            }, CancellationToken.None).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredLinearReaderRecoversOriginalTerminalLaneFailureWithoutOutput(bool expire)
    {
        var root = CreateRoot();
        try
        {
            var config = CenteredConfiguration("camera");
            ExposureKey key;
            var clock = new ExposureTestTimeProvider(Timestamp(1).AddMilliseconds(12));
            using (var initial = new CenteredFixture(root, timeProvider: clock))
            {
                var admitted = await initial.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
                var source = admitted.Manifest.CenteredExposure!.Source;
                key = new(source.Camera, source.RunId, admitted.Manifest.Descriptor.Capture.CaptureId);
                var lane = initial.Policy.Definitions.Single(definition => definition.Name == "standard");
                var generation = initial.Changes.Version;
                if (expire) clock.Advance(TimeSpan.FromHours(2));
                var lease = await initial.Ingress.ClaimAsync(lane, "fault-worker", config, CancellationToken.None)
                    .ConfigureAwait(false);
                if (expire)
                {
                    Assert.IsNull(lease);
                }
                else
                {
                    Assert.IsNotNull(lease);
                    Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, await initial.Ingress.FailAsync(
                        lease, CaptureLaneHandlerResult.Terminal("fixture.linear-before-node"), CancellationToken.None)
                        .ConfigureAwait(false));
                }
                Assert.AreNotEqual(generation, initial.Changes.Version);
            }

            using var resumed = new CenteredFixture(root);
            var read = new CenteredExposureProductReader(resumed.Ingress, resumed.Store);
            var disposition = await read.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(disposition);
            Assert.AreEqual(expire ? LinearExposureDisposition.Expired : LinearExposureDisposition.Failed, disposition.Disposition);
            Assert.AreEqual(expire ? "processing.live-maximum-age" : "fixture.linear-before-node", disposition.Reason);
            Assert.IsNull(disposition.Product);
            Assert.IsNull(disposition.Provenance);
            Assert.IsNotNull(disposition.Revision);
            var reconnect = await read.ReadProductsAfterAsync(key.Camera, 0, 1, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(disposition.Revision, reconnect.Single().Revision);
            Assert.AreEqual(expire ? ExposureProductAvailability.Expired : ExposureProductAvailability.Failed, reconnect.Single().Availability);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredNoStep3SettlesInAdmissionAndMemoryOnlyIsRefused()
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            using var setup = new CenteredFixture(root);
            var config = CenteredConfiguration("camera");
            var memoryOnly = config with
            {
                Pipeline = config.Pipeline with
                {
                    Steps =
                [config.Pipeline.Steps[0] with { Publication = new(CaptureProcessingPersistenceMode.MemoryOnly) }]
                }
            };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                setup.Ingress.ReserveCenteredAsync(memoryOnly, CenteredTestResources, null, token)).ConfigureAwait(false);

            config = config with
            {
                Pipeline = config.Pipeline with
                {
                    Steps =
                [new("CenteredProbe", "raw-only", DependsOn: ["$raw"], Required: false)]
                }
            };
            var version = setup.Changes.Version;
            var receipt = await setup.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
            Assert.AreNotEqual(version, setup.Changes.Version);
            var source = receipt.Manifest.CenteredExposure!.Source;
            var key = new ExposureKey(source.Camera, source.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var reader = new CenteredExposureProductReader(setup.Ingress, setup.Store);
            var lookup = await reader.ReadLinearAsync(key, token).ConfigureAwait(false);
            Assert.IsNotNull(lookup);
            Assert.AreEqual(LinearExposureDisposition.Unavailable, lookup.Disposition);
            Assert.AreEqual("linear.no-configured-step", lookup.Reason);
            Assert.IsNotNull(lookup.Revision);
            var cursor = (await reader.ReadProductsAfterAsync(source.Camera, 0, 1, token).ConfigureAwait(false)).Single();
            Assert.AreEqual(lookup.Revision, cursor.Revision);
            Assert.AreEqual(ExposureProductAvailability.Unavailable, cursor.Availability);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredNamedMetadataKeepsOriginalReferenceAfterDeletionAndRestart()
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            var config = WithCenteredProbe(CenteredConfiguration("camera"), emitMetadata: true);
            ExposureKey key;
            ExposureProductLookup original;
            using (var setup = new CenteredFixture(root))
            {
                var raw = await setup.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
                var source = raw.Manifest.CenteredExposure!.Source;
                key = new(source.Camera, source.RunId, raw.Manifest.Descriptor.Capture.CaptureId);
                var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, setup.Telemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance);
                using var handler = new StandardCaptureLaneHandler(setup.Factory, persistence, setup.Telemetry,
                    NullLogger<StandardCaptureLaneHandler>.Instance, setup.Ingress, setup.Options);
                var lane = setup.Policy.Definitions.Single(item => item.Name == "standard");
                var lease = await setup.Ingress.ClaimAsync(lane, "metadata-fixture", config, token).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await handler.HandleAsync(lease.Context, token)
                    .ConfigureAwait(false)).Outcome);
                await setup.Ingress.CompleteAsync(lease, token).ConfigureAwait(false);
                var reader = new CenteredExposureProductReader(setup.Ingress, setup.Store);
                original = (await reader.ReadProductsAfterAsync(key.Camera, 0, 10, token).ConfigureAwait(false))
                    .Single(item => item.Slot.SchemaVersion == "centered-fixture-metadata-v1");
                Assert.AreEqual(ExposureProductAvailability.Available, original.Availability);
                Assert.IsNotNull(original.Product?.ContentIdentitySha256);
                var saved = await setup.Store.ReadOutputByArtifactIdAsync(original.Product.ArtifactId, token).ConfigureAwait(false);
                Assert.IsNotNull(saved);
                Assert.IsTrue(await setup.Store.PlanLifecycleOperationAsync(new("delete:centered-metadata", "delete",
                    original.Product.OutputIdentitySha256, saved.PayloadRelativePath, saved.SidecarRelativePath,
                    "processing-deletion-tombstones/centered-metadata", "retention-expired", 0), token).ConfigureAwait(false));
                await new DerivedProductReconciler(root, setup.Store).RunAsync(token).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(Path.Combine(root, saved.PayloadRelativePath)));
                Assert.IsFalse(File.Exists(Path.Combine(root, saved.SidecarRelativePath)));
            }

            using var restarted = new CenteredFixture(root);
            var reopened = new CenteredExposureProductReader(restarted.Ingress, restarted.Store);
            var exact = await reopened.ReadProductAsync(key, original.Slot, token).ConfigureAwait(false);
            Assert.IsNotNull(exact);
            Assert.AreEqual(ExposureProductAvailability.Expired, exact.Availability);
            Assert.AreEqual(original.Revision, exact.Revision);
            Assert.AreEqual(original.Product, exact.Product);
            Assert.AreEqual("retention-expired", exact.Reason);
            Assert.IsNotNull(original.Revision);
            var ordered = await reopened.ReadProductsAfterAsync(key.Camera, original.Revision.Value - 1, 1, token).ConfigureAwait(false);
            Assert.AreEqual(exact, ordered.Single());
            var gallery = new CameraAgentArtifactService(restarted.Options, restarted.Store, new CameraAgentPreviewEncoder());
            var open = await gallery.OpenContentAsync(original.Product!.ArtifactId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.NotFound, open.Status);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CenteredEnvironmentalAcquisitionSurvivesRestart(bool unavailable)
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            var utc = Timestamp(1);
            var clock = new FixedTimeProvider(utc.AddMilliseconds(12));
            var config = CenteredConfiguration("camera");
            var acquisitionId = Guid.NewGuid();
            ExposureKey key;
            using (var setup = new CenteredFixture(root, timeProvider: clock))
            using (var outbox = new SqliteEnvironmentalObservationOutbox(clock))
            {
                var reservation = await setup.DispatchAsync(config).ConfigureAwait(false);
                if (!unavailable)
                {
                    var command = EnvironmentalCaptureAcquisition.Prefix(acquisitionId) + new string('A', 32);
                    var claim = await outbox.ClaimOnDemandAsync(root, command, new string('B', 64), "sqm", "capture-before", null,
                        utc, TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    await outbox.CompleteOnDemandAsync(root, command, claim.LeaseToken,
                        new("sqm", EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionDisposition.Produced,
                            "fixture", Guid.NewGuid(), utc, utc), token).ConfigureAwait(false);
                }

                var submission = CenteredSubmission(utc) with
                {
                    CenteredAcquisition =
                    new(CaptureCadenceMode.MinimumStartInterval, TimeSpan.FromSeconds(1), utc, TimeSpan.FromMilliseconds(12),
                        acquisitionId, unavailable, ["sqm"], null)
                };
                var capture = await setup.Ingress.AcceptCenteredAsync(config, submission, reservation, token).ConfigureAwait(false);
                var source = capture.Manifest.CenteredExposure!.Source;
                key = new(source.Camera, source.RunId, capture.Manifest.Descriptor.Capture.CaptureId);
            }

            using var recovered = new CenteredFixture(root, timeProvider: clock);
            await recovered.Ingress.InitializeAsync(token).ConfigureAwait(false);
            var raw = await recovered.Ingress.ReadCenteredRawAsync(key, token).ConfigureAwait(false);
            Assert.IsNotNull(raw);
            var evidence = raw.Manifest.Descriptor.CenteredAcquisition!;
            Assert.AreEqual(acquisitionId, evidence.AcquisitionAttemptId);
            Assert.AreEqual(unavailable, evidence.EnvironmentalUnavailable);
            Assert.AreEqual("sqm", evidence.EnvironmentalBeforeCaptureSources!.Single());
            Assert.IsNull(raw.Manifest.Descriptor.CycleEvidence);
            using var restartedOutbox = new SqliteEnvironmentalObservationOutbox(clock);
            var available = await restartedOutbox.ReadCaptureAvailabilityAsync(root, key.CaptureId, acquisitionId, token)
                .ConfigureAwait(false);
            Assert.AreEqual("sqm", available.Sources.Single());
            Assert.AreEqual(unavailable ? 1 : 0, available.UnavailableSources.Count);
            Assert.IsFalse(available.Pending);
            await Assert.ThrowsExactlyAsync<EnvironmentalObservationIdentityConflictException>(async () =>
                await restartedOutbox.ReadCaptureAvailabilityAsync(root, key.CaptureId, Guid.NewGuid(), token).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredDispatchKeepsIntegrityAndCameraCursorUsesItsOwnIndex()
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            using var setup = new CenteredFixture(root);
            using var site = new ProtectedDeploymentLocationStore(setup.Options,
                new DataProtectionDeploymentLocationProtector(new EphemeralDataProtectionProvider()), TimeProvider.System,
                NullLogger<ProtectedDeploymentLocationStore>.Instance);
            var seed = new DeploymentLocationSeed("fixture-site", "fixture", null, DateTimeOffset.UnixEpoch, null,
                new(35, -113, 500, "UTC"));
            var active = await site.InitializeAsync(seed, token).ConfigureAwait(false);
            var left = WithoutCalibration(CenteredConfiguration("left") with { DeploymentLocation = active });
            var right = WithoutCalibration(CenteredConfiguration("right") with { DeploymentLocation = active });
            await setup.AdmitAsync(left, Timestamp(1)).ConfigureAwait(false);
            await site.FullVerifyAsync(seed, token).ConfigureAwait(false);
            Assert.IsFalse(site.RequiresRetainedIntegrityScrub);
            var before = site.RetainedIntegrityGeneration;
            var dispatched = await setup.DispatchAsync(left).ConfigureAwait(false);
            Assert.AreEqual(before, site.RetainedIntegrityGeneration);
            Assert.IsFalse(site.RequiresRetainedIntegrityScrub);
            await setup.Ingress.AcceptCenteredAsync(left, CenteredSubmission(Timestamp(1).AddSeconds(1)), dispatched, token)
                .ConfigureAwait(false);
            var reader = new CenteredExposureProductReader(setup.Ingress, setup.Store);
            var camera = dispatched.Pending.Key.Camera;
            var leftProducts = await reader.ReadProductsAfterAsync(camera, 0, 8, token).ConfigureAwait(false);
            Assert.HasCount(2, leftProducts);
            var watermark = leftProducts[^1].Revision!.Value;
            for (var shot = 3; shot <= 6; shot++)
            {
                await setup.AdmitAsync(right, Timestamp(1).AddSeconds(shot)).ConfigureAwait(false);
                Assert.HasCount(0, await reader.ReadProductsAfterAsync(camera, watermark, 1, token).ConfigureAwait(false));
            }
            Assert.IsFalse(site.RequiresRetainedIntegrityScrub);

            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            using var explain = db.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + SqliteCaptureProcessingStore.CenteredProductsAfterSql;
            explain.Parameters.AddWithValue("$agent", camera.AgentId);
            explain.Parameters.AddWithValue("$camera", camera.CameraId);
            explain.Parameters.AddWithValue("$revision", watermark);
            explain.Parameters.AddWithValue("$limit", 1);
            var steps = new List<string>();
            using var plan = await explain.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await plan.ReadAsync(token).ConfigureAwait(false)) steps.Add(plan.GetString(3));
            Assert.IsTrue(steps.Any(step => step.Contains("ix_centered_product_camera", StringComparison.Ordinal)),
                string.Join("\n", steps));
        }
        finally { DeleteRoot(root); }

        static CameraModuleConfig WithoutCalibration(CameraModuleConfig config) => config with
        {
            Pipeline = config.Pipeline with
            {
                Steps = [new("CenteredProbe", "raw-only", DependsOn: ["$raw"], Required: false)]
            }
        };
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task CenteredInstallerPreflightAcceptsBaselineAndRefusesOlderSchemasOrDrift()
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            var path = Path.Combine(root, "journal", "raw-ingress.db");
            var instance = Guid.NewGuid();
            var paths = Installer.InstallationPaths.Create(root, instance, "hyg-v42-production");
            var files = new Installer.CameraAgentStateDatabaseView(Path.Combine(root, "identity.db"), path);
            var requirements = new Installer.CameraAgentStateRequirements(Installer.CameraAgentStateContract.Current, null, 17, null);
            Assert.HasCount(0, Evaluate(requirements));

            RawCaptureReceipt original;
            // This raw-only predecessor has no processing schema or astrometry index.
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                var config = CenteredConfiguration("camera") with { CenteredExposure = null };
                original = (await ingress.AcceptAsync(config, CenteredSubmission(Timestamp(1)), token).ConfigureAwait(false))!;
            }
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
                await ExecuteTestSqlAsync(connection, DropCenteredSchemaSql + "PRAGMA user_version=16;").ConfigureAwait(false);
            Assert.AreEqual("raw-ingress-schema", Evaluate(requirements).Single().Code);

            using (var setup = new CenteredFixture(root))
                await setup.Ingress.InitializeAsync(token).ConfigureAwait(false);
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(17L, await ScalarLongAsync(connection, "PRAGMA user_version;").ConfigureAwait(false));
                using var manifest = connection.CreateCommand();
                manifest.CommandText = "SELECT manifest_sha256 FROM raw_captures;";
                Assert.AreEqual(original.CommittedManifestSha256, await manifest.ExecuteScalarAsync(token).ConfigureAwait(false));
            }
            Assert.HasCount(0, Evaluate(requirements));
            var rollback = Evaluate(requirements with { RawIngressSchema = 16 }).Single();
            Assert.AreEqual("17", rollback.Observed);
            Assert.AreEqual("16", rollback.Expected);

            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
                await ExecuteTestSqlAsync(connection, "CREATE TABLE centered_contract_drift(id INTEGER);").ConfigureAwait(false);
            Assert.AreEqual("raw-ingress-schema", Evaluate(requirements).Single().Code);

            Installer.CameraAgentStatePreflightFinding[] Evaluate(
                Installer.CameraAgentStateRequirements target)
                => Installer.CameraAgentStatePreflight.Evaluate(paths, instance, null, null, target, Installer.CameraAgentStateContract.Current,
                    1000, 1000, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess, files)
                    .Findings.Where(finding => finding.Boundary == "raw-ingress" && finding.Blocking).ToArray();
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredOpticalScaleUsesArchivedCalibratedReadout()
    {
        var root = CreateRoot();
        var token = CancellationToken.None;
        try
        {
            var config = CenteredConfiguration("camera");
            var optics = config.Rig.Optics with
            {
                FocalLengthXPixels = 80,
                FocalLengthYPixels = 80,
                ImageCircleRadiusPixels = 1,
                CalibrationVersion = "retained-calibration"
            };
            var calibrated = config with { Rig = config.Rig with { Optics = optics } };
            ExposureKey first;
            ExposureKey second;
            using (var setup = new CenteredFixture(root))
            {
                var capture = await setup.AdmitAsync(calibrated, Timestamp(1)).ConfigureAwait(false);
                var source = capture.Manifest.CenteredExposure!.Source;
                first = new(source.Camera, source.RunId, capture.Manifest.Descriptor.Capture.CaptureId);
                var switched = calibrated with
                {
                    Rig = calibrated.Rig with
                    {
                        Optics = optics with
                        {
                            ProjectionModel = "Perspective",
                            FieldOfViewDegrees = 150,
                            FocalLengthYPixels = 95,
                            ImageCircleRadiusPixels = null
                        }
                    }
                };
                capture = await setup.AdmitAsync(switched, Timestamp(2)).ConfigureAwait(false);
                source = capture.Manifest.CenteredExposure!.Source;
                second = new(source.Camera, source.RunId, capture.Manifest.Descriptor.Capture.CaptureId);
                Assert.AreNotEqual(first.RunId, second.RunId);
            }

            using var reopened = new CenteredFixture(root);
            var reader = new CenteredExposureProductReader(reopened.Ingress, reopened.Store);
            var oldInput = await reader.ReadExposureAsync(first, token).ConfigureAwait(false);
            Assert.IsNotNull(oldInput);
            Assert.AreEqual(180 * 3600 / Math.PI / 80, oldInput.Geometry.PixelScaleArcseconds!.Value, 1e-9);
            Assert.AreEqual(optics, (await reopened.Ingress.ReadCenteredRawAsync(first, token).ConfigureAwait(false))!
                .Run.Configuration.Rig.Optics);
            var newInput = await reader.ReadExposureAsync(second, token).ConfigureAwait(false);
            Assert.IsNotNull(newInput);
            Assert.IsNull(newInput.Geometry.PixelScaleArcseconds);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public void CenteredTypedMixedInputsKeepRawPrimaryAndExcludeOptionalProducerFromRequiredEdges()
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            var config = WithCenteredProbe(CenteredConfiguration("camera"));
            var graph = setup.Factory.CreateGraph(config);
            try
            {
                var mixed = graph.Nodes.Single(node => node.Id == "test-consumer");
                CollectionAssert.AreEqual(new List<string> { "$raw", "linear" }, mixed.DeclaredDependencies!.ToArray());
                CollectionAssert.AreEqual(new List<string> { "linear" }, mixed.Dependencies.ToArray());
                Assert.IsTrue(mixed.OptionalDependencies!.Contains("linear"));
                var compiled = graph.SharedPlan!.Nodes.Single(node => node.Definition.Id == mixed.Id);
                Assert.AreEqual(ProcessingGraphInputBindingKind.PrimaryArtifact, compiled.Definition.Inputs[0].BindingKind);
                Assert.IsTrue(compiled.Definition.Inputs[0].Required);
                Assert.AreEqual(ProcessingGraphInputBindingKind.AuxiliaryArtifact, compiled.Definition.Inputs[1].BindingKind);
                Assert.IsFalse(compiled.Definition.Inputs[1].Required);
            }
            finally { graph.DisposeSteps(); }

            Assert.ThrowsExactly<InvalidOperationException>(() => setup.Factory.CreateGraph(config with { CenteredExposure = null }));
            Assert.ThrowsExactly<InvalidOperationException>(() => setup.Factory.CreateGraph(
                WithCenteredProbe(CenteredConfiguration("camera"), requiredVariant: "wrong-variant")));
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredOptionalCalibrationRefusalStillRunsRawConsumer()
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            var config = WithCenteredProbe(CenteredConfiguration("camera"));
            var reservation = await setup.DispatchAsync(config).ConfigureAwait(false);
            var submission = CenteredSubmission(Timestamp(1));
            var frame = submission.Result.Frame!;
            submission = submission with
            {
                Result = submission.Result with
                {
                    Frame = frame with
                    {
                        Layout = frame.Layout! with
                        {
                            StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1,
                            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
                        }
                    }
                }
            };
            var admitted = await setup.Ingress.AcceptCenteredAsync(config, submission, reservation, CancellationToken.None)
                .ConfigureAwait(false);
            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId,
                admitted.Manifest.Descriptor.Capture.CaptureId);
            var lease = await setup.Ingress.ClaimAsync(setup.Policy.Definitions.Single(lane => lane.Name == "standard"),
                "centered-optional-input", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, setup.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using var standard = new StandardCaptureLaneHandler(setup.Factory, persistence, setup.Telemetry,
                NullLogger<StandardCaptureLaneHandler>.Instance, setup.Ingress, setup.Options);
            var result = await standard.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var rawDependent = await setup.Store.ReadNodeAsync(key.CaptureId, "test-consumer", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(DurableProcessingNodeStatus.Skipped, rawDependent!.Status);
            Assert.AreEqual(ProcessingReasonCodes.MissingInput, rawDependent.Reason);
            Assert.IsTrue(rawDependent.Inputs!.Any(input =>
                input.Name == "centered-fixture-saw-raw" &&
                input.IdentitySha256 == admitted.Manifest.Descriptor.Artifact.ChecksumSha256));
            var disposition = await new CenteredExposureProductReader(setup.Ingress, setup.Store)
                .ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LinearExposureDisposition.Unsupported, disposition!.Disposition);
            Assert.AreEqual(FrameWhiteThreshold.OpaqueAdcReason, disposition.Reason);
            await setup.Ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    private static CameraModuleConfig WithCenteredProbe(
        CameraModuleConfig config, string? requiredVariant = null, bool emitMetadata = false)
        => config with
        {
            Pipeline = config.Pipeline with
            {
                Steps = [.. config.Pipeline.Steps,
            new("CenteredProbe", "test-consumer", Options: JsonSerializer.SerializeToElement(new { requiredVariant, emitMetadata }),
                DependsOn: ["$raw", "linear"], Required: false)]
            }
        };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The registered factory constructs the probe options via reflection.")]
    private sealed class CenteredProbeOptions
    {
        public string? RequiredVariant { get; init; }
        public bool EmitMetadata { get; init; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The registered factory constructs this test stage via ActivatorUtilities.")]
    private sealed class CenteredProbeStep(CaptureProcessingStepMetadata metadata, CenteredProbeOptions options)
        : ConfigurableCaptureProcessingStep<CenteredProbeOptions>(metadata, options), ICaptureProcessingGraphStep,
            ICenteredRawPrimaryCaptureProcessingStep
    {
        public bool Enabled => true;
        public string RecipeName => "centered-probe-recipe";
        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
        public string OutputVariant => "centered-probe";
        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated };

        public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements { get; } =
            [new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Calibrated },
                new HashSet<string> { BuiltInProcessingRecipes.LinearNormalization, BuiltInProcessingRecipes.ReferenceCalibration },
                Variant: options.RequiredVariant, Required: options.RequiredVariant is not null)];

        public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        {
            Assert.IsTrue(context.GetDependencyArtifacts().Any(artifact => artifact.Role == FrameArtifactRole.Raw));
            var descriptor = context.ReconstructionDescriptor!;
            context.RecordCanonicalInput("centered-fixture-saw-raw", "centered-fixture-v1", descriptor.Artifact.ChecksumSha256);
            if (Options.EmitMetadata)
            {
                var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                    RecipeName, "1.0.0", "centered-test-v1", JsonSerializer.SerializeToElement(new { })));
                var source = new[] { descriptor.Artifact.ArtifactId };
                var payload = "{\"valid\":true}"u8.ToArray();
                var checksum = ProcessingIdentity.ComputePayloadSha256(payload);
                var product = new ProcessingProduct(OutputRole, OutputVariant,
                    ProcessingIdentity.CreateOutputIdentity(OutputRole, OutputVariant, recipe.IdentitySha256, source),
                    "application/json", null, payload, checksum, recipe, [], source, descriptor.Controls.EffectiveExposure,
                    CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor))
                {
                    Kind = ProcessingProductKind.Metadata,
                    SchemaVersion = "centered-fixture-metadata-v1",
                    ContentIdentitySha256 = checksum
                };
                context.AddProcessingOutcome(ProcessingOutcome.Produced(product));
            }
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow(2800d)]
    [DataRow(2799.75d)]
    public void CenteredWhiteThresholdMapsNativeCodeBeforeCountingSaturation(double white)
    {
        var layout = CenteredLayout() with { WhiteLevel = white };
        Assert.IsTrue(FrameWhiteThreshold.TryResolveStored(layout, out var threshold, out var reason), reason);
        Assert.AreEqual((ushort)44810, threshold);
        CollectionAssert.AreEqual(WhiteClassification,
            new ushort[] { 44794, 44810, 44826 }.Select(sample => sample >= threshold).ToArray());
        var opaque = layout with
        {
            StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
        Assert.IsFalse(FrameWhiteThreshold.TryResolveStored(opaque, out _, out reason));
        Assert.AreEqual(FrameWhiteThreshold.OpaqueAdcReason, reason);
    }

    [TestMethod]
    public async Task CenteredModeAndGraphSwitchKeepInFlightBindingAndLegacyIdentity()
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            var centered = CenteredConfiguration("camera");
            var reserved = await setup.DispatchAsync(centered).ConfigureAwait(false);
            var legacy = centered with { CenteredExposure = null };
            var legacySubmission = CenteredSubmission(Timestamp(2));
            await setup.Ingress.ReconcileCenteredControlAsync(legacy, CancellationToken.None, reserved.Pending.Key.Camera)
                .ConfigureAwait(false);
            var inFlight = await setup.Ingress.AcceptCenteredAsync(centered, CenteredSubmission(Timestamp(1)),
                reserved, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(reserved.Revision.State.RevisionId, inFlight.Manifest.CaptureGraphRevisionId);
            Assert.AreEqual(reserved.Pending.Key, inFlight.Manifest.CenteredExposure!.Source);
            var legacyReceipt = await setup.Ingress.AcceptAsync(legacy, legacySubmission, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNull(legacyReceipt!.Manifest.CenteredExposure);
            Assert.AreEqual(RawCaptureDescriptorFactory.CreateStableIds(legacy, legacySubmission).CaptureId,
                legacyReceipt.Manifest.Descriptor.Capture.CaptureId);

            var selectedAgain = await setup.Ingress.ReserveCenteredAsync(centered, CenteredTestResources, null,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(reserved.Pending.Key.RunId, selectedAgain.Pending.Key.RunId);
            Assert.IsNull(selectedAgain.Pending.SourceCaptureId);
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CenteredPhysicalSceneUsesAuthorizedFrozenSiteAndIndependentCatalog()
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            using var stage = new ProjectedSceneStagingStore(setup.Options);
            var config = CenteredConfiguration("physical");
            var location = config.DeploymentLocation!;
            var when = Timestamp(1);
            var catalog = new CenteredSceneCatalog(
                new CelestialCatalogObject("independent-star", "Independent star",
                    AstronomyTime.LocalMeanSiderealDegrees(when, location.LongitudeDegrees) / 15,
                    location.LatitudeDegrees, 0, 1));
            var stager = new CaptureProjectedSceneStager(stage, catalog);
            var reservation = await setup.Ingress.ReserveCenteredAsync(config, stager.FrozenResources, null,
                CancellationToken.None).ConfigureAwait(false);
            await setup.Ingress.DispatchCenteredAsync(reservation.Pending.Key, CenteredTestSettings, null, "physical-fixture",
                CancellationToken.None).ConfigureAwait(false);
            var receipt = await setup.Ingress.AcceptCenteredAsync(config, CenteredSubmission(when), reservation,
                CancellationToken.None).ConfigureAwait(false);
            var locations = new CenteredSceneTestLocation(location);
            var provider = new CenteredExposureSceneProvider(setup.Ingress, stager, catalog, () => locations);
            var source = reservation.Pending.Key;
            var key = new ExposureKey(source.Camera, source.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var facts = await provider.ReadPredictionFactsAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(facts);
            var prediction = await provider.ResolvePredictionsAsync(facts, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(prediction);
            Assert.AreEqual(ProjectedSceneKind.Predicted, prediction.Kind);
            Assert.AreEqual(when.AddMilliseconds(5), prediction.EffectiveUtc);
            Assert.AreEqual(location.ToProvenance(), locations.Requested);
            Assert.AreEqual(key.CaptureId, prediction.Source.CaptureId);
            Assert.AreEqual("independent-star", prediction.Objects.Single().Id);
            var presentation = await provider.ResolvePresentationAsync(receipt.Manifest.CenteredExposure!,
                receipt.Manifest.Descriptor, new(), null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(prediction.SceneIdentitySha256, presentation!.SceneIdentitySha256);

            var otherResources = facts.Resources with
            {
                Stars = facts.Resources.Stars! with { DatabaseSha256 = new string('B', 64) }
            };
            Assert.IsNull(await provider.ResolvePredictionsAsync(facts with { Resources = otherResources },
                CancellationToken.None).ConfigureAwait(false));
        }
        finally { DeleteRoot(root); }
    }

    private sealed class CenteredSceneCatalog(CelestialCatalogObject star) : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _catalog = new([star]);

        public CatalogMetadata Metadata { get; } = new("centered-study", "fixture", new Uri("https://example.invalid/catalog"),
            new string('A', 64), "fixture", "2");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _catalog.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query, CancellationToken cancellationToken = default)
            => _catalog.QueryCandidatesAsync(query, cancellationToken);
    }

    private sealed class CenteredSceneTestLocation(DeploymentLocationSnapshot snapshot) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => snapshot;

        internal CaptureLocationProvenance? Requested { get; private set; }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed,
            CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);

        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
        {
            Requested = provenance;
            return snapshot;
        }
    }

    internal static async Task RunCenteredRetainedCorpusAsync(string evidenceRoot)
    {
        var root = CreateRoot();
        try
        {
            using var setup = new CenteredFixture(root);
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(evidenceRoot, "mono-november", "expected-mono.json")).ConfigureAwait(false));
            var sequence = 1;
            foreach (var entry in manifest.RootElement.EnumerateArray())
            {
                var artifact = entry.GetProperty("artifact").GetString()!;
                var payload = await File.ReadAllBytesAsync(Path.Combine(evidenceRoot, "mono-november", artifact))
                    .ConfigureAwait(false);
                var width = entry.GetProperty("width").GetInt32();
                var height = entry.GetProperty("height").GetInt32();
                var expectedHash = entry.GetProperty("rawSha256").GetString()!.ToUpperInvariant();
                var layout = CenteredLayout() with
                {
                    Width = width,
                    Height = height,
                    StrideBytes = width * 2,
                    ByteLength = payload.Length,
                    StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                    BlackLevel = entry.GetProperty("blackLevel").GetDouble(),
                    WhiteLevel = entry.GetProperty("whiteLevel").GetDouble(),
                    Readout = new(width, height, 0, 0, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
                };
                await ReplayCenteredCorpusFrameAsync(setup, "mono-study", artifact, payload, expectedHash, layout, Timestamp(sequence++))
                    .ConfigureAwait(false);
            }

            using var oracle = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(evidenceRoot, "geometry", "geometry-oracle.json")).ConfigureAwait(false));
            var example = oracle.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Array)
                .SelectMany(property => property.Value.EnumerateArray())
                .Single(value => value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("file", out var name) && name.GetString() == "bayer-103.bin");
            var bayer = await File.ReadAllBytesAsync(Path.Combine(evidenceRoot, "geometry", "bayer-103.bin")).ConfigureAwait(false);
            var bayerLayout = CenteredLayout() with
            {
                Width = 160,
                Height = 128,
                StrideBytes = 320,
                ByteLength = bayer.Length,
                PixelFormat = CameraPixelFormat.BayerRggb16,
                CfaPattern = ColorFilterArrayPattern.Rggb,
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                BlackLevel = 64,
                WhiteLevel = 4095,
                Readout = new(162, 130, 1, 1, 160, 128, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1)
            };
            await ReplayCenteredCorpusFrameAsync(setup, "odd-bayer-study", "bayer-103.bin", bayer,
                example.GetProperty("sha256").GetString()!.ToUpperInvariant(), bayerLayout, Timestamp(sequence))
                .ConfigureAwait(false);
        }
        finally { DeleteRoot(root); }
    }

    private static async Task ReplayCenteredCorpusFrameAsync(CenteredFixture setup, string camera, string source,
        byte[] payload, string expectedHash, FrameLayoutDescriptor layout, DateTimeOffset utc)
    {
        Assert.AreEqual(expectedHash, PayloadChecksum.ComputeSha256(payload));
        var baseline = CenteredConfiguration(camera);
        var config = baseline with
        {
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with
                {
                    WidthPixels = layout.Readout!.NativeWidth,
                    HeightPixels = layout.Readout.NativeHeight,
                    PixelFormat = layout.PixelFormat,
                    StrideBytes = layout.StrideBytes,
                    ColorMode = layout.PixelFormat == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    ResponseMode = layout.PixelFormat == CameraPixelFormat.Mono16 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw
                }
            }
        };
        var submission = CenteredSubmission(utc);
        submission = submission with
        {
            Result = submission.Result with
            {
                Frame = submission.Result.Frame! with
                {
                    Width = layout.Width,
                    Height = layout.Height,
                    PixelFormat = layout.PixelFormat,
                    PixelData = payload,
                    Metadata = submission.Result.Frame!.Metadata with { SourceId = source },
                    StrideBytes = layout.StrideBytes,
                    Layout = layout
                }
            }
        };
        var reservation = await setup.DispatchAsync(config).ConfigureAwait(false);
        var receipt = await setup.Ingress.AcceptCenteredAsync(config, submission, reservation, CancellationToken.None)
            .ConfigureAwait(false);
        var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId,
            receipt.Manifest.Descriptor.Capture.CaptureId);
        var reader = new CenteredExposureProductReader(setup.Ingress, setup.Store);
        var exposure = await reader.ReadExposureAsync(key, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(exposure);
        Assert.AreEqual(expectedHash, exposure.Raw.Artifact.ChecksumSha256);
        Assert.AreEqual(layout, exposure.Geometry.Layout);
        Assert.IsTrue(FrameWhiteThreshold.TryResolveStored(exposure.Geometry.Layout, out var white, out var reason), reason);
        Console.WriteLine($"Retained fixture {source} {layout.PixelFormat} {layout.Width}x{layout.Height} white={white} sha256={expectedHash}");
    }

    private static CameraModuleConfig CenteredConfiguration(string camera)
    {
        var config = CreateMono16Configuration();
        return config with
        {
            CenteredExposure = new(camera, CenteredExposureProfile.CurrentExecutionVersion),
            Pipeline = new([new("Calibration", "linear", DependsOn: ["$raw"], Required: false)],
                DependencyPolicy: CapturePipelineDependencyPolicy.RejectEnabledDependent),
            DeploymentLocation = DeploymentLocationSnapshot.Create("test-site", 1, "test", null,
                DateTimeOffset.UnixEpoch, null, 35, -113, 500, "UTC")
        };
    }

    private static FrameLayoutDescriptor CenteredLayout() => new(
        2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 12, 16,
        FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 2800, 8)
    {
        LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
        StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1,
        Readout = new(2, 2, 0, 0, 2, 2, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null)
    };

    private static CaptureLoopSubmission CenteredSubmission(DateTimeOffset utc)
    {
        var frame = new CameraFrame(utc, 2, 2, CameraPixelFormat.Mono16,
            new byte[] { 250, 174, 10, 175, 26, 175, 0, 0 }, new FrameMetadata(
                CenteredTestSettings.Exposure, CenteredTestSettings.Gain, 5, SourceId: "same-model"), 4)
        {
            Layout = CenteredLayout()
        };
        return new(new(utc, TimeSpan.FromSeconds(1), CaptureMode.Still, CenteredTestSettings),
            new(frame, CenteredTestSettings, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new(utc, utc.AddMilliseconds(10), utc.AddMilliseconds(12))
            }, utc, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(12));
    }

    private sealed class CenteredFixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly RawIngressTelemetry _rawTelemetry;
        private readonly ProcessingReplayWakeup _replayWakeup;
        internal ProcessingGraphOperationsCoordinator GraphOperations { get; }
        internal IOptions<CameraAgentHostOptions> Options { get; }
        internal CaptureProcessingTelemetry Telemetry { get; } = new();
        internal CenteredExposureProductChanges Changes { get; } = new();
        internal SqliteCaptureProcessingStore Store { get; }
        internal CaptureProcessingPipelineFactory Factory { get; }
        internal RawCaptureIngress Ingress { get; }
        internal CaptureLanePolicy Policy { get; }
        internal RawIngressState RawState { get; }
        internal SqliteCalibrationLibraryStore Calibration => _services.GetRequiredService<SqliteCalibrationLibraryStore>();

        internal CenteredFixture(
            string root, IRawIngressFaultInjector? fault = null, TimeProvider? timeProvider = null,
            int? requiredMaximumPendingCount = null, CenteredWindowWakeup? wakeup = null)
        {
            Options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                CaptureDistribution = requiredMaximumPendingCount is { } limit
                    ? new() { RequiredMaximumPendingCount = limit }
                    : new()
            });
            var clock = timeProvider ?? TimeProvider.System;
            RawState = new RawIngressState(clock);
            _rawTelemetry = new(RawState);
            Policy = new(Options);
            _services = new ServiceCollection()
                .AddSingleton(new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()))
                .AddSingleton(_ => new RawCaptureIngress(Options, new FixedCapacityProvider(long.MaxValue), RawState, clock, _rawTelemetry,
                    NullLogger<RawCaptureIngress>.Instance, fault ?? new NullRawIngressFaultInjector(),
                    Policy, graphOperations: GraphOperations, centeredProductChanges: Changes, centeredWindowWakeup: wakeup))
                .AddSingleton(sp => new SqliteCalibrationLibraryStore(sp.GetRequiredService<RawCaptureIngress>(), Options, clock))
                .AddSingleton(_ => new CalibrationLibraryProcessingInputLoader(Options, new CameraAgentClearReferenceLoader(Options)))
                .BuildServiceProvider();
            Factory = new(_services,
                [new("Calibration", typeof(CalibrationCaptureProcessingStep), typeof(CalibrationProcessingStepOptions), 20, false),
                 new("CenteredProbe", typeof(CenteredProbeStep), typeof(CenteredProbeOptions), 30, false)],
                NullLogger<CaptureProcessingPipelineFactory>.Instance, Telemetry);
            Store = new(Options, clock, centeredProductChanges: Changes, centeredWindowWakeup: wakeup);
            _replayWakeup = new(Options);
            GraphOperations = new(Factory, Store, Options, clock, _replayWakeup);
            Ingress = _services.GetRequiredService<RawCaptureIngress>();
        }

        internal async Task<CenteredExposureReservation> DispatchAsync(CameraModuleConfig config)
        {
            var reservation = await Ingress.ReserveCenteredAsync(config, CenteredTestResources, null, CancellationToken.None)
                .ConfigureAwait(false);
            await Ingress.DispatchCenteredAsync(reservation.Pending.Key, CenteredTestSettings, null, "fixture",
                CancellationToken.None).ConfigureAwait(false);
            return reservation;
        }

        internal async Task<RawCaptureReceipt> AdmitAsync(CameraModuleConfig config, DateTimeOffset utc)
            => await Ingress.AcceptCenteredAsync(config, CenteredSubmission(utc), await DispatchAsync(config)
                .ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);

        public void Dispose()
        {
            _services.Dispose();
            GraphOperations.Dispose();
            Store.Dispose();
            Telemetry.Dispose();
            _rawTelemetry.Dispose();
        }
    }
}
