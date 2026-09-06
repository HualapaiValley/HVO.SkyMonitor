namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal enum RawIngressFaultPoint
{
    AfterMigrationTransactionBegan,
    BeforeMigrationCommit,
    BeforeInitializationLifecycleLock,
    ValidationCompleted,
    PayloadPartiallyWritten,
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
    bool IsEnabled(RawIngressFaultPoint point) => false;

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
    public bool IsEnabled(RawIngressFaultPoint point) => false;

    public void Inject(RawIngressFaultPoint point)
    {
    }
}
