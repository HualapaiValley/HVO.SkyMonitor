using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>Immutable virtual motor inputs. Time advances only when the test explicitly calls AdvanceBy.</summary>
public sealed record VirtualMotorizedFocuserOptions(
    int MinimumPosition = 0,
    int MaximumPosition = 10000,
    int InitialPosition = 5000,
    int BacklashSteps = 0,
    int StepsPerInterval = 10,
    TimeSpan? StepInterval = null,
    double? TemperatureCelsius = null);

/// <summary>
/// Deterministic hardware-free motor, never registered with the host automatically. No timers, workers, device
/// handles or durable state. AdvanceBy performs bounded arithmetic even for maximum travel and elapsed time.
/// OpticalPosition models mechanical play separately from the reported motor coordinate.
/// </summary>
public sealed class VirtualMotorizedFocuser : IMotorizedFocuser
{
    private readonly object _sync = new();
    private readonly int _stepsPerInterval;
    private readonly long _intervalTicks;
    private int _position;
    private int _opticalPosition;
    private int _play;
    private bool _connected = true;
    private bool _stalled;
    private double? _temperature;
    private Move? _move;

    public VirtualMotorizedFocuser(VirtualMotorizedFocuserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumPosition);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumPosition, options.MinimumPosition);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InitialPosition, options.MinimumPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.InitialPosition, options.MaximumPosition);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BacklashSteps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.BacklashSteps, options.MaximumPosition - options.MinimumPosition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.StepsPerInterval);
        var interval = options.StepInterval ?? TimeSpan.FromMilliseconds(100);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Step interval must be positive and at most one minute.");
        }
        ValidateTemperature(options.TemperatureCelsius);
        Capabilities = new(options.MinimumPosition, options.MaximumPosition, options.BacklashSteps);
        _position = _opticalPosition = options.InitialPosition;
        _temperature = options.TemperatureCelsius;
        _stepsPerInterval = options.StepsPerInterval;
        _intervalTicks = interval.Ticks;
    }

    public MotorizedFocuserCapabilities Capabilities { get; }

    public MotorizedFocuserState State
    {
        get
        {
            lock (_sync)
            {
                return Snapshot();
            }
        }
    }

    public int OpticalPosition
    {
        get
        {
            lock (_sync)
            {
                return _opticalPosition;
            }
        }
    }

    public async Task<MotorizedFocuserMoveResult> MoveAsync(int targetPosition, CancellationToken cancellationToken)
    {
        Move move;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfLessThan(targetPosition, Capabilities.MinimumPosition);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPosition, Capabilities.MaximumPosition);
            if (_move is not null)
            {
                throw new InvalidOperationException("A focuser move is already in progress.");
            }
            if (!_connected || _stalled || targetPosition == _position)
            {
                var outcome = !_connected ? MotorizedFocuserMoveOutcome.Disconnected
                    : _stalled ? MotorizedFocuserMoveOutcome.Stalled : MotorizedFocuserMoveOutcome.Completed;
                return new(targetPosition, outcome, Snapshot());
            }
            move = new Move(targetPosition);
            _move = move;
        }
        // Register outside the state lock and dispose after completion: cancellation can enter the same lock.
        using var registration = cancellationToken.Register(() => Cancel(move, cancellationToken));
        return await move.Completion.Task.ConfigureAwait(false);
    }

    public Task<MotorizedFocuserState> AbortAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Finish(MotorizedFocuserMoveOutcome.Aborted);
            return Task.FromResult(Snapshot());
        }
    }

    /// <summary>Advances only this active move; elapsed time while idle is not credit toward a later move.</summary>
    public void AdvanceBy(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        lock (_sync)
        {
            if (_move is not { } move)
            {
                return;
            }
            // Split before adding so even TimeSpan.MaxValue cannot overflow the accumulated remainder.
            var remainder = move.RemainderTicks + elapsed.Ticks % _intervalTicks;
            var intervals = elapsed.Ticks / _intervalTicks + remainder / _intervalTicks;
            move.RemainderTicks = remainder % _intervalTicks;
            var distance = Math.Abs((long)move.Target - _position);
            var intervalsNeeded = (distance + _stepsPerInterval - 1) / _stepsPerInterval;
            var steps = (int)Math.Min(distance, Math.Min(intervals, intervalsNeeded) * _stepsPerInterval);
            ApplySteps(move.Target > _position ? steps : -steps);
            if (_position == move.Target)
            {
                Finish(MotorizedFocuserMoveOutcome.Completed);
            }
        }
    }

    public void SetConnected(bool connected)
    {
        lock (_sync)
        {
            _connected = connected;
            if (!connected)
            {
                Finish(MotorizedFocuserMoveOutcome.Disconnected);
            }
        }
    }

    public void SetStalled(bool stalled)
    {
        lock (_sync)
        {
            _stalled = stalled;
            if (stalled)
            {
                Finish(MotorizedFocuserMoveOutcome.Stalled);
            }
        }
    }

    public void SetTemperature(double? temperatureCelsius)
    {
        ValidateTemperature(temperatureCelsius);
        lock (_sync)
        {
            _temperature = temperatureCelsius;
        }
    }

    private static void ValidateTemperature(double? temperature)
    {
        if (temperature is { } value && !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be finite or unavailable.");
        }
    }

    private MotorizedFocuserState Snapshot() => new(_connected, _position, _move?.Target, _stalled, _temperature);

    private void ApplySteps(int steps)
    {
        _position += steps;
        if (steps >= 0)
        {
            var consumed = Math.Min(steps, _play);
            _play -= consumed;
            _opticalPosition += steps - consumed;
        }
        else
        {
            var consumed = Math.Min(-steps, Capabilities.BacklashSteps - _play);
            _play += consumed;
            _opticalPosition += steps + consumed;
        }
    }

    private void Finish(MotorizedFocuserMoveOutcome outcome)
    {
        if (_move is not { } move)
        {
            return;
        }
        _move = null;
        move.Completion.TrySetResult(new(move.Target, outcome, Snapshot()));
    }

    private void Cancel(Move move, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_move, move))
            {
                _move = null;
                move.Completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    private sealed class Move(int target)
    {
        public int Target { get; } = target;
        public long RemainderTicks { get; set; }
        public TaskCompletionSource<MotorizedFocuserMoveResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
