using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

// Event IDs 7700-7719 belong to the orbital elements store. Messages carry identities and stable codes only,
// never provider bytes, operator idempotency keys or free-form provider text.
public sealed partial class SqliteOrbitalElementsStore
{
    [LoggerMessage(7700, LogLevel.Information,
        "Orbital elements store initialized; {InterruptedAttempts} interrupted attempts settled and {RemovedOrphans} verified orphan files removed")]
    private static partial void StoreInitialized(ILogger logger, int interruptedAttempts, int removedOrphans);

    [LoggerMessage(7701, LogLevel.Warning, "An ambiguous orbital storage entry was left in place under {Directory}")]
    private static partial void AmbiguousEntryLeft(ILogger logger, string directory);

    [LoggerMessage(7702, LogLevel.Information, "Orbital provider attempt {AttemptId} admitted ({Trigger})")]
    private static partial void AttemptAdmitted(ILogger logger, string attemptId, string trigger);

    [LoggerMessage(7703, LogLevel.Warning,
        "Orbital provider halted with HTTP {HttpStatusCode} on attempt {AttemptId}; no further attempts until an operator resumes it")]
    private static partial void ProviderHalted(ILogger logger, int httpStatusCode, string attemptId);

    [LoggerMessage(7704, LogLevel.Warning, "Orbital provider attempt {AttemptId} failed in transport ({ReasonCode})")]
    private static partial void TransportFailed(ILogger logger, string reasonCode, string attemptId);

    [LoggerMessage(7705, LogLevel.Warning,
        "Orbital candidate from attempt {AttemptId} quarantined ({ReasonCode}); evidence retained: {Retained}")]
    private static partial void CandidateQuarantined(ILogger logger, string reasonCode, string attemptId, bool retained);

    [LoggerMessage(7706, LogLevel.Warning, "Quarantine evidence {RawSha256} could not be retained ({ReasonCode})")]
    private static partial void QuarantineFileFailed(ILogger logger, string rawSha256, string reasonCode, Exception exception);

    [LoggerMessage(7707, LogLevel.Information,
        "Orbital snapshot {ManifestSha256} activated by attempt {AttemptId}; payload unchanged: {PayloadUnchanged}")]
    private static partial void SnapshotActivated(ILogger logger, string manifestSha256, bool payloadUnchanged, string attemptId);

    [LoggerMessage(7708, LogLevel.Error,
        "Orbital publication for attempt {AttemptId} failed ({ReasonCode}); the last-known-good snapshot remains active")]
    private static partial void PublicationFailed(ILogger logger, string reasonCode, string attemptId, Exception exception);

    [LoggerMessage(7709, LogLevel.Error,
        "The failure of orbital attempt {AttemptId} could not be recorded; restart reconciliation will settle it")]
    private static partial void FailureNotRecorded(ILogger logger, string attemptId, Exception exception);

    [LoggerMessage(7710, LogLevel.Warning, "Orbital storage cleanup for {Scope} did not complete; reconciliation will retry")]
    private static partial void CleanupFailed(ILogger logger, string scope, Exception exception);

    [LoggerMessage(7711, LogLevel.Information, "Orbital provider halt for HTTP {HttpStatusCode} resumed by an operator at revision {Revision}")]
    private static partial void ProviderResumed(ILogger logger, int? httpStatusCode, long revision);

    [LoggerMessage(7712, LogLevel.Information, "Orbital snapshot {ManifestSha256} retired by retention")]
    private static partial void SnapshotRetired(ILogger logger, string manifestSha256);

    [LoggerMessage(7713, LogLevel.Warning,
        "Retention left orbital snapshot {ManifestSha256} files in place because an entry was not a contained regular file")]
    private static partial void RetentionRefused(ILogger logger, string manifestSha256);

    [LoggerMessage(7714, LogLevel.Warning, "Orbital attempt {AttemptId} was interrupted before it completed")]
    private static partial void AttemptInterrupted(ILogger logger, string attemptId);
}
