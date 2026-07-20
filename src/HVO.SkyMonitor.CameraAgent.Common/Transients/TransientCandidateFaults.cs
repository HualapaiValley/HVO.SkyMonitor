namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

internal enum TransientCandidateFaultPoint
{
    BeforeStageCommit,
    AfterStageCommit,
    BeforeReservationCommit,
    AfterReservationCommit,
    BeforeCandidateCommit,
    AfterCandidateCommit,
    BeforeFinalizationCommit,
    AfterFinalizationCommit,
    BeforeSubmissionCommit,
    AfterSubmissionCommit,
    BeforeAcknowledgementCommit,
    AfterAcknowledgementCommit
}

internal interface ITransientCandidateFaultInjector
{
    void Inject(TransientCandidateFaultPoint point);
}

internal sealed class NullTransientCandidateFaultInjector : ITransientCandidateFaultInjector
{
    internal static NullTransientCandidateFaultInjector Instance { get; } = new();

    public void Inject(TransientCandidateFaultPoint point)
    {
    }
}

internal enum TransientRuntimeFaultPoint
{
    BeforeIdentityBatchCommit,
    AfterIdentityBatchCommit,
    AfterCandidateJournalCommit,
    AfterFinalizationJournalCommit,
    AfterHandoffJournalCommit,
    BeforeRuntimeCompletionCommit
}

internal interface ITransientRuntimeFaultInjector
{
    void Inject(TransientRuntimeFaultPoint point);
}

internal sealed class NullTransientRuntimeFaultInjector : ITransientRuntimeFaultInjector
{
    internal static NullTransientRuntimeFaultInjector Instance { get; } = new();

    public void Inject(TransientRuntimeFaultPoint point)
    {
    }
}
