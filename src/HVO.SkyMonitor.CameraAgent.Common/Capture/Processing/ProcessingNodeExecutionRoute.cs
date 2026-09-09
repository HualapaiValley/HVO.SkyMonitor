namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>
/// The execution route a single durable node attempt took. The value set is closed by construction so the route
/// is safe to persist and to project; it is never derived from an operator-supplied node id.
/// </summary>
/// <remarks>
/// A replay dispatches a recipe-backed node to the local replay runner only when the configured replay profile
/// selects it, and complementary nodes never reach that decision at all. Recording the route the attempt actually
/// took is therefore the only way to read back which nodes dispatched: the dispatched count alone cannot
/// distinguish a graph whose recipe-backed set changed from one whose set is intact.
/// </remarks>
public enum ProcessingNodeExecutionRoute
{
    /// <summary>
    /// No route was recorded for the attempt. The attempt is still running, was interrupted, was skipped before it
    /// executed, or was written before the schema carried a route. This is distinct from
    /// <see cref="InProcess"/>, which is a positive record that the attempt executed without dispatching.
    /// </summary>
    Unknown,

    /// <summary>The attempt executed inside the CameraAgent process.</summary>
    InProcess,

    /// <summary>The attempt dispatched to the local replay runner.</summary>
    LocalRunner
}
