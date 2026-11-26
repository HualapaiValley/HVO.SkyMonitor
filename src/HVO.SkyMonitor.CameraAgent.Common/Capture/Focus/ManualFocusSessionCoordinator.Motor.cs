using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

public sealed partial class ManualFocusSessionCoordinator
{
    private const int SweepSamples = 5;

    private static void ValidateMotorCommand(MotorFocusCommand command, MotorFocusStatus motor)
    {
        var count = (command.AbsolutePosition.HasValue ? 1 : 0) + (command.RelativeSteps.HasValue ? 1 : 0)
            + (command.SweepSpacing.HasValue ? 1 : 0);
        if (count != 1)
            throw new ManualFocusSessionValidationException("Choose one absolute move, relative step, or autofocus sweep.");
        if (!motor.State.Connected || motor.State.Stalled || motor.State.IsMoving)
            throw new ManualFocusSessionUnavailableException("The focuser must be connected, stationary, and clear of stalls.");
        var limits = motor.Limits;
        if (command.SweepSpacing is { } spacing)
        {
            _ = GetSweepStart(limits, motor.State.Position, spacing);
        }
        else
        {
            var target = command.AbsolutePosition ?? (long)motor.State.Position + command.RelativeSteps!.Value;
            if (target < limits.MinimumPosition || target > limits.MaximumPosition)
                throw new ManualFocusSessionValidationException("The requested move is outside the focuser limits.");
        }
    }

    private static int GetSweepStart(MotorizedFocuserCapabilities limits, int initial, int spacing)
    {
        // Shift the whole window so even the first sample can be approached through the full backlash.
        var earliest = (long)limits.MinimumPosition + limits.BacklashSteps;
        var latest = limits.MaximumPosition - (long)(SweepSamples - 1) * spacing;
        if (spacing <= 0 || limits.MinimumPosition < 0 || limits.BacklashSteps < 0 || earliest > latest)
            throw new ManualFocusSessionValidationException("Five sweep positions and backlash take-up must fit inside the focuser limits.");
        var centered = initial - (long)(SweepSamples / 2) * spacing;
        return (int)Math.Clamp(centered, earliest, latest);
    }

    private static double? SimulatedPosition(IMotorizedFocuser motor, CameraSimulatedFocusModel? model)
    {
        if (model is null) return null;
        if (motor is not VirtualMotorizedFocuser simulator)
            throw new ManualFocusSessionValidationException("A simulated optical model requires a virtual focuser.");
        var limits = motor.Capabilities;
        var fraction = (simulator.OpticalPosition - limits.MinimumPosition) / (double)(limits.MaximumPosition - limits.MinimumPosition);
        return Math.Clamp(model.MinimumPosition + fraction * (model.MaximumPosition - model.MinimumPosition),
            model.MinimumPosition, model.MaximumPosition);
    }

