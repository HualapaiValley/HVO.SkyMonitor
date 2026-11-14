using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualMotorizedFocuserTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [TestMethod]
    public async Task Move_RequiresCompleteIntervalsAndReportsPartialPosition()
    {
        var motor = Create();
        var device = motor;
        var move = device.MoveAsync(125, CancellationToken.None);
        Assert.AreEqual(new MotorizedFocuserCapabilities(0, 200, 0), device.Capabilities);
        Assert.AreEqual(new MotorizedFocuserState(true, 100, 125, false, null), device.State);
        motor.AdvanceBy(TimeSpan.FromMilliseconds(99));
        Assert.AreEqual(100, motor.State.Position);
        Assert.IsFalse(move.IsCompleted);
        motor.AdvanceBy(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(110, motor.State.Position);
        motor.AdvanceBy(Interval);
        Assert.AreEqual(120, motor.State.Position);
        Assert.IsTrue(motor.State.IsMoving);
        motor.AdvanceBy(Interval);
        var result = await Finished(move).ConfigureAwait(false);
        Assert.AreEqual(new MotorizedFocuserMoveResult(125, MotorizedFocuserMoveOutcome.Completed,
            new(true, 125, null, false, null)), result);
        Assert.AreEqual(125, motor.OpticalPosition);
    }

    [TestMethod]
    public async Task Moves_ReachBothInclusiveLimitsWithoutOvershoot()
    {
        var motor = Create();
        Assert.AreEqual(200, (await Complete(motor, 200).ConfigureAwait(false)).State.Position);
        Assert.AreEqual(0, (await Complete(motor, 0).ConfigureAwait(false)).State.Position);
        Assert.AreEqual(0, motor.OpticalPosition);
        var idle = motor.MoveAsync(0, CancellationToken.None);
        Assert.IsTrue(idle.IsCompletedSuccessfully);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Completed, (await idle.ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(201)]
    public async Task InvalidTarget_IsRejectedWithoutChangingActiveMove(int target)
    {
        var motor = Create();
        var move = motor.MoveAsync(150, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => motor.MoveAsync(target, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(150, motor.State.TargetPosition);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Completed, (await Finished(move).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public async Task ConflictingMove_IsRejectedWithoutQueueOrImplicitAbort()
    {
        var motor = Create();
        var first = motor.MoveAsync(150, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => motor.MoveAsync(0, CancellationToken.None)).ConfigureAwait(false);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(150, (await Finished(first).ConfigureAwait(false)).State.Position);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(150, motor.State.Position);
    }

    [TestMethod]
    public async Task ConcurrentCallers_AdmitOnlyOneMove()
    {
        var motor = Create();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await start.Task.ConfigureAwait(false); return await motor.MoveAsync(0, CancellationToken.None).ConfigureAwait(false); });
        var second = Task.Run(async () => { await start.Task.ConfigureAwait(false); return await motor.MoveAsync(200, CancellationToken.None).ConfigureAwait(false); });
        start.SetResult();
        var rejected = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await rejected.ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsTrue(motor.State.IsMoving);
        var accepted = ReferenceEquals(rejected, first) ? second : first;
        motor.AdvanceBy(TimeSpan.MaxValue);
        var result = await Finished(accepted).ConfigureAwait(false);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Completed, result.Outcome);
        Assert.AreEqual(result.TargetPosition, motor.State.Position);
    }

    [TestMethod]
    public async Task Backlash_ConsumesReverseTravelAndPreservesPartialReversals()
    {
        var motor = Create(backlash: 6);
        await Complete(motor, 110).ConfigureAwait(false);
        Assert.AreEqual(110, motor.OpticalPosition);
        await Complete(motor, 106).ConfigureAwait(false);
        Assert.AreEqual(110, motor.OpticalPosition, "Four reverse steps take up only part of six steps of play.");
        await Complete(motor, 108).ConfigureAwait(false);
        Assert.AreEqual(110, motor.OpticalPosition, "Two steps back do not reset or fully consume the play.");
        await Complete(motor, 100).ConfigureAwait(false);
        Assert.AreEqual(106, motor.OpticalPosition);
        await Complete(motor, 110).ConfigureAwait(false);
        Assert.AreEqual(110, motor.OpticalPosition);
        Assert.AreEqual(6, motor.Capabilities.BacklashSteps);
    }

    [TestMethod]
    public async Task Backlash_RemainsBoundedAtLimitsAndAcrossAbort()
    {
        var motor = Create(backlash: 200, steps: 1);
        await Complete(motor, 200).ConfigureAwait(false);
        var reverse = motor.MoveAsync(0, CancellationToken.None);
        motor.AdvanceBy(Interval * 50);
        await motor.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Aborted, (await Finished(reverse).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(150, motor.State.Position);
        Assert.AreEqual(200, motor.OpticalPosition);
        await Complete(motor, 200).ConfigureAwait(false);
        Assert.AreEqual(200, motor.OpticalPosition);
        await Complete(motor, 0).ConfigureAwait(false);
        Assert.AreEqual(200, motor.OpticalPosition);
        await Complete(motor, 200).ConfigureAwait(false);
        Assert.AreEqual(200, motor.OpticalPosition);
    }

    [TestMethod]
    public async Task Abort_StopsAtLastCompletedIntervalAndIsIdempotent()
    {
        var motor = Create();
        var move = motor.MoveAsync(200, CancellationToken.None);
        motor.AdvanceBy(Interval + TimeSpan.FromMilliseconds(50));
        var stopped = await motor.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await Finished(move).ConfigureAwait(false);
        Assert.AreEqual(110, stopped.Position);
        Assert.IsFalse(stopped.IsMoving);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Aborted, result.Outcome);
        Assert.AreEqual(stopped, result.State);
        Assert.AreEqual(stopped, await motor.AbortAsync(CancellationToken.None).ConfigureAwait(false));
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(stopped, motor.State);
        await Complete(motor, 130).ConfigureAwait(false);
        Assert.AreEqual(110, result.State.Position, "Later operations cannot rewrite terminal evidence.");
    }

    [TestMethod]
    public async Task Cancellation_StopsMoveAndAllowsExplicitRecovery()
    {
        var motor = Create();
        using var cancellation = new CancellationTokenSource();
        var move = motor.MoveAsync(0, cancellation.Token);
        motor.AdvanceBy(Interval);
        await cancellation.CancelAsync().ConfigureAwait(false);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Finished(move)).ConfigureAwait(false);
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(move.IsCanceled);
        Assert.AreEqual(90, motor.State.Position);
        Assert.IsFalse(motor.State.IsMoving);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(90, motor.State.Position);
        Assert.AreEqual(110, (await Complete(motor, 110).ConfigureAwait(false)).State.Position);
    }

    [TestMethod]
    public async Task PreCancelledMoveAndAbort_DoNotAffectExistingMove()
    {
        var motor = Create();
        var first = motor.MoveAsync(150, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => motor.MoveAsync(0, cancellation.Token)).ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => motor.AbortAsync(cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(150, motor.State.TargetPosition);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Completed, (await Finished(first).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public async Task CancellationOfEndedMove_CannotStopItsSuccessor()
    {
        var motor = Create();
        using var oldCancellation = new CancellationTokenSource();
        var first = motor.MoveAsync(150, oldCancellation.Token);
        await motor.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        var second = motor.MoveAsync(200, CancellationToken.None);
        await oldCancellation.CancelAsync().ConfigureAwait(false);
        Assert.AreEqual(200, motor.State.TargetPosition);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Aborted, (await Finished(first).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Completed, (await Finished(second).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FaultDuringMove_StopsImmediatelyAndNeverResumesOnRecovery(bool disconnect)
    {
        var motor = Create();
        var move = motor.MoveAsync(200, CancellationToken.None);
        motor.AdvanceBy(Interval);
        SetFault(motor, disconnect, true);
        var result = await Finished(move).ConfigureAwait(false);
        var expected = disconnect ? MotorizedFocuserMoveOutcome.Disconnected : MotorizedFocuserMoveOutcome.Stalled;
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(110, result.State.Position);
        Assert.IsFalse(result.State.IsMoving);
        Assert.AreEqual(!disconnect, result.State.Connected);
        Assert.AreEqual(!disconnect, result.State.Stalled);
        Assert.AreEqual(expected, (await motor.MoveAsync(110, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(expected, (await motor.MoveAsync(150, CancellationToken.None).ConfigureAwait(false)).Outcome);
        SetFault(motor, disconnect, false);
        motor.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(110, motor.State.Position);
        Assert.AreEqual(150, (await Complete(motor, 150).ConfigureAwait(false)).State.Position);
        Assert.AreEqual(expected, result.Outcome);
    }

    [TestMethod]
    public async Task FirstTerminalEvent_WinsOverLaterCancellationFaultAndAbort()
    {
        var motor = Create();
        using var cancellation = new CancellationTokenSource();
        var move = motor.MoveAsync(110, cancellation.Token);
        motor.AdvanceBy(Interval);
        await cancellation.CancelAsync().ConfigureAwait(false);
        motor.SetConnected(false);
        motor.SetStalled(true);
        await motor.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await Finished(move).ConfigureAwait(false);
        Assert.AreEqual(MotorizedFocuserMoveOutcome.Completed, result.Outcome);
        Assert.IsTrue(result.State.Connected);
        Assert.IsFalse(result.State.Stalled);
        Assert.IsFalse(motor.State.Connected);
        Assert.IsTrue(motor.State.Stalled);
    }

    [TestMethod]
    public async Task NewMove_DoesNotInheritIdleTimeOrAbortedPartialInterval()
    {
        var motor = Create();
        motor.AdvanceBy(TimeSpan.MaxValue);
        var first = motor.MoveAsync(150, CancellationToken.None);
        motor.AdvanceBy(TimeSpan.FromMilliseconds(99));
        await motor.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        await Finished(first).ConfigureAwait(false);
        var second = motor.MoveAsync(110, CancellationToken.None);
        motor.AdvanceBy(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(100, motor.State.Position);
        Assert.IsFalse(second.IsCompleted);
        motor.AdvanceBy(TimeSpan.FromMilliseconds(99));
        Assert.AreEqual(110, (await Finished(second).ConfigureAwait(false)).State.Position);
    }

    [TestMethod]
    public async Task MaximumElapsedTravelAndStepRate_DoNotOverflow()
    {
        var motor = new VirtualMotorizedFocuser(new(MaximumPosition: int.MaxValue, InitialPosition: 0,
            BacklashSteps: int.MaxValue, StepsPerInterval: int.MaxValue, StepInterval: TimeSpan.FromTicks(1)));
        Assert.AreEqual(int.MaxValue, (await Complete(motor, int.MaxValue).ConfigureAwait(false)).State.Position);
        Assert.AreEqual(0, (await Complete(motor, 0).ConfigureAwait(false)).State.Position);
        Assert.AreEqual(int.MaxValue, motor.OpticalPosition);
        var slow = new VirtualMotorizedFocuser(new(MaximumPosition: int.MaxValue, InitialPosition: 0,
            StepsPerInterval: 1, StepInterval: TimeSpan.FromMinutes(1)));
        var move = slow.MoveAsync(int.MaxValue, CancellationToken.None);
        slow.AdvanceBy(TimeSpan.FromSeconds(59));
        slow.AdvanceBy(TimeSpan.MaxValue);
        Assert.AreEqual(int.MaxValue, (await Finished(move).ConfigureAwait(false)).State.Position);
    }

    [TestMethod]
    public void Temperature_IsOptionalFiniteTelemetryAndNeverMovesMotor()
    {
        var motor = Create();
        Assert.IsNull(motor.State.TemperatureCelsius);
        motor.SetTemperature(-12.5);
        Assert.AreEqual(-12.5, motor.State.TemperatureCelsius);
        Assert.AreEqual(100, motor.State.Position);
        motor.SetTemperature(null);
        Assert.IsNull(motor.State.TemperatureCelsius);
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => motor.SetTemperature(invalid));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VirtualMotorizedFocuser(new(TemperatureCelsius: invalid)));
        }
        Assert.IsNull(motor.State.TemperatureCelsius);
        Assert.IsFalse(motor.State.IsMoving);
        Assert.AreEqual(15.0, new VirtualMotorizedFocuser(new(TemperatureCelsius: 15)).State.TemperatureCelsius);
    }

    [TestMethod]
    public async Task FreshInstance_UsesDeclaredInitialStateWithoutResumingAnotherInstance()
    {
        var old = Create(backlash: 6);
        var move = old.MoveAsync(0, CancellationToken.None);
        old.AdvanceBy(Interval);
        old.SetConnected(false);
        await Finished(move).ConfigureAwait(false);
        var fresh = Create(backlash: 6);
        Assert.AreEqual(new MotorizedFocuserState(true, 100, null, false, null), fresh.State);
        Assert.AreEqual(100, fresh.OpticalPosition);
        Assert.AreEqual(110, (await Complete(fresh, 110).ConfigureAwait(false)).State.Position);
        Assert.IsFalse(old.State.Connected);
    }

    [TestMethod]
    public async Task SameVirtualInputs_ReproduceTheSameMotorAndOpticalTrajectory()
    {
        static async Task<List<(MotorizedFocuserState State, int Optical)>> Run()
        {
            var motor = Create(backlash: 4, steps: 1);
            var trajectory = new List<(MotorizedFocuserState, int)>();
            foreach (var target in new[] { 110, 108, 109, 100, 110 })
            {
                var move = motor.MoveAsync(target, CancellationToken.None);
                for (var i = 0; i < 10; i++)
                {
                    motor.AdvanceBy(Interval);
                    trajectory.Add((motor.State, motor.OpticalPosition));
                }
                await Finished(move).ConfigureAwait(false);
            }
            return trajectory;
        }
        CollectionAssert.AreEqual(await Run().ConfigureAwait(false), await Run().ConfigureAwait(false));
    }

    [TestMethod]
    public void InvalidOptionsAndElapsed_AreRejectedBeforeMutation()
    {
        var options = new[]
        {
            new VirtualMotorizedFocuserOptions(MinimumPosition: -1),
            new VirtualMotorizedFocuserOptions(MinimumPosition: 10, MaximumPosition: 10),
            new VirtualMotorizedFocuserOptions(InitialPosition: -1),
            new VirtualMotorizedFocuserOptions(InitialPosition: 10001),
            new VirtualMotorizedFocuserOptions(BacklashSteps: -1),
            new VirtualMotorizedFocuserOptions(BacklashSteps: 10001),
            new VirtualMotorizedFocuserOptions(StepsPerInterval: 0),
            new VirtualMotorizedFocuserOptions(StepsPerInterval: -1),
            new VirtualMotorizedFocuserOptions(StepInterval: TimeSpan.Zero),
            new VirtualMotorizedFocuserOptions(StepInterval: TimeSpan.FromTicks(-1)),
            new VirtualMotorizedFocuserOptions(StepInterval: TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1))
        };
        foreach (var invalid in options)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VirtualMotorizedFocuser(invalid));
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => new VirtualMotorizedFocuser(null!));
        var motor = Create();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => motor.AdvanceBy(TimeSpan.FromTicks(-1)));
        Assert.AreEqual(100, motor.State.Position);
        Assert.IsFalse(motor.State.IsMoving);
    }

    private static VirtualMotorizedFocuser Create(int backlash = 0, int steps = 10)
        => new(new(MaximumPosition: 200, InitialPosition: 100, BacklashSteps: backlash, StepsPerInterval: steps));

    private static Task<MotorizedFocuserMoveResult> Finished(Task<MotorizedFocuserMoveResult> move)
        => move.WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task<MotorizedFocuserMoveResult> Complete(VirtualMotorizedFocuser motor, int target)
    {
        var move = motor.MoveAsync(target, CancellationToken.None);
        motor.AdvanceBy(TimeSpan.MaxValue);
        return await Finished(move).ConfigureAwait(false);
    }

    private static void SetFault(VirtualMotorizedFocuser motor, bool disconnect, bool faulted)
    {
        if (disconnect)
        {
            motor.SetConnected(!faulted);
        }
        else
        {
            motor.SetStalled(faulted);
        }
    }
}
