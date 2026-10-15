using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class RawCaptureIngress
{
    async ValueTask<bool> IRawCaptureIngress.InitializeForOperationsAsync(CancellationToken cancellationToken)
    {
        await InitializeEvidenceOperationsAsync(cancellationToken).ConfigureAwait(false);
        return !Volatile.Read(ref _evidenceUnavailable);
    }

    public async ValueTask<RawEvidencePage> ReadEvidencePageAsync(
        int pageSize, long? beforeRowId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        await InitializeEvidenceOperationsAsync(cancellationToken).ConfigureAwait(false);
        return await _journal.ReadEvidencePageAsync(pageSize, beforeRowId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RawEvidenceReleaseReceipt> ReleaseEvidenceAsync(
        RawEvidenceTarget target, RawEvidenceReleaseAction action, string idempotencyKey,
        string actor, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(action) || target.CaptureId == Guid.Empty || target.ManifestSha256.Length != 64 ||
            !ValidEvidenceOperatorValue(idempotencyKey) || !ValidEvidenceOperatorValue(actor) ||
            (action == RawEvidenceReleaseAction.Revalidate ? reason != "evidence-restored" :
                reason is not ("irrecoverable-evidence" or "operator-approved-loss")))
            throw new ArgumentException("Raw evidence release command is invalid.");
        await InitializeEvidenceOperationsAsync(cancellationToken).ConfigureAwait(false);
        using var activity = RawIngressTelemetry.ActivitySource.StartActivity("raw-ingress.release-evidence");
        activity?.SetTag("action", action.ToString());
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_options.RawIngressRoot);
        try
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var receipt = await _journal.ReleaseEvidenceAsync(
                    target, action, idempotencyKey, actor, reason, entry => RevalidateEvidenceAsync(entry, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                activity?.SetTag("duplicate", receipt.Duplicate);
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                Volatile.Write(ref _initialized, false);
                var health = await _journal.ReadHealthTotalsAsync(CancellationToken.None).ConfigureAwait(false);
                var held = await _journal.ReadHeldTotalsAsync(CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _evidenceUnavailable, health.FailureCount > 0);
                _state.Set(health.FailureCount > 0 ? RawIngressAvailability.Unhealthy : RawIngressAvailability.Initializing,
                    health.FailureCount > 0 ? "reconciliation-findings" : "evidence-release-reinitializing",
                    held.Count, held.Bytes, health.QuarantineCount, health.QuarantineBytes, held.Oldest);
                return receipt;
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private static bool ValidEvidenceOperatorValue(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private async Task InitializeEvidenceOperationsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _journalValidated) && Volatile.Read(ref _evidenceUnavailable)) return;
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException) when (_journalValidated && _evidenceUnavailable)
        {
            // A validated journal with evidence failures must remain operable while capture admission refuses it.
            if ((await _journal.ReadHealthTotalsAsync(cancellationToken).ConfigureAwait(false)).FailureCount == 0) throw;
        }
    }

    private async Task RevalidateEvidenceAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
    {
        var expected = RawIngressFileStore.GetPaths(_options.RawIngressRoot, entry.ExposureStartedUtc, entry.ArtifactId);
        if (entry.PayloadRelativePath != expected.PayloadRelativePath || entry.SidecarRelativePath != expected.SidecarRelativePath)
            throw new InvalidDataException("Raw evidence paths do not match the committed identity.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, expected.PayloadAbsolutePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, expected.SidecarAbsolutePath);
        if (!File.Exists(expected.PayloadAbsolutePath) || !File.Exists(expected.SidecarAbsolutePath) ||
            !await RawIngressReconciler.MatchesCommittedEntryAsync(
                entry, expected.PayloadAbsolutePath, expected.SidecarAbsolutePath, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Recovered raw evidence does not match the committed identity.");
        RawIngressFileStore.SyncFile(_options.RawIngressRoot, expected.PayloadAbsolutePath);
        _telemetry.RecordFileFlush();
        RawIngressFileStore.SyncFile(_options.RawIngressRoot, expected.SidecarAbsolutePath);
        _telemetry.RecordFileFlush();
        RawIngressFileStore.SyncDirectory(Path.GetDirectoryName(expected.PayloadAbsolutePath)!);
        if (OperatingSystem.IsLinux()) _telemetry.RecordDirectorySync();
    }
}
