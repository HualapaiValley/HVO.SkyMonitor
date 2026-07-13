using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobService
{
    Task<CentralDerivativeJobLease?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<CentralDerivativeJobLease> RenewLeaseAsync(Guid jobId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task CompleteAsync(Guid jobId, Guid leaseToken, Guid resultArtifactId, CancellationToken cancellationToken);

    Task FailAsync(Guid jobId, Guid leaseToken, string error, bool retryable, CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : ICentralDerivativeJobService
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);

    public async Task<CentralDerivativeJobLease?> ClaimNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
        ValidateLeaseDuration(leaseDuration);
        var initialNow = timeProvider.GetUtcNow();
        await dbContext.CentralDerivativeJobs.Where(job =>
                job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseExpiresAtUtc <= initialNow
                && job.AttemptCount >= job.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.LastFailedAtUtc, initialNow)
                .SetProperty(job => job.LastError, "The derivative job lease expired after the maximum number of attempts.")
                .SetProperty(job => job.UpdatedAtUtc, initialNow)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken).ConfigureAwait(false);
        for (var collision = 0; collision < 100; collision++)
        {
            var now = timeProvider.GetUtcNow();
            var candidate = await dbContext.CentralDerivativeJobs
                .Include(job => job.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
                .Where(job => job.AttemptCount < job.MaxAttempts
                    && ((job.Status == CentralDerivativeJobStatus.Pending
                            || job.Status == CentralDerivativeJobStatus.RetryableFailure)
                        && job.AvailableAtUtc <= now
                        || job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc <= now))
                .OrderBy(job => job.Status == CentralDerivativeJobStatus.Leased ? job.LeaseExpiresAtUtc : job.AvailableAtUtc)
                .ThenBy(job => job.CreatedAtUtc)
                .ThenBy(job => job.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            candidate.Status = CentralDerivativeJobStatus.Leased;
            candidate.AttemptCount++;
            candidate.LeaseOwner = workerId;
            candidate.LeaseToken = Guid.NewGuid();
            candidate.LeaseAcquiredAtUtc = now;
            candidate.LeaseExpiresAtUtc = now + leaseDuration;
            candidate.AvailableAtUtc = null;
            candidate.UpdatedAtUtc = now;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return CreateLease(candidate);
            }
            catch (DbUpdateConcurrencyException)
            {
                dbContext.ChangeTracker.Clear();
            }
        }
        throw new CentralDerivativeJobStateException("Unable to claim a derivative job because of sustained concurrency.");
    }

    public async Task<CentralDerivativeJobLease> RenewLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLeaseDuration(leaseDuration);
        var now = timeProvider.GetUtcNow();
        var affected = await dbContext.CentralDerivativeJobs.Where(job =>
                job.Id == jobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == leaseToken
                && job.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseExpiresAtUtc, now + leaseDuration)
                .SetProperty(job => job.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        dbContext.ChangeTracker.Clear();
        return CreateLease(await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false));
    }

    public async Task CompleteAsync(
        Guid jobId,
        Guid leaseToken,
        Guid resultArtifactId,
        CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.Status == CentralDerivativeJobStatus.Completed)
        {
            if (job.ResultArtifact?.ArtifactId == resultArtifactId)
            {
                return;
            }
            throw new CentralDerivativeJobStateException("The derivative job is already completed with a different artifact.");
        }

        var result = await dbContext.CentralArtifacts.SingleOrDefaultAsync(
            artifact => artifact.ArtifactId == resultArtifactId
                && artifact.CentralFrameId == job.SourceArtifact!.CentralFrameId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative result artifact does not exist.");
        if (result.CentralFrameId != job.SourceArtifact!.CentralFrameId
            || result.Role != job.TargetRole
            || result.RecipeVersion != job.TargetRecipeVersion)
        {
            throw new CentralDerivativeJobStateException("The derivative result does not satisfy the job target identity.");
        }

        var now = timeProvider.GetUtcNow();
        var affected = await dbContext.CentralDerivativeJobs.Where(candidate =>
                candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(candidate => candidate.ResultCentralArtifactId, result.Id)
                .SetProperty(candidate => candidate.CompletedAtUtc, now)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken).ConfigureAwait(false);
        if (affected == 1)
        {
            dbContext.ChangeTracker.Clear();
            return;
        }
        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.CentralDerivativeJobs.Include(candidate => candidate.ResultArtifact)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (completed?.Status == CentralDerivativeJobStatus.Completed
            && completed.ResultArtifact?.ArtifactId == resultArtifactId)
        {
            return;
        }
        throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
    }

    public async Task FailAsync(
        Guid jobId,
        Guid leaseToken,
        string error,
        bool retryable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var terminal = !retryable || job.AttemptCount >= job.MaxAttempts;
        var nextStatus = terminal ? CentralDerivativeJobStatus.TerminalFailure : CentralDerivativeJobStatus.RetryableFailure;
        var availableAtUtc = terminal
            ? (DateTimeOffset?)null
            : now + CalculateRetryDelay(job.AttemptCount, InitialRetryDelay, MaximumRetryDelay);
        var lastError = error.Length <= 2048 ? error : error[..2048];
        var affected = await dbContext.CentralDerivativeJobs.Where(candidate =>
                candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, nextStatus)
                .SetProperty(candidate => candidate.AvailableAtUtc, availableAtUtc)
                .SetProperty(candidate => candidate.LastFailedAtUtc, now)
                .SetProperty(candidate => candidate.LastError, lastError)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        dbContext.ChangeTracker.Clear();
    }

    internal static TimeSpan CalculateRetryDelay(int attempt, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, initialDelay);
        var multiplier = 1L << Math.Min(attempt - 1, 30);
        var ticks = initialDelay.Ticks > maximumDelay.Ticks / multiplier
            ? maximumDelay.Ticks
            : initialDelay.Ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }

    private async Task<CentralDerivativeJob> LoadJobAsync(Guid jobId, CancellationToken cancellationToken)
        => await dbContext.CentralDerivativeJobs
            .Include(job => job.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(job => job.ResultArtifact)
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration < MinimumLeaseDuration || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static CentralDerivativeJobLease CreateLease(CentralDerivativeJob job)
    {
        var source = job.SourceArtifact ?? throw new InvalidOperationException("The derivative source artifact was not loaded.");
        var frame = source.Frame ?? throw new InvalidOperationException("The derivative source frame was not loaded.");
        return new CentralDerivativeJobLease(
            job.Id, job.LeaseToken!.Value, job.LeaseExpiresAtUtc!.Value,
            source.ArtifactId, source.Role, source.RecipeVersion, source.StorageReference,
            source.ChecksumSha256, source.MediaType, frame.FrameId, frame.AgentId,
            frame.CapturedAtUtc, frame.RigProfileVersion, frame.SceneProvenanceJson,
            job.TargetRole, job.TargetRecipeVersion, job.AttemptCount, job.MaxAttempts);
    }
}

internal sealed record CentralDerivativeJobLease(
    Guid JobId,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc,
    Guid SourceArtifactId,
    FrameArtifactRole SourceRole,
    string SourceRecipeVersion,
    string SourceStorageReference,
    string SourceChecksumSha256,
    string SourceMediaType,
    Guid FrameId,
    string AgentId,
    DateTimeOffset CapturedAtUtc,
    int? RigProfileVersion,
    string? SceneProvenanceJson,
    FrameArtifactRole TargetRole,
    string TargetRecipeVersion,
    int AttemptCount,
    int MaxAttempts);

internal sealed class CentralDerivativeJobStateException : Exception
{
    public CentralDerivativeJobStateException()
    {
    }

    public CentralDerivativeJobStateException(string message) : base(message)
    {
    }

    public CentralDerivativeJobStateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
