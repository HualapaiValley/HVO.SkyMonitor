namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal enum RawIngressFaultPoint
{
    AfterMigrationTransactionBegan,
    BeforeMigrationCommit,
    ValidationCompleted,
    PayloadWritten,
    PayloadFlushed,
    PayloadPublished,
    PayloadDirectorySynced,
    SidecarWritten,
    SidecarFlushed,
    SidecarPublished,
    SidecarDirectorySynced,
    BeforeJournalCommit,
    AfterJournalTransactionBegan,
    AfterJournalRowInserted,
    BeforeJournalTransactionCommit,
    AfterJournalCommit,
    BeforeIndexProjection,
    AfterIndexProjection,
    BeforeWakeUpNotification
}

internal interface IRawIngressFaultInjector
{
    void Inject(RawIngressFaultPoint point);
}

internal interface IRawIngressRecoveryControl
{
    void InvalidateEvidence();
}

internal interface IRawIngressPressureReporter
{
    void ReportPressure(bool underPressure);
}

internal interface IRawIngressWakeupReporter
{
    void ReportWakeup(bool queued);
}

internal sealed class NullRawIngressFaultInjector : IRawIngressFaultInjector
{
    public void Inject(RawIngressFaultPoint point)
    {
    }
}
