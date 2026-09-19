using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeOutputWriter
{
    Task<IReadOnlyList<Guid>?> TryCompletePendingSetAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);

    Task<Guid?> TryCompletePendingAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken);

    Task<Guid> PersistAsync(
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> PersistSetAsync(
        CentralDerivativeJobLease lease,
        IReadOnlyList<ProcessingProduct> products,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken);
}

internal sealed partial class CentralDerivativeOutputWriter(
    ApplicationDbContext dbContext,
    IObjectStore objectStore,
    ICentralArtifactObjectReader objectReader,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralDerivativeOutputWriter> logger,
    CentralObjectStorageNames? storageNames = null,
    CentralProcessingGraphConvergenceSignal? graphConvergenceSignal = null,
    IOptions<CentralProcessingEntitlementOptions>? entitlementOptions = null) : ICentralDerivativeOutputWriter
{
    private readonly CentralObjectStorageNames _storageNames = storageNames ?? new();
    private string Bucket => _storageNames.ArtifactBucket;
    private const string ManifestSchemaVersion = "central-v1";

    public async Task<IReadOnlyList<Guid>?> TryCompletePendingSetAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.GraphExecutionId is null)
        {
            var artifactId = await TryCompletePendingAsync(lease, cancellationToken).ConfigureAwait(false);
            return artifactId.HasValue ? [artifactId.Value] : null;
        }
        var slots = await dbContext.CentralDerivativeJobOutputs.AsNoTracking()
            .Where(output => output.CentralDerivativeJobId == lease.JobId)
            .OrderBy(output => output.Ordinal)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (slots.Length == 0)
        {
            return null;
        }
        var evidenceRows = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(evidence => evidence.Job)
            .Include(evidence => evidence.Artifact)!.ThenInclude(artifact => artifact!.Sources)
            .Include(evidence => evidence.Artifact)!.ThenInclude(artifact => artifact!.Recipe)
            .Include(evidence => evidence.Artifact)!.ThenInclude(artifact => artifact!.StructuredProduct)
            .Where(evidence => evidence.CentralDerivativeJobId == lease.JobId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var resolved = new List<CentralArtifactProcessingEvidence>(slots.Length);
        foreach (var slot in slots)
        {
            var matching = evidenceRows.Where(evidence => evidence.Artifact is { } artifact &&
                string.Equals(evidence.GraphProductContractIdentitySha256, slot.ContractIdentitySha256,
                    StringComparison.OrdinalIgnoreCase) &&
                artifact.Role == slot.Role && string.Equals(artifact.Variant ?? string.Empty, slot.Variant,
                    StringComparison.Ordinal)).ToArray();
            if (matching.Length == 0)
            {
                return null;
            }
            if (matching.Length != 1 || matching[0].Artifact is not { } artifact ||
                artifact.ObjectState is CentralArtifactObjectState.Quarantined or CentralArtifactObjectState.Expired ||
                artifact.ReconstructionState != CentralReconstructionState.Complete ||
                !HasExpectedSources(artifact, lease) || !HasExpectedFrozenInputSet(matching[0], lease) ||
                HasBoundExpectedIdentity(lease) && !string.Equals(
                    matching[0].RecipeIdentitySha256,
                    lease.ExpectedRecipeIdentitySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CentralDerivativeJobStateException(
                    "The pending derivative output set conflicts with the frozen graph contracts.");
            }
            await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, artifact.StorageReference, cancellationToken).ConfigureAwait(false);
            await EnsureNotRetiredOrExpireOwnIntentAsync(
                lease, artifact.StorageReference, cancellationToken).ConfigureAwait(false);
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
                throw new CentralDerivativeOutputIntegrityException(exception.ReasonCode, exception);
            }
            resolved.Add(matching[0]);
        }
        var inputBytes = lease.Inputs?.Sum(input => input.ByteLength) ?? 0;
        await CompleteAsync(
            lease,
            resolved.Select(evidence => (evidence.CentralArtifactId, (ProcessingProduct?)null)).ToArray(),
            inputBytes,
            resolved.Sum(evidence => evidence.Artifact!.ByteLength),
            TimeSpan.Zero,
            objectLocksHeld: false,
            cancellationToken).ConfigureAwait(false);
        return resolved.Select(evidence => evidence.Artifact!.ArtifactId).ToArray();
    }

    public async Task<Guid?> TryCompletePendingAsync(
        CentralDerivativeJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var graphExecutionId = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Id == lease.JobId)
            .Select(job => job.GraphExecutionId)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        string? graphContractIdentity = null;
        if (graphExecutionId is not null)
        {
            var graphContracts = await dbContext.CentralDerivativeJobOutputs.AsNoTracking()
                .Where(output => output.CentralDerivativeJobId == lease.JobId &&
                    output.Role == lease.TargetRole && output.Variant == lease.TargetVariant)
                .Select(output => output.ContractIdentitySha256)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (graphContracts.Length != 1)
            {
                throw new CentralDerivativeJobStateException(
                    "The derivative graph job does not have one matching frozen output contract.");
            }
            graphContractIdentity = graphContracts[0];
        }
        var expectedRecipeIdentity = lease.ExpectedRecipeIdentitySha256 ?? lease.RequestedRecipeIdentitySha256;
        var expectedOutputIdentity = expectedRecipeIdentity.Length == 64 && expectedRecipeIdentity.All(Uri.IsHexDigit)
            ? ProcessingIdentity.CreateOutputIdentity(
                lease.TargetRole,
                lease.TargetVariant,
                expectedRecipeIdentity,
                lease.Inputs is { Count: > 0 }
                    ? lease.Inputs.OrderBy(input => input.Ordinal).Select(input => input.ArtifactId).ToArray()
                    : [lease.SourceArtifactId])
            : null;
        var evidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(item => item.Job)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Sources)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Recipe)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.StructuredProduct)
            .SingleOrDefaultAsync(item => expectedOutputIdentity == null
                ? item.CentralDerivativeJobId == lease.JobId
                : item.DevicePublicId == lease.SourceDevicePublicId &&
                    item.OutputIdentitySha256 == expectedOutputIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (evidence?.Artifact is not { } artifact
            || artifact.ObjectState is CentralArtifactObjectState.Quarantined or CentralArtifactObjectState.Expired
            || artifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            return null;
        }
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, artifact.StorageReference, cancellationToken).ConfigureAwait(false);
        await EnsureNotRetiredOrExpireOwnIntentAsync(
            lease, artifact.StorageReference, cancellationToken).ConfigureAwait(false);
        if (!HasExpectedSources(artifact, lease) || !HasExpectedFrozenInputSet(evidence, lease)
            || graphContractIdentity is not null && !string.Equals(
                evidence.GraphProductContractIdentitySha256,
                graphContractIdentity,
                StringComparison.OrdinalIgnoreCase)
            || HasBoundExpectedIdentity(lease) && !string.Equals(
                evidence.RecipeIdentitySha256,
                lease.ExpectedRecipeIdentitySha256,
                StringComparison.OrdinalIgnoreCase))
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
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            lease, [(artifact.Id, null)], inputBytes, artifact.ByteLength, TimeSpan.Zero,
            objectLocksHeld: true, cancellationToken)
            .ConfigureAwait(false);
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
        var storageReference = CreateStorageReference(lease, product, Bucket);
        var objectKey = storageReference[_storageNames.ArtifactPrefix.Length..];
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, storageReference, cancellationToken).ConfigureAwait(false);
        await EnsureNotRetiredOrExpireOwnIntentAsync(lease, storageReference, cancellationToken).ConfigureAwait(false);
        var artifact = await EnsureIntentAsync(lease, product, artifactId, objectKey, cancellationToken)
            .ConfigureAwait(false);
        await EnsureNotRetiredOrExpireOwnIntentAsync(lease, storageReference, cancellationToken).ConfigureAwait(false);
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
        try
        {
            await EnsureNotRetiredOrExpireOwnIntentAsync(
                lease, storageReference, cancellationToken).ConfigureAwait(false);
            await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
            await CompleteAsync(
                lease, [(artifact.Id, product)], inputBytes, product.Payload.Length, recipeDuration,
                objectLocksHeld: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CentralDerivativeJobStateException)
        {
            await ExpireOwnIntentIfRetiredAsync(lease, storageReference).ConfigureAwait(false);
            throw;
        }
        return artifactId;
    }

    public async Task<IReadOnlyList<Guid>> PersistSetAsync(
        CentralDerivativeJobLease lease,
        IReadOnlyList<ProcessingProduct> products,
        long inputBytes,
        TimeSpan recipeDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(products);
        if (lease.GraphExecutionId is null)
        {
            if (products.Count != 1)
            {
                throw new CentralDerivativeJobStateException("A legacy derivative job must produce exactly one product.");
            }
            return [await PersistAsync(
                lease, products[0], inputBytes, recipeDuration, cancellationToken).ConfigureAwait(false)];
        }
        var job = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Include(candidate => candidate.Outputs)
            .SingleAsync(candidate => candidate.Id == lease.JobId, cancellationToken).ConfigureAwait(false);
        if (products.Count != job.Outputs.Count || products.Select(product => (product.Role, product.Variant)).Distinct().Count() !=
            products.Count)
        {
            throw new CentralDerivativeJobStateException(
                "The processing outcome does not match the frozen graph output cardinality.");
        }
        var contractedProducts = products.Select(product => new
        {
            Product = product,
            ContractIdentitySha256 = CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product)
        }).ToArray();
        var orderedProducts = job.Outputs.OrderBy(output => output.Ordinal).Select(output =>
        {
            var matching = contractedProducts.Where(candidate => string.Equals(
                candidate.ContractIdentitySha256, output.ContractIdentitySha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matching.Length == 1
                ? matching[0].Product
                : throw new CentralDerivativeJobStateException(
                    "The processing outcome does not match every frozen graph output contract exactly once.");
        }).ToArray();
        var persisted = new List<(Guid CentralArtifactId, ProcessingProduct Product)>(orderedProducts.Length);
        foreach (var product in orderedProducts)
        {
            ValidateProduct(lease, product);
            var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            var storageReference = CreateStorageReference(lease, product, Bucket);
            var objectKey = storageReference[_storageNames.ArtifactPrefix.Length..];
            await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, storageReference, cancellationToken).ConfigureAwait(false);
            await EnsureNotRetiredOrExpireOwnIntentAsync(lease, storageReference, cancellationToken).ConfigureAwait(false);
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
                    await QuarantineAsync(lease, artifact.Id, exception.ReasonCode, cancellationToken)
                        .ConfigureAwait(false);
                    throw new CentralDerivativeOutputIntegrityException(exception.ReasonCode, exception);
                }
            }
            persisted.Add((artifact.Id, product));
        }
        await CompleteAsync(
            lease,
            persisted.Select(item => (item.CentralArtifactId, (ProcessingProduct?)item.Product)).ToArray(),
            inputBytes,
            orderedProducts.Sum(product => (long)product.Payload.Length),
            recipeDuration,
            objectLocksHeld: false,
            cancellationToken).ConfigureAwait(false);
        return orderedProducts.Select(product => ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256)).ToArray();
    }

    private async Task<CentralArtifact> EnsureIntentAsync(
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        Guid artifactId,
        string objectKey,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, lease.JobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var job = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(candidate => candidate.Inputs).ThenInclude(input => input.Artifact)
            .Include(candidate => candidate.Outputs)
            .SingleOrDefaultAsync(candidate => candidate.Id == lease.JobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == lease.LeaseToken
                && candidate.LeaseOwner == lease.WorkerId
                && candidate.LeaseExpiresAtUtc > now,
                cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        var graphContractIdentity = CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product);
        var existing = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(item => item.Job)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Sources)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Recipe)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.StructuredProduct)
            .SingleOrDefaultAsync(item => item.DevicePublicId == lease.SourceDevicePublicId
                && item.OutputIdentitySha256 == product.OutputIdentitySha256, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateExisting(
                existing, lease, product, artifactId, objectKey, Bucket, graphContractIdentity);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing.Artifact!;
        }
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
            StorageReference = _storageNames.ArtifactPrefix + objectKey,
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
            RecipeOperationKind = product.Recipe.OperationKind,
            GraphProductContractIdentitySha256 = graphContractIdentity,
            ProductKind = product.Kind,
            ProductSchemaVersion = product.SchemaVersion,
            ProductMediaType = product.MediaType,
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
            await objectStore.PutAsync(
                Bucket, stagingKey, payload, product.Payload.Length, product.MediaType, cancellationToken)
                .ConfigureAwait(false);
            await objectStore.CopyAsync(Bucket, stagingKey, objectKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await objectStore.DeleteAsync(Bucket, stagingKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectStoreException)
            {
                // The reconciliation service removes stale staging objects.
            }
        }
    }

    private async Task CompleteAsync(
        CentralDerivativeJobLease lease,
        (Guid CentralArtifactId, ProcessingProduct? Product)[] products,
        long inputBytes,
        long outputBytes,
        TimeSpan recipeDuration,
        bool objectLocksHeld,
        CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartStage("complete", lease.RecipeName);
        var started = timeProvider.GetTimestamp();
        dbContext.ChangeTracker.Clear();
        if (products.Length == 0)
        {
            throw new CentralDerivativeJobStateException("A derivative output set cannot be empty.");
        }
        var artifactIds = products.Select(product => product.CentralArtifactId).ToArray();
        var storageReferences = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(candidate => artifactIds.Contains(candidate.Id))
            .Select(candidate => candidate.StorageReference)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (storageReferences.Length != artifactIds.Length)
        {
            throw new CentralDerivativeJobStateException("A derivative output artifact is unavailable.");
        }
        await using var objectLocks = objectLocksHeld
            ? null
            : await CentralObjectApplicationLockSet.AcquireAsync(
                dbContext, storageReferences, cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (objectLocks is not null)
        {
            await objectLocks.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        }
        var now = timeProvider.GetUtcNow();
        var pinStartedAtUtc = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.CentralDerivativeJobId == lease.JobId)
            .MinAsync(input => (DateTimeOffset?)input.SelectedAtUtc, cancellationToken).ConfigureAwait(false);
        var artifacts = await dbContext.CentralArtifacts.Where(candidate => artifactIds.Contains(candidate.Id))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Length != artifactIds.Length)
        {
            throw new CentralDerivativeJobStateException("A derivative output artifact is unavailable.");
        }
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, lease.JobId, cancellationToken).ConfigureAwait(false);
        foreach (var artifact in artifacts)
        {
            await EnsureNotRetiredAsync(artifact.StorageReference, cancellationToken).ConfigureAwait(false);
            if (artifact.ObjectState is CentralArtifactObjectState.Expired or CentralArtifactObjectState.Quarantined)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CentralDerivativeJobStateException(
                    "The derivative output is unavailable and cannot be completed.");
            }
        }
        foreach (var product in products)
        {
            await CentralProcessingGraphOutputBinding.BindAsync(
                dbContext,
                lease.JobId,
                product.CentralArtifactId,
                product.Product,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        var primaryArtifactId = products[0].CentralArtifactId;
        var jobAffected = await dbContext.CentralDerivativeJobs.Where(job => job.Id == lease.JobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == lease.LeaseToken
                && job.LeaseOwner == lease.WorkerId
                && job.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(job => job.ResultCentralArtifactId, primaryArtifactId)
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
        await CentralProcessingUsageRecorder.RecordAsync(
            dbContext, entitlementOptions?.Value, lease.JobId, lease.AttemptCount, cancellationToken).ConfigureAwait(false);
        foreach (var artifact in artifacts)
        {
            artifact.ObjectState = CentralArtifactObjectState.Available;
            artifact.StateReasonCode = null;
            artifact.ReconciledAtUtc = now;
            ClearObjectVerification(artifact);
        }
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
        graphConvergenceSignal?.Signal(lease.GraphExecutionId);
        telemetry.RecordStage(
            "complete", lease.RecipeName, "completed", timeProvider.GetElapsedTime(started));
        if (lease.Inputs is { Count: > 1 } && pinStartedAtUtc.HasValue)
        {
            telemetry.RecordWindowPinDuration(lease.RecipeName, now - pinStartedAtUtc.Value);
        }
        dbContext.ChangeTracker.Clear();
    }

    private async Task EnsureNotRetiredAsync(string storageReference, CancellationToken cancellationToken)
    {
        if (await CentralObjectOwnershipFence.IsRetiredAsync(
                dbContext, storageReference, cancellationToken, _storageNames.ArtifactPrefix).ConfigureAwait(false))
        {
            throw new CentralDerivativeJobStateException(
                "The immutable derivative object key has been permanently retired.");
        }
    }

    private async Task EnsureNotRetiredOrExpireOwnIntentAsync(
        CentralDerivativeJobLease lease,
        string storageReference,
        CancellationToken cancellationToken)
    {
        if (!await CentralObjectOwnershipFence.IsRetiredAsync(
                dbContext, storageReference, cancellationToken, _storageNames.ArtifactPrefix).ConfigureAwait(false))
        {
            return;
        }
        await ExpireOwnIntentAsync(lease, storageReference).ConfigureAwait(false);
        throw new CentralDerivativeJobStateException(
            "The immutable derivative object key has been permanently retired.");
    }

    private async Task ExpireOwnIntentIfRetiredAsync(
        CentralDerivativeJobLease lease,
        string storageReference)
    {
        if (await CentralObjectOwnershipFence.IsRetiredAsync(
                dbContext, storageReference, CancellationToken.None, _storageNames.ArtifactPrefix).ConfigureAwait(false))
        {
            await ExpireOwnIntentAsync(lease, storageReference).ConfigureAwait(false);
        }
    }

    private async Task ExpireOwnIntentAsync(
        CentralDerivativeJobLease lease,
        string storageReference)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.CentralArtifacts.Where(artifact =>
                artifact.ObjectState == CentralArtifactObjectState.Pending
                && artifact.RetentionDeletionToken == null
                && EF.Functions.Collate(
                    artifact.StorageReference,
                    CentralObjectOwnershipFence.BinaryCollation) == storageReference
                && dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                    evidence.CentralArtifactId == artifact.Id
                    && evidence.CentralDerivativeJobId == lease.JobId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(artifact => artifact.ObjectState, CentralArtifactObjectState.Expired)
                .SetProperty(artifact => artifact.StateReasonCode, "retention.publication-rejected")
                .SetProperty(artifact => artifact.ReconciledAtUtc, now)
                .SetProperty(artifact => artifact.ObjectVerificationToken, (Guid?)null)
                .SetProperty(artifact => artifact.ObjectVerificationRequestedAtUtc, (DateTimeOffset?)null)
                .SetProperty(artifact => artifact.ObjectVerificationRetryCount, 0)
                .SetProperty(artifact => artifact.ObjectVerificationRetryAtUtc, (DateTimeOffset?)null), CancellationToken.None)
            .ConfigureAwait(false);
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
        await CentralProcessingUsageRecorder.RecordAsync(
            dbContext, entitlementOptions?.Value, lease.JobId, lease.AttemptCount, cancellationToken).ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Quarantined;
        artifact.ReconstructionState = CentralReconstructionState.Quarantined;
        artifact.StateReasonCode = reasonCode;
        artifact.ReconciledAtUtc = now;
        ClearObjectVerification(artifact);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (pinStartedAtUtc != default)
        {
            telemetry.RecordWindowPinDuration(lease.RecipeName, now - pinStartedAtUtc, "quarantined");
        }
        dbContext.ChangeTracker.Clear();
    }

    private static void ClearObjectVerification(CentralArtifact artifact)
    {
        artifact.ObjectVerificationToken = null;
        artifact.ObjectVerificationRequestedAtUtc = null;
        artifact.ObjectVerificationRetryCount = 0;
        artifact.ObjectVerificationRetryAtUtc = null;
    }

    internal static CentralArtifactLayout CreateLayout(FrameLayoutDescriptor layout) => new()
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
        StoredCodeTransform = layout.StoredCodeTransform?.ToString(),
        LevelCodeSpace = layout.LevelCodeSpace?.ToString(),
        NativeWidth = layout.Readout?.NativeWidth,
        NativeHeight = layout.Readout?.NativeHeight,
        RoiX = layout.Readout?.RoiX,
        RoiY = layout.Readout?.RoiY,
        RoiWidth = layout.Readout?.RoiWidth,
        RoiHeight = layout.Readout?.RoiHeight,
        BinX = layout.Readout?.BinX,
        BinY = layout.Readout?.BinY,
        BinningAlgorithm = layout.Readout?.BinningAlgorithm.ToString(),
        CfaOriginX = layout.Readout?.CfaOriginX,
        CfaOriginY = layout.Readout?.CfaOriginY,
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

    internal static string CreateStorageReference(
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        string bucket = Configuration.CentralObjectStorageOptions.DefaultArtifactBucket)
        => $"{Configuration.CentralObjectStorageOptions.LogicalScheme}{bucket}/derivatives/{lease.SourceDevicePublicId:N}/{product.OutputIdentitySha256}.bin";

    internal static void ValidateProduct(CentralDerivativeJobLease lease, ProcessingProduct product)
    {
        if (lease.GraphExecutionId is null && (product.Role != lease.TargetRole
                || product.Variant != lease.TargetVariant)
            || HasBoundExpectedIdentity(lease) && !string.Equals(
                product.Recipe.IdentitySha256,
                lease.ExpectedRecipeIdentitySha256,
                StringComparison.OrdinalIgnoreCase)
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

    internal static void ValidateExisting(
        CentralArtifactProcessingEvidence evidence,
        CentralDerivativeJobLease lease,
        ProcessingProduct product,
        Guid artifactId,
        string objectKey,
        string bucket,
        string? graphContractIdentity)
    {
        var artifact = evidence.Artifact!;
        // Deterministic evidence produced by the legacy scheduler carries no graph contract identity. A graph job
        // adopts it only when the durable evidence itself satisfies the frozen slot: same frozen input set, requested
        // and actual recipe identities, operation kind, product kind, schema, media type, role, variant, sources and
        // checksum (all verified below). Evidence that was graph-tagged must name this exact contract.
        var contractMismatch = graphContractIdentity is not null &&
            (evidence.GraphProductContractIdentitySha256 is not null
                ? !string.Equals(evidence.GraphProductContractIdentitySha256, graphContractIdentity,
                    StringComparison.OrdinalIgnoreCase)
                : !IsLegacyEvidence(evidence));
        if ((graphContractIdentity is null
                ? evidence.CentralDerivativeJobId != lease.JobId
                : contractMismatch || !HasExpectedFrozenInputSet(evidence, lease))
            || !string.Equals(evidence.RequestedRecipeIdentitySha256, lease.RequestedRecipeIdentitySha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.RecipeIdentitySha256, product.Recipe.IdentitySha256,
                StringComparison.OrdinalIgnoreCase)
            || graphContractIdentity is not null &&
                (evidence.RecipeOperationKind != product.Recipe.OperationKind ||
                 evidence.ProductKind != product.Kind ||
                 !string.Equals(evidence.ProductSchemaVersion, product.SchemaVersion, StringComparison.Ordinal) ||
                 !string.Equals(evidence.ProductMediaType, product.MediaType, StringComparison.OrdinalIgnoreCase))
            || evidence.ProductKind is { } recordedKind && recordedKind != product.Kind
            || evidence.ProductSchemaVersion is not null &&
                !string.Equals(evidence.ProductSchemaVersion, product.SchemaVersion, StringComparison.Ordinal)
            || evidence.ProductMediaType is not null &&
                !string.Equals(evidence.ProductMediaType, product.MediaType, StringComparison.OrdinalIgnoreCase)
            || artifact.ArtifactId != artifactId
            || artifact.Role != product.Role
            || artifact.Variant != product.Variant
            || artifact.RecipeVersion != lease.TargetRecipeVersion
            || !string.Equals(artifact.MediaType, product.MediaType, StringComparison.OrdinalIgnoreCase)
            || !HasExpectedSources(artifact, lease)
            || artifact.ByteLength != product.Payload.Length
            || !string.Equals(artifact.ChecksumSha256, product.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            || artifact.StorageReference != $"{Configuration.CentralObjectStorageOptions.LogicalScheme}{bucket}/{objectKey}")
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

    /// <summary>
    /// Evidence a legacy (non-graph) job produced without a graph contract identity. Graph-owned jobs always tag
    /// their evidence, so untagged evidence of a graph job is inconsistent rather than adoptable; this mirrors the
    /// <c>TR_CentralDerivativeJobOutputs_BindOnce</c> adoption predicate.
    /// </summary>
    internal static bool IsLegacyEvidence(CentralArtifactProcessingEvidence evidence)
        => evidence.GraphProductContractIdentitySha256 is null && evidence.Job is { GraphExecutionId: null };

    private static bool HasExpectedFrozenInputSet(
        CentralArtifactProcessingEvidence evidence,
        CentralDerivativeJobLease lease)
        => lease.InputSetIdentitySha256 is null
            ? evidence.CentralDerivativeJobId == lease.JobId
            : string.Equals(
                evidence.Job?.InputSetIdentitySha256,
                lease.InputSetIdentitySha256,
                StringComparison.OrdinalIgnoreCase);

    private static bool HasBoundExpectedIdentity(CentralDerivativeJobLease lease)
        => !string.IsNullOrWhiteSpace(lease.ExpectedRecipeIdentitySha256)
            && !string.Equals(
                lease.ExpectedRecipeIdentitySha256,
                lease.RequestedRecipeIdentitySha256,
                StringComparison.OrdinalIgnoreCase);

    private static partial class Log
    {
        [LoggerMessage(2134, LogLevel.Information,
            "Central derivative pending output action: JobId={JobId}, Attempt={Attempt}, Action={Action}, Reason={Reason}")]
        public static partial void Recovery(
            ILogger logger, Guid jobId, int attempt, string action, string reason);
    }
}

internal static class CentralProcessingGraphOutputBinding
{
    private static readonly JsonSerializerOptions ContractSerializerOptions = CreateContractSerializerOptions();

    internal static async Task BindAsync(
        ApplicationDbContext dbContext,
        Guid jobId,
        Guid centralArtifactId,
        ProcessingProduct? product,
        DateTimeOffset boundAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var job = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(item => item.Id == jobId)
            .Select(item => new
            {
                item.GraphExecutionId,
                item.TargetRole,
                item.TargetRecipeVersion,
                item.TargetVariant,
                item.RequestedRecipeIdentitySha256,
                item.ExpectedRecipeIdentitySha256,
                item.InputSetIdentitySha256,
                SourceFrameId = item.SourceArtifact!.CentralFrameId
            })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (job.GraphExecutionId is null)
        {
            return;
        }
        var execution = await dbContext.CentralProcessingGraphExecutions.AsNoTracking()
            .Where(item => item.Id == job.GraphExecutionId.Value)
            .Select(item => new
            {
                item.ExpandedAtUtc,
                AnchorFrameId = item.AnchorSourceArtifact!.CentralFrameId,
                AnchorDevicePublicId = item.AnchorSourceArtifact.DevicePublicId
            })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (execution.ExpandedAtUtc is null)
        {
            throw new CentralDerivativeJobStateException("The processing graph execution is not expansion-sealed.");
        }

        var artifact = await dbContext.CentralArtifacts.AsNoTracking()
            .Include(item => item.Recipe)
            .Include(item => item.StructuredProduct)
            .SingleAsync(item => item.Id == centralArtifactId, cancellationToken).ConfigureAwait(false);
        var evidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .Include(item => item.Job)
            .SingleOrDefaultAsync(item => item.CentralArtifactId == centralArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (evidence is null || artifact.CentralFrameId != execution.AnchorFrameId ||
            job.SourceFrameId != execution.AnchorFrameId || artifact.DevicePublicId != execution.AnchorDevicePublicId ||
            artifact.RecipeVersion != job.TargetRecipeVersion ||
            evidence.DevicePublicId != artifact.DevicePublicId ||
            !string.Equals(evidence.Job?.InputSetIdentitySha256, job.InputSetIdentitySha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.RequestedRecipeIdentitySha256, job.RequestedRecipeIdentitySha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.RecipeIdentitySha256, job.ExpectedRecipeIdentitySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            // The frozen expected identity is authoritative (mirrors TR_CentralDerivativeJobOutputs_BindOnce); an
            // annotation node whose execution froze "no annotation" never binds annotated evidence.
            throw new CentralDerivativeJobStateException(
                "The derivative graph output does not match its execution and processing evidence.");
        }
        if (product is not null && !string.Equals(
                product.OutputIdentitySha256, evidence.OutputIdentitySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDerivativeJobStateException(
                "The derivative graph output identity does not match its processing evidence.");
        }

        var candidates = await dbContext.CentralDerivativeJobOutputs
            .Where(item => item.CentralDerivativeJobId == jobId && item.ResultCentralArtifactId == null &&
                item.Role == artifact.Role)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var matching = candidates.Where(slot =>
            string.Equals(slot.Variant, artifact.Variant ?? string.Empty, StringComparison.Ordinal) &&
            ContractMatches(slot, artifact, evidence, product)).ToArray();
        if (matching.Length != 1)
        {
            throw new CentralDerivativeJobStateException(
                "The derivative graph output does not match exactly one frozen output contract.");
        }
        matching[0].ResultCentralArtifactId = artifact.Id;
        matching[0].ResultOutputIdentitySha256 = evidence.OutputIdentitySha256;
        matching[0].BoundAtUtc = boundAtUtc;
    }

    internal static string? ResolveContractIdentity(CentralDerivativeJob job, ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(product);
        if (job.GraphExecutionId is null)
        {
            return null;
        }
        var matching = job.Outputs.Where(slot =>
        {
            if (slot.Role != product.Role || !string.Equals(slot.Variant, product.Variant, StringComparison.Ordinal))
            {
                return false;
            }
            return TryReadContract(slot, out var contract) && ProductMatches(contract!, product);
        }).ToArray();
        return matching.Length == 1
            ? matching[0].ContractIdentitySha256
            : throw new CentralDerivativeJobStateException(
                "The processing product does not match exactly one frozen graph output contract.");
    }

    internal static bool ContractMatches(
        CentralDerivativeJobOutput slot,
        CentralArtifact artifact,
        CentralArtifactProcessingEvidence evidence,
        ProcessingProduct? product)
    {
        if (!TryReadContract(slot, out var contract))
        {
            return false;
        }
        // Legacy-scheduler evidence carries no graph contract identity; it binds only through the durable product
        // validation below (or the live product when one is present). Graph-tagged evidence must name this slot.
        if (contract is null || contract.Role != slot.Role || contract.Variant != slot.Variant ||
            contract.ProductKind != slot.ProductKind ||
            (evidence.GraphProductContractIdentitySha256 is not null
                ? !string.Equals(evidence.GraphProductContractIdentitySha256, slot.ContractIdentitySha256,
                    StringComparison.OrdinalIgnoreCase)
                : !CentralDerivativeOutputWriter.IsLegacyEvidence(evidence)) ||
            contract.MediaType is not null && !string.Equals(
                contract.MediaType, artifact.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (product is not null)
        {
            return ProductMatches(contract, product) &&
                (evidence.GraphProductContractIdentitySha256 is not null || DurableEvidenceMatches(contract, artifact, evidence));
        }
        return DurableEvidenceMatches(contract, artifact, evidence);
    }

    private static bool DurableEvidenceMatches(
        ProcessingGraphProductContract contract,
        CentralArtifact artifact,
        CentralArtifactProcessingEvidence evidence)
    {
        var durableKind = evidence.ProductKind ?? (artifact.StructuredProduct is { } structured
            ? Enum.TryParse<ProcessingProductKind>(structured.ProductKind, ignoreCase: false, out var kind)
                ? kind
                : (ProcessingProductKind?)null
            : artifact.Role == FrameArtifactRole.Metadata
                ? ProcessingProductKind.Metadata
                : ProcessingProductKind.PixelData);
        var durableSchemaVersion = evidence.ProductSchemaVersion ?? artifact.StructuredProduct?.ProductSchemaVersion;
        ProcessingAlgorithmIdentity[] algorithms;
        try
        {
            algorithms = JsonSerializer.Deserialize<ProcessingAlgorithmIdentity[]>(
                evidence.AlgorithmsJson, ContractSerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }
        return durableKind == contract.ProductKind && durableSchemaVersion == contract.SchemaVersion &&
            string.Equals(evidence.ProductMediaType ?? artifact.MediaType, artifact.MediaType,
                StringComparison.OrdinalIgnoreCase) &&
            RecipeMatches(contract.Recipe, artifact.Recipe, evidence.RecipeOperationKind) &&
            AlgorithmsMatch(contract.Algorithms, algorithms);
    }

    internal static bool TryReadContract(
        CentralDerivativeJobOutput slot,
        out ProcessingGraphProductContract? contract)
    {
        try
        {
            if (!string.Equals(
                    CentralDerivativeJobOutput.ComputeContractIdentitySha256(slot.ContractJson),
                    slot.ContractIdentitySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                contract = null;
                return false;
            }
            contract = JsonSerializer.Deserialize<ProcessingGraphProductContract>(
                slot.ContractJson, ContractSerializerOptions);
            return contract is not null && contract.Role == slot.Role && contract.Variant == slot.Variant &&
                contract.ProductKind == slot.ProductKind;
        }
        catch (JsonException)
        {
            contract = null;
            return false;
        }
    }

    internal static bool ProductMatches(ProcessingGraphProductContract contract, ProcessingProduct product)
        => product.Role == contract.Role && product.Variant == contract.Variant &&
            product.Kind == contract.ProductKind && product.SchemaVersion == contract.SchemaVersion &&
            (contract.MediaType is null || string.Equals(
                product.MediaType, contract.MediaType, StringComparison.OrdinalIgnoreCase)) &&
            RecipeMatches(contract.Recipe, product.Recipe) &&
            AlgorithmsMatch(contract.Algorithms, product.Algorithms);

    internal static bool RecipeMatches(ProcessingRecipeDefinition? expected, ProcessingRecipeIdentity? actual)
        => expected is null || actual is not null && expected.Name == actual.Descriptor.Name &&
            expected.SemanticVersion == actual.Descriptor.SemanticVersion &&
            expected.ImplementationVersion == actual.Descriptor.ImplementationVersion &&
            expected.OperationKind == actual.OperationKind;

    internal static bool RecipeMatches(
        ProcessingRecipeDefinition? expected,
        CentralArtifactRecipe? actual,
        ProcessingOperationKind? actualOperationKind)
        => expected is null || actual is not null && expected.Name == actual.Name &&
            expected.SemanticVersion == actual.SemanticVersion &&
            expected.ImplementationVersion == actual.ImplementationVersion &&
            expected.OperationKind == actualOperationKind;

    /// <summary>
    /// A contract that omits or pins an empty <c>algorithms</c> set leaves the product's algorithms unconstrained;
    /// a non-empty pinned set requires exact ordered equality. Mirrors the <c>$.algorithms</c> predicate in
    /// <c>TR_CentralDerivativeJobOutputs_BindOnce</c> and <c>TR_CentralArtifactProcessingEvidence_GraphContractImmutable</c>.
    /// </summary>
    internal static bool AlgorithmsMatch(
        System.Collections.Immutable.ImmutableArray<ProcessingAlgorithmIdentity> expected,
        IReadOnlyList<ProcessingAlgorithmIdentity> actual)
        => expected.IsDefaultOrEmpty || SequenceEqual(expected, actual);

    internal static bool SequenceEqual(
        System.Collections.Immutable.ImmutableArray<ProcessingAlgorithmIdentity> expected,
        IReadOnlyList<ProcessingAlgorithmIdentity> actual)
        => (expected.IsDefault ? [] : expected).SequenceEqual(actual);

    private static JsonSerializerOptions CreateContractSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
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
