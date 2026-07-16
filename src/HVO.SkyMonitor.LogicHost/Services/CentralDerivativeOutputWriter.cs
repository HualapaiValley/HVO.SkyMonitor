using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeOutputWriter
{
    Task<Guid?> TryCompletePendingAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);

    Task<Guid> PersistAsync(
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken);
}

internal sealed partial class CentralDerivativeOutputWriter(
    ApplicationDbContext dbContext,
    IMinioClient minio,
    ICentralArtifactObjectReader objectReader,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralDerivativeOutputWriter> logger) : ICentralDerivativeOutputWriter
{
    private const string Bucket = "skymonitor-artifacts";
    private const string ManifestSchemaVersion = "central-v1";

    public async Task<Guid?> TryCompletePendingAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var evidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Sources)
            .SingleOrDefaultAsync(item => item.CentralDerivativeJobId == lease.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (evidence?.Artifact is not { } artifact
            || artifact.ObjectState is CentralArtifactObjectState.Quarantined or CentralArtifactObjectState.Expired
            || artifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            return null;
        }
        if (!HasExpectedSources(artifact, lease))
        {
            throw new CentralDerivativeJobStateException(
                "The pending derivative output does not match the frozen input set.");
        }
        try
        {
            _ = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
        catch (CentralArtifactMissingException)
        {
            return null;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            await QuarantineAsync(lease, artifact.Id, exception.ReasonCode, cancellationToken).ConfigureAwait(false);
            Log.Recovery(logger, lease.JobId, lease.AttemptCount, "quarantined", exception.ReasonCode);
            throw new CentralDerivativeOutputIntegrityException(exception.ReasonCode, exception);
        }
        var inputBytes = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Id == lease.JobId)
            .Select(job => job.Inputs.Sum(input => input.ByteLength))
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            lease, artifact.Id, inputBytes, artifact.ByteLength, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        Log.Recovery(logger, lease.JobId, lease.AttemptCount, "adopted", "derivative.output-recovered");
        return artifact.ArtifactId;
    }

    public async Task<Guid> PersistAsync(
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(product);
        ValidateProduct(lease, product);
        var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
        var objectKey = $"derivatives/{lease.SourceDevicePublicId:N}/{product.OutputIdentitySha256}.bin";
        var artifact = await EnsureIntentAsync(lease, product, artifactId, objectKey, cancellationToken)
            .ConfigureAwait(false);
        if (!await HasValidObjectAsync(lease, artifact, cancellationToken).ConfigureAwait(false))
        {
            await PublishAsync(product, objectKey, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            }
            catch (CentralArtifactMissingException exception)
            {
                throw new CentralArtifactStorageException(
                    "The published derivative output is not yet readable.", exception);
            }
            catch (CentralArtifactIntegrityException exception)
            {
                await QuarantineAsync(lease, artifact.Id, exception.ReasonCode, cancellationToken).ConfigureAwait(false);
                Log.Recovery(logger, lease.JobId, lease.AttemptCount, "quarantined", exception.ReasonCode);
                throw new CentralDerivativeOutputIntegrityException(exception.ReasonCode, exception);
            }
        }
        await CompleteAsync(lease, artifact.Id, inputBytes, product.Payload.Length, recipeDuration, cancellationToken)
            .ConfigureAwait(false);
        return artifactId;
    }

    private async Task<CentralArtifact> EnsureIntentAsync(
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        Guid artifactId,
        string objectKey,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var existing = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Sources)
            .SingleOrDefaultAsync(item => item.DevicePublicId == lease.SourceDevicePublicId
                && item.OutputIdentitySha256 == product.OutputIdentitySha256, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateExisting(existing, lease, product, artifactId, objectKey);
            return existing.Artifact!;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, lease.JobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var job = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(candidate => candidate.Inputs).ThenInclude(input => input.Artifact)
            .SingleOrDefaultAsync(candidate => candidate.Id == lease.JobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == lease.LeaseToken
                && candidate.LeaseOwner == lease.WorkerId
                && candidate.LeaseExpiresAtUtc > now,
                cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        var source = job.SourceArtifact!;
        var resolvedInputs = job.Inputs.ToDictionary(input => input.Artifact!.ArtifactId);
        var artifact = new CentralArtifact
        {
            CentralFrameId = source.CentralFrameId,
            Frame = source.Frame,
            DevicePublicId = source.Frame!.DevicePublicId,
            ArtifactId = artifactId,
            Role = product.Role,
            Variant = product.Variant,
            RecipeVersion = job.TargetRecipeVersion,
            ManifestSchemaVersion = ManifestSchemaVersion,
            MediaType = product.MediaType,
            ByteLength = product.Payload.Length,
            ChecksumSha256 = product.ChecksumSha256,
            StorageReference = $"minio://{Bucket}/{objectKey}",
            ReceivedAtUtc = now,
            IdempotencyKey = CreateArtifactIdempotencyKey(
                lease.SourceDevicePublicId, product.OutputIdentitySha256),
            SourceId = "central-derivative-worker",
            CreatedUtc = now,
            ObjectState = CentralArtifactObjectState.Pending,
            ReconstructionState = CentralReconstructionState.Complete,
            StateReasonCode = "derivative.output-pending",
            Recipe = new CentralArtifactRecipe
            {
                Name = product.Recipe.Descriptor.Name,
                SemanticVersion = product.Recipe.Descriptor.SemanticVersion,
                ImplementationVersion = product.Recipe.Descriptor.ImplementationVersion,
                OptionsJson = CaptureContractJson.Canonicalize(product.Recipe.Descriptor.Options).GetRawText(),
                OptionsSha256 = product.Recipe.Descriptor.OptionsSha256
            }
        };
        if (product.Layout is { } layout)
        {
            artifact.Layout = CreateLayout(layout);
        }
        for (var ordinal = 0; ordinal < product.SourceArtifactIds.Count; ordinal++)
        {
            var sourceArtifactId = product.SourceArtifactIds[ordinal];
            var resolvedId = resolvedInputs.TryGetValue(sourceArtifactId, out var resolvedInput)
                ? resolvedInput.CentralArtifactId
                : throw new CentralDerivativeJobStateException("A derivative output source is not part of the frozen input set.");
            artifact.Sources.Add(new CentralArtifactSource
            {
                Ordinal = ordinal,
                SourceArtifactId = sourceArtifactId,
                ResolvedCentralArtifactId = resolvedId
            });
        }
        dbContext.CentralArtifacts.Add(artifact);
        dbContext.CentralArtifactProcessingEvidence.Add(new CentralArtifactProcessingEvidence
        {
            CentralArtifactId = artifact.Id,
            DevicePublicId = lease.SourceDevicePublicId,
            Artifact = artifact,
            OutputIdentitySha256 = product.OutputIdentitySha256,
            RequestedRecipeIdentitySha256 = job.RequestedRecipeIdentitySha256,
            RecipeIdentitySha256 = product.Recipe.IdentitySha256,
            AlgorithmsJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(product.Algorithms)).GetRawText(),
            CompatibilityJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(product.Compatibility)).GetRawText(),
            TotalIntegrationTicks = product.TotalIntegration.Ticks,
            CentralDerivativeJobId = job.Id,
            AttemptNumber = job.AttemptCount,
            CreatedAtUtc = now
        });
        // Fences invalidation that loaded this job before output publication began.
        job.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        return artifact;
    }

    private async Task<bool> HasValidObjectAsync(
        CentralDerivativeJobLease lease,
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await objectReader.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CentralArtifactMissingException)
        {
            return false;
        }
        catch (CentralArtifactIntegrityException exception)
        {
            await QuarantineAsync(lease, artifact.Id, exception.ReasonCode, cancellationToken).ConfigureAwait(false);
            Log.Recovery(logger, lease.JobId, lease.AttemptCount, "quarantined", exception.ReasonCode);
            throw new CentralDerivativeOutputIntegrityException(exception.ReasonCode, exception);
        }
    }

    private async Task PublishAsync(ProcessingProduct product, string objectKey, CancellationToken cancellationToken)
    {
        var stagingKey = $"staging/derivatives/{Guid.NewGuid():N}";
        try
        {
            await using var payload = new MemoryStream(product.Payload.ToArray(), writable: false);
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(Bucket)
                .WithObject(stagingKey)
                .WithStreamData(payload)
                .WithObjectSize(product.Payload.Length)
                .WithContentType(product.MediaType), cancellationToken).ConfigureAwait(false);
            var source = new CopySourceObjectArgs().WithBucket(Bucket).WithObject(stagingKey);
            await minio.CopyObjectAsync(new CopyObjectArgs()
                .WithBucket(Bucket)
                .WithObject(objectKey)
                .WithCopyObjectSource(source), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(Bucket).WithObject(stagingKey), CancellationToken.None).ConfigureAwait(false);
            }
            catch (MinioException)
            {
                // The reconciliation service removes stale staging objects.
            }
        }
    }

    private async Task CompleteAsync(
        CentralDerivativeJobLease lease,
        Guid centralArtifactId,
        long inputBytes,
        long outputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartStage("complete", lease.RecipeName);
        var started = timeProvider.GetTimestamp();
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var pinStartedAtUtc = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.CentralDerivativeJobId == lease.JobId)
            .MinAsync(input => (DateTimeOffset?)input.SelectedAtUtc, cancellationToken).ConfigureAwait(false);
        var artifact = await dbContext.CentralArtifacts.SingleAsync(
            candidate => candidate.Id == centralArtifactId, cancellationToken).ConfigureAwait(false);
        if (artifact.ObjectState is CentralArtifactObjectState.Expired or CentralArtifactObjectState.Quarantined)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The derivative output is unavailable and cannot be completed.");
        }
        var jobAffected = await dbContext.CentralDerivativeJobs.Where(job => job.Id == lease.JobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == lease.LeaseToken
                && job.LeaseOwner == lease.WorkerId
                && job.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(job => job.ResultCentralArtifactId, centralArtifactId)
                .SetProperty(job => job.CompletedAtUtc, now)
                .SetProperty(job => job.UpdatedAtUtc, now)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        var attemptAffected = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                attempt.CentralDerivativeJobId == lease.JobId
                && attempt.AttemptNumber == lease.AttemptCount
                && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Outcome, CentralDerivativeAttemptOutcome.Completed)
                .SetProperty(attempt => attempt.EndedAtUtc, now)
                .SetProperty(attempt => attempt.InputBytes, inputBytes)
                .SetProperty(attempt => attempt.OutputBytes, outputBytes)
                .SetProperty(attempt => attempt.RecipeDurationTicks, recipeDuration.Ticks), cancellationToken)
            .ConfigureAwait(false);
        if (jobAffected != 1 || attemptAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease became stale before output completion.");
        }
        artifact.ObjectState = CentralArtifactObjectState.Available;
        artifact.StateReasonCode = null;
        artifact.ReconciledAtUtc = now;
        var predecessorId = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Id == lease.JobId)
            .Select(job => job.PredecessorJobId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (predecessorId.HasValue)
        {
            var predecessorAffected = await dbContext.CentralDerivativeJobs.Where(job =>
                    job.Id == predecessorId.Value
                    && job.Status != CentralDerivativeJobStatus.Superseded)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.Superseded)
                    .SetProperty(job => job.SupersededByJobId, lease.JobId)
                    .SetProperty(job => job.UpdatedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
            if (predecessorAffected != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CentralDerivativeJobStateException(
                    "The derivative predecessor could not be superseded after replacement completion.");
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordStage(
            "complete", lease.RecipeName, "completed", timeProvider.GetElapsedTime(started));
        if (lease.Inputs is { Count: > 1 } && pinStartedAtUtc.HasValue)
        {
            telemetry.RecordWindowPinDuration(lease.RecipeName, now - pinStartedAtUtc.Value);
        }
        dbContext.ChangeTracker.Clear();
    }

    private async Task QuarantineAsync(
        CentralDerivativeJobLease lease,
        Guid centralArtifactId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var pinStartedAtUtc = lease.Inputs is { Count: > 1 }
            ? lease.Inputs.Min(input => input.SelectedAtUtc)
            : default;
        var artifact = await dbContext.CentralArtifacts.SingleAsync(
            candidate => candidate.Id == centralArtifactId, cancellationToken).ConfigureAwait(false);
        var jobAffected = await dbContext.CentralDerivativeJobs.Where(job => job.Id == lease.JobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == lease.LeaseToken
                && job.LeaseOwner == lease.WorkerId
                && job.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Quarantined)
                .SetProperty(job => job.LastFailedAtUtc, now)
                .SetProperty(job => job.LastError, reasonCode)
                .SetProperty(job => job.UpdatedAtUtc, now)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        var attemptAffected = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                attempt.CentralDerivativeJobId == lease.JobId
                && attempt.AttemptNumber == lease.AttemptCount
                && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Outcome, CentralDerivativeAttemptOutcome.Quarantined)
                .SetProperty(attempt => attempt.ReasonCode, reasonCode)
                .SetProperty(attempt => attempt.EndedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (jobAffected != 1 || attemptAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease became stale before quarantine.");
        }
        artifact.ObjectState = CentralArtifactObjectState.Quarantined;
        artifact.ReconstructionState = CentralReconstructionState.Quarantined;
        artifact.StateReasonCode = reasonCode;
        artifact.ReconciledAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (pinStartedAtUtc != default)
        {
            telemetry.RecordWindowPinDuration(lease.RecipeName, now - pinStartedAtUtc, "quarantined");
        }
        dbContext.ChangeTracker.Clear();
    }

    private static CentralArtifactLayout CreateLayout(FrameLayoutDescriptor layout) => new()
    {
        Width = layout.Width,
        Height = layout.Height,
        StrideBytes = layout.StrideBytes,
        PixelFormat = layout.PixelFormat.ToString(),
        ByteOrder = layout.ByteOrder.ToString(),
        SampleDepthBits = layout.SampleDepthBits,
        ContainerDepthBits = layout.ContainerDepthBits,
        Packing = layout.Packing.ToString(),
        CfaPattern = layout.CfaPattern.ToString(),
        BlackLevel = layout.BlackLevel,
        WhiteLevel = layout.WhiteLevel,
        ByteLength = layout.ByteLength
    };

    internal static string CreateArtifactIdempotencyKey(Guid devicePublicId, string outputIdentitySha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIdentitySha256);
        var value = string.Join('\n',
            "hvo-central-derivative-artifact-idempotency-v1",
            devicePublicId.ToString("N"),
            outputIdentitySha256.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void ValidateProduct(CentralDerivativeJobLease lease, ProcessingProduct product)
    {
        if (product.Role != lease.TargetRole
            || product.Variant != lease.TargetVariant
            || !product.SourceArtifactIds.SequenceEqual(
                lease.Inputs is { Count: > 0 }
                    ? lease.Inputs.OrderBy(input => input.Ordinal).Select(input => input.ArtifactId)
                    : [lease.SourceArtifactId])
            || product.Payload.Length != product.Layout?.ByteLength && product.Layout is not null
            || !string.Equals(ProcessingIdentity.ComputePayloadSha256(product.Payload), product.ChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDerivativeJobStateException("The processing product does not satisfy the derivative job identity.");
        }
    }

    private static void ValidateExisting(
        CentralArtifactProcessingEvidence evidence,
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        Guid artifactId,
        string objectKey)
    {
        var artifact = evidence.Artifact!;
        if (evidence.CentralDerivativeJobId != lease.JobId
            || evidence.RecipeIdentitySha256 != product.Recipe.IdentitySha256
            || artifact.ArtifactId != artifactId
            || artifact.Role != product.Role
            || artifact.Variant != product.Variant
            || artifact.RecipeVersion != lease.TargetRecipeVersion
            || !HasExpectedSources(artifact, lease)
            || artifact.ByteLength != product.Payload.Length
            || !string.Equals(artifact.ChecksumSha256, product.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            || artifact.StorageReference != $"minio://{Bucket}/{objectKey}")
        {
            throw new CentralDerivativeJobStateException("The derivative output identity conflicts with existing evidence.");
        }
    }

    private static bool HasExpectedSources(CentralArtifact artifact, CentralDerivativeJobLease lease)
    {
        if (lease.Inputs is not { Count: > 0 })
        {
            return artifact.Sources.Count == 1
                && artifact.Sources.Single().SourceArtifactId == lease.SourceArtifactId;
        }
        var expected = lease.Inputs.OrderBy(input => input.Ordinal)
            .Select(input => (input.ArtifactId, CentralArtifactId: (Guid?)input.CentralArtifactId));
        return artifact.Sources.OrderBy(source => source.Ordinal)
            .Select(source => (source.SourceArtifactId, source.ResolvedCentralArtifactId))
            .SequenceEqual(expected);
    }

    private static partial class Log
    {
        [LoggerMessage(2134, LogLevel.Information,
            "Central derivative pending output action: JobId={JobId}, Attempt={Attempt}, Action={Action}, Reason={Reason}")]
        public static partial void Recovery(
            ILogger logger, Guid jobId, int attempt, string action, string reason);
    }
}

internal sealed class CentralDerivativeOutputIntegrityException : Exception
{
    public CentralDerivativeOutputIntegrityException()
        : this("object.integrity-failed")
    {
    }

    public CentralDerivativeOutputIntegrityException(string reasonCode)
        : base("The derivative output failed integrity verification.")
    {
        ReasonCode = reasonCode;
    }

    public CentralDerivativeOutputIntegrityException(string reasonCode, Exception innerException)
        : base("The derivative output failed integrity verification.", innerException)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
