using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralTransientPayloadReleaseOptions
{
    public const string SectionName = "TransientPayloadRelease";
    public bool Enabled { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

internal sealed record CentralTransientPayloadReleaseResponse(
    Guid ReleaseId,
    CentralTransientPayloadReleaseState State,
    int ReleasedPayloadCount,
    string ETag,
    bool Replayed);

internal enum CentralTransientPayloadReleaseStatus
{
    Released,
    Disabled,
    NotFound,
    Invalid,
    Ineligible,
    PreconditionFailed,
    IdempotencyConflict,
    Failed
}

internal sealed record CentralTransientPayloadReleaseResult(
    CentralTransientPayloadReleaseStatus Status,
    CentralTransientPayloadReleaseResponse? Response = null);

internal interface ICentralTransientPayloadReleaseService
{
    Task<CentralTransientPayloadReleaseResult> ReleaseAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal interface ICentralTransientPayloadReleaseProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}

internal sealed class CentralTransientPayloadReleaseService(
    ApplicationDbContext dbContext,
    ICentralArtifactRetentionReferences retentionReferences,
    IMinioClient minio,
    IOptions<CentralTransientPayloadReleaseOptions> options,
    TimeProvider timeProvider,
    CentralTransientLifecycleTelemetry? telemetry = null)
    : ICentralTransientPayloadReleaseService, ICentralTransientPayloadReleaseProcessor
{
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";

    public async Task<CentralTransientPayloadReleaseResult> ReleaseAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var actor = CentralArtifactCredentialAccess.GetSubject(principal);
        if (!options.Value.Enabled)
        {
            return new(CentralTransientPayloadReleaseStatus.Disabled);
        }
        if (!CentralArtifactCredentialAccess.HasScope(principal, "api.admin") ||
            string.IsNullOrWhiteSpace(actor) || expectedRowVersion.Length != 8 ||
            idempotencyKey.Length is < 1 or > 128)
        {
            return new(CentralTransientPayloadReleaseStatus.Invalid);
        }
        var requestSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            "central-transient-payload-release-v1",
            centralTransientEventId.ToString("N")))));
        CentralTransientPayloadRelease release;
        var replayed = false;
        byte[] resultRowVersion;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
                         IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            var acquired = await dbContext.Database.SqlQuery<int>(
                    $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {centralTransientEventId}")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (acquired != 1)
            {
                return new(CentralTransientPayloadReleaseStatus.NotFound);
            }
            var existing = await dbContext.CentralTransientPayloadReleases.Include(item => item.Items)
                .SingleOrDefaultAsync(item => item.CentralTransientEventId == centralTransientEventId &&
                    item.ActorIdentity == actor && item.IdempotencyKey == idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.CanonicalRequestSha256, requestSha, StringComparison.Ordinal))
                {
                    return new(CentralTransientPayloadReleaseStatus.IdempotencyConflict);
                }
                replayed = true;
                release = existing;
                if (release.State == CentralTransientPayloadReleaseState.Failed)
                {
                    release.State = CentralTransientPayloadReleaseState.Pending;
                    release.CompletedUtc = null;
                    release.ReasonCode = null;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                resultRowVersion = await dbContext.CentralTransientEventCurrent.AsNoTracking()
                    .Where(item => item.CentralTransientEventId == centralTransientEventId)
                    .Select(item => item.RowVersion).SingleAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (await dbContext.CentralTransientPayloadReleases.AsNoTracking().AnyAsync(
                        item => item.CentralTransientEventId == centralTransientEventId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new(CentralTransientPayloadReleaseStatus.Ineligible);
                }
                var current = await dbContext.CentralTransientEventCurrent.SingleOrDefaultAsync(
                    item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    return new(CentralTransientPayloadReleaseStatus.NotFound);
                }
                if (!current.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
                {
                    return new(CentralTransientPayloadReleaseStatus.PreconditionFailed);
                }
                var activeStates = new[]
                {
                    CentralDerivativeJobStatus.Waiting,
                    CentralDerivativeJobStatus.Pending,
                    CentralDerivativeJobStatus.Leased,
                    CentralDerivativeJobStatus.RetryableFailure,
                    CentralDerivativeJobStatus.CancelRequested
                };
                var blocked = current.ReviewState == CentralTransientReviewState.NeedsReview ||
                    await dbContext.CentralTransientNotificationDispatches.AnyAsync(item =>
                        item.CentralTransientEventId == centralTransientEventId &&
                        (item.State == CentralTransientNotificationDispatchState.Pending ||
                         item.State == CentralTransientNotificationDispatchState.Fenced), cancellationToken)
                        .ConfigureAwait(false) ||
                    await dbContext.CentralTransientDerivativeJobs.AnyAsync(item =>
                        item.CentralTransientEventId == centralTransientEventId &&
                        activeStates.Contains(item.Job!.Status), cancellationToken).ConfigureAwait(false) ||
                    await dbContext.CentralTransientReprocessingJobs.AnyAsync(item =>
                        item.CentralTransientEventId == centralTransientEventId &&
                        activeStates.Contains(item.Job!.Status), cancellationToken).ConfigureAwait(false) ||
                    await dbContext.CentralTransientValidationIdentitySlots.AnyAsync(item =>
                        item.CentralTransientEventId == centralTransientEventId &&
                        activeStates.Contains(item.ValidationJob!.Job!.Status), cancellationToken).ConfigureAwait(false) ||
                    await dbContext.CentralTransientValidationJobs.AnyAsync(validation =>
                        validation.ProvisionalCentralDerivativeJobId != null &&
                        activeStates.Contains(validation.Job!.Status) &&
                        dbContext.CentralTransientValidationIdentitySlots.Any(slot =>
                            slot.CentralDerivativeJobId == validation.ProvisionalCentralDerivativeJobId &&
                            slot.CentralTransientEventId == centralTransientEventId), cancellationToken)
                        .ConfigureAwait(false);
                if (blocked)
                {
                    return new(CentralTransientPayloadReleaseStatus.Ineligible);
                }
                var candidateSourceIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
                    .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId)
                    .Select(item => item.CentralArtifactId).Concat(
                        dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                            .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId)
                            .Select(item => item.CentralArtifactId))
                    .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
                var sourceIds = new List<Guid>(candidateSourceIds.Length);
                foreach (var sourceId in candidateSourceIds.Order())
                {
                    if (!await retentionReferences.IsHeldOutsideTransientEventAsync(
                            sourceId, centralTransientEventId, cancellationToken).ConfigureAwait(false))
                    {
                        sourceIds.Add(sourceId);
                    }
                }
                var derivativeIds = await dbContext.CentralTransientDerivatives.AsNoTracking()
                    .Where(item => item.CentralTransientEventId == centralTransientEventId)
                    .Where(item => !dbContext.PublicRecordPublicationDecisions.Any(decision =>
                        decision.CentralTransientDerivativeId == item.DerivativeId
                        && decision.State == PublicationDecisionState.Released
                        && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                            successor.SupersedesDecisionId == decision.Id)))
                    .Select(item => item.OutputIntentId).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
                release = new CentralTransientPayloadRelease
                {
                    CentralTransientEventId = centralTransientEventId,
                    ActorIdentity = actor,
                    IdempotencyKey = idempotencyKey,
                    CanonicalRequestSha256 = requestSha,
                    State = CentralTransientPayloadReleaseState.Pending,
                    CreatedUtc = timeProvider.GetUtcNow()
                };
                var ordinal = 0;
                foreach (var id in sourceIds.Order())
                {
                    release.Items.Add(new CentralTransientPayloadReleaseItem
                    {
                        ReleaseId = release.ReleaseId,
                        Ordinal = ordinal++,
                        Kind = CentralTransientPayloadReleaseItemKind.SourceArtifact,
                        RecordId = id
                    });
                }
                foreach (var id in derivativeIds.Order())
                {
                    release.Items.Add(new CentralTransientPayloadReleaseItem
                    {
                        ReleaseId = release.ReleaseId,
                        Ordinal = ordinal++,
                        Kind = CentralTransientPayloadReleaseItemKind.Derivative,
                        RecordId = id
                    });
                }
                dbContext.CentralTransientPayloadReleases.Add(release);
                current.UpdatedUtc = release.CreatedUtc;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                resultRowVersion = current.RowVersion.ToArray();
            }
        }

        if (release.State == CentralTransientPayloadReleaseState.Pending)
        {
            await ProcessReleaseAsync(release.ReleaseId, cancellationToken).ConfigureAwait(false);
        }
        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .Include(item => item.Items).SingleAsync(item => item.ReleaseId == release.ReleaseId, cancellationToken)
            .ConfigureAwait(false);
        return new(completed.State == CentralTransientPayloadReleaseState.Completed
                ? CentralTransientPayloadReleaseStatus.Released
                : CentralTransientPayloadReleaseStatus.Failed, new(
            completed.ReleaseId,
            completed.State,
            completed.Items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released),
            CentralTransientEventEtag.Create(resultRowVersion),
            replayed));
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var releaseId = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .Where(item => item.State == CentralTransientPayloadReleaseState.Pending)
            .OrderBy(item => item.CreatedUtc).ThenBy(item => item.ReleaseId)
            .Select(item => item.ReleaseId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (releaseId == Guid.Empty)
        {
            return false;
        }
        await ProcessReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessReleaseAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry?.Start("retention");
        await using var releaseLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, $"central-transient-payload-release:{releaseId:N}", cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        var state = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .Where(item => item.ReleaseId == releaseId)
            .Select(item => item.State).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (state != CentralTransientPayloadReleaseState.Pending)
        {
            return;
        }
        try
        {
            await ReleaseItemsAsync(releaseId, cancellationToken).ConfigureAwait(false);
            var itemCount = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
                .CountAsync(item => item.ReleaseId == releaseId &&
                    item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released, cancellationToken)
                .ConfigureAwait(false);
            telemetry?.RecordRetention("completed", itemCount, timeProvider.GetElapsedTime(started));
        }
        catch (CentralArtifactStorageException)
        {
            dbContext.ChangeTracker.Clear();
            var release = await dbContext.CentralTransientPayloadReleases.SingleAsync(
                item => item.ReleaseId == releaseId && item.State == CentralTransientPayloadReleaseState.Pending,
                cancellationToken).ConfigureAwait(false);
            release.State = CentralTransientPayloadReleaseState.Failed;
            release.CompletedUtc = timeProvider.GetUtcNow();
            release.ReasonCode = "transient-retention.release-invalid";
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            telemetry?.RecordRetention("failed", 0, timeProvider.GetElapsedTime(started));
        }
    }

    private async Task ReleaseItemsAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        var itemKeys = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.ReleaseId == releaseId &&
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending)
            .OrderBy(item => item.Ordinal)
            .Select(item => new { item.Ordinal, item.Kind, item.RecordId })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var resolvedItems = new List<(int Ordinal, CentralTransientPayloadReleaseItemKind Kind, Guid RecordId, string StorageReference)>(itemKeys.Length);
        foreach (var item in itemKeys)
        {
            var storageReference = item.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact
                ? await dbContext.CentralArtifacts.AsNoTracking()
                    .Where(value => value.Id == item.RecordId).Select(value => value.StorageReference)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : await dbContext.CentralTransientDerivativeOutputIntents.AsNoTracking()
                    .Where(value => value.Id == item.RecordId).Select(value => value.StorageReference)
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (storageReference is null || !storageReference.StartsWith(BucketPrefix, StringComparison.Ordinal))
            {
                throw new CentralArtifactStorageException("Transient release storage evidence is invalid.");
            }
            resolvedItems.Add((item.Ordinal, item.Kind, item.RecordId, storageReference));
        }
        foreach (var item in resolvedItems)
        {
            dbContext.ChangeTracker.Clear();
            await ReleaseItemAsync(
                releaseId, item.Ordinal, item.Kind, item.RecordId, item.StorageReference, cancellationToken)
                .ConfigureAwait(false);
        }
        dbContext.ChangeTracker.Clear();
        var release = await dbContext.CentralTransientPayloadReleases.SingleAsync(
            item => item.ReleaseId == releaseId, cancellationToken).ConfigureAwait(false);
        if (await dbContext.CentralTransientPayloadReleaseItems.AnyAsync(item =>
                item.ReleaseId == releaseId && item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending,
                cancellationToken).ConfigureAwait(false))
        {
            throw new CentralArtifactStorageException("Transient release still contains pending items.");
        }
        release.State = CentralTransientPayloadReleaseState.Completed;
        release.CompletedUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseItemAsync(
        Guid releaseId,
        int ordinal,
        CentralTransientPayloadReleaseItemKind kind,
        Guid recordId,
        string storageReference,
        CancellationToken cancellationToken)
    {
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, storageReference, cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        if (kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
        {
            var centralTransientEventId = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
                .Where(value => value.ReleaseId == releaseId)
                .Select(value => value.CentralTransientEventId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            await CentralArtifactRetentionLock.AcquireAsync(dbContext, recordId, cancellationToken)
                .ConfigureAwait(false);
            if (await retentionReferences.IsHeldOutsideTransientEventAsync(
                    recordId, centralTransientEventId, cancellationToken).ConfigureAwait(false))
            {
                await dbContext.CentralTransientPayloadReleaseItems.Where(value =>
                        value.ReleaseId == releaseId && value.Ordinal == ordinal &&
                        value.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(value => value.Outcome, CentralTransientPayloadReleaseItemOutcome.PreservedHeld)
                        .SetProperty(value => value.ReleasedUtc, timeProvider.GetUtcNow()), cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            _ = await dbContext.CentralArtifacts.Where(value => value.Id == recordId &&
                    value.ObjectState != CentralArtifactObjectState.Expired)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.ObjectState, CentralArtifactObjectState.Expired)
                    .SetProperty(value => value.StateReasonCode, "transient-retention.evidence-released")
                    .SetProperty(value => value.ReconciledAtUtc, timeProvider.GetUtcNow()), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _ = await dbContext.CentralTransientDerivativeOutputIntents.Where(value =>
                    value.Id == recordId && value.ObjectState != CentralArtifactObjectState.Expired)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.ObjectState, CentralArtifactObjectState.Expired)
                    .SetProperty(value => value.StateReasonCode, "transient-retention.evidence-released"),
                    cancellationToken).ConfigureAwait(false);
        }
        await dbContext.CentralTransientPayloadReleaseItems.Where(value =>
                value.ReleaseId == releaseId && value.Ordinal == ordinal &&
                value.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.Outcome, CentralTransientPayloadReleaseItemOutcome.Released)
                    .SetProperty(value => value.ReleasedUtc, timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        try
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket)
                .WithObject(storageReference[BucketPrefix.Length..]), cancellationToken).ConfigureAwait(false);
        }
        catch (MinioException exception) when (exception is BucketNotFoundException ||
                                               MinioObjectVerification.IsNotFound(exception))
        {
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CentralTransientPayloadReleaseWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CentralTransientPayloadReleaseOptions> options,
    CentralTransientLifecycleTelemetry telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processed = await scope.ServiceProvider
                    .GetRequiredService<ICentralTransientPayloadReleaseProcessor>()
                    .ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                if (!processed)
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is DbException or MinioException or InvalidOperationException)
            {
                telemetry.RecordRetention("failed", 0, TimeSpan.Zero);
                await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
