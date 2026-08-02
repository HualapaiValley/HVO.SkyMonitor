using System.Buffers;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class CentralArtifactReconciliationService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CentralIngestTelemetry telemetry,
    ILogger<CentralArtifactReconciliationService> logger) : BackgroundService
{
    private const string SchedulingConcurrencyMarker = "HVO.SkyMonitor.ReconciliationSchedulingConcurrency";
    private const string VerificationRetryFenceMarker = "HVO.SkyMonitor.VerificationRetryFence";
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";
    private const string BinaryCollation = "Latin1_General_100_BIN2";
    private const int MaximumPendingArtifactsPerCycle = 100;
    internal const int MaximumSqlInventoryArtifactsPerCycle = 25;
    internal const int MaximumMinioInventoryObjectsPerCycle = 100;
    internal const int MaximumRecoveryDispositionsPerCycle = 25;
    internal const int MaximumStagingObjectsPerCycle = 1000;
    internal static readonly TimeSpan StagingObjectGracePeriod = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan VerificationInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan InitialReferenceRetryDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumReferenceRetryDelay = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan InitialVerificationRetryDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumVerificationRetryDelay = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan InventoryInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan RecoveryLeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CatchAllLeaseRenewalPeriod = TimeSpan.FromMinutes(1);
    private const int CatchAllLeaseRenewalInterval = 100;
    internal const int MaximumStagingConvergenceCycles = 47;
    private const int StagingPartitionCount = MaximumStagingConvergenceCycles;
    private const int ObjectPartitionCount = 256;
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var runImmediately = false;
            try
            {
                runImmediately = await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                if (MayContainObjectLocation(exception))
                {
                    LogObjectStoreCycleFailed(GetFailureCategory(exception));
                }
                else
                {
                    LogCycleFailed(exception);
                }
            }
            if (runImmediately)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = CentralIngestTelemetry.StartActivity("central-artifact.reconcile");
        var token = Guid.NewGuid();
        await EnsureCheckpointAsync(cancellationToken).ConfigureAwait(false);
        if (!await TryAcquireLeaseAsync(token, cancellationToken).ConfigureAwait(false))
        {
            telemetry.RecordRecoveryCycle("lease-unavailable", TimeSpan.Zero);
            activity?.SetTag("recovery.outcome", "lease-unavailable");
            return false;
        }

        var statistics = new RecoveryStatistics();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
            await CleanupStagingObjectsAsync(db, minio, token, cancellationToken).ConfigureAwait(false);
            await ProcessRecoveryDispositionsAsync(db, minio, token, statistics, cancellationToken).ConfigureAwait(false);
            var attemptedArtifacts = await ReconcilePendingArtifactsAsync(db, token, statistics, cancellationToken)
                .ConfigureAwait(false);
            await RunInventoryAsync(db, minio, token, statistics, attemptedArtifacts, cancellationToken)
                .ConfigureAwait(false);
            await RecordBacklogAsync(db, cancellationToken).ConfigureAwait(false);
            var immediateWork = await HasImmediateWorkAsync(db, cancellationToken).ConfigureAwait(false);

            var completedAt = timeProvider.GetUtcNow();
            var released = await db.CentralRecoveryCheckpoints
                .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId && item.LeaseToken == token)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastCycleAtUtc, completedAt)
                    .SetProperty(item => item.LeaseToken, (Guid?)null)
                    .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
                .ConfigureAwait(false);
            if (released != 1)
            {
                throw new InvalidOperationException("The central recovery lease was lost.");
            }
            var elapsed = timeProvider.GetElapsedTime(started);
            telemetry.RecordRecoveryCycle("completed", elapsed);
            telemetry.RecordReconciliationDuration("completed", elapsed);
            SetActivity(activity, statistics, "completed", ActivityStatusCode.Ok);
            LogCycleCompleted(
                statistics.Scanned,
                statistics.Matched,
                statistics.Missing,
                statistics.Corrupt,
                statistics.Orphans,
                statistics.Quarantined,
                statistics.Deleted,
                elapsed.TotalMilliseconds);
            return immediateWork;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLeaseAsync(token).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            await RecordCycleFailureAsync(token, cancellationToken).ConfigureAwait(false);
            var elapsed = timeProvider.GetElapsedTime(started);
            telemetry.RecordRecoveryCycle("failed", elapsed);
            telemetry.RecordReconciliationDuration("failed", elapsed);
            SetActivity(activity, statistics, "failed", ActivityStatusCode.Error);
            throw;
        }
    }

    private async Task ReleaseLeaseAsync(Guid token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralRecoveryCheckpoints
            .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId && item.LeaseToken == token)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LeaseToken, (Guid?)null)
                .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task EnsureCheckpointAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.CentralRecoveryCheckpoints.AsNoTracking()
            .AnyAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }
        db.CentralRecoveryCheckpoints.Add(new CentralRecoveryCheckpoint
        {
            Id = CentralRecoveryCheckpoint.SingletonId,
            Generation = 0,
            Phase = CentralRecoveryPhases.Idle,
            NextInventoryAtUtc = DateTimeOffset.UnixEpoch
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.CentralRecoveryCheckpoints.AsNoTracking()
                .AnyAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    private async Task<bool> TryAcquireLeaseAsync(Guid token, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [CentralRecoveryCheckpoints]
            SET [LeaseToken] = {token},
                [LeaseExpiresAtUtc] = DATEADD(second, {(int)RecoveryLeaseDuration.TotalSeconds}, SYSUTCDATETIME())
            WHERE [Id] = {CentralRecoveryCheckpoint.SingletonId}
              AND ([LeaseExpiresAtUtc] IS NULL OR [LeaseExpiresAtUtc] <= SYSUTCDATETIME());
            """, cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    private async Task RenewLeaseAsync(ApplicationDbContext db, Guid token, CancellationToken cancellationToken)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [CentralRecoveryCheckpoints]
            SET [LeaseExpiresAtUtc] = DATEADD(second, {(int)RecoveryLeaseDuration.TotalSeconds}, SYSUTCDATETIME())
            WHERE [Id] = {CentralRecoveryCheckpoint.SingletonId}
              AND [LeaseToken] = {token}
              AND [LeaseExpiresAtUtc] > SYSUTCDATETIME();
            """, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("The central recovery lease was lost.");
        }
    }

    private async Task RecordCycleFailureAsync(Guid token, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = timeProvider.GetUtcNow();
            await db.CentralRecoveryCheckpoints
                .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId && item.LeaseToken == token)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastCycleAtUtc, now)
                    .SetProperty(item => item.LastFailureAtUtc, now)
                    .SetProperty(item => item.LeaseToken, (Guid?)null)
                    .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LogFailureCheckpointFailed(exception);
        }
    }

    private async Task<IReadOnlyList<Guid>> ReconcilePendingArtifactsAsync(
        ApplicationDbContext db,
        Guid token,
        RecoveryStatistics statistics,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var verificationCutoff = now - VerificationInterval;
        var activeGeneration = await db.CentralRecoveryCheckpoints.AsNoTracking()
            .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId
                && item.Phase != CentralRecoveryPhases.Idle)
            .Select(item => (long?)item.Generation)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var verificationIds = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.ObjectVerificationToken != null
                && (artifact.ObjectVerificationRetryAtUtc == null || artifact.ObjectVerificationRetryAtUtc <= now))
            .OrderBy(artifact => artifact.ObjectVerificationRetryAtUtc ?? artifact.ObjectVerificationRequestedAtUtc)
            .ThenBy(artifact => artifact.Id)
            .Take(MaximumPendingArtifactsPerCycle)
            .Select(artifact => artifact.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pendingObjectIds = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.ObjectState == CentralArtifactObjectState.Pending
                && artifact.ObjectVerificationToken == null
                && (artifact.StateReasonCode != "object.missing"
                    || artifact.ObjectVerifiedAtUtc == null
                    || artifact.ObjectVerifiedAtUtc <= verificationCutoff)
                && (artifact.StateReasonCode != "object.reference-unsupported"
                    || artifact.ReferenceRetryAtUtc == null || artifact.ReferenceRetryAtUtc <= now))
            .OrderBy(artifact => artifact.ReceivedAtUtc)
            .ThenBy(artifact => artifact.Id)
            .Take(MaximumPendingArtifactsPerCycle)
            .Select(artifact => artifact.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pendingReferenceIds = await db.CentralArtifacts.AsNoTracking()
                .Where(artifact => artifact.ReconstructionState == CentralReconstructionState.PendingReference
                    && artifact.ObjectVerificationToken == null
                    && (artifact.ReferenceRetryAtUtc == null || artifact.ReferenceRetryAtUtc <= now)
                    && !pendingObjectIds.Contains(artifact.Id))
                .OrderBy(artifact => artifact.ReferenceRetryAtUtc ?? artifact.ReceivedAtUtc)
                .ThenBy(artifact => artifact.Id)
                .Take(MaximumPendingArtifactsPerCycle)
                .Select(artifact => artifact.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var queues = new[] { verificationIds, pendingObjectIds, pendingReferenceIds };
        var artifactIds = Enumerable.Range(0, queues.Max(queue => queue.Count))
            .SelectMany(index => queues.Where(queue => index < queue.Count).Select(queue => queue[index]))
            .Take(MaximumPendingArtifactsPerCycle)
            .ToList();
        foreach (var artifactId in artifactIds)
        {
            await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await ReconcileOneWithConcurrencyRetryAsync(
                    artifactId, activeGeneration, token, cancellationToken)
                    .ConfigureAwait(false);
                statistics.Add(result);
                if (result.Transitioned)
                {
                    await RecordFindingAsync(db, token, result.Outcome, result.Bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                await ScheduleVerificationRetryAsync(
                    db,
                    artifactId,
                    exception.Data[VerificationRetryFenceMarker] as VerificationRetryFence,
                    cancellationToken).ConfigureAwait(false);
                if (MayContainObjectLocation(exception))
                {
                    LogObjectStoreRecordFailed(GetFailureCategory(exception));
                }
                else
                {
                    LogRecordFailed(exception);
                }
            }
        }
        return artifactIds;
    }

    private async Task<bool> HasImmediateWorkAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var checkpoint = await db.CentralRecoveryCheckpoints.AsNoTracking()
            .SingleAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        var verificationCutoff = now - VerificationInterval;
        if (await db.CentralArtifacts.AsNoTracking()
            .AnyAsync(artifact => artifact.ObjectVerificationToken != null
                && (artifact.ObjectVerificationRetryAtUtc == null || artifact.ObjectVerificationRetryAtUtc <= now),
                cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }
        if (await db.CentralObjectRecoveryDispositions.AsNoTracking()
            .AnyAsync(item => item.OperationToken == null
                && (item.State == CentralObjectRecoveryStates.PendingCopy
                    || item.State == CentralObjectRecoveryStates.PendingDelete), cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (await db.CentralArtifacts.AsNoTracking()
            .AnyAsync(artifact => artifact.ObjectState == CentralArtifactObjectState.Pending
                && artifact.ObjectVerificationToken == null
                && (artifact.StateReasonCode != "object.missing"
                    || artifact.ObjectVerifiedAtUtc == null
                    || artifact.ObjectVerifiedAtUtc <= verificationCutoff)
                && (artifact.StateReasonCode != "object.reference-unsupported"
                    || artifact.ReferenceRetryAtUtc == null || artifact.ReferenceRetryAtUtc <= now), cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }
        if (await db.CentralArtifacts.AsNoTracking()
            .AnyAsync(artifact => artifact.ReconstructionState == CentralReconstructionState.PendingReference
                    && artifact.ObjectVerificationToken == null
                    && (artifact.ReferenceRetryAtUtc == null || artifact.ReferenceRetryAtUtc <= now),
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        return checkpoint.Phase != CentralRecoveryPhases.Idle || checkpoint.NextInventoryAtUtc <= now;
    }

    private async Task RunInventoryAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        Guid token,
        RecoveryStatistics statistics,
        IReadOnlyList<Guid> attemptedArtifacts,
        CancellationToken cancellationToken)
    {
        var checkpoint = await db.CentralRecoveryCheckpoints.AsNoTracking()
            .SingleAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (checkpoint.Phase == CentralRecoveryPhases.Idle)
        {
            if (checkpoint.NextInventoryAtUtc > now)
            {
                return;
            }
            var affected = await db.CentralRecoveryCheckpoints
                .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId && item.LeaseToken == token)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Generation, item => item.Generation + 1)
                    .SetProperty(item => item.Phase, CentralRecoveryPhases.SqlArtifacts)
                    .SetProperty(item => item.ObjectPartition, 0)
                    .SetProperty(item => item.ObjectCursor, (string?)null)
                    .SetProperty(item => item.FindingCount, 0L)
                    .SetProperty(item => item.FindingBytes, 0L)
                    .SetProperty(item => item.InventoryStartedAtUtc, now)
                    .SetProperty(item => item.LastProgressAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("The central recovery lease was lost.");
            }
            checkpoint = await db.CentralRecoveryCheckpoints.AsNoTracking()
                .SingleAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (checkpoint.Phase == CentralRecoveryPhases.SqlArtifacts)
        {
            await InventorySqlArtifactsAsync(
                db, token, checkpoint.Generation, statistics, attemptedArtifacts, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (checkpoint.Phase == CentralRecoveryPhases.MinioArtifacts)
        {
            await InventoryMinioPrefixAsync(db, minio, token, checkpoint, "artifacts/",
                CentralRecoveryPhases.MinioArtifactsCatchAll, statistics, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (checkpoint.Phase == CentralRecoveryPhases.MinioArtifactsCatchAll)
        {
            await InventoryMinioCatchAllAsync(db, minio, token, "artifacts/",
                CentralRecoveryPhases.MinioDerivatives, statistics, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (checkpoint.Phase == CentralRecoveryPhases.MinioDerivatives)
        {
            await InventoryMinioPrefixAsync(db, minio, token, checkpoint, "derivatives/",
                CentralRecoveryPhases.MinioDerivativesCatchAll, statistics, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (checkpoint.Phase == CentralRecoveryPhases.MinioDerivativesCatchAll)
        {
            await InventoryMinioCatchAllAsync(db, minio, token, "derivatives/",
                CentralRecoveryPhases.Idle, statistics, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InventorySqlArtifactsAsync(
        ApplicationDbContext db,
        Guid token,
        long generation,
        RecoveryStatistics statistics,
        IReadOnlyList<Guid> attemptedArtifacts,
        CancellationToken cancellationToken)
    {
        var ids = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.RecoveryGeneration < generation
                && !attemptedArtifacts.Contains(artifact.Id)
                && (artifact.StorageReference.StartsWith(BucketPrefix + "artifacts/")
                    || artifact.StorageReference.StartsWith(BucketPrefix + "derivatives/")))
            .OrderBy(artifact => artifact.RecoveryGeneration)
            .ThenBy(artifact => artifact.Id)
            .Take(MaximumSqlInventoryArtifactsPerCycle + 1)
            .Select(artifact => artifact.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = ids.Count > MaximumSqlInventoryArtifactsPerCycle;
        foreach (var id in ids.Take(MaximumSqlInventoryArtifactsPerCycle))
        {
            await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
            var result = await ReconcileOneWithConcurrencyRetryAsync(id, generation, token, cancellationToken)
                .ConfigureAwait(false);
            statistics.Add(result);
            if (result.Transitioned)
            {
                await RecordFindingAsync(db, token, result.Outcome, result.Bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        if (!hasMore)
        {
            var now = timeProvider.GetUtcNow();
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(item => item.Phase, CentralRecoveryPhases.MinioArtifacts)
                .SetProperty(item => item.ObjectPartition, 0)
                .SetProperty(item => item.ObjectCursor, (string?)null)
                .SetProperty(item => item.LastProgressAtUtc, now), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await MarkProgressAsync(db, token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InventoryMinioPrefixAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        Guid token,
        CentralRecoveryCheckpoint checkpoint,
        string prefix,
        string nextPhase,
        RecoveryStatistics statistics,
        CancellationToken cancellationToken)
    {
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("The central artifact bucket is unavailable.");
        }
        var processed = 0;
        var pageBytes = 0L;
        var hasMore = false;
        // MinIO .NET 7 does not expose S3 continuation/start-after tokens. Restrict replay to one
        // generated device-key partition instead of repeatedly skipping the entire object namespace.
        var partitionPrefix = $"{prefix}{checkpoint.ObjectPartition:x2}";
        var args = new ListObjectsArgs().WithBucket(Bucket).WithPrefix(partitionPrefix).WithRecursive(true);
        await foreach (var item in minio.ListObjectsEnumAsync(args, cancellationToken).ConfigureAwait(false))
        {
            if (checkpoint.ObjectCursor is not null
                && string.CompareOrdinal(item.Key, checkpoint.ObjectCursor) <= 0)
            {
                continue;
            }
            if (processed >= MaximumMinioInventoryObjectsPerCycle)
            {
                hasMore = true;
                break;
            }
            await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
            processed++;
            pageBytes += checked((long)item.Size);
            statistics.Scanned++;
            statistics.ScannedBytes += checked((long)item.Size);
            await InventoryMinioObjectAsync(db, token, item.Key, checked((long)item.Size), statistics, cancellationToken)
                .ConfigureAwait(false);
            var cursor = item.Key;
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(item => item.ObjectCursor, cursor)
                .SetProperty(item => item.LastProgressAtUtc, timeProvider.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
        }

        telemetry.RecordRecoveryInventory("scanned", processed, pageBytes);
        if (!hasMore)
        {
            var now = timeProvider.GetUtcNow();
            if (checkpoint.ObjectPartition + 1 < ObjectPartitionCount)
            {
                var nextPartition = checkpoint.ObjectPartition + 1;
                await UpdateCheckpointAsync(db, token, setters => setters
                    .SetProperty(item => item.ObjectPartition, nextPartition)
                    .SetProperty(item => item.ObjectCursor, (string?)null)
                    .SetProperty(item => item.LastProgressAtUtc, now), cancellationToken).ConfigureAwait(false);
            }
            else if (nextPhase == CentralRecoveryPhases.Idle)
            {
                await UpdateCheckpointAsync(db, token, setters => setters
                    .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                    .SetProperty(item => item.ObjectPartition, 0)
                    .SetProperty(item => item.ObjectCursor, (string?)null)
                    .SetProperty(item => item.LastProgressAtUtc, now)
                    .SetProperty(item => item.LastCompletedAtUtc, now)
                    .SetProperty(item => item.NextInventoryAtUtc, now + InventoryInterval), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await UpdateCheckpointAsync(db, token, setters => setters
                    .SetProperty(item => item.Phase, nextPhase)
                    .SetProperty(item => item.ObjectPartition, 0)
                    .SetProperty(item => item.ObjectCursor, (string?)null)
                    .SetProperty(item => item.LastProgressAtUtc, now), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task InventoryMinioCatchAllAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        Guid token,
        string prefix,
        string nextPhase,
        RecoveryStatistics statistics,
        CancellationToken cancellationToken)
    {
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("The central artifact bucket is unavailable.");
        }
        long examined = 0;
        var examinedBytes = 0L;
        // MinIO .NET 7 exposes no caller-supplied continuation token. Audit the prefix exactly once per
        // recovery generation in one O(namespace) streaming pass. Deployments must size the 30-minute lease
        // for a complete artifacts/ or derivatives/ listing; canonical generated-key work remains partitioned.
        var args = new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true);
        var leaseRenewedAt = timeProvider.GetTimestamp();
        await foreach (var item in minio.ListObjectsEnumAsync(args, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (examined % CatchAllLeaseRenewalInterval == 0
                || timeProvider.GetElapsedTime(leaseRenewedAt) >= CatchAllLeaseRenewalPeriod)
            {
                await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                leaseRenewedAt = timeProvider.GetTimestamp();
            }
            examined++;
            examinedBytes += checked((long)item.Size);
            if (!IsCanonicalGeneratedObjectKey(item.Key, prefix))
            {
                statistics.Scanned++;
                statistics.ScannedBytes += checked((long)item.Size);
                await InventoryMinioObjectAsync(
                    db, token, item.Key, checked((long)item.Size), statistics, cancellationToken).ConfigureAwait(false);
            }
        }

        telemetry.RecordRecoveryInventory("catch-all-examined", examined, examinedBytes);
        var now = timeProvider.GetUtcNow();
        if (nextPhase == CentralRecoveryPhases.Idle)
        {
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(item => item.Phase, CentralRecoveryPhases.Idle)
                .SetProperty(item => item.ObjectPartition, 0)
                .SetProperty(item => item.ObjectCursor, (string?)null)
                .SetProperty(item => item.LastProgressAtUtc, now)
                .SetProperty(item => item.LastCompletedAtUtc, now)
                .SetProperty(item => item.NextInventoryAtUtc, now + InventoryInterval), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(item => item.Phase, nextPhase)
                .SetProperty(item => item.ObjectPartition, 0)
                .SetProperty(item => item.ObjectCursor, (string?)null)
                .SetProperty(item => item.LastProgressAtUtc, now), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InventoryMinioObjectAsync(
        ApplicationDbContext db,
        Guid token,
        string objectKey,
        long byteLength,
        RecoveryStatistics statistics,
        CancellationToken cancellationToken)
    {
        var owners = await GetObjectOwnerStatesAsync(db, objectKey, cancellationToken).ConfigureAwait(false);
        if (owners.Count == 0)
        {
            if (await EnsureDispositionAsync(db, token, objectKey, byteLength,
                    CentralObjectRecoveryKinds.OrphanQuarantine, cancellationToken).ConfigureAwait(false))
            {
                statistics.Orphans++;
                statistics.OrphanBytes += byteLength;
                LogFinding("orphan", byteLength);
            }
        }
        else if (owners.All(state => state == CentralArtifactObjectState.Expired))
        {
            if (await ReopenCompletedRetentionDeletionAsync(
                    db, objectKey, byteLength, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            _ = await EnsureDispositionAsync(db, token, objectKey, byteLength,
                CentralObjectRecoveryKinds.ExpiredDelete, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            statistics.Matched++;
            statistics.MatchedBytes += byteLength;
        }
    }

    private static bool IsCanonicalGeneratedObjectKey(string objectKey, string prefix)
    {
        if (!objectKey.StartsWith(prefix, StringComparison.Ordinal) || objectKey.Length < prefix.Length + 33
            || objectKey[prefix.Length + 32] != '/')
        {
            return false;
        }
        return objectKey.AsSpan(prefix.Length, 32).IndexOfAnyExcept(LowerHexCharacters) < 0;
    }

    private async Task<bool> EnsureDispositionAsync(
        ApplicationDbContext db,
        Guid token,
        string sourceObjectKey,
        long byteLength,
        string kind,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var identity = CreateObjectKeyIdentity(sourceObjectKey);
        var target = kind == CentralObjectRecoveryKinds.OrphanQuarantine
            ? CreateQuarantineObjectKey(sourceObjectKey)
            : null;
        var identityMatches = await db.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.SourceObjectIdentitySha256 == identity)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existing = identityMatches.SingleOrDefault(item =>
            string.Equals(item.SourceObjectKey, sourceObjectKey, StringComparison.Ordinal));
        if (existing is null && identityMatches.Count != 0)
        {
            throw new InvalidOperationException("A central recovery object-key identity collision was detected.");
        }
        if (existing is not null)
        {
            var existingId = existing.Id;
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            _ = await CentralArtifactRetentionLock.AcquireDispositionAsync(db, existingId, cancellationToken)
                .ConfigureAwait(false);
            existing = await db.CentralObjectRecoveryDispositions.SingleOrDefaultAsync(item =>
                item.Id == existingId, cancellationToken).ConfigureAwait(false);
            if (existing is null
                || existing.SourceObjectIdentitySha256 != identity
                || !string.Equals(existing.SourceObjectKey, sourceObjectKey, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return false;
            }
            if (existing.OperationToken is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return false;
            }
            if (existing.State is not (CentralObjectRecoveryStates.Completed or CentralObjectRecoveryStates.Cancelled))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return false;
            }
            existing.Kind = kind;
            existing.TargetObjectKey = target;
            existing.State = kind == CentralObjectRecoveryKinds.OrphanQuarantine
                ? CentralObjectRecoveryStates.PendingCopy
                : CentralObjectRecoveryStates.PendingDelete;
            existing.ByteLength = byteLength;
            existing.ContentChecksumSha256 = null;
            existing.ReasonCode = null;
            existing.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await IncrementFindingAsync(db, token, byteLength, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return true;
        }
        db.CentralObjectRecoveryDispositions.Add(new CentralObjectRecoveryDisposition
        {
            SourceObjectIdentitySha256 = identity,
            SourceObjectKey = sourceObjectKey,
            TargetObjectKey = target,
            Kind = kind,
            State = kind == CentralObjectRecoveryKinds.OrphanQuarantine
                ? CentralObjectRecoveryStates.PendingCopy
                : CentralObjectRecoveryStates.PendingDelete,
            ByteLength = byteLength,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Entries<CentralObjectRecoveryDisposition>()
                .Where(entry => entry.State == EntityState.Added
                    && entry.Entity.SourceObjectIdentitySha256 == identity)
                .ToList()
                .ForEach(entry => entry.State = EntityState.Detached);
            var concurrent = await db.CentralObjectRecoveryDispositions.AsNoTracking()
                .Where(item => item.SourceObjectIdentitySha256 == identity)
                .Select(item => item.SourceObjectKey)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (concurrent is not null && string.Equals(concurrent, sourceObjectKey, StringComparison.Ordinal))
            {
                return false;
            }
            if (concurrent is not null)
            {
                throw new InvalidOperationException("A central recovery object-key identity collision was detected.");
            }
            throw;
        }
        await IncrementFindingAsync(db, token, byteLength, now, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ReopenCompletedRetentionDeletionAsync(
        ApplicationDbContext db,
        string objectKey,
        long byteLength,
        CancellationToken cancellationToken)
    {
        var storageReference = BucketPrefix + objectKey;
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            db, storageReference, cancellationToken).ConfigureAwait(false);
        var identity = CreateObjectKeyIdentity(objectKey);
        var candidate = await db.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.SourceObjectIdentitySha256 == identity
                && item.OperationToken != null
                && item.CentralArtifactId != null)
            .Select(item => new
            {
                item.Id,
                CentralArtifactId = item.CentralArtifactId!.Value
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralArtifactRetentionLock.AcquireAsync(
            db, candidate.CentralArtifactId, cancellationToken).ConfigureAwait(false);
        _ = await CentralArtifactRetentionLock.AcquireDispositionAsync(
            db, candidate.Id, cancellationToken).ConfigureAwait(false);
        var linked = await (from disposition in db.CentralObjectRecoveryDispositions
                            join artifact in db.CentralArtifacts
                                on disposition.CentralArtifactId equals (Guid?)artifact.Id
                            where disposition.Id == candidate.Id
                                && disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                                && disposition.State == CentralObjectRecoveryStates.Completed
                                && disposition.OperationToken != null
                                && disposition.CompletedAtUtc != null
                                && artifact.Id == candidate.CentralArtifactId
                                && artifact.ObjectState == CentralArtifactObjectState.Expired
                                && artifact.RetentionDeletionToken == disposition.OperationToken
                                && artifact.RetentionDeletionRequestedAtUtc != null
                                && artifact.RetentionDeletionCompletedAtUtc != null
                                && EF.Functions.Collate(
                                    disposition.SourceObjectKey,
                                    BinaryCollation) == objectKey
                                && EF.Functions.Collate(
                                    artifact.StorageReference,
                                    BinaryCollation) == storageReference
                            select new { disposition, artifact })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (linked is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        var now = timeProvider.GetUtcNow();
        linked.artifact.RetentionDeletionCompletedAtUtc = null;
        linked.disposition.State = CentralObjectRecoveryStates.PendingDelete;
        linked.disposition.ByteLength = byteLength;
        linked.disposition.NextAttemptAtUtc = now;
        linked.disposition.CompletedAtUtc = null;
        linked.disposition.ReasonCode = null;
        linked.disposition.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return true;
    }

    private static string CreateQuarantineObjectKey(string sourceObjectKey)
        => $"quarantine/orphans/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceObjectKey)))}/{Guid.NewGuid():N}.object";

    internal static string CreateObjectKeyIdentity(string sourceObjectKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceObjectKey)));

    private static Task IncrementFindingAsync(
        ApplicationDbContext db,
        Guid token,
        long byteLength,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => UpdateCheckpointAsync(db, token, setters => setters
            .SetProperty(item => item.FindingCount, item => item.FindingCount + 1)
            .SetProperty(item => item.FindingBytes, item => item.FindingBytes + byteLength)
            .SetProperty(item => item.LastProgressAtUtc, now), cancellationToken);

    private async Task ProcessRecoveryDispositionsAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        Guid token,
        RecoveryStatistics statistics,
        CancellationToken cancellationToken)
    {
        var ids = await db.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.OperationToken == null
                && (item.State == CentralObjectRecoveryStates.PendingCopy
                    || item.State == CentralObjectRecoveryStates.PendingDelete))
            .OrderBy(item => item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .Take(MaximumRecoveryDispositionsPerCycle)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
            var sourceObjectKey = await db.CentralObjectRecoveryDispositions.AsNoTracking()
                .Where(item => item.Id == id)
                .Select(item => item.SourceObjectKey)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                db, BucketPrefix + sourceObjectKey, cancellationToken).ConfigureAwait(false);
            try
            {
                await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                var disposition = await db.CentralObjectRecoveryDispositions.SingleAsync(item => item.Id == id, cancellationToken)
                    .ConfigureAwait(false);
                if (disposition.State is not (CentralObjectRecoveryStates.PendingCopy or CentralObjectRecoveryStates.PendingDelete))
                {
                    continue;
                }
                if (disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                    && disposition.State == CentralObjectRecoveryStates.PendingDelete)
                {
                    var readyForLegacyDelete = await AdoptOrFenceLegacyExpiredDeleteAsync(
                        db, disposition.Id, cancellationToken).ConfigureAwait(false);
                    if (!readyForLegacyDelete)
                    {
                        continue;
                    }
                    db.ChangeTracker.Clear();
                    disposition = await db.CentralObjectRecoveryDispositions.SingleAsync(
                        item => item.Id == id, cancellationToken).ConfigureAwait(false);
                }
                if (disposition.State == CentralObjectRecoveryStates.PendingCopy)
                {
                    if (!await FenceDispositionOwnershipAsync(db, disposition, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                    var source = await TryGetObjectFingerprintAsync(minio, disposition.SourceObjectKey, cancellationToken)
                        .ConfigureAwait(false);
                    var target = await TryGetObjectFingerprintAsync(minio, disposition.TargetObjectKey!, cancellationToken)
                        .ConfigureAwait(false);
                    if (source is null)
                    {
                        if (target is null || disposition.ContentChecksumSha256 is null
                            || target.ByteLength != disposition.ByteLength
                            || !string.Equals(target.ChecksumSha256, disposition.ContentChecksumSha256, StringComparison.Ordinal))
                        {
                            disposition.State = CentralObjectRecoveryStates.Failed;
                            disposition.ReasonCode = "orphan.source-and-quarantine-missing";
                            disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                            await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        disposition.State = CentralObjectRecoveryStates.PendingDelete;
                        disposition.ReasonCode = null;
                        disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                        await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    disposition.ByteLength = source.ByteLength;
                    disposition.ContentChecksumSha256 = source.ChecksumSha256;
                    disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                    await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (target is null || target.ByteLength != source.ByteLength
                        || !string.Equals(target.ChecksumSha256, source.ChecksumSha256, StringComparison.Ordinal))
                    {
                        if (!await FenceDispositionOwnershipAsync(db, disposition, cancellationToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                        var copySource = new CopySourceObjectArgs().WithBucket(Bucket).WithObject(disposition.SourceObjectKey);
                        await minio.CopyObjectAsync(new CopyObjectArgs().WithBucket(Bucket)
                            .WithObject(disposition.TargetObjectKey!).WithCopyObjectSource(copySource), cancellationToken)
                            .ConfigureAwait(false);
                        target = await TryGetObjectFingerprintAsync(minio, disposition.TargetObjectKey!, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                    if (target is null || target.ByteLength != source.ByteLength
                        || !string.Equals(target.ChecksumSha256, source.ChecksumSha256, StringComparison.Ordinal))
                    {
                        disposition.State = CentralObjectRecoveryStates.Failed;
                        disposition.ReasonCode = "orphan.quarantine-content-mismatch";
                    }
                    else
                    {
                        disposition.State = CentralObjectRecoveryStates.PendingDelete;
                        disposition.ReasonCode = null;
                    }
                    disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (disposition.Kind == CentralObjectRecoveryKinds.OrphanQuarantine)
                {
                    var target = await TryGetObjectFingerprintAsync(minio, disposition.TargetObjectKey!, cancellationToken)
                        .ConfigureAwait(false);
                    if (target is null || disposition.ContentChecksumSha256 is null
                        || target.ByteLength != disposition.ByteLength
                        || !string.Equals(target.ChecksumSha256, disposition.ContentChecksumSha256, StringComparison.Ordinal))
                    {
                        disposition.State = CentralObjectRecoveryStates.Failed;
                        disposition.ReasonCode = "orphan.quarantine-not-durable";
                        disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                        await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var source = await TryGetObjectFingerprintAsync(minio, disposition.SourceObjectKey, cancellationToken)
                        .ConfigureAwait(false);
                    if (source is not null
                        && (source.ByteLength != disposition.ByteLength
                            || !string.Equals(source.ChecksumSha256, disposition.ContentChecksumSha256, StringComparison.Ordinal)))
                    {
                        disposition.TargetObjectKey = CreateQuarantineObjectKey(disposition.SourceObjectKey);
                        disposition.State = CentralObjectRecoveryStates.PendingCopy;
                        disposition.ByteLength = source.ByteLength;
                        disposition.ContentChecksumSha256 = null;
                        disposition.ReasonCode = null;
                        disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                        await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                if (!await FenceDispositionOwnershipAsync(db, disposition, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                try
                {
                    await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket)
                        .WithObject(disposition.SourceObjectKey), cancellationToken).ConfigureAwait(false);
                }
                catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
                {
                }
                await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                disposition.State = CentralObjectRecoveryStates.Completed;
                disposition.ReasonCode = null;
                disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (disposition.Kind == CentralObjectRecoveryKinds.OrphanQuarantine)
                {
                    statistics.Quarantined++;
                    statistics.QuarantinedBytes += disposition.ByteLength;
                    telemetry.RecordRecoveryInventory("quarantined", 1, disposition.ByteLength);
                }
                else
                {
                    statistics.Deleted++;
                    statistics.DeletedBytes += disposition.ByteLength;
                    telemetry.RecordRecoveryInventory("deleted", 1, disposition.ByteLength);
                }
            }
            finally
            {
                await objectLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> AdoptOrFenceLegacyExpiredDeleteAsync(
        ApplicationDbContext db,
        Guid dispositionId,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralArtifactRetentionLock.AcquireDispositionAsync(db, dispositionId, cancellationToken)
            .ConfigureAwait(false);
        var disposition = await db.CentralObjectRecoveryDispositions.SingleOrDefaultAsync(item =>
            item.Id == dispositionId
            && item.OperationToken == null
            && item.Kind == CentralObjectRecoveryKinds.ExpiredDelete
            && item.State == CentralObjectRecoveryStates.PendingDelete, cancellationToken).ConfigureAwait(false);
        if (disposition is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var storageReference = BucketPrefix + disposition.SourceObjectKey;
        var artifacts = storageReference.Length > 512
            ? []
            : await db.CentralArtifacts.FromSqlInterpolated($"""
                    SELECT * FROM [CentralArtifacts] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [StorageReference] COLLATE Latin1_General_100_BIN2 = {storageReference}
                    """)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var storageReferenceSha256 = SHA256.HashData(Encoding.Unicode.GetBytes(storageReference));
        var transientOwnerStates = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .Where(intent => EF.Property<byte[]>(intent, "StorageReferenceSha256") == storageReferenceSha256
                && EF.Functions.Collate(intent.StorageReference, BinaryCollation) == storageReference)
            .Select(intent => intent.ObjectState)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (artifacts.Count == 0 && transientOwnerStates.Length == 0)
        {
            disposition.Kind = CentralObjectRecoveryKinds.OrphanQuarantine;
            disposition.TargetObjectKey = CreateQuarantineObjectKey(disposition.SourceObjectKey);
            disposition.State = CentralObjectRecoveryStates.PendingCopy;
            disposition.ContentChecksumSha256 = null;
            disposition.ReasonCode = "ownership.expired-record-removed";
            disposition.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var artifact = artifacts.Count == 1 ? artifacts[0] : null;
        var references = new CentralArtifactRetentionReferences(db);
        var held = false;
        foreach (var owner in artifacts)
        {
            held |= await references.IsHeldAsync(owner.Id, cancellationToken).ConfigureAwait(false);
        }
        var activeOwner = artifacts.Any(owner => owner.ObjectState != CentralArtifactObjectState.Expired)
            || transientOwnerStates.Any(state => state != CentralArtifactObjectState.Expired);
        var tokenizedOwner = artifacts.Any(owner => owner.RetentionDeletionToken is not null);
        if (activeOwner || tokenizedOwner || held)
        {
            disposition.State = CentralObjectRecoveryStates.Cancelled;
            disposition.ReasonCode = held ? "ownership.held" : "ownership.ambiguous";
            disposition.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (artifact is null)
        {
            disposition.ReasonCode = null;
            disposition.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return true;
        }

        var operationToken = Guid.NewGuid();
        artifact.RetentionDeletionToken = operationToken;
        artifact.RetentionDeletionRequestedAtUtc = now;
        artifact.RetentionDeletionCompletedAtUtc = null;
        disposition.CentralArtifactId = artifact.Id;
        disposition.OperationToken = operationToken;
        disposition.AttemptCount = 0;
        disposition.LastAttemptAtUtc = null;
        disposition.NextAttemptAtUtc = now;
        disposition.CompletedAtUtc = null;
        disposition.ReasonCode = null;
        disposition.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return false;
    }

    private async Task<bool> FenceDispositionOwnershipAsync(
        ApplicationDbContext db,
        CentralObjectRecoveryDisposition disposition,
        CancellationToken cancellationToken)
    {
        var owners = await GetObjectOwnerStatesAsync(db, disposition.SourceObjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete && owners.Count == 0)
        {
            disposition.Kind = CentralObjectRecoveryKinds.OrphanQuarantine;
            disposition.TargetObjectKey = CreateQuarantineObjectKey(disposition.SourceObjectKey);
            disposition.State = CentralObjectRecoveryStates.PendingCopy;
            disposition.ContentChecksumSha256 = null;
            disposition.ReasonCode = "ownership.expired-record-removed";
            disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (owners.Any(state => state != CentralArtifactObjectState.Expired))
        {
            disposition.State = CentralObjectRecoveryStates.Cancelled;
            disposition.ReasonCode = "ownership.active";
            disposition.UpdatedAtUtc = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private static async Task<IReadOnlyList<CentralArtifactObjectState>> GetObjectOwnerStatesAsync(
        ApplicationDbContext db,
        string objectKey,
        CancellationToken cancellationToken)
    {
        var storageReference = BucketPrefix + objectKey;
        if (storageReference.Length > 512)
        {
            return [];
        }
        var artifactStates = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => EF.Functions.Collate(artifact.StorageReference, BinaryCollation) == storageReference)
            .Select(artifact => artifact.ObjectState)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var storageReferenceSha256 = SHA256.HashData(Encoding.Unicode.GetBytes(storageReference));
        var derivativeStates = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .Where(intent => EF.Property<byte[]>(intent, "StorageReferenceSha256") == storageReferenceSha256 &&
                EF.Functions.Collate(intent.StorageReference, BinaryCollation) == storageReference)
            .Select(intent => intent.ObjectState)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return artifactStates.Concat(derivativeStates).ToArray();
    }

    private async Task CleanupStagingObjectsAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        Guid token,
        CancellationToken cancellationToken)
    {
        var checkpoint = await db.CentralRecoveryCheckpoints.AsNoTracking()
            .SingleAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        var nextPartition = (checkpoint.StagingPartition + 1) % StagingPartitionCount;
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), cancellationToken)
                .ConfigureAwait(false))
        {
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(item => item.StagingPartition, nextPartition)
                .SetProperty(item => item.StagingCursor, (string?)null), cancellationToken).ConfigureAwait(false);
            return;
        }

        var cutoffUtc = timeProvider.GetUtcNow() - StagingObjectGracePeriod;
        var scanned = 0L;
        var retained = 0L;
        var deleted = 0L;
        var failed = 0L;
        var deletedBytes = 0L;
        var hasMore = false;
        var prefix = GetStagingPartitionPrefix(checkpoint.StagingPartition);
        var args = new ListObjectsArgs().WithBucket(Bucket).WithPrefix(prefix).WithRecursive(true);
        await foreach (var item in minio.ListObjectsEnumAsync(args, cancellationToken).ConfigureAwait(false))
        {
            if (checkpoint.StagingCursor is not null
                && string.CompareOrdinal(item.Key, checkpoint.StagingCursor) <= 0)
            {
                continue;
            }
            if (scanned >= MaximumStagingObjectsPerCycle)
            {
                hasMore = true;
                break;
            }
            if (scanned % MaximumRecoveryDispositionsPerCycle == 0)
            {
                await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
            }
            scanned++;
            var stagingCursor = item.Key;
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(candidate => candidate.StagingCursor, stagingCursor), cancellationToken).ConfigureAwait(false);
            var lastModifiedUtc = item.LastModifiedDateTime;
            if (!lastModifiedUtc.HasValue || new DateTimeOffset(lastModifiedUtc.Value.ToUniversalTime()) > cutoffUtc)
            {
                retained++;
                continue;
            }
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket)
                    .WithObject(item.Key), cancellationToken).ConfigureAwait(false);
                deleted++;
                deletedBytes += checked((long)item.Size);
            }
            catch (Exception exception) when (exception is MinioException or HttpRequestException or IOException)
            {
                failed++;
                LogStagingDeleteFailed(GetFailureCategory(exception));
            }
        }
        if (!hasMore)
        {
            await UpdateCheckpointAsync(db, token, setters => setters
                .SetProperty(item => item.StagingPartition, nextPartition)
                .SetProperty(item => item.StagingCursor, (string?)null), cancellationToken).ConfigureAwait(false);
        }
        telemetry.RecordStagingCleanup("scanned", scanned);
        telemetry.RecordStagingCleanup("retained", retained);
        telemetry.RecordStagingCleanup("deleted", deleted, deletedBytes);
        telemetry.RecordStagingCleanup("failed", failed);
    }

    private static string GetStagingPartitionPrefix(int partition)
    {
        if (partition < 13)
        {
            return $"staging/{partition:x}";
        }
        if (partition < 29)
        {
            return $"staging/d{partition - 13:x}";
        }
        if (partition < 31)
        {
            return partition == 29 ? "staging/e" : "staging/f";
        }
        return $"staging/derivatives/{partition - 31:x}";
    }

    private async Task<RecoveryArtifactResult> ReconcileOneWithConcurrencyRetryAsync(
        Guid artifactId,
        long? recoveryGeneration,
        Guid token,
        CancellationToken cancellationToken)
    {
        var retryFence = new VerificationRetryFence();
        try
        {
            return await ReconcileOneAsync(
                artifactId, recoveryGeneration, token, retryFence, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException firstException) when (IsSchedulingConcurrency(firstException))
        {
            telemetry.RecordReconciliationConcurrency("retry");
            LogConcurrencyRetry();
            try
            {
                var result = await ReconcileOneAsync(
                    artifactId, recoveryGeneration, token, retryFence, cancellationToken).ConfigureAwait(false);
                telemetry.RecordReconciliationConcurrency("converged");
                LogConcurrencyConverged();
                return result;
            }
            catch (DbUpdateConcurrencyException exception) when (IsSchedulingConcurrency(exception))
            {
                telemetry.RecordReconciliationConcurrency("exhausted");
                throw;
            }
        }
    }

    private async Task<RecoveryArtifactResult> ReconcileOneAsync(
        Guid artifactId,
        long? recoveryGeneration,
        Guid token,
        VerificationRetryFence retryFence,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReconcileOneCoreAsync(
                artifactId, recoveryGeneration, token, retryFence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            exception.Data[VerificationRetryFenceMarker] = retryFence;
            throw;
        }
    }

    private async Task<RecoveryArtifactResult> ReconcileOneCoreAsync(
        Guid artifactId,
        long? recoveryGeneration,
        Guid token,
        VerificationRetryFence retryFence,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        var objectReader = scope.ServiceProvider.GetRequiredService<ICentralArtifactObjectReader>();
        var scheduler = scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobScheduler>();
        var artifact = await db.CentralArtifacts
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .Include(item => item.Sources)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return RecoveryArtifactResult.None;
        }
        retryFence.ObjectVerificationToken = artifact.ObjectVerificationToken;
        var reconciledAtUtc = timeProvider.GetUtcNow();
        var canonical = artifact.StorageReference.StartsWith(BucketPrefix + "artifacts/", StringComparison.Ordinal)
            || artifact.StorageReference.StartsWith(BucketPrefix + "derivatives/", StringComparison.Ordinal);
        if (!canonical && artifact.ObjectVerificationToken is null)
        {
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
            if (recoveryGeneration.HasValue)
            {
                artifact.RecoveryGeneration = recoveryGeneration.Value;
            }
            ScheduleReferenceRetry(artifact, reconciledAtUtc);
            if (artifact.ObjectState == CentralArtifactObjectState.Pending)
            {
                artifact.StateReasonCode = "object.reference-unsupported";
                artifact.ReconciledAtUtc = reconciledAtUtc;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryArtifactResult("unsupported", artifact.ByteLength);
        }

        await using var objectLock = await AcquireCurrentObjectLockAsync(
            db, artifact, cancellationToken).ConfigureAwait(false);
        canonical = artifact.StorageReference.StartsWith(BucketPrefix + "artifacts/", StringComparison.Ordinal)
            || artifact.StorageReference.StartsWith(BucketPrefix + "derivatives/", StringComparison.Ordinal);
        if (!canonical && artifact.ObjectVerificationToken is null)
        {
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
            if (recoveryGeneration.HasValue)
            {
                artifact.RecoveryGeneration = recoveryGeneration.Value;
            }
            ScheduleReferenceRetry(artifact, reconciledAtUtc);
            if (artifact.ObjectState == CentralArtifactObjectState.Pending)
            {
                artifact.StateReasonCode = "object.reference-unsupported";
                artifact.ReconciledAtUtc = reconciledAtUtc;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryArtifactResult("unsupported", artifact.ByteLength);
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Expired)
        {
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return RecoveryArtifactResult.None;
        }
        if (await CentralObjectOwnershipFence.IsRetiredAsync(
                db, artifact.StorageReference, cancellationToken).ConfigureAwait(false))
        {
            artifact.ObjectState = CentralArtifactObjectState.Expired;
            artifact.StateReasonCode = "retention.key-retired";
            artifact.ReconciledAtUtc = reconciledAtUtc;
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
            await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken)
                .ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryArtifactResult("retired", artifact.ByteLength);
        }

        var verifiedThisAttempt = false;
        RecoveryArtifactResult result = RecoveryArtifactResult.None;
        var verificationDue = !artifact.ObjectVerifiedAtUtc.HasValue
            || artifact.ObjectVerifiedAtUtc <= reconciledAtUtc - VerificationInterval;
        if (artifact.ObjectVerificationToken.HasValue
            || recoveryGeneration.HasValue
            || artifact.ObjectState == CentralArtifactObjectState.Pending && verificationDue
            || artifact.ReconstructionState == CentralReconstructionState.PendingReference && verificationDue)
        {
            await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
            await EnsureVerificationReservationAsync(
                db, objectLock, artifact, token, reconciledAtUtc, cancellationToken).ConfigureAwait(false);
            retryFence.ObjectVerificationToken = artifact.ObjectVerificationToken;
            result = await VerifyAndApplyAsync(
                db, minio, objectReader, objectLock, artifact, reconciledAtUtc, recoveryGeneration, token, cancellationToken)
                .ConfigureAwait(false);
            retryFence.CommittedVerifiedAtUtc = result.Outcome == "matched" ? artifact.ObjectVerifiedAtUtc : null;
            await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
            verifiedThisAttempt = true;
            if (result.Outcome is "missing" or "corrupt")
            {
                return result;
            }
        }

        if (artifact.ReconstructionState == CentralReconstructionState.PendingReference)
        {
            await ResolveReferencesAsync(db, artifact, cancellationToken).ConfigureAwait(false);
            artifact.ReconciledAtUtc = reconciledAtUtc;
            if (artifact.ReconstructionState == CentralReconstructionState.PendingReference)
            {
                ScheduleReferenceRetry(artifact, reconciledAtUtc);
            }
            else
            {
                artifact.ReferenceRetryCount = 0;
                artifact.ReferenceRetryAtUtc = null;
            }
        }
        else if (artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Quarantined
            && artifact.StateReasonCode != CentralDerivativeJobScheduler.LocationUnresolvedReason
            && artifact.StateReasonCode != CentralDerivativeJobScheduler.LocationMismatchReason)
        {
            await ResolveReferencesAsync(db, artifact, cancellationToken).ConfigureAwait(false);
        }
        if (artifact.ObjectState != CentralArtifactObjectState.Available
            || artifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            if (artifact.ObjectState != CentralArtifactObjectState.Available)
            {
                await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken).ConfigureAwait(false);
            }
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete)
        {
            if (!verifiedThisAttempt && verificationDue)
            {
                await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
                await EnsureVerificationReservationAsync(
                    db, objectLock, artifact, token, reconciledAtUtc, cancellationToken).ConfigureAwait(false);
                retryFence.ObjectVerificationToken = artifact.ObjectVerificationToken;
                result = await VerifyAndApplyAsync(
                    db, minio, objectReader, objectLock, artifact, reconciledAtUtc, recoveryGeneration, token, cancellationToken)
                    .ConfigureAwait(false);
                retryFence.CommittedVerifiedAtUtc = result.Outcome == "matched" ? artifact.ObjectVerifiedAtUtc : null;
                await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                if (result.Outcome is "missing" or "corrupt")
                {
                    return result;
                }
            }
            artifact.ReconciledAtUtc = reconciledAtUtc;
            artifact.StateReasonCode = null;
            if (artifact.ManifestSchemaVersion == HVO.SkyMonitor.AgentCore.ArtifactUploadManifest.CurrentSchemaVersion
                || artifact.Role == HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw)
            {
                await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                try
                {
                    await scheduler.EnsureRequiredJobsAsync(artifact, reconciledAtUtc, cancellationToken).ConfigureAwait(false);
                    // The scheduler commits idempotent jobs independently; fence the recovery lease before
                    // the reconciler performs any final save after that durable scheduling boundary.
                    await RenewLeaseAsync(db, token, cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException exception) when (IsSchedulingArtifactConflict(exception, artifact.Id))
                {
                    exception.Data[SchedulingConcurrencyMarker] = true;
                    throw;
                }
            }
            telemetry.RecordReconciled("completed");
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Available
            && await CentralObjectOwnershipFence.IsRetiredAsync(
                db, artifact.StorageReference, cancellationToken).ConfigureAwait(false))
        {
            artifact.ObjectState = CentralArtifactObjectState.Expired;
            artifact.StateReasonCode = "retention.key-retired";
            await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken)
                .ConfigureAwait(false);
        }
        artifact.ObjectVerificationToken = null;
        artifact.ObjectVerificationRequestedAtUtc = null;
        artifact.ObjectVerificationRetryCount = 0;
        artifact.ObjectVerificationRetryAtUtc = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result.Outcome == "none" ? new RecoveryArtifactResult("matched", artifact.ByteLength) : result;
    }

    internal static async Task<CentralObjectApplicationLock> AcquireCurrentObjectLockAsync(
        ApplicationDbContext db,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var expectedStorageReference = artifact.StorageReference;
            var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                db, expectedStorageReference, cancellationToken).ConfigureAwait(false);
            try
            {
                await db.Entry(artifact).ReloadAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(
                        artifact.StorageReference,
                        expectedStorageReference,
                        StringComparison.Ordinal))
                {
                    return objectLock;
                }
            }
            catch
            {
                await objectLock.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            await objectLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<RecoveryArtifactResult> VerifyAndApplyAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        ICentralArtifactObjectReader objectReader,
        CentralObjectApplicationLock objectLock,
        CentralArtifact artifact,
        DateTimeOffset verifiedAtUtc,
        long? recoveryGeneration,
        Guid recoveryLeaseToken,
        CancellationToken cancellationToken)
    {
        string? verification;
        string? storageETag = null;
        try
        {
            var snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            storageETag = snapshot.StorageETag;
            verification = null;
        }
        catch (CentralArtifactMissingException)
        {
            verification = "missing";
        }
        catch (CentralArtifactIntegrityException exception)
        {
            storageETag = exception.StorageETag;
            verification = exception.ReasonCode;
        }
        if (storageETag is { Length: > 0 }
            && !await objectReader.IsCurrentGenerationAsync(
                artifact, storageETag, cancellationToken).ConfigureAwait(false))
        {
            throw new CentralArtifactStorageException(
                "The artifact object generation changed during recovery verification.");
        }
        if (verification == "missing"
            && !await IsObjectMissingAsync(minio, artifact, cancellationToken).ConfigureAwait(false))
        {
            throw new CentralArtifactStorageException(
                "The missing artifact object appeared during recovery verification.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        await RenewLeaseAsync(db, recoveryLeaseToken, cancellationToken).ConfigureAwait(false);
        artifact.ObjectVerifiedAtUtc = verifiedAtUtc;
        if (recoveryGeneration.HasValue)
        {
            artifact.RecoveryGeneration = recoveryGeneration.Value;
        }
        telemetry.RecordChecksum("recovery", verification switch
        {
            null => "matched",
            "missing" => "missing",
            "object.length-mismatch" => "length-mismatch",
            _ => "checksum-mismatch"
        });
        if (verification == "missing")
        {
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
            var transitioned = artifact.ObjectState != CentralArtifactObjectState.Pending
                || artifact.StateReasonCode != "object.missing";
            artifact.ObjectState = CentralArtifactObjectState.Pending;
            artifact.StateReasonCode = "object.missing";
            artifact.ReconciledAtUtc = verifiedAtUtc;
            if (transitioned)
            {
                await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken).ConfigureAwait(false);
                telemetry.RecordReconciled("missing");
            }
            telemetry.RecordRecoveryInventory("missing", 1, artifact.ByteLength);
            await CommitVerificationAsync(db, objectLock, transaction, cancellationToken).ConfigureAwait(false);
            return new RecoveryArtifactResult("missing", artifact.ByteLength, transitioned);
        }
        if (verification is not null)
        {
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
            var transitioned = artifact.ObjectState != CentralArtifactObjectState.Quarantined
                || artifact.ReconstructionState != CentralReconstructionState.Quarantined;
            artifact.ObjectState = CentralArtifactObjectState.Quarantined;
            artifact.ReconstructionState = CentralReconstructionState.Quarantined;
            artifact.StateReasonCode = verification;
            artifact.ReconciledAtUtc = verifiedAtUtc;
            if (transitioned)
            {
                await ArtifactIngestService.InvalidateDependentsAsync(db, artifact, cancellationToken).ConfigureAwait(false);
                telemetry.RecordQuarantine(verification);
                telemetry.RecordReconciled("quarantined");
            }
            telemetry.RecordRecoveryInventory("corrupt", 1, artifact.ByteLength);
            await CommitVerificationAsync(db, objectLock, transaction, cancellationToken).ConfigureAwait(false);
            return new RecoveryArtifactResult("corrupt", artifact.ByteLength, transitioned);
        }
        if (artifact.ObjectState == CentralArtifactObjectState.Pending)
        {
            artifact.ObjectState = CentralArtifactObjectState.Available;
        }
        artifact.ObjectVerificationToken = null;
        artifact.ObjectVerificationRequestedAtUtc = null;
        artifact.ObjectVerificationRetryCount = 0;
        artifact.ObjectVerificationRetryAtUtc = null;
        if (artifact.StateReasonCode?.StartsWith("object.", StringComparison.Ordinal) == true)
        {
            artifact.StateReasonCode = null;
        }
        telemetry.RecordRecoveryInventory("matched", 1, artifact.ByteLength);
        await CommitVerificationAsync(db, objectLock, transaction, cancellationToken).ConfigureAwait(false);
        return new RecoveryArtifactResult("matched", artifact.ByteLength);
    }

    private async Task EnsureVerificationReservationAsync(
        ApplicationDbContext db,
        CentralObjectApplicationLock objectLock,
        CentralArtifact artifact,
        Guid recoveryLeaseToken,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken)
    {
        if (artifact.ObjectVerificationToken.HasValue)
        {
            return;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        await RenewLeaseAsync(db, recoveryLeaseToken, cancellationToken).ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Pending;
        artifact.ObjectVerificationToken = Guid.NewGuid();
        artifact.ObjectVerificationRequestedAtUtc = requestedAtUtc;
        artifact.ObjectVerificationRetryCount = 0;
        artifact.ObjectVerificationRetryAtUtc = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CommitVerificationAsync(
        ApplicationDbContext db,
        CentralObjectApplicationLock objectLock,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordBacklogAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var pendingObjects = await GetBacklogAsync(db, artifact => artifact.ObjectState == CentralArtifactObjectState.Pending
                && artifact.ObjectVerificationToken == null,
            cancellationToken).ConfigureAwait(false);
        var pendingReferences = await GetBacklogAsync(db,
            artifact => artifact.ReconstructionState == CentralReconstructionState.PendingReference
                && artifact.ObjectVerificationToken == null,
            cancellationToken).ConfigureAwait(false);
        var quarantined = await GetBacklogAsync(db,
            artifact => artifact.ObjectState == CentralArtifactObjectState.Quarantined
                || artifact.ReconstructionState == CentralReconstructionState.Quarantined,
            cancellationToken).ConfigureAwait(false);
        var pendingVerifications = await db.CentralArtifacts
            .Where(artifact => artifact.ObjectVerificationToken != null)
            .GroupBy(static _ => 1)
            .Select(group => new BacklogSnapshot(
                group.LongCount(),
                group.Sum(artifact => artifact.ByteLength),
                group.Min(artifact => artifact.ObjectVerificationRequestedAtUtc) ?? DateTimeOffset.UnixEpoch))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordBacklog(
            pendingObjects?.Count ?? 0,
            pendingObjects?.Bytes ?? 0,
            GetAgeSeconds(pendingObjects?.OldestAtUtc),
            pendingReferences?.Count ?? 0,
            pendingReferences?.Bytes ?? 0,
            GetAgeSeconds(pendingReferences?.OldestAtUtc),
            quarantined?.Count ?? 0,
            quarantined?.Bytes ?? 0,
            GetAgeSeconds(quarantined?.OldestAtUtc),
            pendingVerifications?.Count ?? 0,
            pendingVerifications?.Bytes ?? 0,
            GetAgeSeconds(pendingVerifications?.OldestAtUtc));
    }

    private static async Task<bool> IsObjectMissingAsync(
        IMinioClient minio,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(Bucket)
                .WithObject(artifact.StorageReference[BucketPrefix.Length..]), cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return true;
        }
    }

    private static Task<BacklogSnapshot?> GetBacklogAsync(
        ApplicationDbContext db,
        System.Linq.Expressions.Expression<Func<CentralArtifact, bool>> predicate,
        CancellationToken cancellationToken)
        => db.CentralArtifacts.Where(predicate)
            .GroupBy(static _ => 1)
            .Select(group => new BacklogSnapshot(
                group.LongCount(), group.Sum(artifact => artifact.ByteLength), group.Min(artifact => artifact.ReceivedAtUtc)))
            .SingleOrDefaultAsync(cancellationToken);

    private long GetAgeSeconds(DateTimeOffset? oldestAtUtc)
        => oldestAtUtc.HasValue
            ? Math.Max(0, (long)(timeProvider.GetUtcNow() - oldestAtUtc.Value).TotalSeconds)
            : 0;

    internal static TimeSpan CalculateReferenceRetryDelay(int attempt)
    {
        if (attempt <= 1)
        {
            return InitialReferenceRetryDelay;
        }
        var cappedExponent = Math.Min(attempt - 1, 6);
        var delayTicks = InitialReferenceRetryDelay.Ticks * (1L << cappedExponent);
        return TimeSpan.FromTicks(Math.Min(MaximumReferenceRetryDelay.Ticks, delayTicks));
    }

    private static void ScheduleReferenceRetry(CentralArtifact artifact, DateTimeOffset now)
    {
        if (artifact.ReferenceRetryCount < int.MaxValue)
        {
            artifact.ReferenceRetryCount++;
        }
        artifact.ReferenceRetryAtUtc = now + CalculateReferenceRetryDelay(artifact.ReferenceRetryCount);
    }

    internal static TimeSpan CalculateVerificationRetryDelay(int attempt)
    {
        if (attempt <= 1)
        {
            return InitialVerificationRetryDelay;
        }
        var cappedExponent = Math.Min(attempt - 1, 6);
        var delayTicks = InitialVerificationRetryDelay.Ticks * (1L << cappedExponent);
        return TimeSpan.FromTicks(Math.Min(MaximumVerificationRetryDelay.Ticks, delayTicks));
    }

    private async Task ScheduleVerificationRetryAsync(
        ApplicationDbContext db,
        Guid artifactId,
        VerificationRetryFence? retryFence,
        CancellationToken cancellationToken)
    {
        var retry = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.Id == artifactId)
            .Select(artifact => new
            {
                artifact.ObjectState,
                artifact.ObjectVerificationToken,
                artifact.ObjectVerificationRetryCount
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (retry is null)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        if (retry.ObjectVerificationToken is null)
        {
            if (retry.ObjectState != CentralArtifactObjectState.Available
                || retryFence?.CommittedVerifiedAtUtc is not { } committedVerifiedAtUtc)
            {
                return;
            }
            _ = await db.CentralArtifacts
                .Where(artifact => artifact.Id == artifactId
                    && artifact.ObjectState == CentralArtifactObjectState.Available
                    && artifact.ObjectVerificationToken == null
                    && artifact.ObjectVerifiedAtUtc == committedVerifiedAtUtc)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(artifact => artifact.ObjectState, CentralArtifactObjectState.Pending)
                    .SetProperty(artifact => artifact.ObjectVerificationToken, Guid.NewGuid())
                    .SetProperty(artifact => artifact.ObjectVerificationRequestedAtUtc, now)
                    .SetProperty(artifact => artifact.ObjectVerificationRetryCount, 1)
                    .SetProperty(
                        artifact => artifact.ObjectVerificationRetryAtUtc,
                        now + CalculateVerificationRetryDelay(1)), cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (retryFence?.ObjectVerificationToken != retry.ObjectVerificationToken)
        {
            return;
        }
        var nextRetryCount = retry.ObjectVerificationRetryCount == int.MaxValue
            ? int.MaxValue
            : retry.ObjectVerificationRetryCount + 1;
        var retryAtUtc = timeProvider.GetUtcNow() + CalculateVerificationRetryDelay(nextRetryCount);
        _ = await db.CentralArtifacts
            .Where(artifact => artifact.Id == artifactId
                && artifact.ObjectVerificationToken == retryFence.ObjectVerificationToken
                && artifact.ObjectVerificationRetryCount == retry.ObjectVerificationRetryCount)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(artifact => artifact.ObjectVerificationRetryCount, nextRetryCount)
                .SetProperty(artifact => artifact.ObjectVerificationRetryAtUtc, retryAtUtc), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MarkProgressAsync(ApplicationDbContext db, Guid token, CancellationToken cancellationToken)
        => await UpdateCheckpointAsync(db, token, setters => setters
            .SetProperty(item => item.LastProgressAtUtc, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);

    private async Task RecordFindingAsync(
        ApplicationDbContext db,
        Guid token,
        string outcome,
        long bytes,
        CancellationToken cancellationToken)
    {
        await UpdateCheckpointAsync(db, token, setters => setters
            .SetProperty(item => item.FindingCount, item => item.FindingCount + 1)
            .SetProperty(item => item.FindingBytes, item => item.FindingBytes + bytes)
            .SetProperty(item => item.LastProgressAtUtc, timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        LogFinding(outcome, bytes);
    }

    private static async Task UpdateCheckpointAsync(
        ApplicationDbContext db,
        Guid token,
        Action<UpdateSettersBuilder<CentralRecoveryCheckpoint>> updates,
        CancellationToken cancellationToken)
    {
        var affected = await db.CentralRecoveryCheckpoints
            .Where(item => item.Id == CentralRecoveryCheckpoint.SingletonId && item.LeaseToken == token)
            .ExecuteUpdateAsync(updates, cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException("The central recovery lease was lost.");
        }
    }

    private static async Task<ObjectFingerprint?> TryGetObjectFingerprintAsync(
        IMinioClient minio,
        string objectKey,
        CancellationToken cancellationToken)
    {
        long length = 0;
        byte[]? checksum = null;
        try
        {
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(Bucket)
                .WithObject(objectKey)
                .WithCallbackStream(stream =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = stream.Read(buffer)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        length += read;
                    }
                    checksum = hash.GetHashAndReset();
                }), cancellationToken).ConfigureAwait(false);
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return null;
        }
        return new ObjectFingerprint(length, Convert.ToHexString(checksum!));
    }

    private static bool IsRecoverable(Exception exception)
        => exception is DbException or DbUpdateConcurrencyException or MinioException
            or CentralArtifactStorageException or HttpRequestException or IOException or InvalidOperationException;

    private static bool MayContainObjectLocation(Exception exception)
        => exception is MinioException or CentralArtifactStorageException or HttpRequestException or IOException;

    private static string GetFailureCategory(Exception exception)
        => exception switch
        {
            CentralArtifactStorageException => "object-store",
            MinioException => "object-store",
            HttpRequestException => "network",
            IOException => "io",
            _ => "other"
        };

    private static void SetActivity(
        Activity? activity,
        RecoveryStatistics statistics,
        string outcome,
        ActivityStatusCode status)
    {
        activity?.SetTag("recovery.outcome", outcome);
        activity?.SetTag("recovery.scanned", statistics.Scanned);
        activity?.SetTag("recovery.matched", statistics.Matched);
        activity?.SetTag("recovery.missing", statistics.Missing);
        activity?.SetTag("recovery.corrupt", statistics.Corrupt);
        activity?.SetTag("recovery.orphans", statistics.Orphans);
        activity?.SetTag("recovery.quarantined", statistics.Quarantined);
        activity?.SetTag("recovery.deleted", statistics.Deleted);
        activity?.SetStatus(status);
    }

    private sealed record BacklogSnapshot(long Count, long Bytes, DateTimeOffset OldestAtUtc);

    private sealed class VerificationRetryFence
    {
        public Guid? ObjectVerificationToken { get; set; }

        public DateTimeOffset? CommittedVerifiedAtUtc { get; set; }
    }

    private sealed record ObjectFingerprint(long ByteLength, string ChecksumSha256);

    private sealed record RecoveryArtifactResult(string Outcome, long Bytes, bool Transitioned = false)
    {
        public static RecoveryArtifactResult None { get; } = new("none", 0);
    }

    private sealed class RecoveryStatistics
    {
        public long Scanned { get; set; }
        public long ScannedBytes { get; set; }
        public long Matched { get; set; }
        public long MatchedBytes { get; set; }
        public long Missing { get; set; }
        public long MissingBytes { get; set; }
        public long Corrupt { get; set; }
        public long CorruptBytes { get; set; }
        public long Orphans { get; set; }
        public long OrphanBytes { get; set; }
        public long Quarantined { get; set; }
        public long QuarantinedBytes { get; set; }
        public long Deleted { get; set; }
        public long DeletedBytes { get; set; }

        public void Add(RecoveryArtifactResult result)
        {
            if (result.Outcome == "none" || result.Outcome == "unsupported")
            {
                return;
            }
            Scanned++;
            ScannedBytes += result.Bytes;
            if (result.Outcome == "matched")
            {
                Matched++;
                MatchedBytes += result.Bytes;
            }
            else if (result.Outcome == "missing")
            {
                Missing++;
                MissingBytes += result.Bytes;
            }
            else if (result.Outcome == "corrupt")
            {
                Corrupt++;
                CorruptBytes += result.Bytes;
            }
        }
    }

    private static bool IsSchedulingArtifactConflict(DbUpdateConcurrencyException exception, Guid artifactId)
        => exception.Entries.Count > 0
            && exception.Entries.All(entry => entry.Entity is CentralArtifact artifact && artifact.Id == artifactId);

    private static bool IsSchedulingConcurrency(DbUpdateConcurrencyException exception)
        => exception.Data[SchedulingConcurrencyMarker] is true;

    private static async Task ResolveReferencesAsync(
        ApplicationDbContext db,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        var frame = artifact.Frame!;
        var unavailableSource = false;
        if (!frame.DeviceRigProfileId.HasValue)
        {
            var rig = frame.Profiles.Single(profile => profile.Kind == CentralProfileKind.Rig);
            var profile = await HistoricalRigProfileResolver.ResolveAsync(
                db,
                frame.DevicePublicId,
                new(rig.Name, rig.Version, rig.Sha256),
                frame.Timing!.ExposureStartedUtc,
                cancellationToken).ConfigureAwait(false);
            if (profile is not null)
            {
                rig.DeviceRigProfileId = profile.Id;
                rig.DeviceRigProfile = profile;
                frame.DeviceRigProfileId = profile.Id;
                frame.DeviceRigProfile = profile;
                frame.RigProfileVersion = profile.Version;
            }
        }

        foreach (var source in artifact.Sources.Where(source => source.ResolvedCentralArtifactId != null))
        {
            var usable = await db.CentralArtifacts.AnyAsync(candidate =>
                candidate.Id == source.ResolvedCentralArtifactId
                && candidate.ObjectState == CentralArtifactObjectState.Available
                && candidate.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false);
            if (!usable)
            {
                source.ResolvedCentralArtifactId = null;
                source.ResolvedArtifact = null;
                unavailableSource = true;
            }
        }
        foreach (var source in artifact.Sources.Where(source => source.ResolvedCentralArtifactId == null))
        {
            var resolved = await db.CentralArtifacts.FirstOrDefaultAsync(candidate =>
                candidate.ArtifactId == source.SourceArtifactId
                && candidate.DevicePublicId == frame.DevicePublicId
                && candidate.ObjectState == CentralArtifactObjectState.Available
                && candidate.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                source.ResolvedCentralArtifactId = resolved.Id;
                source.ResolvedArtifact = resolved;
            }
        }
        if (!frame.DeviceRigProfileId.HasValue)
        {
            artifact.ReconstructionState = CentralReconstructionState.PendingReference;
            artifact.StateReasonCode = "profile.rig-not-found";
            return;
        }
        if (artifact.Sources.Any(source => source.ResolvedCentralArtifactId == null))
        {
            artifact.ReconstructionState = CentralReconstructionState.PendingReference;
            artifact.StateReasonCode = unavailableSource
                || artifact.StateReasonCode == "lineage.source-unavailable"
                    ? "lineage.source-unavailable"
                    : "lineage.source-not-found";
            return;
        }
        artifact.ReconstructionState = CentralReconstructionState.Complete;
        artifact.StateReasonCode = null;
        artifact.ReferenceRetryCount = 0;
        artifact.ReferenceRetryAtUtc = null;
    }

    [LoggerMessage(2120, LogLevel.Information,
        "Central recovery cycle completed: Scanned={Scanned} Matched={Matched} Missing={Missing} Corrupt={Corrupt} Orphans={Orphans} Quarantined={Quarantined} Deleted={Deleted} DurationMs={DurationMs}")]
    private partial void LogCycleCompleted(long scanned, long matched, long missing, long corrupt, long orphans,
        long quarantined, long deleted, double durationMs);

    [LoggerMessage(2121, LogLevel.Warning, "Central recovery finding recorded: Outcome={Outcome} Bytes={Bytes}")]
    private partial void LogFinding(string outcome, long bytes);

    [LoggerMessage(2122, LogLevel.Error, "Central artifact recovery cycle failed")]
    private partial void LogCycleFailed(Exception exception);

    [LoggerMessage(2123, LogLevel.Warning, "Central artifact recovery could not persist cycle failure state")]
    private partial void LogFailureCheckpointFailed(Exception exception);

    [LoggerMessage(2124, LogLevel.Error, "Central artifact reconciliation failed for one durable record")]
    private partial void LogRecordFailed(Exception exception);

    [LoggerMessage(2125, LogLevel.Warning,
        "Failed to remove one stale MinIO staging object: FailureCategory={FailureCategory}")]
    private partial void LogStagingDeleteFailed(string failureCategory);

    [LoggerMessage(2126, LogLevel.Information,
        "Central artifact reconciliation observed a concurrent durable update and will retry once")]
    private partial void LogConcurrencyRetry();

    [LoggerMessage(2127, LogLevel.Information,
        "Central artifact reconciliation converged after a concurrent durable update")]
    private partial void LogConcurrencyConverged();

    [LoggerMessage(2128, LogLevel.Error,
        "Central artifact recovery object-store cycle failed: FailureCategory={FailureCategory}")]
    private partial void LogObjectStoreCycleFailed(string failureCategory);

    [LoggerMessage(2129, LogLevel.Error,
        "Central artifact recovery failed for one object-store record: FailureCategory={FailureCategory}")]
    private partial void LogObjectStoreRecordFailed(string failureCategory);
}

internal static class MinioObjectVerification
{
    public static bool IsNotFound(MinioException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ObjectNotFoundException)
        {
            return true;
        }
        var errorCode = exception.Response?.Code;
        return errorCode is "NoSuchKey" or "NoSuchObject"
            && exception.ServerResponse?.StatusCode == HttpStatusCode.NotFound;
    }
}
