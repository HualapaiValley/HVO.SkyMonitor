namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum CaptureProcessingFaultPoint
{
    BeforeNodeExecution,
    AfterOutputsPublishedBeforeNodeCommit
}

internal interface ICaptureProcessingFaultInjector
{
    void Inject(CaptureProcessingFaultPoint point, string nodeId);
}

internal sealed class NullCaptureProcessingFaultInjector : ICaptureProcessingFaultInjector
{
    internal static NullCaptureProcessingFaultInjector Instance { get; } = new();

    public void Inject(CaptureProcessingFaultPoint point, string nodeId)
    {
    }
}
