using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
            left = left with { Rig = left.Rig with
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
            } };
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
                await using var first = new CenteredRunnerModule(async (attempt, request, token) =>
                {
                    slot++;
                    Assert.AreEqual(slot - 100L, attempt.Ordinal);
                    await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint!, slot != 101, token).ConfigureAwait(false);
                    return CenteredTraceFrame(slot, request, effectiveGain: slot == 103 ? 7 : null);
                });
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
                await using var uncertain = new CenteredRunnerModule(async (attempt, request, token) =>
                {
                    Assert.AreEqual(target104, attempt);
                    Assert.AreEqual(7d, request.RequestedSetpoint!.Gain);
                    await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint, true, token).ConfigureAwait(false);
                    throw new TimeoutException("Device changed one control before its acknowledgment was lost.");
                });
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
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = new CaptureProcessingPersistence(fresh.Options, fresh.Store, storage, fresh.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance);
            using var handler = new StandardCaptureLaneHandler(fresh.Factory, persistence, fresh.Telemetry,
                NullLogger<StandardCaptureLaneHandler>.Instance, fresh.Ingress, fresh.Options);
            var nextSlot = 104;
            var last = new CenteredTraceNotifier();
            last.Committed = () =>
            {
                if (nextSlot == 106) stop.Cancel();
            };
            await using var resumed = new CenteredRunnerModule(async (attempt, request, token) =>
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
        Assert.AreEqual(metered, !data.IsDBNull(1));
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

    private sealed class CenteredRunnerModule(
        Func<ExposureAttemptKey, CaptureRequest, CancellationToken, Task<CaptureResult>> capture)
        : ICameraModule, ICameraBoundExposureCapture
    {
        public string Id => "centered-trace";
        public string DisplayName => Id;
        public string ModuleType => Id;
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;
        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
            => throw new AssertFailedException("Centered acquisition must use the bound module entry point.");
        public Task<CaptureResult> CaptureBoundAsync(
            ExposureAttemptKey attempt, CaptureRequest request, CancellationToken cancellationToken)
            => capture(attempt, request, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, storage, setup.Telemetry,
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
            var result = await standard.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
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
                await WriteAsync(settlements[0]).ConfigureAwait(false);
                Assert.AreEqual(versionAfterSettling, setup.Changes.Version);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    WriteAsync(settlements[0] with { PayloadSha256 = new string('A', 64) })).ConfigureAwait(false);
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
                await using var recoveredHold = await custody.OpenLinearAsync(settled.Product, TimeSpan.FromMinutes(1),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(recoveredHold);
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
            await setup.Store.PlanLifecycleOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
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

            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, storage, setup.Telemetry,
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
    public async Task CenteredLinearReaderRecoversOriginalTerminalLaneFailureWithoutOutput()
    {
        var root = CreateRoot();
        try
        {
            var config = CenteredConfiguration("camera");
            ExposureKey key;
            using (var initial = new CenteredFixture(root))
            {
                var admitted = await initial.AdmitAsync(config, Timestamp(1)).ConfigureAwait(false);
                var source = admitted.Manifest.CenteredExposure!.Source;
                key = new(source.Camera, source.RunId, admitted.Manifest.Descriptor.Capture.CaptureId);
                var lane = initial.Policy.Definitions.Single(definition => definition.Name == "standard");
                var lease = await initial.Ingress.ClaimAsync(lane, "fault-worker", config, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.IsNotNull(lease);
                Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, await initial.Ingress.FailAsync(
                    lease, CaptureLaneHandlerResult.Terminal("fixture.linear-before-node"), CancellationToken.None)
                    .ConfigureAwait(false));
            }

            using var resumed = new CenteredFixture(root);
            var read = new CenteredExposureProductReader(resumed.Ingress, resumed.Store);
            var disposition = await read.ReadLinearAsync(key, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(disposition);
            Assert.AreEqual(LinearExposureDisposition.Failed, disposition.Disposition);
            Assert.AreEqual("fixture.linear-before-node", disposition.Reason);
            Assert.IsNull(disposition.Product);
            Assert.IsNull(disposition.Provenance);
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
            submission = submission with { Result = submission.Result with { Frame = frame with
            {
                Layout = frame.Layout! with
                {
                    StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1,
                    LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
                }
            } } };
            var admitted = await setup.Ingress.AcceptCenteredAsync(config, submission, reservation, CancellationToken.None)
                .ConfigureAwait(false);
            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId,
                admitted.Manifest.Descriptor.Capture.CaptureId);
            var lease = await setup.Ingress.ClaimAsync(setup.Policy.Definitions.Single(lane => lane.Name == "standard"),
                "centered-optional-input", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = new CaptureProcessingPersistence(setup.Options, setup.Store, storage, setup.Telemetry,
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

    private static CameraModuleConfig WithCenteredProbe(CameraModuleConfig config, string? requiredVariant = null)
        => config with { Pipeline = config.Pipeline with { Steps = [.. config.Pipeline.Steps,
            new("CenteredProbe", "test-consumer", Options: JsonSerializer.SerializeToElement(new { requiredVariant }),
                DependsOn: ["$raw", "linear"], Required: false)] } };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The registered factory constructs the probe options via reflection.")]
    private sealed class CenteredProbeOptions
    {
        public string? RequiredVariant { get; init; }
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
            context.RecordCanonicalInput("centered-fixture-saw-raw", "centered-fixture-v1",
                context.ReconstructionDescriptor!.Artifact.ChecksumSha256);
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
                Width = 160, Height = 128, StrideBytes = 320, ByteLength = bayer.Length,
                PixelFormat = CameraPixelFormat.BayerRggb16,
                CfaPattern = ColorFilterArrayPattern.Rggb,
                StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
                BlackLevel = 64, WhiteLevel = 4095,
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
        var config = baseline with { Rig = baseline.Rig with
        {
            Sensor = baseline.Rig.Sensor with
            {
                WidthPixels = layout.Readout!.NativeWidth, HeightPixels = layout.Readout.NativeHeight,
                PixelFormat = layout.PixelFormat, StrideBytes = layout.StrideBytes,
                ColorMode = layout.PixelFormat == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                ResponseMode = layout.PixelFormat == CameraPixelFormat.Mono16 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw
            }
        } };
        var submission = CenteredSubmission(utc);
        submission = submission with { Result = submission.Result with { Frame = submission.Result.Frame! with
        {
            Width = layout.Width, Height = layout.Height, PixelFormat = layout.PixelFormat, PixelData = payload,
            Metadata = submission.Result.Frame!.Metadata with { SourceId = source },
            StrideBytes = layout.StrideBytes, Layout = layout
        } } };
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

        internal CenteredFixture(string root, IRawIngressFaultInjector? fault = null, TimeProvider? timeProvider = null)
        {
            Options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0
            });
            _services = new ServiceCollection()
                .AddSingleton(new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()))
                .BuildServiceProvider();
            Factory = new(_services,
                [new("Calibration", typeof(CalibrationCaptureProcessingStep), typeof(CalibrationProcessingStepOptions), 20, false),
                 new("CenteredProbe", typeof(CenteredProbeStep), typeof(CenteredProbeOptions), 30, false)],
                NullLogger<CaptureProcessingPipelineFactory>.Instance, Telemetry);
            timeProvider ??= TimeProvider.System;
            Store = new(Options, timeProvider, centeredProductChanges: Changes);
            _replayWakeup = new(Options);
            GraphOperations = new(Factory, Store, Options, timeProvider, _replayWakeup);
            var state = new RawIngressState(timeProvider);
            _rawTelemetry = new(state);
            Policy = new(Options);
            Ingress = new(Options, new FixedCapacityProvider(long.MaxValue), state, timeProvider, _rawTelemetry,
                NullLogger<RawCaptureIngress>.Instance, fault ?? new NullRawIngressFaultInjector(),
                Policy, graphOperations: GraphOperations);
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
            Ingress.Dispose();
            GraphOperations.Dispose();
            Store.Dispose();
            Telemetry.Dispose();
            _rawTelemetry.Dispose();
            _services.Dispose();
        }
    }
}
