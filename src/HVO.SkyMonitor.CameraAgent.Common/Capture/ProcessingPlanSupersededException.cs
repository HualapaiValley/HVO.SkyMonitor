namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

/// <summary>
/// Durable processing work was planned under a node plan the running code no longer builds, for example live work
/// accepted before an upgrade that changed a producer's plan. The work can never run here, but its raw evidence is
/// intact, so it is abandoned rather than quarantined. It deliberately does not derive from
/// <see cref="InvalidDataException"/>, which marks genuinely corrupt evidence.
/// </summary>
public sealed class ProcessingPlanSupersededException : Exception
{
    internal const string LaneReason = "plan-superseded";
    internal const string ExecutionReason = "processing.plan-superseded";

    public ProcessingPlanSupersededException()
    {
    }

    public ProcessingPlanSupersededException(string message)
        : base(message)
    {
    }

    public ProcessingPlanSupersededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
