using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class DeploymentLocationReconciliationOptions
{
    public const string SectionName = "DeploymentLocationReconciliation";

    public bool Enabled { get; set; } = true;

    [Required, StringLength(256, MinimumLength = 1)]
    public string WorkerId { get; set; } = "logic-host-deployment-location-reconciliation";

    [Range(1, 1000)]
    public int CaptureBatchSize { get; set; } = 250;

    [Range(1, 100)]
    public int SchedulingBatchSize { get; set; } = 25;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan BacklogDegradedAfter { get; set; } = TimeSpan.FromMinutes(10);
}

internal interface IDeploymentLocationReconciliationProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class DeploymentLocationReconciliationService(
    ApplicationDbContext dbContext,
    ICentralDerivativeJobScheduler derivativeJobScheduler,
    DeploymentLocationTelemetry telemetry,
    IOptions<DeploymentLocationReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<DeploymentLocationReconciliationService> logger) : IDeploymentLocationReconciliationProcessor
{
    private const string OrdinalCollation = "Latin1_General_100_BIN2";
    private readonly DeploymentLocationReconciliationOptions settings = options.Value;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every non-shutdown processing failure must become durable retry state.")]
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid();
        var workId = await TryClaimAsync(leaseToken, cancellationToken).ConfigureAwait(false);
        if (workId is null)
        {
            return false;
        }

        var attemptStarted = timeProvider.GetTimestamp();
        try
        {
            dbContext.ChangeTracker.Clear();
            var work = await dbContext.DeploymentLocationReconciliationWork
                .Include(item => item.DeploymentLocation)!.ThenInclude(item => item!.ObservatoryLocationVersion)
                .SingleAsync(item => item.Id == workId && item.LeaseToken == leaseToken, cancellationToken)
                .ConfigureAwait(false);
            if (work.DeploymentLocation is null)
            {
                throw new InvalidOperationException("Deployment reconciliation work has no authority record.");
            }
            if (work.AuthorityConcurrencyToken != work.DeploymentLocation.ConcurrencyToken)
            {
                throw new DbUpdateConcurrencyException("Deployment reconciliation authority generation changed.");
            }

            if (work.CaptureCount is null)
            {
                await DiscoverAsync(work, cancellationToken).ConfigureAwait(false);
            }
            else if (work.ActiveBatchUpperCentralFrameId is null)
            {
                await StartStateBatchAsync(work, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ScheduleBatchAsync(work, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseForRetryAsync(workId.Value, leaseToken, "cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var failureCode = FailureCode(exception);
            await ReleaseForRetryAsync(workId.Value, leaseToken, failureCode, cancellationToken).ConfigureAwait(false);
            telemetry.RecordReconciliation("work", "failed", 0, timeProvider.GetElapsedTime(attemptStarted));
            Log.StepFailed(logger, failureCode, exception);
            return true;
        }
    }

    private async Task<Guid?> TryClaimAsync(Guid leaseToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var work = await dbContext.DeploymentLocationReconciliationWork.FromSqlInterpolated($"""
            SELECT TOP(1) *
            FROM [DeploymentLocationReconciliationWork] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
            WHERE ([Status] IN (N'Pending', N'Retry') AND [NextAttemptAtUtc] <= {now})
               OR ([Status] = N'Processing' AND [LeaseExpiresAtUtc] <= {now})
            ORDER BY CASE WHEN [Status] = N'Processing' THEN [LeaseExpiresAtUtc] ELSE [NextAttemptAtUtc] END,
                     [CreatedAtUtc], [Id]
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (work is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        work.Status = DeploymentLocationReconciliationStatuses.Processing;
        work.LeaseToken = leaseToken;
        work.LeaseOwner = settings.WorkerId;
        work.LeaseExpiresAtUtc = now + settings.LeaseDuration;
        work.StartedAtUtc ??= now;
        work.LastAttemptAtUtc = now;
        work.NextAttemptAtUtc = null;
        work.AttemptCount++;
        work.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return work.Id;
    }

    private async Task DiscoverAsync(
        DeploymentLocationReconciliationWork work,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var deployment = work.DeploymentLocation!;
        var observatory = deployment.ObservatoryLocationVersion
            ?? throw new InvalidOperationException("Deployment reconciliation authority has no observatory version.");
        work.DiscoveryCutoffUtc ??= timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var candidates = dbContext.CentralCaptureLocations.AsNoTracking()
            .Where(item => item.CentralFrame!.RegistrationId == deployment.RegistrationId
                && EF.Functions.Collate(item.LocationId, OrdinalCollation) == deployment.LocationId
                && item.Version == deployment.Version
                && item.CentralFrame.FirstReceivedAtUtc <= work.DiscoveryCutoffUtc.Value
                && item.CentralFrame.CapturedAtUtc >= observatory.EffectiveFromUtc
                && (observatory.SupersededAtUtc == null
                    || item.CentralFrame.CapturedAtUtc < observatory.SupersededAtUtc));
        if (work.DiscoveryCursorFirstReceivedAtUtc is { } cursorReceivedAt
            && work.DiscoveryCursorCentralFrameId is { } cursorFrameId)
        {
            candidates = candidates.Where(item => item.CentralFrame!.FirstReceivedAtUtc > cursorReceivedAt
                || item.CentralFrame.FirstReceivedAtUtc == cursorReceivedAt
                    && item.CentralFrameId.CompareTo(cursorFrameId) > 0);
        }
        var batch = await candidates.OrderBy(item => item.CentralFrame!.FirstReceivedAtUtc)
            .ThenBy(item => item.CentralFrameId)
            .Take(settings.CaptureBatchSize)
            .Select(item => new { item.CentralFrameId, item.CentralFrame!.FirstReceivedAtUtc })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (batch.Length != 0)
        {
            dbContext.DeploymentLocationReconciliationCaptures.AddRange(batch.Select(item =>
                new DeploymentLocationReconciliationCapture
                {
                    DeploymentLocationReconciliationWorkId = work.Id,
                    AuthorityConcurrencyToken = work.AuthorityConcurrencyToken,
                    CentralFrameId = item.CentralFrameId,
                    FirstReceivedAtUtc = item.FirstReceivedAtUtc
                }));
            work.DiscoveredCaptureCount += batch.Length;
            work.DiscoveryCursorFirstReceivedAtUtc = batch[^1].FirstReceivedAtUtc;
            work.DiscoveryCursorCentralFrameId = batch[^1].CentralFrameId;
        }
        if (batch.Length < settings.CaptureBatchSize)
        {
            work.CaptureCount = work.DiscoveredCaptureCount;
        }
        ReleaseForNext(work);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordReconciliation(
            "discovery", work.CaptureCount.HasValue ? "completed" : "continued", batch.Length,
            timeProvider.GetElapsedTime(started));
    }

    private async Task StartStateBatchAsync(
        DeploymentLocationReconciliationWork work,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var deployment = work.DeploymentLocation!;
        var query = dbContext.DeploymentLocationReconciliationCaptures
            .Where(item => item.DeploymentLocationReconciliationWorkId == work.Id
                && item.AuthorityConcurrencyToken == work.AuthorityConcurrencyToken);
        if (work.LastCompletedFirstReceivedAtUtc is { } cursorReceivedAt
            && work.LastCompletedCentralFrameId is { } cursorFrameId)
        {
            query = query.Where(item => item.FirstReceivedAtUtc > cursorReceivedAt
                || item.FirstReceivedAtUtc == cursorReceivedAt
                    && item.CentralFrameId.CompareTo(cursorFrameId) > 0);
        }
        var batch = await query.OrderBy(item => item.FirstReceivedAtUtc)
            .ThenBy(item => item.CentralFrameId)
            .Take(settings.CaptureBatchSize)
            .Select(item => new { item.CentralFrameId, item.FirstReceivedAtUtc })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (batch.Length == 0)
        {
            Complete(work);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordReconciliation("completion", "completed", 0, timeProvider.GetElapsedTime(started));
            return;
        }
        var frameIds = batch.Select(item => item.CentralFrameId).ToArray();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await dbContext.CentralCaptureLocations.Where(item => frameIds.Contains(item.CentralFrameId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.DeviceDeploymentLocationVersionId, (Guid?)null), cancellationToken).ConfigureAwait(false);
        await dbContext.CentralFrames.Where(frame => frameIds.Contains(frame.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(frame => frame.LocationEvidenceState,
                CentralCaptureLocationEvidenceState.Mismatch), cancellationToken).ConfigureAwait(false);

        var exactFrameIds = dbContext.CentralCaptureLocations
            .Where(item => frameIds.Contains(item.CentralFrameId)
                && EF.Functions.Collate(item.LocationId, OrdinalCollation) == deployment.LocationId
                && item.Version == deployment.Version
                && EF.Functions.Collate(item.Source, OrdinalCollation) == deployment.Source
                && item.HorizontalAccuracyMeters == deployment.HorizontalAccuracyMeters
                && item.EffectiveFromUtc == deployment.EffectiveFromUtc
                && item.EffectiveUntilUtc == deployment.EffectiveUntilUtc
                && item.CentralFrame!.CapturedAtUtc >= deployment.EffectiveFromUtc
                && (deployment.EffectiveUntilUtc == null
                    || item.CentralFrame.CapturedAtUtc < deployment.EffectiveUntilUtc))
            .Select(item => item.CentralFrameId);
        await dbContext.CentralCaptureLocations.Where(item => exactFrameIds.Contains(item.CentralFrameId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.DeviceDeploymentLocationVersionId, deployment.Id), cancellationToken).ConfigureAwait(false);
        if (deployment.Status == DeploymentLocationResolutionStatus.Acknowledged)
        {
            await dbContext.CentralFrames.Where(frame => exactFrameIds.Contains(frame.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(frame => frame.LocationEvidenceState,
                    CentralCaptureLocationEvidenceState.ReportedResolved), cancellationToken).ConfigureAwait(false);
        }
        work.ActiveBatchUpperFirstReceivedAtUtc = batch[^1].FirstReceivedAtUtc;
        work.ActiveBatchUpperCentralFrameId = batch[^1].CentralFrameId;
        work.ActiveBatchCaptureCount = batch.Length;
        work.SchedulingCentralFrameId = null;
        work.SchedulingCentralArtifactId = null;
        ReleaseForNext(work);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordReconciliation("state", "completed", batch.Length, timeProvider.GetElapsedTime(started));
    }

    private async Task ScheduleBatchAsync(
        DeploymentLocationReconciliationWork work,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var deployment = work.DeploymentLocation!;
        var upperReceivedAt = work.ActiveBatchUpperFirstReceivedAtUtc!.Value;
        var upperFrameId = work.ActiveBatchUpperCentralFrameId!.Value;
        var activeFrameIds = dbContext.DeploymentLocationReconciliationCaptures
            .Where(item => item.DeploymentLocationReconciliationWorkId == work.Id
                && item.AuthorityConcurrencyToken == work.AuthorityConcurrencyToken
                && (item.FirstReceivedAtUtc < upperReceivedAt
                    || item.FirstReceivedAtUtc == upperReceivedAt
                        && item.CentralFrameId.CompareTo(upperFrameId) <= 0)
                && (work.LastCompletedFirstReceivedAtUtc == null
                    || item.FirstReceivedAtUtc > work.LastCompletedFirstReceivedAtUtc
                    || item.FirstReceivedAtUtc == work.LastCompletedFirstReceivedAtUtc
                        && item.CentralFrameId.CompareTo(work.LastCompletedCentralFrameId!.Value) > 0))
            .Select(item => item.CentralFrameId);
        var artifacts = dbContext.CentralArtifacts.AsNoTracking()
            .Where(item => activeFrameIds.Contains(item.CentralFrameId)
                && item.ObjectState == CentralArtifactObjectState.Available
                && item.ReconstructionState == CentralReconstructionState.Complete);
        if (work.SchedulingCentralFrameId is { } schedulingFrame
            && work.SchedulingCentralArtifactId is { } schedulingArtifact)
        {
            artifacts = artifacts.Where(item => item.Frame!.Id.CompareTo(schedulingFrame) > 0
                || item.Frame.Id == schedulingFrame && item.ArtifactId.CompareTo(schedulingArtifact) > 0);
        }
        var batch = await artifacts.OrderBy(item => item.Frame!.Id).ThenBy(item => item.ArtifactId)
            .Take(settings.SchedulingBatchSize)
            .Select(item => new { FrameId = item.Frame!.Id, item.DevicePublicId, item.ArtifactId })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (batch.Length == 0)
        {
            work.LastCompletedFirstReceivedAtUtc = upperReceivedAt;
            work.LastCompletedCentralFrameId = upperFrameId;
            work.CompletedCaptureCount += work.ActiveBatchCaptureCount;
            work.ActiveBatchUpperFirstReceivedAtUtc = null;
            work.ActiveBatchUpperCentralFrameId = null;
            work.ActiveBatchCaptureCount = 0;
            work.SchedulingCentralFrameId = null;
            work.SchedulingCentralArtifactId = null;
            ReleaseForNext(work);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordReconciliation("scheduling", "completed", 0, timeProvider.GetElapsedTime(started));
            return;
        }

        var leaseToken = work.LeaseToken!.Value;
        foreach (var artifact in batch)
        {
            await ScheduleOneAsync(
                work.Id, leaseToken, work.AuthorityConcurrencyToken, artifact.DevicePublicId, artifact.ArtifactId,
                cancellationToken)
                .ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
        }
        work = await dbContext.DeploymentLocationReconciliationWork.SingleAsync(item =>
            item.Id == work.Id && item.LeaseToken == leaseToken
                && item.AuthorityConcurrencyToken == deployment.ConcurrencyToken, cancellationToken).ConfigureAwait(false);
        work.SchedulingCentralFrameId = batch[^1].FrameId;
        work.SchedulingCentralArtifactId = batch[^1].ArtifactId;
        work.ScheduledArtifactCount += batch.Length;
        ReleaseForNext(work);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordReconciliation("scheduling", "completed", batch.Length, timeProvider.GetElapsedTime(started));
    }

    private async Task ScheduleOneAsync(
        Guid workId,
        Guid leaseToken,
        Guid authorityConcurrencyToken,
        Guid devicePublicId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var centralArtifactId = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.DevicePublicId == devicePublicId && artifact.ArtifactId == artifactId)
            .Select(artifact => artifact.Id)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var holdTargets = await CentralDerivativeJobScheduler.ReadRequiredPayloadHoldTargetsAsync(
            dbContext, centralArtifactId, cancellationToken).ConfigureAwait(false);
        await using (var holdScope = await CentralTransientPayloadHoldFence.AcquireAsync(
            dbContext, holdTargets, cancellationToken).ConfigureAwait(false))
        {
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
            {
                var fencedWork = await dbContext.DeploymentLocationReconciliationWork.FromSqlInterpolated($"""
                    SELECT *
                    FROM [DeploymentLocationReconciliationWork] WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE [Id] = {workId}
                      AND [Status] = {DeploymentLocationReconciliationStatuses.Processing}
                      AND [LeaseToken] = {leaseToken}
                      AND [AuthorityConcurrencyToken] = {authorityConcurrencyToken}
                    """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (fencedWork is null)
                {
                    throw new DbUpdateConcurrencyException(
                        "Deployment reconciliation lease or authority generation changed.");
                }
                fencedWork.LeaseExpiresAtUtc = now + settings.LeaseDuration;
                fencedWork.UpdatedAtUtc = now;
                await derivativeJobScheduler.EnsureRequiredJobsUnderPayloadLocksAsync(
                    devicePublicId, artifactId, now, holdScope.Targets, cancellationToken).ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await derivativeJobScheduler.ResolveAffectedWindowsAsync(
            devicePublicId, artifactId, now, cancellationToken).ConfigureAwait(false);
    }

    private void ReleaseForNext(DeploymentLocationReconciliationWork work)
    {
        var now = timeProvider.GetUtcNow();
        work.Status = DeploymentLocationReconciliationStatuses.Pending;
        work.UpdatedAtUtc = now;
        work.NextAttemptAtUtc = now;
        work.LastErrorCode = null;
        work.AttemptCount = 0;
        ClearLease(work);
    }

    private void Complete(DeploymentLocationReconciliationWork work)
    {
        var now = timeProvider.GetUtcNow();
        work.CompletedCaptureCount = work.CaptureCount
            ?? throw new InvalidOperationException("Deployment reconciliation cannot complete before discovery.");
        work.Status = DeploymentLocationReconciliationStatuses.Completed;
        work.UpdatedAtUtc = now;
        work.CompletedAtUtc = now;
        work.NextAttemptAtUtc = null;
        work.LastErrorCode = null;
        work.AttemptCount = 0;
        ClearLease(work);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Retry-persistence failures must be logged without terminating the durable worker.")]
    private async Task ReleaseForRetryAsync(
        Guid workId,
        Guid leaseToken,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var work = await dbContext.DeploymentLocationReconciliationWork.SingleOrDefaultAsync(item =>
                item.Id == workId && item.LeaseToken == leaseToken, cancellationToken).ConfigureAwait(false);
            if (work is null)
            {
                return;
            }
            var exponent = Math.Min(work.AttemptCount - 1, 16);
            var delayTicks = Math.Min(settings.InitialRetryDelay.Ticks * (1L << exponent), settings.MaximumRetryDelay.Ticks);
            var now = timeProvider.GetUtcNow();
            work.Status = DeploymentLocationReconciliationStatuses.Retry;
            work.UpdatedAtUtc = now;
            work.NextAttemptAtUtc = now + TimeSpan.FromTicks(delayTicks);
            work.LastErrorCode = failureCode;
            ClearLease(work);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.RetryPersistenceFailed(logger, exception);
        }
    }

    private static void ClearLease(DeploymentLocationReconciliationWork work)
    {
        work.LeaseToken = null;
        work.LeaseOwner = null;
        work.LeaseExpiresAtUtc = null;
    }

    private static string FailureCode(Exception exception)
        => exception is DbUpdateConcurrencyException ? "concurrency"
            : exception is DbException or DbUpdateException or TimeoutException ? "database"
            : exception is InvalidOperationException ? "invariant"
            : "processing";

    private static partial class Log
    {
        [LoggerMessage(7410, LogLevel.Warning,
            "Deployment location reconciliation step failed: Reason={Reason}")]
        internal static partial void StepFailed(ILogger logger, string reason, Exception exception);

        [LoggerMessage(7411, LogLevel.Error,
            "Deployment location reconciliation retry state could not be persisted")]
        internal static partial void RetryPersistenceFailed(ILogger logger, Exception exception);
    }
}

internal sealed partial class DeploymentLocationReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<DeploymentLocationReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<DeploymentLocationReconciliationWorker> logger) : BackgroundService
{
    private readonly DeploymentLocationReconciliationOptions settings = options.Value;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed cycle must not terminate the durable background worker.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IDeploymentLocationReconciliationProcessor>();
                if (await processor.ProcessNextAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Yield();
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.CycleFailed(logger, exception);
            }
            await Task.Delay(settings.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7412, LogLevel.Error, "Deployment location reconciliation worker cycle failed")]
        internal static partial void CycleFailed(ILogger logger, Exception exception);
    }
}