    private Task RunMotorAsync(Session session, CancellationToken token)
    {
        var source = MotorSource!;
        var motor = source.Focuser!;
        var evidence = session.Motor!;
        return source.Admission.WithFocusHoldAsync(async () =>
        {
            var initial = evidence.Initial.State.Position;
            var optical = (motor as VirtualMotorizedFocuser)?.OpticalPosition;
            try
            {
                var command = evidence.Command;
                int applied;
                if (command.SweepSpacing is { } spacing)
                {
                    applied = await SweepAsync(session, source, motor, spacing, token).ConfigureAwait(false);
                }
                else
                {
                    applied = command.AbsolutePosition ?? checked(initial + command.RelativeSteps!.Value);
                    await MotorOperationAsync(session, source, motor, applied, null, false, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                var settled = motor.State;
                VerifyPosition(settled, applied);
                lock (_sync)
                {
                    session.Motor = evidence with { Final = settled, AppliedPosition = applied };
                }
            }
            catch
            {
                await RestoreStartingPositionAsync(session, source, motor, initial, optical).ConfigureAwait(false);
                throw;
            }
        }, token);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A driver failure during recovery must close admission and preserve the original failure.")]
    private async Task RestoreStartingPositionAsync(Session session, CameraModuleManualFocusPreviewSource source,
        IMotorizedFocuser motor, int initial, int? optical)
    {
        // Cleanup is independent of the cancelled caller. Never move a replacement camera's focuser.
        MotorizedFocuserState? final = null;
        var restored = false;
        try
        {
            final = motor.State;
            if (AtStart(final))
            {
                restored = true;
            }
            else
            {
                using var cleanup = new CancellationTokenSource(_limits.StopWaitTimeout, _timeProvider);
                (final, restored) = await source.AtBoundaryAsync(CaptureBoundaryKind.MotorCleanup, session.ModuleGeneration, async (_, _, ct) =>
                {
                    try
                    {
                        if (optical is { } originalOptical && motor.Capabilities.BacklashSteps > 0)
                        {
                            // Approach the original optical coordinate from below, then put back its original play.
                            await MoveAsync(motor, Math.Max(motor.Capabilities.MinimumPosition,
                                originalOptical - motor.Capabilities.BacklashSteps), ct).ConfigureAwait(false);
                            await MoveAsync(motor, originalOptical, ct).ConfigureAwait(false);
                        }
                        await MoveAsync(motor, initial, ct).ConfigureAwait(false);
                        var state = motor.State;
                        return (state, AtStart(state));
                    }
                    catch
                    {
                        await AbortMotorAsync(motor).ConfigureAwait(false);
                        throw;
                    }
                }, cleanup.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The device may have moved since the last successful read.
            final = null;
            if (_logger is not null) LogLoopFailed(_logger, exception, session.Id);
        }
        if (!restored)
        {
            await source.Admission.FailFocusClosedAsync().ConfigureAwait(false);
            if (_logger is not null) LogRestorationUnverified(_logger, session.Id);
        }
        lock (_sync)
        {
            session.Motor = session.Motor! with
            {
                Final = final,
                Recovery = restored ? "Starting position verified." : "Starting position unverified. Normal capture requires recovery."
            };
        }

        bool AtStart(MotorizedFocuserState state) =>
            state is { Connected: true, Stalled: false, IsMoving: false } && state.Position == initial &&
            (optical is null || ((VirtualMotorizedFocuser)motor).OpticalPosition == optical);
    }

    private async Task<int> SweepAsync(Session session, CameraModuleManualFocusPreviewSource source,
        IMotorizedFocuser motor, int spacing, CancellationToken token)
    {
        var limits = motor.Capabilities;
        var first = GetSweepStart(limits, session.Motor!.Initial.State.Position, spacing);
        var samples = new ManualFocusSample[SweepSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            var position = first + i * spacing;
            samples[i] = (await MotorOperationAsync(session, source, motor, position,
                i == 0 ? position - limits.BacklashSteps : null, true, token).ConfigureAwait(false))!;
            if (!samples[i].Measurement.HasMeasurement || samples[i].ComparisonGroupId != samples[0].ComparisonGroupId)
                throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.AutofocusInvalidSample,
                    "The sweep needs valid measurements of the same star under comparable preview conditions.");
        }
        var widths = samples.Select(sample => sample.Measurement.HalfFluxDiameterPixels!.Value).ToArray();
        var best = Array.IndexOf(widths, widths.Min());
        if (best == 0 || best == widths.Length - 1 ||
            Enumerable.Range(1, widths.Length - 1).Any(i => i <= best ? widths[i] >= widths[i - 1] : widths[i] <= widths[i - 1]))
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.AutofocusUnbracketed,
                "The HFD measurements did not bracket a distinct minimum. Adjust the starting position or sweep spacing.");
        var selected = samples[best].MotorPosition!.Value;
        // Repeat the same approach direction to reproduce optical focus despite backlash.
        var verified = await MotorOperationAsync(session, source, motor, selected, selected - limits.BacklashSteps,
            true, token).ConfigureAwait(false);
        if (verified?.ComparisonGroupId != samples[best].ComparisonGroupId ||
            verified?.Measurement.HalfFluxDiameterPixels is not { } hfd ||
            hfd >= Math.Min(widths[best - 1], widths[best + 1]))
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.AutofocusVerificationFailed,
                "The return exposure did not confirm the measured HFD minimum.");
        return selected;
    }

    private async Task<ManualFocusSample?> MotorOperationAsync(Session session, CameraModuleManualFocusPreviewSource source,
        IMotorizedFocuser motor, int target, int? approach, bool measure, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(session.Settings.Exposure + _limits.SampleDeadlineGrace, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            return await source.AtBoundaryAsync<ManualFocusSample?>(CaptureBoundaryKind.Motor, session.ModuleGeneration, async (lease, preview, ct) =>
            {
                try
                {
                    if (approach.HasValue && motor.Capabilities.BacklashSteps > 0)
                        await MoveAsync(motor, approach.Value, ct).ConfigureAwait(false);
                    await MoveAsync(motor, target, ct).ConfigureAwait(false);
                    if (!measure) return null;
                    var position = SimulatedPosition(motor, preview.SimulatedFocus);
                    var frame = await source.CaptureAsync(lease, preview, session.Settings, position, ct).ConfigureAwait(false);
                    var sequence = session.TotalSamples + 1;
                    var tracked = session.Target;
                    var hint = session.Hint;
                    var outcome = ManualFocusPreviewMeasurement.Measure(frame.Frame, sequence, tracked, hint, ct, frame.ImageCircle);
                    var sample = CreateSample(sequence, session.Settings, position, session.TargetSource, session.ComparisonRevision,
                        frame, outcome) with
                    { MotorPosition = target };
                    ct.ThrowIfCancellationRequested();
                    VerifyPosition(motor.State, target);
                    lock (_sync)
                    {
                        session.Position = position;
                        session.Record(sample, outcome, tracked, hint);
                    }
                    return sample;
                }
                catch
                {
                    await AbortMotorAsync(motor).ConfigureAwait(false);
                    throw;
                }
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.SampleDeadlineExceeded,
                "The motor operation exceeded its preview deadline.");
        }
    }

    [LoggerMessage(EventId = 4705, Level = LogLevel.Warning,
        Message = "Focus session {SessionId} could not verify the starting position. Capture admission is closed.")]
    private static partial void LogRestorationUnverified(ILogger logger, string sessionId);

    private async Task AbortMotorAsync(IMotorizedFocuser motor)
    {
        using var cleanup = new CancellationTokenSource(_limits.StopWaitTimeout, _timeProvider);
        await motor.AbortAsync(cleanup.Token).ConfigureAwait(false);
    }

    private static async Task MoveAsync(IMotorizedFocuser motor, int target, CancellationToken token)
    {
        var result = await motor.MoveAsync(target, token).ConfigureAwait(false);
        if (result.Outcome != MotorizedFocuserMoveOutcome.Completed)
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.MotorMoveFailed, $"Focuser move ended as {result.Outcome}.");
        token.ThrowIfCancellationRequested();
        VerifyPosition(result.State, target);
    }

    private static void VerifyPosition(MotorizedFocuserState state, int target)
    {
        if (!state.Connected || state.IsMoving || state.Stalled || state.Position != target)
            throw new ManualFocusSessionUnavailableException(ManualFocusReasonCodes.MotorPositionUnverified, "The focuser did not confirm its settled target position.");
    }
}
