namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal enum CaptureLaneFaultPoint
{
    AfterWorkRowsInserted,
    AfterClaimCommitted,
    BeforeHandler,
    AfterHandler,
    BeforeCompletionCommit,
    AfterCompletionCommit,
    AfterRetryCommit,
    AfterQuarantineCommit
}

internal interface ICaptureLaneFaultInjector
{
    void Inject(CaptureLaneFaultPoint point);
}

internal sealed class NullCaptureLaneFaultInjector : ICaptureLaneFaultInjector
{
    public void Inject(CaptureLaneFaultPoint point)
    {
    }
}
