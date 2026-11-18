using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

public sealed partial class VirtualCalibrationAcquisitionCoordinatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LifecycleClaimDuringPlanningLeavesNoBlockingAcquisition(bool claimAfterPersistence)
    {
        var root = CreateRoot();
        try
        {
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var controls = LifecycleControls(root);
            var accessor = new PlanningBarrierAccessor(Configuration());
            using var meter = new Meter(CalibrationTelemetry.MeterName);
            using var telemetry = new CalibrationTelemetry(meter, NullLogger<CalibrationTelemetry>.Instance);
            var failures = 0L;
            var rejected = 0L;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, owner) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)) owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == "camera_agent.calibration.failures") Interlocked.Add(ref failures, measurement);
                if (instrument.Name != "camera_agent.calibration.acquisitions") return;
                foreach (var tag in tags)
                    if (tag.Key == "outcome" && Equals(tag.Value, "rejected")) Interlocked.Add(ref rejected, measurement);
            });
            listener.Start();
            using var fixture = await Fixture.CreateAsync(root, lifecycleControls: controls,
                configurationAccessor: accessor, calibrationTelemetry: telemetry).ConfigureAwait(false);
            using var exposure = await fixture.Admission.EnterAsync(watchdog.Token).ConfigureAwait(false);
            var acquisition = fixture.Coordinator.AcquireAsync(Request("claim-during-plan"), watchdog.Token);
            await accessor.Entered.Task.WaitAsync(watchdog.Token).ConfigureAwait(false);
            try
            {
                if (claimAfterPersistence)
                {
                    accessor.Release.TrySetResult();
                    while (await fixture.Store.ReadPendingAcquisitionJobAsync(watchdog.Token).ConfigureAwait(false) is null)
                        await Task.Delay(5, watchdog.Token).ConfigureAwait(false);
                }
                var receipt = await ClaimLifecycleAsync(controls, CameraAgentControlPhase.Draining,
                    fixture.Admission.Snapshot.Version).ConfigureAwait(false);
                accessor.Release.TrySetResult();
                exposure.MarkPublished();
                exposure.Dispose();
                await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() => acquisition).ConfigureAwait(false);
                Assert.AreEqual(0L, failures, "Lifecycle refusal must not consume the acquisition failure metric.");
                Assert.AreEqual(1L, rejected);
                var pending = await fixture.Store.ReadPendingAcquisitionJobAsync(watchdog.Token).ConfigureAwait(false);
                Assert.IsNull(pending, $"Refusal left blocking job: {pending?.State}, attempts={pending?.AttemptCount}");
                var replay = await fixture.Store.ReadAcquireReplayAsync("claim-during-plan",
                    VirtualCalibrationAcquisitionContractJson.ComputeRequestIdentitySha256(Request("claim-during-plan")),
                    watchdog.Token).ConfigureAwait(false);
                if (replay is not null)
                {
                    Assert.IsTrue(replay.IsTerminal);
                    Assert.AreEqual(0, replay.AttemptCount);
                }
                using var connection = await OpenAsync(root).ConfigureAwait(false);
                Assert.AreEqual(0L, await ScalarLongAsync(connection,
                    "SELECT COUNT(*) FROM calibration_library_commands WHERE completed_unix_ms IS NULL;").ConfigureAwait(false));
                await EndLifecycleAsync(controls, receipt).ConfigureAwait(false);
                Assert.IsFalse(controls.AdmissionHeld);
                Assert.AreEqual(CalibrationAcquisitionStates.Published,
                    (await fixture.Coordinator.AcquireAsync(Request("after-planning-refusal"), watchdog.Token).ConfigureAwait(false)).State);
            }
            finally
            {
                accessor.Release.TrySetResult();
                exposure.MarkPublished();
                exposure.Dispose();
                try { await acquisition.ConfigureAwait(false); }
                catch (CaptureControlConflictException) { }
            }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public async Task UnstartedRetirementPreservesAnAttemptStartedByAnotherOwner()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(root,
                new OneShotCancellationFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.Coordinator.AcquireAsync(Request("started-before-retirement"), CancellationToken.None)).ConfigureAwait(false);
            var pending = await fixture.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(pending);
            Assert.AreEqual(CalibrationAcquisitionStates.Acquiring, pending.State);
            Assert.AreEqual(0, pending.AttemptCount, "Interruption does not consume the failure budget.");
            var retained = await fixture.Store.CancelAcquisitionAsync(pending.Plan.JobId, CancellationToken.None,
                onlyIfUnstarted: true).ConfigureAwait(false);
            Assert.AreEqual(pending.State, retained.State);
            Assert.AreEqual(pending.Phase, retained.Phase);
            Assert.AreEqual(pending.AttemptCount, retained.AttemptCount);
            Assert.IsFalse(retained.IsTerminal);
            Assert.AreEqual(CalibrationAcquisitionStates.Published,
                (await fixture.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false))!.State);
        }
        finally { DeleteRoot(root); }
    }

    private sealed class PlanningBarrierAccessor(CameraModuleConfig configuration) : ICameraAgentConfigurationAccessor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConfigured => true;
        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();
        public async ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return configuration;
        }
    }
}
