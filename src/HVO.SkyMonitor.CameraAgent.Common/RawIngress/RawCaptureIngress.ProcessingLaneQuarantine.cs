namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class RawCaptureIngress
{
    public async ValueTask<ProcessingLaneQuarantinePage> ReadPageAsync(
        int pageSize, long? beforeWorkId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _laneStore.ReadProcessingLaneQuarantinePageAsync(pageSize, beforeWorkId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ProcessingLaneQuarantineReceipt> AcknowledgeAsync(
        ProcessingLaneQuarantineTarget target, string idempotencyKey, string actor, string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.WorkId <= 0 || target.CaptureId == Guid.Empty || target.ManifestSha256.Length != 64 ||
            target.UpdatedUnixMs <= 0 ||
            !ValidProcessingLaneValue(idempotencyKey) || !ValidProcessingLaneValue(actor) ||
            reason != IProcessingLaneQuarantineOperations.ReasonCode ||
            target.FailureReason is "evidence-invalid" or "evidence-expired" ||
            string.IsNullOrWhiteSpace(target.FailureReason) || target.FailureReason.Length > 64)
        {
            throw new ArgumentException("Processing lane acknowledgement is invalid.");
        }
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleGate = Storage.RawIngressLifecycleLock.ForRoot(_options.RawIngressRoot);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _laneStore.AcknowledgeProcessingLaneQuarantineAsync(
                target, idempotencyKey, actor, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private static bool ValidProcessingLaneValue(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}
