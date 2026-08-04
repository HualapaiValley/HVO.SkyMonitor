using System.Data;
using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
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
    public TimeSpan ReservationLeaseTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    public int MaximumRetryCount { get; set; } = 5;
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
    Accepted,
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

internal sealed record CentralTransientPayloadReservationCurrent(
    CentralTransientPayloadReleaseItemOutcome Outcome,
    Guid? ReservationToken,
    DateTimeOffset? RequestedAtUtc,
    ReadOnlyMemory<byte> ItemRowVersion,
    ReadOnlyMemory<byte>? ReservedTargetRowVersion,
    long? ReservedTargetGeneration,
    string? ReservedStorageReference,
    ReadOnlyMemory<byte> CurrentTargetRowVersion,
    long CurrentTargetGeneration,
    string CurrentStorageReference);

internal sealed record CentralTransientPayloadReservationExpected(
    Guid ReservationToken,
    DateTimeOffset RequestedAtUtc,
    ReadOnlyMemory<byte> ItemRowVersion,
    ReadOnlyMemory<byte> TargetRowVersion,
    long TargetGeneration,
    string StorageReference);

internal static class CentralTransientPayloadReservationComparison
{
    internal static bool Matches(
        CentralTransientPayloadReservationCurrent current,
        CentralTransientPayloadReservationExpected expected)
        => current.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
           current.ReservationToken == expected.ReservationToken &&
           current.RequestedAtUtc == expected.RequestedAtUtc &&
           current.ItemRowVersion.Span.SequenceEqual(expected.ItemRowVersion.Span) &&
           current.ReservedTargetRowVersion is { } reservedTargetRowVersion &&
           reservedTargetRowVersion.Span.SequenceEqual(expected.TargetRowVersion.Span) &&
           current.ReservedTargetGeneration == expected.TargetGeneration &&
           string.Equals(current.ReservedStorageReference, expected.StorageReference, StringComparison.Ordinal) &&
           current.CurrentTargetRowVersion.Span.SequenceEqual(expected.TargetRowVersion.Span) &&
           current.CurrentTargetGeneration == expected.TargetGeneration &&
           string.Equals(current.CurrentStorageReference, expected.StorageReference, StringComparison.Ordinal);
}

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

internal enum CentralTransientPayloadReleaseFaultStage
{
    BeforeReservationCommit,
    ReservationCommittedBeforeDelete,
    BeforeDelete,
    DeleteCompletedBeforeFinalize,
    FinalItemBeforeParentCompletion
}

internal enum CentralTransientPayloadReleaseCreationFaultStage
{
    BeforeCommit,
    AfterCommit
}

