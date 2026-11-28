using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public static class ProcessingGraphOperationsTelemetry
{
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.ProcessingGraphOperations";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
