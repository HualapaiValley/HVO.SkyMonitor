using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>Independent metadata-only lane. Observations never change a producer fact or its acknowledgement.</summary>
internal sealed class ExecutionEvidenceReconciler(ExecutionEvidenceDatabase database, ExecutionEvidenceService service,
    IObjectStore objectStore, CentralObjectStorageNames storageNames, TimeProvider timeProvider,
    IOptions<ExecutionEvidenceOptions> options, ExecutionEvidenceTelemetry telemetry)
{
    private readonly ApplicationDbContext _db = database.Context;

    public async Task<int> ReconcileAsync(int take, CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 32) { throw new ArgumentOutOfRangeException(nameof(take)); }
        using var activity = ExecutionEvidenceTelemetry.StartActivity("execution-evidence.reconcile");
        var now = timeProvider.GetUtcNow();
        var ids = await _db.Set<ExecutionEvidenceArtifactCheck>().AsNoTracking().Where(c => c.NextCheckAtUtc <= now)
            .OrderBy(c => c.NextCheckAtUtc).ThenBy(c => c.ArtifactReferenceId).Take(take)
            .Select(c => new { c.ArtifactReferenceId, c.Version }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var selected in ids)
        {
            var id = selected.ArtifactReferenceId;
            _db.ChangeTracker.Clear();
            var reference = await _db.Set<ImportedExecutionEvidenceArtifact>().AsNoTracking().SingleAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
            var binding = await _db.Set<ExecutionEvidenceBinding>().AsNoTracking().SingleAsync(b => b.Id == reference.BindingId, cancellationToken).ConfigureAwait(false);
            // All object I/O finishes before taking any evidence lock or starting a transaction.
            var observation = await ObserveAsync(binding, reference, cancellationToken).ConfigureAwait(false);
            var observedAt = timeProvider.GetUtcNow();
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var usage = await service.LockUsageAsync(binding.Id, cancellationToken).ConfigureAwait(false);
            var check = await _db.Set<ExecutionEvidenceArtifactCheck>().SingleAsync(c => c.ArtifactReferenceId == id, cancellationToken).ConfigureAwait(false);
            // Another pass may have completed while this pass waited on object metadata. A random version,
            // rather than a timestamp, fences that stale result even with a frozen/coarse clock.
            if (check.Version != selected.Version) { continue; }
            check.Version = Guid.NewGuid();
            check.NextCheckAtUtc = timeProvider.GetUtcNow().AddSeconds(options.Value.ReconciliationIntervalSeconds);
            if (observation != (check.State, check.ReasonCode, check.ObjectGeneration))
            {
                if (usage.ObservationCount >= options.Value.MaximumObservationsPerBinding ||
                    usage.StoredBytes > options.Value.MaximumStoredBytesPerBinding - 512)
                {
                    // Keep the last observation and every producer fact. Capacity never silently deletes history.
                    await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    telemetry.Observation("pressure");
                    continue;
                }
                check.State = observation.State;
                check.ReasonCode = observation.Reason;
                check.ObjectGeneration = observation.Generation;
                _db.Add(new ImportedExecutionEvidenceObservation
                {
                    BindingId = binding.Id,
                    ArtifactReferenceId = id,
                    State = observation.State,
                    ReasonCode = observation.Reason,
                    ObjectGeneration = observation.Generation,
                    ObservedAtUtc = observedAt
                });
                usage.ObservationCount++;
                usage.StoredBytes += 512;
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.Observation(observation.State);
        }
        return ids.Count;
    }

    private async Task<(string State, string Reason, string? Generation)> ObserveAsync(ExecutionEvidenceBinding binding,
        ImportedExecutionEvidenceArtifact reference, CancellationToken cancellationToken)
    {
        var artifact = await _db.CentralArtifacts.AsNoTracking().AsSplitQuery()
            .Include(a => a.Frame!).ThenInclude(f => f.Timing)
            .Include(a => a.Frame!).ThenInclude(f => f.Control)
            .Include(a => a.Frame!).ThenInclude(f => f.Profiles)
            .Include(a => a.Frame!).ThenInclude(f => f.Location)
            .Include(a => a.Layout).Include(a => a.Recipe).Include(a => a.Sources).Include(a => a.StructuredProduct)
            .SingleOrDefaultAsync(a => a.DevicePublicId == binding.InstallationPublicId && a.ArtifactId == reference.ArtifactId,
                cancellationToken).ConfigureAwait(false);
        if (artifact is null) { return ("Missing", "evidence.artifact-not-received", null); }
        if (artifact.Frame is null || !ExecutionEvidenceService.Owns(binding, artifact.Frame) || artifact.Frame.FrameId != reference.CaptureId)
        { return ("Quarantined", "evidence.resource-ownership", null); }
        if (reference.PayloadSha256 is { } checksum && !string.Equals(checksum, artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase) ||
            reference.PayloadLength is { } length && length != artifact.ByteLength ||
            reference.MediaType is { } mediaType && mediaType != artifact.MediaType ||
            reference.Role is { } role && role != artifact.Role.ToString() ||
            reference.Variant is { } variant && variant != artifact.Variant)
        { return ("Quarantined", "evidence.metadata-mismatch", null); }
        if (reference.OutputIdentitySha256 is { } output)
        {
            var centralOutput = artifact.StructuredProduct?.OutputIdentitySha256 ?? await _db.CentralArtifactProcessingEvidence.AsNoTracking()
                .Where(e => e.CentralArtifactId == artifact.Id).Select(e => e.OutputIdentitySha256).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (centralOutput is not null && !string.Equals(centralOutput, output, StringComparison.OrdinalIgnoreCase))
            { return ("Quarantined", "evidence.output-identity-mismatch", null); }
            // Pixel manifest-v2 does not carry the full output identity. Its content-addressed artifact ID and
            // checksum still match; the canonical imported body retains the full producer identity.
        }
        if (reference.DescriptorSha256 is { } descriptor)
        {
            if (artifact.ReconstructionState != CentralReconstructionState.Complete) { return ("Unresolved", "evidence.descriptor-pending", null); }
            try
            {
                if (!string.Equals(CaptureContractJson.ComputeDescriptorSha256(CentralReconstructionDescriptorFactory.Create(artifact.Frame, artifact)),
                    descriptor, StringComparison.OrdinalIgnoreCase)) { return ("Quarantined", "evidence.descriptor-mismatch", null); }
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            { return ("Quarantined", "evidence.descriptor-invalid", null); }
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Quarantined) { return ("Quarantined", "evidence.object-quarantined", null); }
        if (artifact.ObjectState == CentralArtifactObjectState.Expired) { return ("Missing", "evidence.object-expired", null); }
        if (artifact.ObjectState == CentralArtifactObjectState.Pending) { return ("Unresolved", "evidence.object-pending", null); }
        if (!artifact.StorageReference.StartsWith(storageNames.ArtifactPrefix, StringComparison.Ordinal)) { return ("Quarantined", "evidence.object-reference", null); }
        try
        {
            var stat = await objectStore.StatAsync(storageNames.ArtifactBucket,
                artifact.StorageReference[storageNames.ArtifactPrefix.Length..], cancellationToken).ConfigureAwait(false);
            if (stat.ContentLength != artifact.ByteLength) { return ("Quarantined", "evidence.object-length", null); }
            // A generation is metadata, not proof that the receiver read or rehashed any image bytes.
            return ("Available", "evidence.metadata-matched", stat.Generation.Length <= 256 ? stat.Generation : null);
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        { return ("Missing", "evidence.object-missing", null); }
        catch (ObjectStoreException) { return ("Unresolved", "evidence.object-unavailable", null); }
    }
}

internal sealed partial class ExecutionEvidenceReconciliationWorker(IServiceScopeFactory scopeFactory,
    IOptions<ExecutionEvidenceOptions> options, ILogger<ExecutionEvidenceReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, options.Value.ReconciliationIntervalSeconds)), stoppingToken).ConfigureAwait(false);
                await using var scope = scopeFactory.CreateAsyncScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
                _ = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceReconciler>().ReconcileAsync(32, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException or DbUpdateException or OperationCanceledException)
            { ReconciliationUnavailable(logger); }
        }
    }

    [LoggerMessage(EventId = 2480, Level = LogLevel.Warning, Message = "Execution evidence reconciliation is temporarily unavailable; retained evidence will be retried.")]
    private static partial void ReconciliationUnavailable(ILogger logger);
}
