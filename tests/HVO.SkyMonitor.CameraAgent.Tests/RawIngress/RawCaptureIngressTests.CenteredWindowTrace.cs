using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task CenteredRunnerRestartsAcrossRawAndWindowCommitWithoutAdmittingLate105To103()
    {
        var root = CreateRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var start = Timestamp(1);
        var clock = new ExposureTestTimeProvider(start);
        var config = CenteredConfiguration("window-trace");
        var interval = TimeSpan.FromSeconds(1);
        config = config with
        {
            Rig = config.Rig with
            {
                ControlPolicy = new() { ExposureControl = AutomaticControlOwnership.HostMetered, GainControl = AutomaticControlOwnership.Disabled },
                Pipeline = config.Rig.Pipeline with
                {
                    CaptureInterval = interval,
                    DayExposure = CenteredTestSettings.Exposure,
                    NightExposure = CenteredTestSettings.Exposure,
                    DayGain = CenteredTestSettings.Gain,
                    NightGain = CenteredTestSettings.Gain,
                    CadenceMode = CaptureCadenceMode.Continuous
                }
            }
        };
        var camera = new CameraKey(config.AgentId!, "window-trace");
        var journal = new CenteredWindowRuntimeJournal();
        var fault = new CombinedWindowFault();
        var rawWakeup = new CenteredWindowWakeup();
        var productWakeup = new CenteredWindowWakeup();
        var capturedAttempts = new List<ExposureAttemptKey>();
        long lastDispatchWake = 0;
        double appliedGain = CenteredTestSettings.Gain;
        try
        {
            using (var beforeRawRestart = new CenteredFixture(root, fault, clock, wakeup: rawWakeup))
            {
                var module = new CenteredRunnerModule((attempt, request, _) =>
                {
                    capturedAttempts.Add(attempt);
                    lastDispatchWake = rawWakeup.Version;
                    var slot = 100 + capturedAttempts.Count;
                    var frame = CenteredTraceFrame(slot, request, slot == 103 ? 7 : null);
                    AdvanceTo(frame.AcquisitionTiming!.ReadoutCompletedUtc);
                    return Task.FromResult(frame);
                });
                await using var moduleLifetime = module.ConfigureAwait(false);
                var notifier = new CenteredTraceNotifier { Committed = () => AdvanceTo(start + capturedAttempts.Count * interval) };
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(() => RunAsync(beforeRawRestart, module, notifier, token))
                    .ConfigureAwait(false);
                Assert.HasCount(3, capturedAttempts);
                Assert.IsGreaterThan(lastDispatchWake, rawWakeup.Version, "The raw commit must wake the scheduler before the caller sees success.");
            }

            CenteredWindowSnapshot opened;
            ExposureAttemptKey uncertain104;
            using (var afterRawRestart = new CenteredFixture(root, timeProvider: clock, wakeup: productWakeup))
            {
                await afterRawRestart.Ingress.InitializeAsync(token).ConfigureAwait(false);
                var head = await afterRawRestart.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false);
                var raw103 = head.AcquisitionHead!.Exposure;
                var raw = await afterRawRestart.Ingress.ReadCenteredRawAsync(raw103, token).ConfigureAwait(false);
                Assert.IsNotNull(raw);
                Assert.AreEqual(capturedAttempts[2], raw.Manifest.CenteredExposure!.Source);
                uncertain104 = raw.Manifest.CenteredExposure.Target;
                var reader = new CenteredWindowReader(afterRawRestart.Store);
                opened = (await reader.ReadAsync(raw103.CaptureId, token).ConfigureAwait(false))!;
                Assert.IsNotNull(opened);
                Assert.IsNull(opened.ClosureIdentity);
                var beforeSettlement = productWakeup.Version;
                await SettleTraceLinearAsync(afterRawRestart, config, clock, 3, token).ConfigureAwait(false);
                Assert.IsGreaterThan(beforeSettlement, productWakeup.Version, "The committed linear evidence must wake the scheduler.");

                AdvanceTo(start + 3 * interval);
                var module = new CenteredRunnerModule(async (attempt, request, ct) =>
                {
                    Assert.AreEqual(uncertain104, attempt);
                    appliedGain = request.RequestedSetpoint!.Gain;
                    Assert.AreEqual(7d, appliedGain);
                    await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint, metered: true, ct).ConfigureAwait(false);
                    throw new TimeoutException("The device applied 104; its acknowledgement was lost.");
                });
                await using var moduleLifetime = module.ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<TimeoutException>(() => RunAsync(afterRawRestart, module, new CenteredTraceNotifier(), token))
                    .ConfigureAwait(false);
            }

            Guid lateCapture;
            ExposureAttemptKey target106;
            using (var beforeCloseRestart = new CenteredFixture(root, timeProvider: clock, wakeup: new CenteredWindowWakeup()))
            using (var stopAfter105 = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                await beforeCloseRestart.Ingress.InitializeAsync(token).ConfigureAwait(false);
                await beforeCloseRestart.Ingress.ReconcileCenteredControlAsync(config, token).ConfigureAwait(false);
                AdvanceTo(start + 4 * interval);
                var module = new CenteredRunnerModule(async (attempt, request, ct) =>
                {
                    Assert.AreNotEqual(uncertain104.RunId, attempt.RunId);
                    Assert.AreEqual(7d, appliedGain);
                    Assert.AreEqual(CenteredTestSettings, request.RequestedSetpoint);
                    await AssertDispatchedAsync(root, attempt, request.RequestedSetpoint!, metered: false, ct).ConfigureAwait(false);
                    appliedGain = request.RequestedSetpoint!.Gain;
                    var result = CenteredTraceFrame(105, request, appliedGain);

                    // The runner has dispatched and completed sensor integration, but holds this result
                    // outside admission while the original window close transaction is interrupted.
                    AdvanceTo(opened.DeadlineUtc.AddMilliseconds(1));
                    fault.InterruptClose = opened.WindowIdentity;
                    using var faultedStore = new SqliteCaptureProcessingStore(beforeCloseRestart.Options, clock, fault);
                    using var coordinator = new CenteredWindowCoordinator(faultedStore, journal, clock, NullLogger<CenteredWindowCoordinator>.Instance);
                    await coordinator.CloseDueAsync(ct).ConfigureAwait(false);
                    Assert.IsTrue(fault.CloseInterrupted);
                    Assert.IsNull(await faultedStore.ReadCenteredWindowByReferenceAsync(opened.ReferenceCaptureId, ct).ConfigureAwait(false));
                    return result;
                });
                await using var moduleLifetime = module.ConfigureAwait(false);
                await RunAsync(beforeCloseRestart, module, new CenteredTraceNotifier { Committed = stopAfter105.Cancel }, stopAfter105.Token)
                    .ConfigureAwait(false);
                var head = await beforeCloseRestart.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false);
                lateCapture = head.AcquisitionHead!.Exposure.CaptureId;
                var late = await beforeCloseRestart.Ingress.ReadCenteredRawAsync(head.AcquisitionHead.Exposure, token).ConfigureAwait(false);
                Assert.IsNotNull(late);
                Assert.AreEqual(CenteredTestSettings.Gain, late.Manifest.Descriptor.Controls.EffectiveGain);
                Assert.IsTrue(head.AcquisitionHead.CommittedUtc > opened.DeadlineUtc);
                target106 = late.Manifest.CenteredExposure!.Target;
            }

            CenteredWindowDisposition frozen;
            using (var afterCloseRestart = new CenteredFixture(root, timeProvider: clock, wakeup: new CenteredWindowWakeup()))
            {
                await afterCloseRestart.Ingress.InitializeAsync(token).ConfigureAwait(false);
                await SettleTraceLinearAsync(afterCloseRestart, config, clock, 1, token).ConfigureAwait(false);
                using var stopAfter106 = CancellationTokenSource.CreateLinkedTokenSource(token);
                var module = new CenteredRunnerModule((attempt, request, _) =>
                {
                    Assert.AreEqual(target106, attempt);
                    Assert.AreEqual(CenteredTestSettings, request.RequestedSetpoint);
                    var frame = CenteredTraceFrame(106, request);
                    AdvanceTo(frame.AcquisitionTiming!.ReadoutCompletedUtc);
                    return Task.FromResult(frame);
                });
                await using var moduleLifetime = module.ConfigureAwait(false);
                await RunAsync(afterCloseRestart, module, new CenteredTraceNotifier { Committed = stopAfter106.Cancel }, stopAfter106.Token)
                    .ConfigureAwait(false);
                var current = await afterCloseRestart.Ingress.ReadCurrentAsync(camera, token).ConfigureAwait(false);
                var capture106 = current.AcquisitionHead!.Exposure.CaptureId;
                Assert.AreNotEqual(lateCapture, capture106);
                using var coordinator = new CenteredWindowCoordinator(afterCloseRestart.Store, journal, clock,
                    NullLogger<CenteredWindowCoordinator>.Instance);
                await coordinator.CloseDueAsync(token).ConfigureAwait(false);
                var persistence = new CaptureProcessingPersistence(afterCloseRestart.Options, afterCloseRestart.Store, afterCloseRestart.Telemetry, NullLogger<CaptureProcessingPersistence>.Instance);
                var source = new CenteredWindowRetainedRegistrationSource(
                    new CenteredExposureProductReader(afterCloseRestart.Ingress, afterCloseRestart.Store), afterCloseRestart.Ingress, persistence);
                using var registration = new CenteredWindowRegistrationWorker(afterCloseRestart.Store, persistence, journal, source, clock,
                    NullLogger<CenteredWindowRegistrationWorker>.Instance);
                await registration.RegisterPendingAsync(token).ConfigureAwait(false);
                frozen = (await afterCloseRestart.Store.ReadCenteredWindowByReferenceAsync(opened.ReferenceCaptureId, token).ConfigureAwait(false))!;
                Assert.IsNotNull(frozen?.Publication, frozen?.FailureReason);
                Assert.AreEqual(opened.WindowIdentity, frozen.Closure.Schedule.Identity);
                Assert.AreEqual(opened.DeadlineUtc, frozen.Closure.Schedule.DeadlineUtc);
                Assert.AreEqual(opened.DeadlineUtc, frozen.Closure.EvidenceCutoffUtc);
                CollectionAssert.AreEqual(capturedAttempts.ToArray(), frozen.Closure.Slots.Take(3).Select(slot => slot.Attempt).ToArray());
                CollectionAssert.AreEqual(new[]
                {
                    CenteredWindowSlotState.Eligible, CenteredWindowSlotState.Eligible, CenteredWindowSlotState.Eligible,
                    CenteredWindowSlotState.UncertainAttempt, CenteredWindowSlotState.OutsideRun
                }, frozen.Closure.Slots.Select(slot => slot.State).ToArray());
                Assert.AreEqual(uncertain104, frozen.Closure.Slots[3].Attempt);
                Assert.IsFalse(frozen.Closure.Slots.Any(slot => slot.Input?.Key.CaptureId == lateCapture));
                Assert.IsFalse(frozen.Closure.Slots.Any(slot => slot.Input?.Key.CaptureId == capture106));
                Assert.AreEqual(opened.ReferenceCaptureId, frozen.Publication.ReferenceCaptureId);
                Assert.AreEqual(opened.ReferenceCaptureId, Assert.ContainsSingle(frozen.Publication.ContributingCaptureIds));

                var recomputationId = Guid.NewGuid();
                var replay = await CenteredWindowRegistrationResult.RecomputeAsync(frozen.Closure, source, recomputationId, token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(replay.Result, replay.Outcome.ReasonCode);
                Assert.AreEqual(recomputationId, replay.Result.RecomputationId);
                Assert.AreNotEqual(frozen.Publication.PlanSha256, replay.Result.Publication.PlanSha256);
                Assert.AreNotEqual(frozen.Publication.EvidenceArtifactId, replay.Result.Publication.EvidenceArtifactId);
                Assert.IsFalse(replay.Result.Publication.ContributingCaptureIds.Contains(lateCapture));
                var reference = await source.ReadExposureAsync(frozen.Closure.Center.Input!.Key, token).ConfigureAwait(false);
                Assert.IsNotNull(reference);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => persistence.PersistCenteredRegistrationAsync(
                    replay.Result, reference.Raw, journal, token).AsTask()).ConfigureAwait(false);
                foreach (var artifactId in replay.Result.Publication.RegistrationArtifactIds)
                    Assert.IsNull(await afterCloseRestart.Store.ReadOutputByArtifactIdAsync(artifactId, token).ConfigureAwait(false));
            }

            using var final = new CenteredFixture(root, timeProvider: clock);
            using var repeated = new CenteredWindowCoordinator(final.Store, journal, clock, NullLogger<CenteredWindowCoordinator>.Instance);
            await repeated.CloseDueAsync(token).ConfigureAwait(false);
            var reopened = await final.Store.ReadCenteredWindowByReferenceAsync(opened.ReferenceCaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(reopened?.Publication);
            Assert.AreEqual(frozen.Closure.Identity, reopened.Closure.Identity);
            Assert.AreEqual(frozen.Publication!.EvidenceArtifactId, reopened.Publication.EvidenceArtifactId);
            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM processing_centered_windows WHERE reference_capture_id = $capture AND terminal = 1;";
            command.Parameters.AddWithValue("$capture", opened.ReferenceCaptureId.ToString("N"));
            Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
        }
        finally { DeleteRoot(root); }

        void AdvanceTo(DateTimeOffset value) => clock.Advance(value - clock.GetUtcNow());

        Task RunAsync(CenteredFixture fixture, CenteredRunnerModule module, CenteredTraceNotifier notifier, CancellationToken ct) =>
            new CameraModuleRunner(module, new CaptureHostContext(config, fixture.Ingress, notifier), clock,
                NullLogger.Instance, new AstronomyEnginePlanetEphemeris()).RunAsync(ct);
    }

    private static async Task SettleTraceLinearAsync(CenteredFixture fixture, CameraModuleConfig config,
        TimeProvider clock, int count, CancellationToken token)
    {
        var persistence = new CaptureProcessingPersistence(fixture.Options, fixture.Store, fixture.Telemetry,
            NullLogger<CaptureProcessingPersistence>.Instance);
        using var handler = new StandardCaptureLaneHandler(fixture.Factory, persistence, fixture.Telemetry,
            NullLogger<StandardCaptureLaneHandler>.Instance, fixture.Ingress, fixture.Options, timeProvider: clock);
        var lane = fixture.Policy.Definitions.Single(definition => definition.Name == "standard");
        for (var index = 0; index < count; index++)
        {
            var lease = await fixture.Ingress.ClaimAsync(lane, "window-trace", config, token).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            await fixture.Ingress.CompleteAsync(lease, token).ConfigureAwait(false);
        }
    }

    private sealed class CombinedWindowFault : IRawIngressFaultInjector, ICaptureProcessingFaultInjector
    {
        private int _rawCommits;
        internal string? InterruptClose { get; set; }
        internal bool CloseInterrupted { get; private set; }

        public bool IsEnabled(RawIngressFaultPoint point) => point == RawIngressFaultPoint.AfterJournalCommit;

        public void Inject(RawIngressFaultPoint point)
        {
            if (point == RawIngressFaultPoint.AfterJournalCommit && ++_rawCommits == 3)
                throw new InjectedRawIngressFaultException();
        }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.BeforeCenteredWindowCloseCommit || nodeId != InterruptClose)
                return;
            CloseInterrupted = true;
            throw new IOException("Restart before the reference-103 close transaction commits.");
        }
    }
}
