using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeJobInput(
    LogicHostProcessingInput ProcessingInput,
    long ByteLength);

internal sealed class CentralDerivativeInputRejectedException : Exception
{
    public CentralDerivativeInputRejectedException()
    {
    }

    public CentralDerivativeInputRejectedException(string message) : base(message)
    {
    }

    public CentralDerivativeInputRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal interface ICentralDerivativeJobInputReader
{
    Task<CentralDerivativeJobInput> ReadAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobInputReader(
    ApplicationDbContext dbContext,
    ICentralArtifactObjectReader objectReader,
    ICentralDerivativeJobService jobService,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider) : ICentralDerivativeJobInputReader
{
    public async Task<CentralDerivativeJobInput> ReadAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var artifact = await LoadAuthorizedSourceAsync(lease, cancellationToken).ConfigureAwait(false);
        if (artifact.ByteLength is < 0 or > int.MaxValue)
        {
            throw new CentralDerivativeInputRejectedException(
                "The derivative source is too large for shared recipe execution.");
        }

        CentralArtifactObjectSnapshot snapshot;
        var verifyStarted = timeProvider.GetTimestamp();
        try
        {
            using (telemetry.StartStage("verify", lease.RecipeName))
            {
                snapshot = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
            telemetry.RecordStage(
                "verify", lease.RecipeName, "completed", timeProvider.GetElapsedTime(verifyStarted));
        }
        catch (CentralArtifactMissingException)
        {
            telemetry.RecordStage(
                "verify", lease.RecipeName, "missing", timeProvider.GetElapsedTime(verifyStarted));
            await jobService.MarkInputUnavailableAsync(
                lease.JobId, lease.LeaseToken, artifact.RowVersion,
                "object.missing", quarantine: false, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            telemetry.RecordStage(
                "verify", lease.RecipeName, "integrity", timeProvider.GetElapsedTime(verifyStarted));
            await jobService.MarkInputUnavailableAsync(
                lease.JobId, lease.LeaseToken, artifact.RowVersion,
                exception.ReasonCode, quarantine: true, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        if (!await IsLeaseCurrentAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            throw new CentralDerivativeJobStateException("The derivative job lease became stale during input verification.");
        }
        var payload = GC.AllocateUninitializedArray<byte>((int)artifact.ByteLength);
        var loadStarted = timeProvider.GetTimestamp();
        try
        {
            await using var destination = new MemoryStream(payload, writable: true);
            using (telemetry.StartStage("load", lease.RecipeName))
            {
                await objectReader.CopyToAsync(snapshot, destination, range: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (destination.Position != artifact.ByteLength)
            {
                throw new CentralArtifactIntegrityException("object.length-mismatch", snapshot.StorageETag);
            }
            telemetry.RecordStage(
                "load", lease.RecipeName, "completed", timeProvider.GetElapsedTime(loadStarted), artifact.ByteLength);
        }
        catch (CentralArtifactMissingException)
        {
            telemetry.RecordStage(
                "load", lease.RecipeName, "missing", timeProvider.GetElapsedTime(loadStarted));
            await jobService.MarkInputUnavailableAsync(
                lease.JobId, lease.LeaseToken, artifact.RowVersion,
                "object.missing", quarantine: false, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            telemetry.RecordStage(
                "load", lease.RecipeName, "integrity", timeProvider.GetElapsedTime(loadStarted));
            await jobService.MarkInputUnavailableAsync(
                lease.JobId, lease.LeaseToken, artifact.RowVersion,
                exception.ReasonCode, quarantine: true, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        if (!await IsLeaseCurrentAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            throw new CentralDerivativeJobStateException("The derivative job lease became stale while loading input.");
        }
        return new CentralDerivativeJobInput(
            new LogicHostProcessingInput(
                CentralReconstructionDescriptorFactory.Create(artifact.Frame!, artifact),
                payload),
            artifact.ByteLength);
    }

    private async Task<CentralArtifact> LoadAuthorizedSourceAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var job = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(candidate => candidate.Id == lease.JobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == lease.LeaseToken
                && candidate.LeaseOwner == lease.WorkerId
                && candidate.LeaseExpiresAtUtc > now
                && candidate.SourceArtifact!.ArtifactId == lease.SourceArtifactId
                && candidate.SourceArtifact.ObjectState == CentralArtifactObjectState.Available
                && candidate.SourceArtifact.ReconstructionState == CentralReconstructionState.Complete)
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Layout)
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Recipe)
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Sources)
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)!.ThenInclude(frame => frame!.Profiles)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative source or lease is stale or invalid.");
        return job.SourceArtifact!;
    }

    private Task<bool> IsLeaseCurrentAsync(CentralDerivativeJobLease lease, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return dbContext.CentralDerivativeJobs.AsNoTracking().AnyAsync(candidate =>
            candidate.Id == lease.JobId
            && candidate.Status == CentralDerivativeJobStatus.Leased
            && candidate.LeaseToken == lease.LeaseToken
            && candidate.LeaseOwner == lease.WorkerId
            && candidate.LeaseExpiresAtUtc > now
            && candidate.SourceArtifact!.ArtifactId == lease.SourceArtifactId
            && candidate.SourceArtifact.ObjectState == CentralArtifactObjectState.Available
            && candidate.SourceArtifact.ReconstructionState == CentralReconstructionState.Complete,
            cancellationToken);
    }
}
