using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture.Focus")]
public sealed class MotorFocusSessionTests
{
    private static readonly ManualFocusPreviewSettings Settings = new(TimeSpan.FromSeconds(1), 20);
    private static readonly ManualFocusSessionRequest Sweep = new(Settings, Motor: new(SweepSpacing: 100));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Sweep_MeasuresComparableFramesAndVerifiesTheBacklashCorrectedReturn()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        var motor = NewMotor();
        using var clock = AdvanceMotor(motor);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var owner = Coordinator(fixture, motor, store: store);
        await owner.StartAsync(Sweep, "alice", CancellationToken.None).ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(owner).ConfigureAwait(false);
        Assert.AreEqual("autofocus-complete", ended.EndReason, $"{ended.LastFailure} {string.Join(", ", ended.History.Select(s => s.Measurement.HalfFluxDiameterPixels))}");
        Assert.AreEqual(600, ended.Motor!.AppliedPosition);
        Assert.AreEqual(600, motor.OpticalPosition);
        CollectionAssert.AreEqual(new int?[] { 300, 400, 500, 600, 700, 600 }, ended.History.Select(s => s.MotorPosition).ToArray());
        Assert.HasCount(1, ended.History.Select(s => s.ComparisonGroupId).Distinct().ToArray());
        Assert.IsTrue(ended.History.All(s => s.Measurement.HasMeasurement && s.Provenance.FrameSha256.Length == 64
            && s.Provenance.ModuleGeneration == 1 && s.SimulatedFocusPosition == s.MotorPosition));
        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() => owner.SaveAsync(ended.SessionId, "bob", CancellationToken.None)).ConfigureAwait(false);
        var saved = await owner.SaveAsync(ended.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        var content = await store.ReadAsync(saved.SavedRecordId!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ended.Motor, content!.Record.Session.Motor);
        Assert.AreEqual(ended.Motor, content.Summary.Motor);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(Timeout).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("preview-failure")]
    [DataRow("observer-lost")]
    [DataRow("motor-disconnected")]
    [DataRow("camera-revoked")]
    public async Task InterruptedSweep_CleansUpPreviewAndProvesRestorationOrClosesAdmission(string fault)
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        var camera = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        var motor = NewMotor();
        using var clock = AdvanceMotor(motor);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        camera.BeforePreview = async (_, token) =>
        {
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            if (fault == "preview-failure") throw new IOException("Preview failed.");
        };
        var limits = ManualFocusSessionLimits.Default with { ObserverTimeout = fault == "observer-lost" ? TimeSpan.FromSeconds(1) : Timeout };
        using var owner = Coordinator(fixture, motor, limits);
        var session = await owner.StartAsync(Sweep, "alice", CancellationToken.None).ConfigureAwait(false);
        await camera.PreviewStarted.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.AreEqual(300, motor.State.Position);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false));
        Assert.ThrowsExactly<ManualFocusSessionStateException>(() => owner.Adjust(session.SessionId, "alice", new(Settings)));
        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() => owner.StopAsync(session.SessionId, "bob", CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() => owner.StartAsync(new(Settings), "bob", CancellationToken.None)).ConfigureAwait(false);

        switch (fault)
        {
            case "cancel":
                await owner.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
                break;
            case "motor-disconnected":
                motor.SetConnected(false);
                release.SetResult();
                break;
            case "camera-revoked":
                Assert.IsTrue(await fixture.Ownership.RevokeAsync(Timeout).ConfigureAwait(false));
                break;
            case "preview-failure":
                release.SetResult();
                break;
        }
        var ended = await FocusWait.EndedAsync(owner).ConfigureAwait(false);
        Assert.IsNull(ended.Motor!.AppliedPosition);
        Assert.IsFalse(motor.State.IsMoving);
        if (fault is "camera-revoked" or "motor-disconnected")
        {
            Assert.AreEqual(CaptureAdmissionState.Unavailable, fixture.Admission.Snapshot.State);
            StringAssert.Contains(ended.Motor.Recovery!, "unverified", StringComparison.Ordinal);
            Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false));
        }
        else
        {
            Assert.AreEqual(500, motor.State.Position);
            Assert.AreEqual(500, motor.OpticalPosition);
            Assert.AreEqual("Starting position verified.", ended.Motor.Recovery);
            Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(Timeout).ConfigureAwait(false));
        }
        if (fault is "cancel" or "observer-lost" or "camera-revoked") Assert.AreEqual(1, camera.CancelledPreviews);
        Assert.AreEqual(fault switch
        {
            "cancel" => ManualFocusReasonCodes.StoppedByOperator,
            "observer-lost" => ManualFocusReasonCodes.ObserverLost,
            "camera-revoked" => ManualFocusReasonCodes.CameraWithdrawn,
            "motor-disconnected" => "motor-position-unverified",
            _ => ManualFocusReasonCodes.LoopFailed
        }, ended.EndReason);
    }

    [TestMethod]
    public async Task Sweep_RejectsAnUnbracketedMinimumAndRestoresInitialPosition()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync(focus: new VirtualSimulatedFocusOptions { BestPosition = 300 }).ConfigureAwait(false);
        var motor = NewMotor();
        using var clock = AdvanceMotor(motor);
        using var owner = Coordinator(fixture, motor);
        await owner.StartAsync(Sweep, "alice", CancellationToken.None).ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(owner).ConfigureAwait(false);
        Assert.AreEqual("autofocus-unbracketed", ended.EndReason);
        Assert.AreEqual(5L, ended.TotalSamples);
        Assert.IsNull(ended.Motor!.AppliedPosition);
        Assert.AreEqual(500, motor.OpticalPosition);
    }

    [TestMethod]
    [DataRow("no-star", ManualFocusReasonCodes.AutofocusInvalidSample, 3)]
    [DataRow("geometry", ManualFocusReasonCodes.AutofocusInvalidSample, 3)]
    [DataRow("non-monotonic", ManualFocusReasonCodes.AutofocusUnbracketed, 5)]
    [DataRow("defocused-verification", ManualFocusReasonCodes.AutofocusVerificationFailed, 6)]
    public async Task Sweep_RejectsInvalidEvidenceAndVerifiesRestoration(string fault, string reason, int expectedSamples)
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        var camera = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        CameraFrame? first = null;
        camera.TransformFrame = (number, frame) =>
        {
            first ??= frame;
            return (fault, number) switch
            {
                ("no-star", 3) => frame with { PixelData = new byte[frame.PixelData.Length] },
                ("geometry", 3) => frame with
                {
                    Height = frame.Height - 1,
                    PixelData = frame.PixelData[..(frame.PixelData.Length - frame.PixelData.Length / frame.Height)],
                    Layout = null
                },
                // Reuse the broad star from trial 1 on the way down or at verification.
                ("non-monotonic", 3) or ("defocused-verification", 6) => frame with { PixelData = first.PixelData },
                _ => frame
            };
        };
        var motor = NewMotor();
        using var clock = AdvanceMotor(motor);
        using var owner = Coordinator(fixture, motor);
        await owner.StartAsync(Sweep, "alice", CancellationToken.None).ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(owner).ConfigureAwait(false);
        Assert.AreEqual(reason, ended.EndReason, ended.LastFailure);
        Assert.AreEqual(expectedSamples, camera.Previews);
        Assert.HasCount(expectedSamples, ended.History);
        var samples = ended.History;
        var widths = samples.Select(s => s.Measurement.HalfFluxDiameterPixels).ToArray();
        switch (fault)
        {
            case "no-star":
                Assert.AreEqual(FocusStarStatus.NoStar, samples[2].Measurement.Status);
                break;
            case "geometry":
                Assert.IsTrue(samples[2].Measurement.HasMeasurement);
                Assert.AreNotEqual(samples[0].ComparisonGroupId, samples[2].ComparisonGroupId);
                break;
            case "non-monotonic":
                Assert.IsTrue(widths[0] > widths[1] && widths[1] < widths[2]);
                Assert.AreEqual(widths.Min(), widths[3]);
                break;
            case "defocused-verification":
                Assert.AreEqual(samples[3].ComparisonGroupId, samples[5].ComparisonGroupId);
                Assert.IsTrue(widths[5] > widths[2] && widths[5] > widths[4]);
                break;
        }
        AssertVerifiedRestore(ended, motor);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(Timeout).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Sweep_ClosesAdmissionWhenOnlyTheMotorCoordinateCanBeRestoredAtTheLowerLimit()
    {
        var fleet = new FleetRuntimeState(TimeProvider.System);
        fleet.ModuleAvailable();
        var fixture = await FocusOwnerFixture.CreateAsync(fleet).ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        var camera = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        camera.BeforePreview = (_, _) => Task.FromException(new IOException("Preview interrupted."));
        var motor = new VirtualMotorizedFocuser(new(MaximumPosition: 1000, InitialPosition: 0, BacklashSteps: 12));
        using var clock = AdvanceMotor(motor);
        using var owner = Coordinator(fixture, motor);

        await owner.StartAsync(Sweep, "alice", CancellationToken.None).ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(owner).ConfigureAwait(false);
        Assert.AreEqual(ManualFocusReasonCodes.LoopFailed, ended.EndReason);
        Assert.IsNull(ended.Motor!.AppliedPosition);
        Assert.AreEqual(0, ended.Motor.Final!.Position);
        Assert.AreEqual(0, motor.State.Position);
        Assert.AreEqual(12, motor.OpticalPosition);
        StringAssert.Contains(ended.Motor.Recovery!, "unverified", StringComparison.Ordinal);
        Assert.AreEqual(CaptureAdmissionState.Unavailable, fixture.Admission.Snapshot.State);
        Assert.AreEqual(FleetAvailability.Unavailable, fleet.Snapshot.Capture.Availability);
        Assert.AreEqual("focus-restoration-unverified", fleet.Snapshot.Capture.Reason);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task FailedMove_AbortsTimedOutRestoreBeforeReleasingFocusHold()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        await fixture.PublishScriptedAsync().ConfigureAwait(false);
        var motor = new RestoreTimeoutFocuser();
        using var owner = Coordinator(fixture, motor, ManualFocusSessionLimits.Default with { StopWaitTimeout = TimeSpan.FromSeconds(1) });
        await owner.StartAsync(new(Settings, Motor: new(AbsolutePosition: 300)), "alice", CancellationToken.None).ConfigureAwait(false);

        await motor.RestoreAbortStarted.Task.WaitAsync(Timeout).ConfigureAwait(false);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(20)).ConfigureAwait(false));
        Assert.IsNull(owner.Snapshot.EndedUtc);
        motor.ReleaseAbort.SetResult();
        var ended = await FocusWait.EndedAsync(owner).ConfigureAwait(false);
        Assert.AreEqual(ManualFocusReasonCodes.MotorMoveFailed, ended.EndReason);
        Assert.IsNull(ended.Motor!.AppliedPosition);
        Assert.IsFalse(motor.State.IsMoving);
        Assert.AreEqual(CaptureAdmissionState.Unavailable, fixture.Admission.Snapshot.State);
    }

    [TestMethod]
    [DataRow(false, ManualFocusReasonCodes.AdmissionUnavailable)]
    [DataRow(true, ManualFocusReasonCodes.HostStopping)]
    public async Task Sweep_RestoresUnderInstallerClaimOrArmedHostStop(bool armRestart, string expectedReason)
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        var camera = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        camera.BeforePreview = (_, token) => release.Task.WaitAsync(token);
        var motor = NewMotor();
        using var clock = AdvanceMotor(motor);
        using var owner = Coordinator(fixture, motor);
        using var deadline = new CancellationTokenSource(Timeout);
        var token = deadline.Token;
        var admission = fixture.Admission;
        if (armRestart)
            await admission.PauseAsync("pause-before-sweep", null, "operator", null, token).ConfigureAwait(false);
        var version = admission.Snapshot.Version;
        await owner.StartAsync(Sweep, "alice", token).ConfigureAwait(false);
        await camera.PreviewStarted.Task.WaitAsync(token).ConfigureAwait(false);
        Assert.AreEqual(300, motor.OpticalPosition);
        var controls = fixture.Controls;
        var worker = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await controls.ExchangeAsync(new(worker), token).ConfigureAwait(false);
        var queued = await controls.SubmitAsync(new(operation, CameraAgentControlKind.Restart, "operator", "focus test", version, now), token).ConfigureAwait(false);
        var draining = queued with { Phase = CameraAgentControlPhase.Draining, Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, now, "lifecycle.draining")] };
        await controls.ExchangeAsync(new(worker, draining), token).ConfigureAwait(false);
        Assert.IsTrue(controls.AdmissionHeld);
        if (armRestart)
        {
            var restarting = draining with
            {
                Phase = CameraAgentControlPhase.Restarting,
                Events = [.. draining.Events, new(CameraAgentControlPhase.Restarting, now, "lifecycle.restarting")],
                RestartRequested = true,
                PausedVersion = version
            };
            await controls.ExchangeAsync(new(worker, restarting), token).ConfigureAwait(false);
            Assert.IsTrue(await controls.ArmRestartAsync(new(worker, operation, version), version, token).ConfigureAwait(false));
            // Only the armed process stop still fences admission once the retained claim is terminal.
            var terminal = restarting with
            {
                Phase = CameraAgentControlPhase.Failed,
                Events = [.. restarting.Events, new(CameraAgentControlPhase.Failed, now, "lifecycle.test-ended")]
            };
            await controls.ExchangeAsync(new(worker, terminal), token).ConfigureAwait(false);
            Assert.IsTrue(controls.StopPending);
            await ((IHostedService)owner).StopAsync(token).ConfigureAwait(false);
        }
        else
        {
            release.SetResult();
        }
        var ended = await FocusWait.EndedAsync(owner, Timeout).ConfigureAwait(false);
        Assert.AreEqual(expectedReason, ended.EndReason);
        Assert.AreEqual(1, camera.Previews);
        AssertVerifiedRestore(ended, motor);
        if (!armRestart)
        {
            var paused = await admission.PauseAsync($"lifecycle:{operation:D}:pause", version, $"installer-lifecycle:{operation:D}", "lifecycle pause", token).ConfigureAwait(false);
            Assert.IsTrue(paused.Changed);
        }
        Assert.AreEqual(CaptureAdmissionState.Paused, admission.Snapshot.State);
    }

    private static void AssertVerifiedRestore(ManualFocusSessionSnapshot ended, VirtualMotorizedFocuser motor)
    {
        Assert.IsNull(ended.Motor!.AppliedPosition);
        Assert.AreEqual("Starting position verified.", ended.Motor.Recovery);
        Assert.AreEqual(500, ended.Motor.Final!.Position);
        Assert.AreEqual(500, motor.State.Position);
        Assert.AreEqual(500, motor.OpticalPosition);
    }

    [TestMethod]
    public async Task MoveAndStep_ValidateBeforeMotionAndHonorAnExistingCaptureAndOperatorPause()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var absent = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionUnavailableException>(() => absent.StartAsync(Sweep, "alice", CancellationToken.None)).ConfigureAwait(false);
        var motor = NewMotor();
        using var clock = AdvanceMotor(motor);
        using var owner = Coordinator(fixture, motor);
        foreach (var invalid in new MotorFocusCommand[] { new(), new(-1), new(1001), new(RelativeSteps: int.MaxValue),
            new(SweepSpacing: 0), new(SweepSpacing: 250), new(0, 1) })
        {
            await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
                owner.StartAsync(new(Settings, Motor: invalid), "alice", CancellationToken.None)).ConfigureAwait(false);
        }
        using (var capture = await fixture.EnterCaptureAsync(CancellationToken.None).ConfigureAwait(false))
        {
            capture.MarkNoPublicationRequired();
            await owner.StartAsync(new(Settings, Motor: new(AbsolutePosition: 0)), "alice", CancellationToken.None).ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(Timeout);
            // Wait for the boundary to close behind the active normal exposure.
            while (await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false))
                await Task.Delay(1, deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(500, motor.State.Position);
        }
        Assert.AreEqual(0, (await FocusWait.EndedAsync(owner).ConfigureAwait(false)).Motor!.AppliedPosition);
        await fixture.Admission.PauseAsync("motor-test", null, "alice", "Keep paused", CancellationToken.None).ConfigureAwait(false);
        await owner.StartAsync(new(Settings, Motor: new(RelativeSteps: 1000)), "alice", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1000, (await FocusWait.EndedAsync(owner).ConfigureAwait(false)).Motor!.AppliedPosition);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Admission.Snapshot.State);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task BetweenSweepSamples_OtherExposuresWaitAndOperatorPauseSurvivesTeardown()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var scope = fixture.ConfigureAwait(false);
        var admission = fixture.Admission;
        await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() => admission.ExecuteBoundaryAsync(
            CaptureBoundaryKind.MotorCleanup, _ => Task.FromResult(true), CancellationToken.None)).ConfigureAwait(false);
        await admission.WithFocusHoldAsync(async () =>
        {
            Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(40)).ConfigureAwait(false));
            await Assert.ThrowsExactlyAsync<CaptureControlConflictException>(() => admission.ExecuteCaptureBoundaryAsync(
                _ => Task.FromResult(true), CancellationToken.None)).ConfigureAwait(false);
            await admission.PauseAsync("pause-focus", null, "alice", null, CancellationToken.None).ConfigureAwait(false);
            await admission.ResumeAsync("resume-focus", null, "alice", null, CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(40)).ConfigureAwait(false));
            await admission.PauseAsync("pause-focus-again", null, "alice", null, CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, admission.Snapshot.State);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(40)).ConfigureAwait(false));
    }

    private sealed class RestoreTimeoutFocuser : IMotorizedFocuser
    {
        public MotorizedFocuserCapabilities Capabilities { get; } = new(0, 1000, 0);
        public MotorizedFocuserState State { get; private set; } = new(true, 500, null, false, null);
        public TaskCompletionSource RestoreAbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAbort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MotorizedFocuserMoveResult> MoveAsync(int targetPosition, CancellationToken cancellationToken)
        {
            if (targetPosition == 300)
            {
                State = State with { Position = 350 };
                return new(targetPosition, MotorizedFocuserMoveOutcome.Aborted, State);
            }
            State = State with { TargetPosition = targetPosition };
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The restore must be interrupted by its deadline.");
        }

        public async Task<MotorizedFocuserState> AbortAsync(CancellationToken cancellationToken)
        {
            if (State.IsMoving)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestoreAbortStarted.SetResult();
                await ReleaseAbort.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                State = State with { TargetPosition = null };
            }
            return State;
        }
    }

    private static VirtualMotorizedFocuser NewMotor() => new(new(MaximumPosition: 1000, InitialPosition: 500, BacklashSteps: 12));

    private static ITimer AdvanceMotor(VirtualMotorizedFocuser motor) => TimeProvider.System.CreateTimer(
        _ => motor.AdvanceBy(TimeSpan.MaxValue), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(2));

    private static ManualFocusSessionCoordinator Coordinator(FocusOwnerFixture fixture, IMotorizedFocuser motor,
        ManualFocusSessionLimits? limits = null, ManualFocusSessionStore? store = null) =>
        new(new CameraModuleManualFocusPreviewSource(fixture.Ownership, fixture.Admission, TimeProvider.System, motor),
            TimeProvider.System, limits, store);
}
