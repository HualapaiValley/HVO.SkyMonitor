namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The host's resolved replay execution configuration, as read from bound options.
/// <para>
/// This is the machine-readable form of the facts the operator replay page already showed. Before it
/// existed, a test or deployment check could learn the replay profile only by inspecting the container
/// environment, which reports what the process was started with rather than what it resolved (#804).
/// Every value here is configuration rather than a probe: <c>ReplayProfile</c> is the route replays are
/// configured to take, not evidence that the local runner is reachable, and the route a particular
/// execution actually took is recorded per node on the execution detail instead.
/// </para>
/// </summary>
/// <param name="ReplayProfile">The resolved profile name, one of <c>InProcess</c> or <c>LocalRunner</c>.</param>
/// <param name="MaximumConcurrency">Replays permitted to execute at once.</param>
/// <param name="MaximumPendingCount">Replays permitted to sit queued before submission is refused.</param>
/// <param name="DeadlineSeconds">Seconds a single replay may run before it is abandoned.</param>
/// <param name="MaximumQueueAgeSeconds">Seconds a queued replay may wait before it is abandoned.</param>
public sealed record CameraAgentReplayCapacityResponse(
    string ReplayProfile,
    int MaximumConcurrency,
    int MaximumPendingCount,
    int DeadlineSeconds,
    int MaximumQueueAgeSeconds);
