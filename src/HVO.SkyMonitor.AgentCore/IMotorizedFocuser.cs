namespace HVO.SkyMonitor.AgentCore;

/// <summary>
/// Optional SDK-free motor capability. Only the host's exclusive focus-session/acquisition owner may dispatch
/// moves, linking its deadline, cancellation and module revocation and awaiting termination before releasing
/// acquisition. This contract grants no authorization, camera ownership or unattended-operation policy.
/// </summary>
public interface IMotorizedFocuser
{
    MotorizedFocuserCapabilities Capabilities { get; }

    MotorizedFocuserState State { get; }

    /// <summary>
    /// Moves to an inclusive-limit absolute motor coordinate without automatic backlash compensation.
    /// Rejects an out-of-range target or a concurrent move. Cancellation stops this move and cancels its task.
    /// A disconnected/stalled device returns that explicit outcome. The first terminal event wins.
    /// </summary>
    Task<MotorizedFocuserMoveResult> MoveAsync(int targetPosition, CancellationToken cancellationToken);

    /// <summary>
    /// Stops the active move and waits for termination; its result is Aborted. Idle abort is idempotent.
    /// A pre-cancelled token changes nothing. Callers must await this operation before releasing acquisition.
    /// </summary>
    Task<MotorizedFocuserState> AbortAsync(CancellationToken cancellationToken);
}

/// <summary>Inclusive nonnegative motor-step limits and declared mechanical play; no automatic compensation.</summary>
public sealed record MotorizedFocuserCapabilities(int MinimumPosition, int MaximumPosition, int BacklashSteps);

/// <summary>An atomic device snapshot. Null temperature means unavailable; null target means stationary.</summary>
public sealed record MotorizedFocuserState(
    bool Connected,
    int Position,
    int? TargetPosition,
    bool Stalled,
    double? TemperatureCelsius)
{
    public bool IsMoving => TargetPosition.HasValue;
}

public enum MotorizedFocuserMoveOutcome
{
    Completed,
    Aborted,
    Stalled,
    Disconnected
}

/// <summary>Immutable requested target, explicit outcome and actual stationary state at termination.</summary>
public sealed record MotorizedFocuserMoveResult(
    int TargetPosition, MotorizedFocuserMoveOutcome Outcome, MotorizedFocuserState State);