internal interface ICentralTransientPayloadReleaseFaultInjector
{
    Task OnStageAsync(
        CentralTransientPayloadReleaseFaultStage stage,
        Guid releaseId,
        int ordinal,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientPayloadReleaseService(
    ApplicationDbContext dbContext,
    ICentralArtifactRetentionReferences retentionReferences,
    IMinioClient minio,
    IOptions<CentralTransientPayloadReleaseOptions> options,
    TimeProvider timeProvider,
    CentralTransientLifecycleTelemetry? telemetry = null,
    ICentralTransientPayloadReleaseFaultInjector? faultInjector = null)
    : ICentralTransientPayloadReleaseService, ICentralTransientPayloadReleaseProcessor
{
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";
    internal const int MaximumCreationConflictRetries = 3;

    internal Func<int, Guid, CentralTransientPayloadReleaseCreationFaultStage, Exception?>? CreationFaultInjector
    {
        get;
        set;
    }

    internal Func<Exception, bool>? CreationDeadlockClassifier { get; set; }

    internal Func<Exception, bool>? CreationAmbiguousOutcomeClassifier { get; set; }

    internal Func<Exception?>? CreationProbeFaultInjector { get; set; }

    internal Func<int, Guid, CancellationToken, Task>? CreationConcurrencyHook { get; set; }

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
        var creation = await CreateOrReplayWithRetryAsync(
            centralTransientEventId,
            expectedRowVersion,
            actor,
            idempotencyKey,
            requestSha,
            cancellationToken).ConfigureAwait(false);
        if (creation.Status is not null)
        {
            return new(creation.Status.Value);
        }
        var release = creation.Release!;
        var replayed = creation.Replayed;
        var resultRowVersion = creation.EventRowVersion!;

        if (release.State == CentralTransientPayloadReleaseState.Pending)
        {
            await using var releaseLock = await CentralObjectApplicationLock.TryAcquireAsync(
                dbContext, $"central-transient-payload-release:{release.ReleaseId:N}", cancellationToken)
                .ConfigureAwait(false);
            if (releaseLock is not null)
            {
                await ProcessReleaseAsync(
                    release.ReleaseId,
                    normalizePendingItems: replayed,
                    parentLockHeld: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .Include(item => item.Items).SingleAsync(item => item.ReleaseId == release.ReleaseId, cancellationToken)
            .ConfigureAwait(false);
        var status = completed.State switch
        {
            CentralTransientPayloadReleaseState.Completed => CentralTransientPayloadReleaseStatus.Released,
            CentralTransientPayloadReleaseState.Pending => CentralTransientPayloadReleaseStatus.Accepted,
            _ => CentralTransientPayloadReleaseStatus.Failed
        };
        return new(status, new(
            completed.ReleaseId,
            completed.State,
            completed.Items.Count(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Released),
            CentralTransientEventEtag.Create(resultRowVersion),
            replayed));
    }

    private async Task<CreationResult> CreateOrReplayWithRetryAsync(
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string actor,
        string idempotencyKey,
        string requestSha,
        CancellationToken cancellationToken)
    {
        var releaseId = Guid.NewGuid();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CreateOrReplayAsync(
                    releaseId,
                    centralTransientEventId,
                    expectedRowVersion,
                    actor,
                    idempotencyKey,
                    requestSha,
                    attempt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableCreationConflict(exception))
            {
                dbContext.ChangeTracker.Clear();
                var reason = IsCreationDeadlock(exception) ? "deadlock" : "ambiguous-commit";
                if (attempt >= MaximumCreationConflictRetries)
                {
                    telemetry?.RecordRetentionCreationConflict(reason, "exhausted", attempt + 1);
                    throw new InvalidOperationException(
                        "Central transient payload release creation exhausted SQL conflict retries.",
                        exception);
                }
                telemetry?.RecordRetentionCreationConflict(reason, "retry", attempt + 1);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(10 * (1 << attempt)),
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<CreationResult> CreateOrReplayAsync(
        Guid releaseId,
        Guid centralTransientEventId,
        byte[] expectedRowVersion,
        string actor,
        string idempotencyKey,
        string requestSha,
        int attempt,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var fastReplay = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CentralTransientEventId == centralTransientEventId &&
                item.ActorIdentity == actor && item.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (fastReplay is not null)
        {
            return await ReplayAsync(fastReplay, centralTransientEventId, requestSha, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var acquired = await dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralTransientEvents] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {centralTransientEventId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (acquired != 1)
        {
            return new(Status: CentralTransientPayloadReleaseStatus.NotFound);
        }
        if (CreationConcurrencyHook is not null)
        {
            await CreationConcurrencyHook(attempt, releaseId, cancellationToken).ConfigureAwait(false);
        }
        var existing = await dbContext.CentralTransientPayloadReleases.FromSqlInterpolated($"""
                SELECT *
                FROM [CentralTransientPayloadReleases]
                    WITH (UPDLOCK, HOLDLOCK,
                          INDEX([IX_CentralTransientPayloadReleases_CentralTransientEventId_ActorIdentity_IdempotencyKey]))
                WHERE [CentralTransientEventId] = {centralTransientEventId}
                  AND [ActorIdentity] = {actor}
                  AND [IdempotencyKey] = {idempotencyKey}
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var replay = await ReplayAsync(existing, centralTransientEventId, requestSha, cancellationToken)
                .ConfigureAwait(false);
            if (replay.Status is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return replay;
        }
        if (await dbContext.CentralTransientPayloadReleases.AsNoTracking().AnyAsync(
                item => item.CentralTransientEventId == centralTransientEventId, cancellationToken)
            .ConfigureAwait(false))
        {
            return new(Status: CentralTransientPayloadReleaseStatus.Ineligible);
        }
        var current = await dbContext.CentralTransientEventCurrent.SingleOrDefaultAsync(
            item => item.CentralTransientEventId == centralTransientEventId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new(Status: CentralTransientPayloadReleaseStatus.NotFound);
        }
        if (!current.RowVersion.AsSpan().SequenceEqual(expectedRowVersion))
        {
            return new(Status: CentralTransientPayloadReleaseStatus.PreconditionFailed);
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
            return new(Status: CentralTransientPayloadReleaseStatus.Ineligible);
        }
        var sourceIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId)
            .Select(item => item.CentralArtifactId).Concat(
                dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                    .Where(item => item.Observation!.CentralTransientEventId == centralTransientEventId)
                    .Select(item => item.CentralArtifactId))
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var derivativeIds = await dbContext.CentralTransientDerivatives.AsNoTracking()
            .Where(item => item.CentralTransientEventId == centralTransientEventId)
            .Select(item => item.OutputIntentId).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var release = new CentralTransientPayloadRelease
        {
            ReleaseId = releaseId,
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
        var injectedBeforeCommit = CreationFaultInjector?.Invoke(
            attempt, releaseId, CentralTransientPayloadReleaseCreationFaultStage.BeforeCommit);
        if (injectedBeforeCommit is not null)
        {
            throw injectedBeforeCommit;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var injectedAfterCommit = CreationFaultInjector?.Invoke(
                attempt, releaseId, CentralTransientPayloadReleaseCreationFaultStage.AfterCommit);
            if (injectedAfterCommit is not null)
            {
                throw injectedAfterCommit;
            }
        }
        catch (Exception exception) when (!IsCreationDeadlock(exception) && IsCreationOutcomeAmbiguous(exception))
        {
            dbContext.ChangeTracker.Clear();
            CreationResult? probed;
            try
            {
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
                probed = await ProbeCreationAsync(
                    releaseId,
                    centralTransientEventId,
                    actor,
                    idempotencyKey,
                    requestSha,
                    probeTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception probeException) when (probeException is DbException or OperationCanceledException)
            {
                throw new AmbiguousCreationOutcomeException(
                    new AggregateException(exception, probeException));
            }
            if (probed is not null)
            {
                telemetry?.RecordRetentionCreationConflict("ambiguous-commit", "recovered", attempt + 1);
                return probed;
            }
            throw new AmbiguousCreationOutcomeException(exception);
        }
        return new(release, current.RowVersion.ToArray(), Replayed: false);
    }

    private async Task<CreationResult> ReplayAsync(
        CentralTransientPayloadRelease release,
        Guid centralTransientEventId,
        string requestSha,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(release.CanonicalRequestSha256, requestSha, StringComparison.Ordinal))
        {
            return new(Status: CentralTransientPayloadReleaseStatus.IdempotencyConflict);
        }
        var eventRowVersion = await dbContext.CentralTransientEventCurrent.AsNoTracking()
            .Where(item => item.CentralTransientEventId == centralTransientEventId)
            .Select(item => item.RowVersion).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new(release, eventRowVersion, Replayed: true);
    }

    private async Task<CreationResult?> ProbeCreationAsync(
        Guid releaseId,
        Guid centralTransientEventId,
        string actor,
        string idempotencyKey,
        string requestSha,
        CancellationToken cancellationToken)
    {
        await using var probe = CreateCreationProbeContext();
        var injectedFault = CreationProbeFaultInjector?.Invoke();
        if (injectedFault is not null)
        {
            throw injectedFault;
        }
        var release = await probe.CentralTransientPayloadReleases.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ReleaseId == releaseId ||
                (item.CentralTransientEventId == centralTransientEventId &&
                 item.ActorIdentity == actor && item.IdempotencyKey == idempotencyKey),
                cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return null;
        }
        if (!string.Equals(release.CanonicalRequestSha256, requestSha, StringComparison.Ordinal))
        {
            return new(Status: CentralTransientPayloadReleaseStatus.IdempotencyConflict);
        }
        var eventRowVersion = await probe.CentralTransientEventCurrent.AsNoTracking()
            .Where(item => item.CentralTransientEventId == centralTransientEventId)
            .Select(item => item.RowVersion).SingleAsync(cancellationToken).ConfigureAwait(false);
        return new(release, eventRowVersion, Replayed: release.ReleaseId != releaseId);
    }

    private ApplicationDbContext CreateCreationProbeContext()
    {
        var connectionString = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Transient payload release recovery requires SQL Server.");
        return new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options);
    }

    private bool IsRetryableCreationConflict(Exception exception)
        => IsCreationDeadlock(exception) || exception is AmbiguousCreationOutcomeException;

    private bool IsCreationDeadlock(Exception exception)
        => ContainsSqlError(exception, 1205) || CreationDeadlockClassifier?.Invoke(exception) == true;

    private bool IsCreationOutcomeAmbiguous(Exception exception)
        => CreationAmbiguousOutcomeClassifier?.Invoke(exception)
           ?? exception is DbException or OperationCanceledException;

    private static bool ContainsSqlError(Exception exception, int errorNumber)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is SqlException sqlException &&
                (sqlException.Number == errorNumber ||
                 sqlException.Errors.Cast<SqlError>().Any(error => error.Number == errorNumber)))
            {
                return true;
            }
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
        return false;
    }

    private sealed record CreationResult(
        CentralTransientPayloadRelease? Release = null,
        byte[]? EventRowVersion = null,
        bool Replayed = false,
        CentralTransientPayloadReleaseStatus? Status = null);

    private sealed class AmbiguousCreationOutcomeException : Exception
    {
        public AmbiguousCreationOutcomeException()
        {
        }

        public AmbiguousCreationOutcomeException(string message)
            : base(message)
        {
        }

        public AmbiguousCreationOutcomeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public AmbiguousCreationOutcomeException(Exception innerException)
            : this("Central transient payload release creation commit outcome is ambiguous.", innerException)
        {
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now - options.Value.ReservationLeaseTimeout;
        var releaseIds = await dbContext.Database.SqlQuery<Guid>($"""
                SELECT TOP(16) release.[ReleaseId] AS [Value]
                FROM [CentralTransientPayloadReleases] AS release
                OUTER APPLY
                (
                    SELECT TOP(1) item.[ReservationToken], item.[RequestedAtUtc], item.[RetryAtUtc]
                    FROM [CentralTransientPayloadReleaseItems] AS item
                    WHERE item.[ReleaseId] = release.[ReleaseId] AND item.[Outcome] = N'Pending'
                    ORDER BY item.[Ordinal]
                ) AS next_item
                WHERE release.[State] = N'Pending'
                  AND (next_item.[RequestedAtUtc] IS NULL
                    OR (next_item.[ReservationToken] IS NULL AND
                        (next_item.[RetryAtUtc] IS NULL OR next_item.[RetryAtUtc] <= {now}))
                    OR (next_item.[ReservationToken] IS NOT NULL AND next_item.[RequestedAtUtc] <= {staleBefore}))
                ORDER BY release.[CreatedUtc], release.[ReleaseId]
                """)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var releaseId in releaseIds)
        {
            await using var releaseLock = await CentralObjectApplicationLock.TryAcquireAsync(
                dbContext, $"central-transient-payload-release:{releaseId:N}", cancellationToken).ConfigureAwait(false);
            if (releaseLock is null)
            {
                continue;
            }
            await ProcessReleaseAsync(
                releaseId, parentLockHeld: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task ProcessReleaseAsync(
        Guid releaseId,
        bool normalizePendingItems = true,
        bool parentLockHeld = false,
        CancellationToken cancellationToken = default)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry?.Start("retention");
        await using var releaseLock = parentLockHeld
            ? null
            : await CentralObjectApplicationLock.AcquireAsync(
                dbContext, $"central-transient-payload-release:{releaseId:N}", cancellationToken).ConfigureAwait(false);
        try
        {
            if (normalizePendingItems)
            {
                await NormalizePendingItemsAsync(releaseId, cancellationToken).ConfigureAwait(false);
            }
            while (await IsReleasePendingAsync(releaseId, cancellationToken).ConfigureAwait(false))
            {
                var item = await LoadNextPendingItemAsync(releaseId, cancellationToken).ConfigureAwait(false);
                if (item is null)
                {
                    await InvokeFaultAsync(
                        CentralTransientPayloadReleaseFaultStage.FinalItemBeforeParentCompletion,
                        releaseId,
                        -1,
                        cancellationToken).ConfigureAwait(false);
                    var parentState = await TryFinalizeParentAsync(releaseId, cancellationToken).ConfigureAwait(false);
                    if (parentState.HasValue)
                    {
                        var itemCount = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
                            .CountAsync(value => value.ReleaseId == releaseId &&
                                value.Outcome == CentralTransientPayloadReleaseItemOutcome.Released, cancellationToken)
                            .ConfigureAwait(false);
                        telemetry?.RecordRetention(
                            parentState == CentralTransientPayloadReleaseState.Completed ? "completed" : "failed",
                            itemCount,
                            timeProvider.GetElapsedTime(started));
                    }
                    return;
                }

                var now = timeProvider.GetUtcNow();
                if (!IsDue(item, now))
                {
                    return;
                }
                var storageReference = item.StorageReference;
                if (!IsCanonicalStorageReference(storageReference))
                {
                    if (await FailItemAsync(
                            item, "transient-retention.invalid-storage-reference", cancellationToken)
                        .ConfigureAwait(false))
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "failed", item.RetryCount);
                        continue;
                    }
                    return;
                }
                var canonicalStorageReference = storageReference!;

                var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                    dbContext, canonicalStorageReference, cancellationToken).ConfigureAwait(false);
                try
                {
                    var reservation = await ReserveAsync(item, canonicalStorageReference, cancellationToken)
                        .ConfigureAwait(false);
                    if (reservation.Outcome == ReservationOutcome.Preserved)
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "preserved", reservation.RetryCount);
                        continue;
                    }
                    if (reservation.Outcome == ReservationOutcome.RetryScheduled)
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "retry", reservation.RetryCount);
                        return;
                    }
                    if (reservation.Outcome == ReservationOutcome.Failed)
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "failed", reservation.RetryCount);
                        continue;
                    }
                    if (reservation.Outcome != ReservationOutcome.Reserved || reservation.Snapshot is null)
                    {
                        return;
                    }

                    telemetry?.RecordRetentionItem(item.Kind.ToString(), "reserved", reservation.RetryCount);
                    await InvokeFaultAsync(
                        CentralTransientPayloadReleaseFaultStage.ReservationCommittedBeforeDelete,
                        releaseId,
                        item.Ordinal,
                        cancellationToken).ConfigureAwait(false);
                    var beforeDelete = await RevalidateBeforeDeleteAsync(
                        reservation.Snapshot, cancellationToken).ConfigureAwait(false);
                    if (beforeDelete == PreDeleteOutcome.Preserved)
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "preserved", reservation.RetryCount);
                        continue;
                    }
                    if (beforeDelete == PreDeleteOutcome.Failed)
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "failed", reservation.RetryCount);
                        continue;
                    }
                    if (beforeDelete != PreDeleteOutcome.Ready)
                    {
                        return;
                    }

                    await InvokeFaultAsync(
                        CentralTransientPayloadReleaseFaultStage.BeforeDelete,
                        releaseId,
                        item.Ordinal,
                        cancellationToken).ConfigureAwait(false);
                    await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
                    if (dbContext.Database.CurrentTransaction is not null)
                    {
                        throw new InvalidOperationException("Transient payload DELETE cannot run inside a SQL transaction.");
                    }
                    try
                    {
                        await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket)
                            .WithObject(canonicalStorageReference[BucketPrefix.Length..]), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
                    {
                    }
                    catch (Exception exception) when (IsRetryableStorageFailure(exception))
                    {
                        var existence = await ProbeObjectExistenceAsync(
                            canonicalStorageReference, cancellationToken).ConfigureAwait(false);
                        if (existence == ObjectExistence.Absent)
                        {
                            await InvokeFaultAsync(
                                CentralTransientPayloadReleaseFaultStage.DeleteCompletedBeforeFinalize,
                                releaseId,
                                item.Ordinal,
                                cancellationToken).ConfigureAwait(false);
                            var recovered = await TryFinalizeAsync(
                                reservation.Snapshot, cancellationToken).ConfigureAwait(false);
                            if (recovered == FinalizationOutcome.Released)
                            {
                                telemetry?.RecordRetentionItem(
                                    item.Kind.ToString(), "released", reservation.RetryCount);
                                continue;
                            }
                            if (recovered == FinalizationOutcome.Failed)
                            {
                                telemetry?.RecordRetentionItem(
                                    item.Kind.ToString(), "failed", reservation.RetryCount);
                                continue;
                            }
                            return;
                        }
                        var disposition = await ScheduleRetryAsync(
                            reservation.Snapshot,
                            existence == ObjectExistence.Present
                                ? "transient-retention.delete-retry-exhausted"
                                : "transient-retention.delete-outcome-ambiguous",
                            cancellationToken)
                            .ConfigureAwait(false);
                        telemetry?.RecordRetentionItem(
                            item.Kind.ToString(),
                            disposition == RetryDisposition.Failed ? "failed" : "retry",
                            reservation.RetryCount + 1);
                        if (disposition == RetryDisposition.Failed)
                        {
                            continue;
                        }
                        return;
                    }
                    catch (Exception exception) when (IsTerminalStorageFailure(exception))
                    {
                        if (await FailReservedItemAsync(
                                reservation.Snapshot, GetStorageFailureReason(exception), cancellationToken)
                            .ConfigureAwait(false))
                        {
                            telemetry?.RecordRetentionItem(
                                item.Kind.ToString(), "failed", reservation.RetryCount);
                            continue;
                        }
                        return;
                    }

                    await InvokeFaultAsync(
                        CentralTransientPayloadReleaseFaultStage.DeleteCompletedBeforeFinalize,
                        releaseId,
                        item.Ordinal,
                        cancellationToken).ConfigureAwait(false);
                    var finalization = await TryFinalizeAsync(
                        reservation.Snapshot, cancellationToken).ConfigureAwait(false);
                    if (finalization == FinalizationOutcome.Failed)
                    {
                        telemetry?.RecordRetentionItem(item.Kind.ToString(), "failed", reservation.RetryCount + 1);
                        continue;
                    }
                    if (finalization != FinalizationOutcome.Released)
                    {
                        return;
                    }
                    telemetry?.RecordRetentionItem(item.Kind.ToString(), "released", reservation.RetryCount);
                }
                finally
                {
                    await objectLock.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (CentralArtifactStorageException)
        {
            telemetry?.RecordRetention("failed", 0, timeProvider.GetElapsedTime(started));
            throw;
        }
    }

    private async Task<ReservationResult> ReserveAsync(
        PendingItem itemKey,
        string expectedStorageReference,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var item = await LockItemAsync(itemKey.ReleaseId, itemKey.Ordinal, cancellationToken).ConfigureAwait(false);
        if (item is null || item.Outcome != CentralTransientPayloadReleaseItemOutcome.Pending ||
            !IsDue(item, timeProvider.GetUtcNow()))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(ReservationOutcome.Unavailable, null, item?.RetryCount ?? 0);
        }
        var target = await LoadTargetForUpdateAsync(item.Kind, item.RecordId, cancellationToken)
            .ConfigureAwait(false);
        if (target is null || !IsCanonicalStorageReference(target.StorageReference))
        {
            TransitionFailed(item, "transient-retention.invalid-target", incrementRetry: false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ReservationOutcome.Failed, null, item.RetryCount);
        }
        if (!string.Equals(target.StorageReference, expectedStorageReference, StringComparison.Ordinal))
        {
            var disposition = await ApplyRetryOrFailAsync(
                item, "transient-retention.target-changed", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                disposition == RetryDisposition.Failed
                    ? ReservationOutcome.Failed
                    : ReservationOutcome.RetryScheduled,
                null,
                item.RetryCount);
        }
        if (await HasActiveStorageOwnerAsync(
                item.Kind, item.RecordId, expectedStorageReference, cancellationToken).ConfigureAwait(false))
        {
            ApplyTargetSnapshot(item, target, expectedStorageReference);
            TransitionPreserved(item);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ReservationOutcome.Preserved, null, item.RetryCount);
        }
        var held = await IsHeldAsync(item, cancellationToken).ConfigureAwait(false);
        var hadPriorAttempt = item.RequestedAtUtc.HasValue;
        if (held)
        {
            if (hadPriorAttempt)
            {
                TransitionFailed(item, "transient-retention.hold-after-reservation", incrementRetry: false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(ReservationOutcome.Failed, null, item.RetryCount);
            }
            ApplyTargetSnapshot(item, target, expectedStorageReference);
            TransitionPreserved(item);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ReservationOutcome.Preserved, null, item.RetryCount);
        }

        item.ReservationToken = Guid.NewGuid();
        ApplyTargetSnapshot(item, target, expectedStorageReference);
        item.RetryAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await InvokeFaultAsync(
            CentralTransientPayloadReleaseFaultStage.BeforeReservationCommit,
            item.ReleaseId,
            item.Ordinal,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            ReservationOutcome.Reserved,
            new ReservationSnapshot(
                item.ReleaseId,
                item.Ordinal,
                item.Kind,
                item.RecordId,
                item.ReservationToken!.Value,
                item.RequestedAtUtc!.Value,
                expectedStorageReference,
                item.TargetRowVersion!.ToArray(),
                item.TargetGeneration!.Value,
                item.RowVersion.ToArray(),
                item.RetryCount),
            item.RetryCount);
    }

    private async Task<PreDeleteOutcome> RevalidateBeforeDeleteAsync(
        ReservationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var item = await LockItemAsync(snapshot.ReleaseId, snapshot.Ordinal, cancellationToken).ConfigureAwait(false);
        var target = await LoadTargetForUpdateAsync(snapshot.Kind, snapshot.RecordId, cancellationToken)
            .ConfigureAwait(false);
        if (!MatchesReservation(item, target, snapshot))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var disposition = await ScheduleRetryAsync(
                snapshot, "transient-retention.reservation-stale", cancellationToken).ConfigureAwait(false);
            return disposition == RetryDisposition.Failed
                ? PreDeleteOutcome.Failed
                : PreDeleteOutcome.RetryScheduled;
        }
        if (await HasActiveStorageOwnerAsync(
                snapshot.Kind, snapshot.RecordId, snapshot.StorageReference, cancellationToken)
            .ConfigureAwait(false))
        {
            TransitionPreserved(item!);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PreDeleteOutcome.Preserved;
        }
        if (await IsHeldAsync(item!, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var failed = await FailReservedItemAsync(
                snapshot, "transient-retention.hold-after-reservation", cancellationToken).ConfigureAwait(false);
            return failed ? PreDeleteOutcome.Failed : PreDeleteOutcome.RetryScheduled;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PreDeleteOutcome.Ready;
    }

    private async Task<FinalizationOutcome> TryFinalizeAsync(
        ReservationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var item = await LockItemAsync(snapshot.ReleaseId, snapshot.Ordinal, cancellationToken).ConfigureAwait(false);
        var target = await LoadTargetForUpdateAsync(snapshot.Kind, snapshot.RecordId, cancellationToken)
            .ConfigureAwait(false);
        if (!MatchesReservation(item, target, snapshot))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var disposition = await ScheduleRetryAsync(
                snapshot, "transient-retention.finalization-stale", cancellationToken).ConfigureAwait(false);
            return disposition == RetryDisposition.Failed
                ? FinalizationOutcome.Failed
                : FinalizationOutcome.Unavailable;
        }
        if (await IsHeldAsync(item!, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await FailReservedItemAsync(
                    snapshot, "transient-retention.hold-after-delete", cancellationToken)
                .ConfigureAwait(false)
                ? FinalizationOutcome.Failed
                : FinalizationOutcome.Unavailable;
        }
        if (await HasActiveStorageOwnerAsync(
                snapshot.Kind, snapshot.RecordId, snapshot.StorageReference, cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await FailReservedItemAsync(
                    snapshot, "transient-retention.shared-owner-after-delete", cancellationToken)
                .ConfigureAwait(false)
                ? FinalizationOutcome.Failed
                : FinalizationOutcome.Unavailable;
        }

        var now = timeProvider.GetUtcNow();
        if (snapshot.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
        {
            var artifact = await dbContext.CentralArtifacts.SingleAsync(value => value.Id == snapshot.RecordId,
                cancellationToken).ConfigureAwait(false);
            artifact.ObjectState = CentralArtifactObjectState.Expired;
            artifact.StateReasonCode = "transient-retention.evidence-released";
            artifact.ReconciledAtUtc = now;
            artifact.ObjectVerificationToken = null;
            artifact.ObjectVerificationRequestedAtUtc = null;
            artifact.ObjectVerificationRetryCount = 0;
            artifact.ObjectVerificationRetryAtUtc = null;
        }
        else
        {
            var intent = await dbContext.CentralTransientDerivativeOutputIntents.SingleAsync(
                value => value.Id == snapshot.RecordId, cancellationToken).ConfigureAwait(false);
            intent.ObjectState = CentralArtifactObjectState.Expired;
            intent.StateReasonCode = "transient-retention.evidence-released";
        }
        item!.Outcome = CentralTransientPayloadReleaseItemOutcome.Released;
        item.ReleasedUtc = now;
        item.ReservationToken = null;
        item.RetryAtUtc = null;
        item.FailureReasonCode = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return FinalizationOutcome.Released;
    }

    private async Task<RetryDisposition> ScheduleRetryAsync(
        ReservationSnapshot snapshot,
        string terminalReasonCode,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var item = await LockItemAsync(snapshot.ReleaseId, snapshot.Ordinal, cancellationToken).ConfigureAwait(false);
        if (item is not null && item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
            item.ReservationToken == snapshot.ReservationToken &&
            item.RowVersion.AsSpan().SequenceEqual(snapshot.ItemRowVersion))
        {
            var disposition = await ApplyRetryOrFailAsync(
                item, terminalReasonCode, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return disposition;
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return RetryDisposition.Unavailable;
        }
    }

    private async Task<RetryDisposition> ApplyRetryOrFailAsync(
        CentralTransientPayloadReleaseItem item,
        string terminalReasonCode,
        CancellationToken cancellationToken)
    {
        item.ReservationToken = null;
        item.RetryCount = checked(item.RetryCount + 1);
        if (item.RetryCount >= options.Value.MaximumRetryCount)
        {
            TransitionFailed(item, terminalReasonCode, incrementRetry: false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return RetryDisposition.Failed;
        }
        item.FailureReasonCode = null;
        item.RetryAtUtc = timeProvider.GetUtcNow() + CalculateRetryDelay(item.RetryCount);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return RetryDisposition.Scheduled;
    }

    private async Task<bool> FailItemAsync(
        PendingItem itemKey,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var item = await LockItemAsync(itemKey.ReleaseId, itemKey.Ordinal, cancellationToken).ConfigureAwait(false);
        if (item is null || item.Outcome != CentralTransientPayloadReleaseItemOutcome.Pending ||
            item.Kind != itemKey.Kind || item.RecordId != itemKey.RecordId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        TransitionFailed(item, reasonCode, incrementRetry: false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> FailReservedItemAsync(
        ReservationSnapshot snapshot,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var item = await LockItemAsync(snapshot.ReleaseId, snapshot.Ordinal, cancellationToken).ConfigureAwait(false);
        if (item is null || item.Outcome != CentralTransientPayloadReleaseItemOutcome.Pending ||
            item.ReservationToken != snapshot.ReservationToken ||
            !item.RowVersion.AsSpan().SequenceEqual(snapshot.ItemRowVersion))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        TransitionFailed(item, reasonCode, incrementRetry: false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void TransitionFailed(
        CentralTransientPayloadReleaseItem item,
        string reasonCode,
        bool incrementRetry)
    {
        if (incrementRetry)
        {
            item.RetryCount = checked(item.RetryCount + 1);
        }
        item.Outcome = CentralTransientPayloadReleaseItemOutcome.Failed;
        item.ReleasedUtc = timeProvider.GetUtcNow();
        item.ReservationToken = null;
        item.RetryAtUtc = null;
        item.FailureReasonCode = reasonCode.Length <= 128 ? reasonCode : reasonCode[..128];
    }

    private void TransitionPreserved(CentralTransientPayloadReleaseItem item)
    {
        item.Outcome = CentralTransientPayloadReleaseItemOutcome.PreservedHeld;
        item.ReleasedUtc = timeProvider.GetUtcNow();
        item.ReservationToken = null;
        item.RetryAtUtc = null;
        item.FailureReasonCode = null;
    }

    private Task<bool> HasActiveStorageOwnerAsync(
        CentralTransientPayloadReleaseItemKind kind,
        Guid recordId,
        string storageReference,
        CancellationToken cancellationToken)
        => CentralObjectOwnershipFence.HasActiveOwnerAsync(
            dbContext,
            storageReference,
            kind == CentralTransientPayloadReleaseItemKind.SourceArtifact ? recordId : Guid.Empty,
            kind == CentralTransientPayloadReleaseItemKind.Derivative ? recordId : Guid.Empty,
            cancellationToken);

    private async Task<CentralTransientPayloadReleaseState?> TryFinalizeParentAsync(
        Guid releaseId,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var release = await dbContext.CentralTransientPayloadReleases.FromSqlInterpolated($"""
                SELECT * FROM [CentralTransientPayloadReleases] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ReleaseId] = {releaseId}
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || release.State != CentralTransientPayloadReleaseState.Pending ||
            await dbContext.CentralTransientPayloadReleaseItems.AnyAsync(item =>
                item.ReleaseId == releaseId && item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var failed = await dbContext.CentralTransientPayloadReleaseItems.AnyAsync(item =>
            item.ReleaseId == releaseId && item.Outcome == CentralTransientPayloadReleaseItemOutcome.Failed,
            cancellationToken).ConfigureAwait(false);
        release.State = failed
            ? CentralTransientPayloadReleaseState.Failed
            : CentralTransientPayloadReleaseState.Completed;
        release.CompletedUtc = timeProvider.GetUtcNow();
        release.ReasonCode = failed ? "transient-retention.item-failed" : null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return release.State;
    }

    private async Task<CentralTransientPayloadReleaseItem?> LockItemAsync(
        Guid releaseId,
        int ordinal,
        CancellationToken cancellationToken)
        => await dbContext.CentralTransientPayloadReleaseItems.FromSqlInterpolated($"""
                SELECT * FROM [CentralTransientPayloadReleaseItems] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ReleaseId] = {releaseId} AND [Ordinal] = {ordinal}
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<TargetSnapshot?> LoadTargetForUpdateAsync(
        CentralTransientPayloadReleaseItemKind kind,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        if (kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
        {
            return await dbContext.CentralArtifacts.FromSqlInterpolated($"""
                    SELECT * FROM [CentralArtifacts] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Id] = {recordId}
                    """)
                .AsNoTracking()
                .Select(value => new TargetSnapshot(
                    value.StorageReference,
                    value.RowVersion,
                    value.RecoveryGeneration))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        return await dbContext.CentralTransientDerivativeOutputIntents.FromSqlInterpolated($"""
                SELECT * FROM [CentralTransientDerivativeOutputIntents] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {recordId}
                """)
            .AsNoTracking()
            .Select(value => new TargetSnapshot(value.StorageReference, value.RowVersion, 0))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsHeldAsync(
        CentralTransientPayloadReleaseItem item,
        CancellationToken cancellationToken)
    {
        if (item.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
        {
            var eventId = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
                .Where(value => value.ReleaseId == item.ReleaseId)
                .Select(value => value.CentralTransientEventId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            return await retentionReferences.IsHeldOutsideTransientEventAsync(
                item.RecordId, eventId, cancellationToken).ConfigureAwait(false);
        }
        return await dbContext.CentralTransientDerivatives.AsNoTracking()
            .Where(value => value.OutputIntentId == item.RecordId)
            .AnyAsync(value => dbContext.PublicRecordPublicationDecisions.Any(decision =>
                decision.CentralTransientDerivativeId == value.DerivativeId &&
                decision.State == PublicationDecisionState.Released &&
                !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id)), cancellationToken).ConfigureAwait(false);
    }

    private async Task NormalizePendingItemsAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var release = await dbContext.CentralTransientPayloadReleases.FromSqlInterpolated($"""
                SELECT * FROM [CentralTransientPayloadReleases] WITH (UPDLOCK, HOLDLOCK)
                WHERE [ReleaseId] = {releaseId}
                """)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || release.State != CentralTransientPayloadReleaseState.Pending)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var sourceIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(item => item.Observation!.CentralTransientEventId == release.CentralTransientEventId)
            .Select(item => item.CentralArtifactId)
            .Concat(dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                .Where(item => item.Observation!.CentralTransientEventId == release.CentralTransientEventId)
                .Select(item => item.CentralArtifactId))
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var derivativeIds = await dbContext.CentralTransientDerivatives.AsNoTracking()
            .Where(item => item.CentralTransientEventId == release.CentralTransientEventId)
            .Select(item => item.OutputIntentId)
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var existing = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.ReleaseId == releaseId)
            .Select(item => new { item.Kind, item.RecordId, item.Ordinal })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var existingTargets = existing.Select(item => (item.Kind, item.RecordId)).ToHashSet();
        var ordinal = existing.Select(item => item.Ordinal).DefaultIfEmpty(-1).Max() + 1;
        var missing = sourceIds
            .Select(id => (Kind: CentralTransientPayloadReleaseItemKind.SourceArtifact, RecordId: id))
            .Concat(derivativeIds.Select(id =>
                (Kind: CentralTransientPayloadReleaseItemKind.Derivative, RecordId: id)))
            .Where(item => !existingTargets.Contains(item))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.RecordId)
            .ToArray();
        foreach (var target in missing)
        {
            dbContext.CentralTransientPayloadReleaseItems.Add(new CentralTransientPayloadReleaseItem
            {
                ReleaseId = releaseId,
                Ordinal = ordinal++,
                Kind = target.Kind,
                RecordId = target.RecordId
            });
        }
        if (missing.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingItem?> LoadNextPendingItemAsync(Guid releaseId, CancellationToken cancellationToken)
        => await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.ReleaseId == releaseId &&
                item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending)
            .OrderBy(item => item.Ordinal)
            .Select(item => new PendingItem(
                item.ReleaseId,
                item.Ordinal,
                item.Kind,
                item.RecordId,
                item.ReservationToken,
                item.RequestedAtUtc,
                item.RetryAtUtc,
                item.RetryCount,
                item.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact
                    ? dbContext.CentralArtifacts
                        .Where(target => target.Id == item.RecordId)
                        .Select(target => EF.Functions.Collate(
                            target.StorageReference, "Latin1_General_100_BIN2"))
                        .SingleOrDefault()
                    : dbContext.CentralTransientDerivativeOutputIntents
                        .Where(target => target.Id == item.RecordId)
                        .Select(target => EF.Functions.Collate(
                            target.StorageReference, "Latin1_General_100_BIN2"))
                        .SingleOrDefault()))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private Task<bool> IsReleasePendingAsync(Guid releaseId, CancellationToken cancellationToken)
        => dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .AnyAsync(item => item.ReleaseId == releaseId &&
                item.State == CentralTransientPayloadReleaseState.Pending, cancellationToken);

    private static bool MatchesReservation(
        CentralTransientPayloadReleaseItem? item,
        TargetSnapshot? target,
        ReservationSnapshot snapshot)
        => item is not null && target is not null && CentralTransientPayloadReservationComparison.Matches(
            new(
                item.Outcome,
                item.ReservationToken,
                item.RequestedAtUtc,
                item.RowVersion,
                item.TargetRowVersion,
                item.TargetGeneration,
                item.StorageReference,
                target.RowVersion,
                target.Generation,
                target.StorageReference),
            new(
                snapshot.ReservationToken,
                snapshot.RequestedAtUtc,
                snapshot.ItemRowVersion,
                snapshot.TargetRowVersion,
                snapshot.TargetGeneration,
                snapshot.StorageReference));

    private void ApplyTargetSnapshot(
        CentralTransientPayloadReleaseItem item,
        TargetSnapshot target,
        string storageReference)
    {
        item.RequestedAtUtc = timeProvider.GetUtcNow();
        item.StorageReference = storageReference;
        item.TargetRowVersion = target.RowVersion.ToArray();
        item.TargetGeneration = target.Generation;
    }

    private bool IsDue(CentralTransientPayloadReleaseItem item, DateTimeOffset now)
        => IsDue(new PendingItem(
            item.ReleaseId,
            item.Ordinal,
            item.Kind,
            item.RecordId,
            item.ReservationToken,
            item.RequestedAtUtc,
            item.RetryAtUtc,
            item.RetryCount,
            null), now);

    private bool IsDue(PendingItem item, DateTimeOffset now)
        => item.ReservationToken.HasValue
            ? item.RequestedAtUtc <= now - options.Value.ReservationLeaseTimeout
            : !item.RetryAtUtc.HasValue || item.RetryAtUtc <= now;

    private TimeSpan CalculateRetryDelay(int retryCount)
    {
        var exponent = Math.Min(30, Math.Max(0, retryCount - 1));
        var ticks = Math.Min(
            options.Value.MaximumRetryDelay.Ticks,
            options.Value.InitialRetryDelay.Ticks * Math.Pow(2, exponent));
        return TimeSpan.FromTicks((long)ticks);
    }

    private static bool IsCanonicalStorageReference(string? storageReference)
        => storageReference is not null && storageReference.Length <= 1024 &&
           storageReference.StartsWith(BucketPrefix, StringComparison.Ordinal) &&
           storageReference.Length > BucketPrefix.Length;

    private async Task<ObjectExistence> ProbeObjectExistenceAsync(
        string canonicalStorageReference,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Object existence cannot be probed inside a SQL transaction.");
        }
        try
        {
            _ = await minio.StatObjectAsync(new StatObjectArgs().WithBucket(Bucket)
                .WithObject(canonicalStorageReference[BucketPrefix.Length..]), cancellationToken).ConfigureAwait(false);
            return ObjectExistence.Present;
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return ObjectExistence.Absent;
        }
        catch (Exception exception) when (exception is MinioException or HttpRequestException or IOException
            or TimeoutException)
        {
            return ObjectExistence.Unknown;
        }
    }

    private static bool IsRetryableStorageFailure(Exception exception)
        => !IsTerminalStorageFailure(exception) &&
           exception is MinioException or HttpRequestException or IOException or TimeoutException;

    private static bool IsTerminalStorageFailure(Exception exception)
    {
        if (exception is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return true;
        }
        if (exception is not MinioException minioException)
        {
            return false;
        }
        var status = minioException.ServerResponse?.StatusCode;
        var code = minioException.Response?.Code;
        return status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
            || code is "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch"
                or "InvalidRequest" or "InvalidBucketName" or "NoSuchBucket" or "NotImplemented";
    }

    private static string GetStorageFailureReason(Exception exception)
    {
        if (exception is MinioException minioException)
        {
            return minioException.Response?.Code switch
            {
                "AccessDenied" => "transient-retention.storage-authorization",
                "InvalidAccessKeyId" or "SignatureDoesNotMatch" => "transient-retention.storage-authentication",
                "InvalidRequest" or "InvalidBucketName" => "transient-retention.invalid-storage-reference",
                "NoSuchBucket" => "transient-retention.storage-configuration",
                "NotImplemented" => "transient-retention.storage-unsupported",
                _ => "transient-retention.storage-terminal"
            };
        }
        return exception switch
        {
            ArgumentException => "transient-retention.invalid-storage-reference",
            NotSupportedException => "transient-retention.storage-unsupported",
            _ => "transient-retention.storage-configuration"
        };
    }

    private Task InvokeFaultAsync(
        CentralTransientPayloadReleaseFaultStage stage,
        Guid releaseId,
        int ordinal,
        CancellationToken cancellationToken)
        => faultInjector?.OnStageAsync(stage, releaseId, ordinal, cancellationToken) ?? Task.CompletedTask;

    private enum ReservationOutcome
    {
        Unavailable,
        Reserved,
        Preserved,
        RetryScheduled,
        Failed
    }

    private enum PreDeleteOutcome
    {
        Ready,
        Preserved,
        RetryScheduled,
        Failed
    }

    private enum FinalizationOutcome
    {
        Released,
        Failed,
        Unavailable
    }

    private enum RetryDisposition
    {
        Scheduled,
        Failed,
        Unavailable
    }

    private enum ObjectExistence
    {
        Present,
        Absent,
        Unknown
    }

    private sealed record PendingItem(
        Guid ReleaseId,
        int Ordinal,
        CentralTransientPayloadReleaseItemKind Kind,
        Guid RecordId,
        Guid? ReservationToken,
        DateTimeOffset? RequestedAtUtc,
        DateTimeOffset? RetryAtUtc,
        int RetryCount,
        string? StorageReference);

    private sealed record TargetSnapshot(string StorageReference, byte[] RowVersion, long Generation);

    private sealed record ReservationSnapshot(
        Guid ReleaseId,
        int Ordinal,
        CentralTransientPayloadReleaseItemKind Kind,
        Guid RecordId,
        Guid ReservationToken,
        DateTimeOffset RequestedAtUtc,
        string StorageReference,
        byte[] TargetRowVersion,
        long TargetGeneration,
        byte[] ItemRowVersion,
        int RetryCount);

    private sealed record ReservationResult(
        ReservationOutcome Outcome,
        ReservationSnapshot? Snapshot,
        int RetryCount);
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
