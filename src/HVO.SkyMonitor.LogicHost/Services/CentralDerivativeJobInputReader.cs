using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeJobInputs(
    IReadOnlyList<LogicHostProcessingInput> ProcessingInputs,
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
    Task<CentralDerivativeJobInputs> ReadAsync(
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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<CentralDerivativeJobInputs> ReadAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var leaseInputs = lease.Inputs is { Count: > 0 }
            ? lease.Inputs
            : [new CentralDerivativeJobLeaseInput(
                0, Guid.Empty, lease.SourceDevicePublicId, lease.SourceArtifactId, lease.SourceRole,
                lease.SourceRecipeVersion, lease.SourceChecksumSha256, lease.SourceMediaType, 0,
                lease.FrameId, lease.AgentId, null, lease.CapturedAtUtc, string.Empty)];
        var processingInputs = new List<LogicHostProcessingInput>(leaseInputs.Count);
        long totalBytes = 0;
        foreach (var leaseInput in leaseInputs.OrderBy(input => input.Ordinal))
        {
            var artifact = await LoadAuthorizedSourceAsync(lease, leaseInput, cancellationToken).ConfigureAwait(false);
            processingInputs.Add(await ReadArtifactAsync(lease, leaseInput, artifact, cancellationToken).ConfigureAwait(false));
            totalBytes = checked(totalBytes + artifact.ByteLength);
        }
        return new CentralDerivativeJobInputs(processingInputs, totalBytes);
    }

    private async Task<LogicHostProcessingInput> ReadArtifactAsync(
        CentralDerivativeJobLease lease,
        CentralDerivativeJobLeaseInput leaseInput,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
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
                lease.JobId, lease.LeaseToken, artifact.Id,
                artifact.RowVersion,
                "object.missing", quarantine: false, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            telemetry.RecordStage(
                "verify", lease.RecipeName, "integrity", timeProvider.GetElapsedTime(verifyStarted));
            await jobService.MarkInputUnavailableAsync(
                lease.JobId, lease.LeaseToken, artifact.Id,
                artifact.RowVersion,
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
                lease.JobId, lease.LeaseToken, artifact.Id,
                artifact.RowVersion,
                "object.missing", quarantine: false, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            telemetry.RecordStage(
                "load", lease.RecipeName, "integrity", timeProvider.GetElapsedTime(loadStarted));
            await jobService.MarkInputUnavailableAsync(
                lease.JobId, lease.LeaseToken, artifact.Id,
                artifact.RowVersion,
                exception.ReasonCode, quarantine: true, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        if (!await IsLeaseCurrentAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            throw new CentralDerivativeJobStateException("The derivative job lease became stale while loading input.");
        }
        if (artifact.Layout is null)
        {
            var evidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
                .SingleOrDefaultAsync(item => item.CentralArtifactId == artifact.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new CentralDerivativeInputRejectedException(
                    "A layoutless derivative input requires immutable processing evidence.");
            var compatibility = JsonSerializer.Deserialize<ProcessingCompatibilityIdentity>(
                evidence.CompatibilityJson,
                SerializerOptions)
                ?? throw new CentralDerivativeInputRejectedException(
                    "The layoutless derivative compatibility evidence is invalid.");
            return new LogicHostProcessingInput(
                Descriptor: null,
                payload,
                leaseInput.BindingName,
                new ProcessingArtifact(
                    artifact.ArtifactId,
                    artifact.Role,
                    artifact.Variant ?? string.Empty,
                    evidence.RecipeIdentitySha256,
                    artifact.MediaType,
                    Layout: null,
                    payload,
                    artifact.CreatedUtc ?? artifact.Frame!.CapturedAtUtc,
                    TimeSpan.FromTicks(evidence.TotalIntegrationTicks),
                    compatibility,
                    artifact.Frame!.CaptureSequence,
                    artifact.Sources.OrderBy(source => source.Ordinal)
                        .Select(source => source.SourceArtifactId).ToArray(),
                    artifact.Frame!.Timing!.ExposureStartedUtc,
                    ProcessingArtifact.ResolveObservationEndedUtc(
                        artifact.Frame.Timing.ExposureStartedUtc,
                        artifact.Frame.Timing.ExposureEndedUtc,
                        TimeSpan.FromTicks(evidence.TotalIntegrationTicks))));
        }
        return new LogicHostProcessingInput(
            CentralReconstructionDescriptorFactory.Create(artifact.Frame!, artifact),
            payload,
            leaseInput.BindingName);
    }

    private async Task<CentralArtifact> LoadAuthorizedSourceAsync(
        CentralDerivativeJobLease lease,
        CentralDerivativeJobLeaseInput leaseInput,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (leaseInput.CentralArtifactId == Guid.Empty)
        {
            return await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(candidate => candidate.Id == lease.JobId
                    && candidate.Status == CentralDerivativeJobStatus.Leased
                    && candidate.LeaseToken == lease.LeaseToken
                    && candidate.LeaseOwner == lease.WorkerId
                    && candidate.LeaseExpiresAtUtc > now
                    && candidate.SourceArtifact!.ArtifactId == lease.SourceArtifactId
                    && candidate.SourceArtifact.ObjectState == CentralArtifactObjectState.Available
                    && candidate.SourceArtifact.ReconstructionState == CentralReconstructionState.Complete)
                .Select(candidate => candidate.SourceArtifact!)
                .Include(artifact => artifact.Layout)
                .Include(artifact => artifact.Recipe)
                .Include(artifact => artifact.Sources)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Control)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
                .AsSplitQuery()
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new CentralDerivativeJobStateException("The derivative source or lease is stale or invalid.");
        }
        var job = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.Id == leaseInput.CentralArtifactId
                && artifact.ArtifactId == leaseInput.ArtifactId
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete
                && dbContext.CentralDerivativeJobs.Any(candidate => candidate.Id == lease.JobId
                    && candidate.Status == CentralDerivativeJobStatus.Leased
                    && candidate.LeaseToken == lease.LeaseToken
                    && candidate.LeaseOwner == lease.WorkerId
                    && candidate.LeaseExpiresAtUtc > now
                    && candidate.Inputs.Any(input => input.CentralArtifactId == artifact.Id
                        && input.Ordinal == leaseInput.Ordinal)))
            .Include(artifact => artifact.Layout)
            .Include(artifact => artifact.Recipe)
            .Include(artifact => artifact.Sources)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative source or lease is stale or invalid.");
        return job;
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
            && candidate.Inputs.Any()
            && !candidate.Inputs.Any(input => input.Artifact!.ObjectState != CentralArtifactObjectState.Available
                || input.Artifact.ReconstructionState != CentralReconstructionState.Complete),
            cancellationToken);
    }
}
