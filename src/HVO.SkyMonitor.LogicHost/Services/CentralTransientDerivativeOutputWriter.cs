using System.Data;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientDerivativeOutputWriter
{
    Task<bool> IsCommittedAsync(CentralDerivativeJobLease lease, CancellationToken cancellationToken);

    Task PersistAsync(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeBundle bundle,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientDerivativeOutputWriter(
    ApplicationDbContext dbContext,
    IMinioClient minio,
    ICentralTransientEventVersionAppender versionAppender,
    TimeProvider timeProvider,
    CentralTransientLifecycleTelemetry? telemetry = null) : ICentralTransientDerivativeOutputWriter
{
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";
    private static readonly TransientDerivativeLimitation[] Limitations =
    [
        TransientDerivativeLimitation.IntraExposureTimingUnavailable,
        TransientDerivativeLimitation.SaturatedPhotometryUnrecoverable
    ];

    public async Task<bool> IsCommittedAsync(CentralDerivativeJobLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var intents = await dbContext.CentralTransientDerivativeJobs.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == lease.JobId && item.CommittedAtUtc != null)
            .SelectMany(item => item.OutputIntents.Where(intent => intent.CommittedAtUtc != null))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (intents.Length != 5)
        {
            return false;
        }
        foreach (var intent in intents)
        {
            var storageETag = await HasValidObjectAsync(intent, cancellationToken).ConfigureAwait(false);
            if (storageETag is null || !string.Equals(
                    storageETag, intent.StorageETag, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    public async Task PersistAsync(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeBundle bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(bundle);
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry?.Start("derivative");
        var outputs = CreateOutputs(lease, bundle);
        var objectLocks = new List<CentralObjectApplicationLock>(outputs.Count);
        try
        {
            foreach (var storageReference in CreateStorageReferences(outputs))
            {
                objectLocks.Add(await CentralObjectApplicationLock.AcquireAsync(
                    dbContext, storageReference, cancellationToken).ConfigureAwait(false));
            }
            await EnsureOutputsNotRetiredOrExpireOwnIntentsAsync(
                lease.JobId, outputs, cancellationToken).ConfigureAwait(false);
            var intents = await EnsureIntentsAsync(lease, outputs, cancellationToken).ConfigureAwait(false);
            await EnsureOutputsNotRetiredOrExpireOwnIntentsAsync(
                lease.JobId, outputs, cancellationToken).ConfigureAwait(false);
            var adopted = intents.Any(intent => intent.ObjectState == CentralArtifactObjectState.Available);
            foreach (var output in outputs)
            {
                var intent = intents.Single(item => item.Kind == output.Kind);
                _ = await PublishOneUnderLockAsync(lease, intent, output.Payload, cancellationToken)
                    .ConfigureAwait(false);
                telemetry?.RecordDerivative(
                    output.Kind.ToString(),
                    intent.ObjectState == CentralArtifactObjectState.Available ? "adopted" : "persisted",
                    output.Payload.Length);
            }
            await EnsureOutputsNotRetiredOrExpireOwnIntentsAsync(
                lease.JobId, outputs, cancellationToken).ConfigureAwait(false);
            await CommitBundleAsync(lease, bundle, cancellationToken).ConfigureAwait(false);
            telemetry?.RecordDerivativeBundle(
                adopted ? "adopted" : "persisted", outputs.Count, timeProvider.GetElapsedTime(started));
        }
        catch (CentralDerivativeJobStateException)
        {
            await ExpireOwnIntentsIfRetiredAsync(lease.JobId, outputs).ConfigureAwait(false);
            throw;
        }
        finally
        {
            for (var index = objectLocks.Count - 1; index >= 0; index--)
            {
                await objectLocks[index].DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<string> PublishOneUnderLockAsync(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeOutputIntent intent,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await EnsureNotRetiredAsync(intent.StorageReference, cancellationToken).ConfigureAwait(false);
        var storageETag = await HasValidObjectAsync(intent, cancellationToken).ConfigureAwait(false);
        if (storageETag is null)
        {
            await PublishAsync(intent, payload, cancellationToken).ConfigureAwait(false);
            storageETag = await HasValidObjectAsync(intent, cancellationToken).ConfigureAwait(false);
            if (storageETag is null)
            {
                throw new CentralArtifactStorageException("The transient derivative output is not readable after publication.");
            }
        }
        if (intent.ObjectState == CentralArtifactObjectState.Pending)
        {
            await MarkVerifiedAsync(
                    lease, intent.Id, intent.StorageReference, intent.RowVersion, storageETag, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (intent.ObjectState != CentralArtifactObjectState.Available
            || intent.ObjectVerifiedAtUtc is null
            || string.IsNullOrWhiteSpace(intent.StorageETag)
            || !string.Equals(intent.StorageETag, storageETag, StringComparison.Ordinal))
        {
            throw new CentralDerivativeJobStateException(
                "The verified transient derivative output cannot be adopted.");
        }
        return storageETag;
    }

    private async Task<IReadOnlyList<CentralTransientDerivativeOutputIntent>> EnsureIntentsAsync(
        CentralDerivativeJobLease lease,
        IReadOnlyList<Output> outputs,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, lease.JobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var job = await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(item =>
            item.Id == lease.JobId && item.Status == CentralDerivativeJobStatus.Leased &&
            item.LeaseToken == lease.LeaseToken && item.LeaseOwner == lease.WorkerId &&
            item.LeaseExpiresAtUtc > now, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The transient derivative lease is stale or invalid.");
        var derivativeJob = await dbContext.CentralTransientDerivativeJobs
            .Include(item => item.OutputIntents)
            .SingleAsync(item => item.CentralDerivativeJobId == lease.JobId, cancellationToken).ConfigureAwait(false);
        foreach (var output in outputs)
        {
            var existing = derivativeJob.OutputIntents.SingleOrDefault(item => item.Kind == output.Kind);
            if (existing is not null)
            {
                ValidateIntent(existing, output);
                continue;
            }
            _ = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientDerivativeOutputIntents]
                    ([Id], [CentralDerivativeJobId], [CentralTransientEventId], [Kind], [DerivativeId],
                     [ArtifactId], [ArtifactRole], [ArtifactVariant], [MediaType], [ByteLength],
                     [ChecksumSha256], [OutputIdentitySha256], [StorageReference], [ObjectState],
                     [StateReasonCode], [CreatedAtUtc])
                VALUES
                    ({Guid.NewGuid()}, {lease.JobId}, {derivativeJob.CentralTransientEventId}, {output.Kind.ToString()},
                     {output.DerivativeId}, {output.ArtifactId}, {output.Role.ToString()}, {output.Variant},
                     {output.MediaType}, {output.Payload.Length}, {output.ChecksumSha256},
                     {output.OutputIdentitySha256}, {$"{BucketPrefix}{output.ObjectKey}"},
                     {CentralArtifactObjectState.Pending.ToString()}, N'transient-derivative.output-pending',
                     {derivativeJob.CreatedAtUtc});
                """, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        return await dbContext.CentralTransientDerivativeOutputIntents.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == lease.JobId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkVerifiedAsync(
        CentralDerivativeJobLease lease,
        Guid intentId,
        string storageReference,
        byte[] expectedRowVersion,
        string storageETag,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await EnsureNotRetiredAsync(storageReference, cancellationToken).ConfigureAwait(false);
        var storageReferenceSha256 = SHA256.HashData(Encoding.Unicode.GetBytes(storageReference));
        var now = timeProvider.GetUtcNow();
        var affected = await dbContext.CentralTransientDerivativeOutputIntents.Where(intent =>
                intent.Id == intentId
                && intent.CentralDerivativeJobId == lease.JobId
                && intent.ObjectState == CentralArtifactObjectState.Pending
                && intent.CommittedAtUtc == null
                && intent.RowVersion == expectedRowVersion
                && EF.Functions.Collate(intent.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                    == storageReference
                && !dbContext.CentralArtifacts.Any(artifact =>
                    (artifact.RetentionDeletionToken != null
                        || artifact.ObjectState == CentralArtifactObjectState.Expired)
                    && EF.Functions.Collate(
                        artifact.StorageReference,
                        CentralObjectOwnershipFence.BinaryCollation) == storageReference)
                && !dbContext.CentralTransientDerivativeOutputIntents.Any(retired =>
                    retired.Id != intentId
                    && EF.Property<byte[]>(retired, "StorageReferenceSha256") == storageReferenceSha256
                    && retired.ObjectState == CentralArtifactObjectState.Expired
                    && EF.Functions.Collate(
                        retired.StorageReference,
                        CentralObjectOwnershipFence.BinaryCollation) == storageReference)
                && dbContext.CentralDerivativeJobs.Any(job =>
                    job.Id == lease.JobId && job.Status == CentralDerivativeJobStatus.Leased &&
                    job.LeaseToken == lease.LeaseToken && job.LeaseOwner == lease.WorkerId &&
                    job.LeaseExpiresAtUtc > now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(intent => intent.ObjectState, CentralArtifactObjectState.Available)
                .SetProperty(intent => intent.StateReasonCode, (string?)null)
                .SetProperty(intent => intent.StorageETag, storageETag)
                .SetProperty(intent => intent.ObjectVerifiedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The transient derivative lease became stale during publication.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private async Task EnsureNotRetiredAsync(string storageReference, CancellationToken cancellationToken)
    {
        if (await CentralObjectOwnershipFence.IsRetiredAsync(
                dbContext, storageReference, cancellationToken).ConfigureAwait(false))
        {
            throw new CentralDerivativeJobStateException(
                "The immutable transient derivative object key has been permanently retired.");
        }
    }

    private async Task EnsureOutputsNotRetiredOrExpireOwnIntentsAsync(
        Guid centralDerivativeJobId,
        IReadOnlyList<Output> outputs,
        CancellationToken cancellationToken)
    {
        foreach (var output in outputs)
        {
            var storageReference = $"{BucketPrefix}{output.ObjectKey}";
            if (!await CentralObjectOwnershipFence.IsRetiredAsync(
                    dbContext, storageReference, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            await ExpireOwnIntentsAsync(centralDerivativeJobId, outputs).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The immutable transient derivative object key has been permanently retired.");
        }
    }

    private async Task ExpireOwnIntentsIfRetiredAsync(
        Guid centralDerivativeJobId,
        IReadOnlyList<Output> outputs)
    {
        foreach (var output in outputs)
        {
            if (await CentralObjectOwnershipFence.IsRetiredAsync(
                    dbContext, $"{BucketPrefix}{output.ObjectKey}", CancellationToken.None).ConfigureAwait(false))
            {
                await ExpireOwnIntentsAsync(centralDerivativeJobId, outputs).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task ExpireOwnIntentsAsync(
        Guid centralDerivativeJobId,
        IReadOnlyList<Output> outputs)
    {
        foreach (var rejected in outputs)
        {
            var rejectedReference = $"{BucketPrefix}{rejected.ObjectKey}";
            await dbContext.CentralTransientDerivativeOutputIntents.Where(intent =>
                    intent.CentralDerivativeJobId == centralDerivativeJobId
                    && intent.CommittedAtUtc == null
                    && (intent.ObjectState == CentralArtifactObjectState.Pending
                        || intent.ObjectState == CentralArtifactObjectState.Available)
                    && EF.Functions.Collate(
                        intent.StorageReference,
                        CentralObjectOwnershipFence.BinaryCollation) == rejectedReference)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(intent => intent.ObjectState, CentralArtifactObjectState.Expired)
                    .SetProperty(intent => intent.StateReasonCode, "retention.publication-rejected"),
                    CancellationToken.None).ConfigureAwait(false);
        }
        dbContext.ChangeTracker.Clear();
    }

    private async Task CommitBundleAsync(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeBundle bundle,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var eventId = await dbContext.CentralTransientDerivativeJobs.AsNoTracking()
            .Where(item => item.CentralDerivativeJobId == lease.JobId)
            .Select(item => item.CentralTransientEventId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var sourceIds = await dbContext.CentralTransientObservationSources.AsNoTracking()
            .Where(item => item.Observation!.CentralTransientEventId == eventId)
            .Select(item => item.CentralArtifactId)
            .Concat(dbContext.CentralTransientObservationBackgrounds.AsNoTracking()
                .Where(item => item.Observation!.CentralTransientEventId == eventId)
                .Select(item => item.CentralArtifactId))
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var holdTargets = await CentralTransientPayloadHoldFence.ReadArtifactsAsync(
            dbContext, sourceIds, cancellationToken).ConfigureAwait(false);
        if (holdTargets.Count != sourceIds.Length)
        {
            throw new CentralDerivativeJobStateException("Transient derivative source hold targets are missing.");
        }
        await using var holdScope = await CentralTransientPayloadHoldFence.AcquireAsync(
            dbContext, holdTargets, cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, lease.JobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var genericJob = await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(item =>
            item.Id == lease.JobId && item.Status == CentralDerivativeJobStatus.Leased &&
            item.LeaseToken == lease.LeaseToken && item.LeaseOwner == lease.WorkerId &&
            item.LeaseExpiresAtUtc > now, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The transient derivative lease became stale before commit.");
        var derivativeJob = await dbContext.CentralTransientDerivativeJobs
            .Include(item => item.OutputIntents)
            .SingleAsync(item => item.CentralDerivativeJobId == lease.JobId, cancellationToken).ConfigureAwait(false);
        try
        {
            await CentralTransientPayloadHoldFence.ValidateAsync(
                dbContext, holdScope.Targets, cancellationToken).ConfigureAwait(false);
        }
        catch (CentralTransientPayloadHoldRejectedException exception)
        {
            throw new CentralDerivativeJobStateException(exception.Message);
        }
        if (derivativeJob.CommittedAtUtc.HasValue)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var intents = derivativeJob.OutputIntents.OrderBy(item => item.Kind).ToArray();
        if (intents.Length != 5 || intents.Any(item => item.ObjectState != CentralArtifactObjectState.Available ||
                item.ObjectVerifiedAtUtc is null || string.IsNullOrWhiteSpace(item.StorageETag) ||
                item.CommittedAtUtc is not null))
        {
            throw new CentralDerivativeJobStateException("The transient derivative outputs are incomplete.");
        }
        var eventRecord = await dbContext.CentralTransientEvents
            .Include(item => item.Observations).ThenInclude(item => item.Source)
            .Include(item => item.Observations).ThenInclude(item => item.Backgrounds)
            .SingleAsync(item => item.Id == derivativeJob.CentralTransientEventId, cancellationToken)
            .ConfigureAwait(false);
        var activeAssessment = bundle.SourceEvent.Assessments[^1];
        var latestReview = bundle.SourceEvent.Reviews.LastOrDefault(item => item.AssessmentId == activeAssessment.AssessmentId);
        foreach (var intent in intents)
        {
            var limitations = LimitationsFor(intent.Kind);
            var derivative = new CentralTransientDerivativeRecord
            {
                DerivativeId = intent.DerivativeId,
                CentralTransientEventId = derivativeJob.CentralTransientEventId,
                SourceEventVersionId = derivativeJob.SourceEventVersionId,
                CentralDerivativeJobId = derivativeJob.CentralDerivativeJobId,
                OutputIntentId = intent.Id,
                CreatedUtc = now,
                Kind = intent.Kind,
                ArtifactId = intent.ArtifactId,
                ArtifactRole = intent.ArtifactRole,
                ArtifactVariant = intent.ArtifactVariant,
                MediaType = intent.MediaType,
                ByteLength = intent.ByteLength,
                ArtifactChecksumSha256 = intent.ChecksumSha256,
                RecipeIdentitySha256 = derivativeJob.RecipeIdentitySha256,
                OptionsIdentitySha256 = derivativeJob.OptionsIdentitySha256,
                OutputIdentitySha256 = intent.OutputIdentitySha256,
                LimitationsJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(limitations)).GetRawText(),
                AssessmentId = activeAssessment.AssessmentId,
                ReviewId = latestReview?.ReviewId
            };
            foreach (var observation in bundle.SourceEvent.Observations.OrderBy(item => item.Ordinal))
            {
                var persisted = eventRecord.Observations.Single(item => item.ObservationId == observation.ObservationId);
                derivative.Sources.Add(new CentralTransientDerivativeSourceReference
                {
                    DerivativeId = derivative.DerivativeId,
                    CentralTransientEventId = derivative.CentralTransientEventId,
                    Ordinal = observation.Ordinal,
                    ObservationId = observation.ObservationId,
                    EvidenceId = observation.Source.EvidenceId,
                    CentralArtifactId = persisted.Source!.CentralArtifactId,
                    ArtifactId = observation.Source.Locator.Artifact.ArtifactId,
                    ArtifactChecksumSha256 = observation.Source.Locator.Artifact.ChecksumSha256,
                    ObservationStartedUtc = observation.Source.ObservationStartedUtc,
                    ObservationEndedUtc = observation.Source.ObservationEndedUtc
                });
                for (var backgroundOrdinal = 0; backgroundOrdinal < observation.BackgroundArtifacts.Count; backgroundOrdinal++)
                {
                    var background = observation.BackgroundArtifacts[backgroundOrdinal];
                    var persistedBackground = persisted.Backgrounds.Single(item => item.Ordinal == backgroundOrdinal);
                    derivative.Backgrounds.Add(new CentralTransientDerivativeBackgroundReference
                    {
                        DerivativeId = derivative.DerivativeId,
                        CentralTransientEventId = derivative.CentralTransientEventId,
                        ObservationOrdinal = observation.Ordinal,
                        BackgroundOrdinal = backgroundOrdinal,
                        ObservationId = observation.ObservationId,
                        CentralArtifactId = persistedBackground.CentralArtifactId,
                        ArtifactId = background.ArtifactId,
                        ArtifactChecksumSha256 = background.ChecksumSha256
                    });
                }
            }
            dbContext.CentralTransientDerivatives.Add(derivative);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var intent in intents)
        {
            intent.CommittedAtUtc = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        derivativeJob.CommittedAtUtc = now;
        genericJob.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _ = await versionAppender.AppendGeneratedAsync(derivativeJob.CentralTransientEventId, previous =>
        {
            var createdUtc = now <= previous.VersionCreatedUtc ? previous.VersionCreatedUtc.AddTicks(1) : now;
            var additions = intents.Select(intent => new TransientDerivativeV1(
                intent.DerivativeId,
                now,
                intent.Kind,
                new TransientArtifactReferenceV1(
                    intent.ArtifactId,
                    intent.ArtifactRole,
                    intent.ArtifactVariant,
                    derivativeJob.RecipeIdentitySha256,
                    intent.ChecksumSha256),
                derivativeJob.RecipeIdentitySha256,
                bundle.SourceEvent.Observations.OrderBy(item => item.Ordinal)
                    .Select(item => item.Source.EvidenceId).ToArray(),
                LimitationsFor(intent.Kind))).ToArray();
            return previous with
            {
                EventVersionId = Guid.NewGuid(),
                Version = previous.Version + 1,
                PreviousEventVersionId = previous.EventVersionId,
                PreviousVersionCreatedUtc = previous.VersionCreatedUtc,
                VersionCreatedUtc = createdUtc,
                Derivatives = previous.Derivatives.Concat(additions).ToArray()
            };
        }, resetReviewState: false, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private async Task<string?> HasValidObjectAsync(
        CentralTransientDerivativeOutputIntent intent,
        CancellationToken cancellationToken)
    {
        var objectKey = intent.StorageReference[BucketPrefix.Length..];
        try
        {
            var stat = await minio.StatObjectAsync(new StatObjectArgs().WithBucket(Bucket).WithObject(objectKey),
                cancellationToken).ConfigureAwait(false);
            if (stat.Size != intent.ByteLength)
            {
                throw new CentralDerivativeOutputIntegrityException("object.length-mismatch");
            }
            long bytesRead = 0;
            string? checksum = null;
            await minio.GetObjectAsync(new GetObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                .WithMatchETag(stat.ETag).WithCallbackStream(async (stream, token) =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        bytesRead += read;
                    }
                    checksum = Convert.ToHexString(hash.GetHashAndReset());
                }), cancellationToken).ConfigureAwait(false);
            if (bytesRead != intent.ByteLength ||
                !string.Equals(checksum, intent.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CentralDerivativeOutputIntegrityException("object.checksum-mismatch");
            }
            return stat.ETag;
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return null;
        }
    }

    private async Task PublishAsync(
        CentralTransientDerivativeOutputIntent intent,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var objectKey = intent.StorageReference[BucketPrefix.Length..];
        var stagingKey = $"staging/derivatives/{Guid.NewGuid():N}";
        try
        {
            await using var stream = MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array is not null
                ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: false)
                : new MemoryStream(payload.ToArray(), writable: false);
            await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(stagingKey)
                .WithStreamData(stream).WithObjectSize(payload.Length).WithContentType(intent.MediaType),
                cancellationToken).ConfigureAwait(false);
            await minio.CopyObjectAsync(new CopyObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(Bucket).WithObject(stagingKey)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(stagingKey),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (MinioException)
            {
                // The reconciliation worker removes stale staging objects.
            }
        }
    }

    private static IReadOnlyList<Output> CreateOutputs(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeBundle bundle)
    {
        var products = bundle.Products;
        return
        [
            CreateOutput(lease, bundle, TransientDerivativeKind.Crop, FrameArtifactRole.Preview,
                "event-crop", JpegImageCodec.MediaType, products.Crop),
            CreateOutput(lease, bundle, TransientDerivativeKind.Preview, FrameArtifactRole.Preview,
                "event-preview", JpegImageCodec.MediaType, products.Preview),
            CreateOutput(lease, bundle, TransientDerivativeKind.Mask, FrameArtifactRole.Metadata,
                "event-mask", Linear16TransientDerivativeProductFactory.PackedMaskMediaType, products.Mask),
            CreateOutput(lease, bundle, TransientDerivativeKind.Overlay, FrameArtifactRole.AnnotatedPreview,
                "event-overlay", JpegImageCodec.MediaType, products.Overlay),
            CreateOutput(lease, bundle, TransientDerivativeKind.Reconstruction, FrameArtifactRole.Combined,
                "event-reconstruction", Linear16TransientDerivativeProductFactory.Linear16MediaType,
                products.Reconstruction)
        ];
    }

    internal static string[] CreateStorageReferences(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeBundle bundle)
        => CreateStorageReferences(CreateOutputs(lease, bundle));

    private static string[] CreateStorageReferences(IReadOnlyList<Output> outputs)
        => outputs.Select(output => $"{BucketPrefix}{output.ObjectKey}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static Output CreateOutput(
        CentralDerivativeJobLease lease,
        CentralTransientDerivativeBundle bundle,
        TransientDerivativeKind kind,
        FrameArtifactRole role,
        string variant,
        string mediaType,
        ReadOnlyMemory<byte> payload)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(payload.Span));
        var identityValue = string.Join('\n',
            "central-transient-derivative-output-v1",
            lease.JobId.ToString("N"),
            bundle.SourceEvent.EventVersionId.ToString("N"),
            kind.ToString(),
            CentralTransientDerivativeRuntime.RecipeIdentitySha256,
            CentralTransientDerivativeRuntime.OptionsIdentitySha256,
            checksum);
        var outputIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityValue)));
        var derivativeIdentity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{outputIdentity}\nderivative")));
        return new(
            kind,
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            ProcessingIdentity.CreateArtifactId(derivativeIdentity),
            role,
            variant,
            mediaType,
            payload,
            checksum,
            outputIdentity,
            $"derivatives/transient-events/{bundle.SourceEvent.EventId:N}/{outputIdentity}.bin");
    }

    private static void ValidateIntent(CentralTransientDerivativeOutputIntent intent, Output output)
    {
        if (intent.DerivativeId != output.DerivativeId || intent.ArtifactId != output.ArtifactId ||
            intent.ArtifactRole != output.Role || !string.Equals(intent.ArtifactVariant, output.Variant, StringComparison.Ordinal) ||
            !string.Equals(intent.MediaType, output.MediaType, StringComparison.Ordinal) ||
            intent.ByteLength != output.Payload.Length ||
            !string.Equals(intent.ChecksumSha256, output.ChecksumSha256, StringComparison.Ordinal) ||
            !string.Equals(intent.OutputIdentitySha256, output.OutputIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(intent.StorageReference, $"{BucketPrefix}{output.ObjectKey}", StringComparison.Ordinal))
        {
            throw new CentralDerivativeJobStateException("A persisted transient derivative intent conflicts with the frozen output.");
        }
    }

    private static TransientDerivativeLimitation[] LimitationsFor(TransientDerivativeKind kind)
        => kind == TransientDerivativeKind.Reconstruction ? Limitations : [];

    private sealed record Output(
        TransientDerivativeKind Kind,
        Guid ArtifactId,
        Guid DerivativeId,
        FrameArtifactRole Role,
        string Variant,
        string MediaType,
        ReadOnlyMemory<byte> Payload,
        string ChecksumSha256,
        string OutputIdentitySha256,
        string ObjectKey);
}
