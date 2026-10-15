namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Durable replay reason codes that call for a specific operator reading.</summary>
public static class ProcessingReplayReasons
{
    /// <summary>
    /// The replay was abandoned, not failed: this version no longer builds the plan it was accepted under. It is
    /// recorded as <see cref="ProcessingGraphExecutionStatus.Expired"/>, exactly as live work under a superseded plan
    /// is, never retries and never counts as a terminal replay failure.
    /// </summary>
    public const string PlanSuperseded = ProcessingPlanSupersededException.ExecutionReason;

    /// <summary>
    /// An earlier attempt's lease expired before it completed, for example because its host stopped. The replay is
    /// still retryable within its remaining attempts and deadline.
    /// </summary>
    public const string LeaseExpired = "processing.replay-lease-expired";
}
