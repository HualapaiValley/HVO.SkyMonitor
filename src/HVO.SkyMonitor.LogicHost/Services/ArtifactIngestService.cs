using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IArtifactIngestService
{
    Task<ArtifactIngestResult> IngestAsync(ArtifactManifestDocument manifest, Stream payload, CancellationToken cancellationToken);

    Task<ArtifactIngestResult?> CheckStatusAsync(ArtifactManifestDocument manifest, CancellationToken cancellationToken);
}

internal sealed record ArtifactIngestResult(DeviceUploadResult Upload, bool ReadyForAcknowledgement);

internal sealed record ArtifactIngestManifest(
    string SchemaVersion,
    string AgentId,
    Guid ArtifactId,
    Guid FrameId,
    FrameArtifactRole Role,
    string MediaType,
    long ByteLength,
    string ChecksumSha256,
    DateTimeOffset CapturedAtUtc,
    string RecipeVersion,
    string IdempotencyKey,
    SceneProvenance? Scene,
    ReconstructionDescriptor? Descriptor)
{
    public bool IsReconstructable => Descriptor is not null;

    public static ArtifactIngestManifest Create(ArtifactManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.LegacyManifest is { } legacy)
        {
            legacy.Validate();
            return new(
                legacy.SchemaVersion, legacy.AgentId, legacy.ArtifactId, legacy.FrameId, legacy.Role,
                legacy.MediaType, legacy.ByteLength, legacy.ChecksumSha256, legacy.CapturedAtUtc,
                legacy.RecipeVersion, legacy.IdempotencyKey, legacy.Scene, null);
        }
        var current = document.Manifest ?? throw new ArgumentException("Manifest document has no supported manifest.", nameof(document));
        var validation = current.Validate();
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Manifest v2 is invalid: {validation.ReasonCode} at {validation.FieldPath}.", nameof(document));
        }
        var descriptor = current.Descriptor;
        return new(
            current.SchemaVersion,
            descriptor.Capture.AgentId,
            descriptor.Artifact.ArtifactId,
            descriptor.Capture.CaptureId,
            descriptor.Artifact.Role,
            descriptor.Artifact.MediaType,
            descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256,
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Artifact.Recipe.ImplementationVersion,
            current.IdempotencyKey,
            current.Scene,
            descriptor);
    }
}

/// <summary>Streams a versioned artifact into MinIO and records an idempotent metadata row.</summary>
internal sealed class ArtifactIngestService(
    ApplicationDbContext dbContext,
    IMinioClient minio,
    TimeProvider timeProvider,
    ICentralDerivativeJobScheduler derivativeJobScheduler,
    CentralIngestTelemetry telemetry) : IArtifactIngestService
{
    private const string Bucket = "skymonitor-artifacts";
    private const string CaptureSequenceIdentityIndex = "IX_CentralFrames_DevicePublicId_CaptureSequence";
    private static readonly SemaphoreSlim BucketInitialization = new(1, 1);
    private static readonly ConcurrentDictionary<(string AgentId, Guid ArtifactId), ArtifactGate> ArtifactGates = new();

    public async Task<ArtifactIngestResult> IngestAsync(ArtifactManifestDocument document, Stream payload, CancellationToken cancellationToken)
    {
        var manifest = ArtifactIngestManifest.Create(document);
        telemetry.RecordValidation(manifest.SchemaVersion, "accepted");
        ArgumentNullException.ThrowIfNull(payload);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);
        var stagingKey = $"staging/{Guid.NewGuid():N}";
        try
        {
            using var verifyingPayload = new HashingReadStream(payload);
            var writeStarted = timeProvider.GetTimestamp();
            try
            {
                await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(stagingKey).WithStreamData(verifyingPayload)
                    .WithObjectSize(manifest.ByteLength).WithContentType(manifest.MediaType), cancellationToken).ConfigureAwait(false);
                telemetry.RecordObjectWrite("staging", "completed", timeProvider.GetElapsedTime(writeStarted));
            }
            catch
            {
                telemetry.RecordObjectWrite("staging", "failed", timeProvider.GetElapsedTime(writeStarted));
                throw;
            }
            var hasTrailingBytes = verifyingPayload.ReadByte() != -1;
            if (hasTrailingBytes
                || verifyingPayload.BytesRead != manifest.ByteLength
                || !string.Equals(verifyingPayload.GetChecksumSha256(), manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                telemetry.RecordChecksum("upload", "mismatch");
                throw new ArtifactIntegrityException("Payload length or checksum does not match the artifact manifest.");
            }
            telemetry.RecordChecksum("upload", "matched");

            return await IngestVerifiedAsync(manifest, stagingKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await TryRemoveObjectAsync(stagingKey).ConfigureAwait(false);
        }
    }

    public async Task<ArtifactIngestResult?> CheckStatusAsync(
        ArtifactManifestDocument document,
        CancellationToken cancellationToken)
    {
        var manifest = ArtifactIngestManifest.Create(document);
        return await RunGatedAsync(
            manifest,
            () => CheckStatusCoreAsync(manifest, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtifactIngestResult?> CheckStatusCoreAsync(
        ArtifactIngestManifest manifest,
        CancellationToken cancellationToken)
    {
        var registration = await dbContext.DeviceRegistrations.SingleOrDefaultAsync(
            item => item.DeviceId == manifest.AgentId && item.Status == DeviceRegistrationStatus.Active,
            cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Agent is not registered or active.");
        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Agent is not fully activated.");
        }

        var existing = await dbContext.CentralArtifacts
            .Include(artifact => artifact.IngestIdentities)
            .Include(artifact => artifact.Frame)
            .SingleOrDefaultAsync(artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey),
                cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ObjectState == CentralArtifactObjectState.Quarantined)
        {
            return null;
        }
        EnsureManifestMatches(existing, manifest);
        dbContext.ChangeTracker.Clear();
        return await ReconcileExistingAsync(
            manifest, timeProvider.GetUtcNow(), ExistingArtifactReconciliationMode.StatusAcknowledgement, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ArtifactIngestResult> IngestVerifiedAsync(
        ArtifactIngestManifest manifest,
        string stagingKey,
        CancellationToken cancellationToken)
        => await RunGatedAsync(
            manifest,
            () => IngestCoreAsync(manifest, stagingKey, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    private static async Task<T> RunGatedAsync<T>(
        ArtifactIngestManifest manifest,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gateKey = (manifest.AgentId, manifest.ArtifactId);
        ArtifactGate gate;
        while (true)
        {
            gate = ArtifactGates.GetOrAdd(gateKey, static _ => new ArtifactGate());
            if (gate.TryAddReference())
            {
                break;
            }
            ArtifactGates.TryRemove(new KeyValuePair<(string AgentId, Guid ArtifactId), ArtifactGate>(gateKey, gate));
        }

        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            if (gate.ReleaseReference())
            {
                ArtifactGates.TryRemove(new KeyValuePair<(string AgentId, Guid ArtifactId), ArtifactGate>(gateKey, gate));
            }
        }
    }

    private async Task<ArtifactIngestResult> IngestCoreAsync(
        ArtifactIngestManifest manifest,
        string stagingKey,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = CentralIngestTelemetry.StartActivity("central-artifact.ingest");
        activity?.SetTag("manifest.schema", manifest.SchemaVersion);

        var registration = await dbContext.DeviceRegistrations.SingleOrDefaultAsync(
            item => item.DeviceId == manifest.AgentId && item.Status == DeviceRegistrationStatus.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Agent is not registered or active.");
        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Agent is not fully activated.");
        }
        var devicePublicId = registration.DevicePublicId.Value;

        var existing = await dbContext.CentralArtifacts
            .Include(artifact => artifact.IngestIdentities)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .SingleOrDefaultAsync(artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureManifestMatches(existing, manifest);
            if (existing.ObjectState == CentralArtifactObjectState.Available)
            {
                dbContext.ChangeTracker.Clear();
                var duplicate = await ReconcileExistingAsync(
                    manifest, timeProvider.GetUtcNow(), ExistingArtifactReconciliationMode.MultipartDuplicate, cancellationToken)
                    .ConfigureAwait(false);
                telemetry.RecordDuplicate(manifest.SchemaVersion);
                telemetry.RecordRequest(
                    manifest.SchemaVersion,
                    duplicate.ReadyForAcknowledgement ? "duplicate" : "pending-reference",
                    manifest.ByteLength,
                    timeProvider.GetElapsedTime(started));
                return duplicate;
            }
            dbContext.ChangeTracker.Clear();
        }

        var identityOnSubmittedFrame = await dbContext.CentralArtifacts.AnyAsync(artifact =>
            artifact.ArtifactId == manifest.ArtifactId
            && (artifact.DevicePublicId == devicePublicId
                || artifact.DevicePublicId == null && artifact.Frame!.DevicePublicId == devicePublicId)
            && artifact.Frame!.FrameId == manifest.FrameId, cancellationToken).ConfigureAwait(false);
        var identityConflict = !identityOnSubmittedFrame
            && await dbContext.CentralArtifacts.AnyAsync(artifact =>
                artifact.ArtifactId == manifest.ArtifactId
                && (artifact.DevicePublicId == devicePublicId
                    || artifact.DevicePublicId == null && artifact.Frame!.DevicePublicId == devicePublicId),
                cancellationToken).ConfigureAwait(false);
        if (identityConflict)
        {
            throw new ArtifactIngestConflictException(
                "The artifact identity is already associated with a different frame for this device.");
        }
        dbContext.ChangeTracker.Clear();

        var existingFrame = await dbContext.CentralFrames
            .Include(frame => frame.Artifacts).ThenInclude(artifact => artifact.IngestIdentities)
            .SingleOrDefaultAsync(
            frame => frame.DevicePublicId == devicePublicId && frame.FrameId == manifest.FrameId,
            cancellationToken).ConfigureAwait(false);
        if (existingFrame is not null)
        {
            EnsureFrameMatches(existingFrame, registration, manifest);
            var compatibilityArtifact = existingFrame.Artifacts.FirstOrDefault(
                artifact => artifact.ArtifactId == manifest.ArtifactId);
            if (compatibilityArtifact is not null
                && compatibilityArtifact.ManifestSchemaVersion != manifest.SchemaVersion)
            {
                EnsureCompatibleArtifactMatches(compatibilityArtifact, manifest);
                if (compatibilityArtifact.ObjectState == CentralArtifactObjectState.Available)
                {
                    await using var compatibilityLock = await CentralObjectApplicationLock.AcquireAsync(
                        dbContext, compatibilityArtifact.StorageReference, cancellationToken).ConfigureAwait(false);
                    var enriched = await EnrichCompatibilityArtifactAsync(
                        registration, manifest, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    telemetry.RecordDuplicate(manifest.SchemaVersion);
                    telemetry.RecordRequest(manifest.SchemaVersion, "cross-schema-duplicate", manifest.ByteLength, timeProvider.GetElapsedTime(started));
                    return enriched;
                }
            }
            else
            {
                EnsureNoLogicalArtifactConflict(existingFrame, manifest);
            }
            dbContext.ChangeTracker.Clear();
        }

        var mediaTypeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.MediaType)));
        var objectKey = $"artifacts/{devicePublicId:N}/{manifest.CapturedAtUtc:yyyy/MM/dd}/{manifest.IdempotencyKey}-{manifest.ChecksumSha256.ToUpperInvariant()}-{mediaTypeKey}.bin";
        var storageReference = $"minio://{Bucket}/{objectKey}";
        if (manifest.IsReconstructable)
        {
            await EnsureV2IntentAsync(registration, manifest, storageReference, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
        }
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, storageReference, cancellationToken).ConfigureAwait(false);
        var source = new CopySourceObjectArgs().WithBucket(Bucket).WithObject(stagingKey);
        var copyStarted = timeProvider.GetTimestamp();
        try
        {
            await minio.CopyObjectAsync(new CopyObjectArgs().WithBucket(Bucket).WithObject(objectKey).WithCopyObjectSource(source), cancellationToken).ConfigureAwait(false);
            telemetry.RecordObjectWrite("canonical-copy", "completed", timeProvider.GetElapsedTime(copyStarted));
        }
        catch
        {
            telemetry.RecordObjectWrite("canonical-copy", "failed", timeProvider.GetElapsedTime(copyStarted));
            throw;
        }

        var now = timeProvider.GetUtcNow();
        try
        {
            var result = await PersistAsync(registration, manifest, storageReference, now, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result.Upload.StorageReference, storageReference, StringComparison.Ordinal))
            {
                await RemoveUncommittedObjectAsync(storageReference, objectKey).ConfigureAwait(false);
            }
            telemetry.RecordRequest(
                manifest.SchemaVersion,
                result.ReadyForAcknowledgement ? "accepted" : "pending-reference",
                manifest.ByteLength,
                timeProvider.GetElapsedTime(started));
            return result;
        }
        catch
        {
            await RemoveUncommittedObjectAsync(storageReference, objectKey).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        await BucketInitialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false))
            {
                await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            BucketInitialization.Release();
        }
    }

    private async Task<ArtifactIngestResult> EnrichCompatibilityArtifactAsync(
        DeviceRegistration registration,
        ArtifactIngestManifest manifest,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        var artifact = await dbContext.CentralArtifacts
            .Include(item => item.IngestIdentities)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(item => item.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(item => item.Sources)
            .AsSplitQuery()
            .SingleAsync(item => item.ArtifactId == manifest.ArtifactId
                && item.Frame!.DevicePublicId == registration.DevicePublicId
                && item.Frame.FrameId == manifest.FrameId, cancellationToken).ConfigureAwait(false);
        EnsureCompatibleArtifactMatches(artifact, manifest);
        if (artifact.DevicePublicId is null
            && await dbContext.CentralArtifacts.AnyAsync(candidate =>
                candidate.Id != artifact.Id
                && candidate.DevicePublicId == registration.DevicePublicId
                && candidate.ArtifactId == manifest.ArtifactId,
                cancellationToken).ConfigureAwait(false))
        {
            throw new ArtifactIngestConflictException(
                "The artifact identity already has a canonical row for this device.");
        }
        artifact.DevicePublicId ??= registration.DevicePublicId!.Value;
        EnsureSceneProvenanceMatches(artifact.Frame!, manifest);
        EnrichSceneProvenance(artifact.Frame!, manifest);
        if (!artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey))
        {
            var identity = new CentralArtifactIngestIdentity
            {
                CentralArtifactId = artifact.Id,
                Artifact = artifact,
                ManifestSchemaVersion = manifest.SchemaVersion,
                IdempotencyKey = manifest.IdempotencyKey.ToUpperInvariant()
            };
            dbContext.CentralArtifactIngestIdentities.Add(identity);
        }
        if (manifest.IsReconstructable)
        {
            await ApplyReconstructionAsync(
                artifact.Frame!, artifact, manifest, registration.DevicePublicId!.Value, receivedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        var integrityReason = await VerifyStoredObjectAsync(artifact, cancellationToken).ConfigureAwait(false);
        if (integrityReason is not null)
        {
            QuarantineObject(artifact, integrityReason);
            await InvalidateDependentsAsync(dbContext, artifact, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new ArtifactIntegrityException("Stored artifact object is missing or inconsistent with SQL metadata.");
        }
        artifact.ObjectState = CentralArtifactObjectState.Available;
        artifact.ReconciledAtUtc = receivedAtUtc;
        await ResolveWaitingSourcesAsync(artifact, registration.DevicePublicId!.Value, cancellationToken).ConfigureAwait(false);
        if (ShouldScheduleDerivatives(artifact))
        {
            await derivativeJobScheduler.EnsureRequiredJobsAsync(artifact, receivedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCaptureSequenceConflict(exception))
        {
            throw CreateCaptureSequenceConflict(exception);
        }
        catch (Exception exception) when (IsPersistenceRace(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await ReconcileExistingUnderObjectLockAsync(
                manifest, receivedAtUtc, ExistingArtifactReconciliationMode.MultipartDuplicate, cancellationToken)
                .ConfigureAwait(false);
        }
        return CreateResult(artifact, manifest);
    }

    private async Task EnsureV2IntentAsync(
        DeviceRegistration registration,
        ArtifactIngestManifest manifest,
        string storageReference,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 10;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                await EnsureV2IntentOnceAsync(
                    registration, manifest, storageReference, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (IsCaptureSequenceConflict(exception))
            {
                throw CreateCaptureSequenceConflict(exception);
            }
            catch (Exception exception) when (IsPersistenceRace(exception) && attempt < maximumAttempts - 1)
            {
                dbContext.ChangeTracker.Clear();
                await DelayPersistenceRetryAsync(manifest.ArtifactId, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ArtifactIngestConflictException("The artifact intent could not be committed because of sustained concurrency.");
    }

    private async Task EnsureV2IntentOnceAsync(
        DeviceRegistration registration,
        ArtifactIngestManifest manifest,
        string storageReference,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var existing = await dbContext.CentralArtifacts
            .Include(artifact => artifact.IngestIdentities)
            .Include(artifact => artifact.Frame)
            .SingleOrDefaultAsync(artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey),
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureManifestMatches(existing, manifest);
            if (existing.ObjectState == CentralArtifactObjectState.Quarantined)
            {
                existing.ObjectState = CentralArtifactObjectState.Pending;
                existing.StateReasonCode = null;
                await InvalidateDependentsAsync(dbContext, existing, cancellationToken).ConfigureAwait(false);
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            return;
        }

        var devicePublicId = registration.DevicePublicId!.Value;
        var frame = await dbContext.CentralFrames
            .Include(item => item.Artifacts)
            .Include(item => item.Timing)
            .Include(item => item.Control)
            .Include(item => item.Profiles)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.DevicePublicId == devicePublicId && item.FrameId == manifest.FrameId, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
        {
            frame = new CentralFrame
            {
                RegistrationId = registration.Id,
                DevicePublicId = devicePublicId,
                ObservatoryId = registration.ObservatoryId,
                AgentId = manifest.AgentId,
                FrameId = manifest.FrameId,
                CapturedAtUtc = manifest.CapturedAtUtc,
                FirstReceivedAtUtc = receivedAtUtc,
                SceneProvenanceJson = SerializeScene(manifest)
            };
            dbContext.CentralFrames.Add(frame);
        }
        else
        {
            EnsureFrameMatches(frame, registration, manifest);
            EnsureNoLogicalArtifactConflict(frame, manifest);
        }

        var artifact = CreateArtifact(frame, manifest, storageReference, receivedAtUtc);
        artifact.DevicePublicId = devicePublicId;
        dbContext.CentralArtifacts.Add(artifact);
        await ApplyReconstructionAsync(frame, artifact, manifest, devicePublicId, receivedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        artifact.ObjectState = CentralArtifactObjectState.Pending;
        artifact.ReconciledAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPersistenceRace(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return true;
        }
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 2601 or 2627 })
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsCaptureSequenceConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } sqlException
                && sqlException.Message.Contains(CaptureSequenceIdentityIndex, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static ArtifactIngestConflictException CreateCaptureSequenceConflict(Exception innerException)
        => new("The capture sequence is already associated with a different frame for this device.", innerException);

    private async Task<ArtifactIngestResult> ReconcileExistingAsync(
        ArtifactIngestManifest manifest,
        DateTimeOffset receivedAtUtc,
        ExistingArtifactReconciliationMode mode,
        CancellationToken cancellationToken)
    {
        var storageReference = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey))
            .Select(artifact => artifact.StorageReference)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, storageReference, cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        return await ReconcileExistingUnderObjectLockAsync(manifest, receivedAtUtc, mode, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ArtifactIngestResult> ReconcileExistingUnderObjectLockAsync(
        ArtifactIngestManifest manifest,
        DateTimeOffset receivedAtUtc,
        ExistingArtifactReconciliationMode mode,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 10;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await dbContext.CentralArtifacts
                    .Include(artifact => artifact.IngestIdentities)
                    .Include(artifact => artifact.Sources)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
                    .AsSplitQuery()
                    .SingleAsync(
                        artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                            || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey),
                        cancellationToken).ConfigureAwait(false);
                EnsureManifestMatches(existing, manifest);
                EnrichSceneProvenance(existing.Frame!, manifest);
                await TryResolvePendingReferenceAsync(existing, manifest, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                var requiresVerification = existing.ReconstructionState != CentralReconstructionState.PendingReference
                    && mode switch
                    {
                        ExistingArtifactReconciliationMode.StatusAcknowledgement => true,
                        ExistingArtifactReconciliationMode.MultipartDuplicate => true,
                        _ => throw new ArgumentOutOfRangeException(nameof(mode))
                    };
                if (requiresVerification)
                {
                    var integrityReason = await VerifyStoredObjectAsync(existing, cancellationToken).ConfigureAwait(false);
                    if (integrityReason is not null)
                    {
                        QuarantineObject(existing, integrityReason);
                        await InvalidateDependentsAsync(dbContext, existing, cancellationToken).ConfigureAwait(false);
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                        throw new ArtifactIntegrityException("Stored artifact object is missing or inconsistent with SQL metadata.");
                    }
                }
                if (existing.ReconstructionState != CentralReconstructionState.PendingReference)
                {
                    existing.ObjectState = CentralArtifactObjectState.Available;
                }
                existing.ReconciledAtUtc = receivedAtUtc;
                if (ShouldScheduleDerivatives(existing))
                {
                    await derivativeJobScheduler.EnsureRequiredJobsAsync(existing, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                }
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                return CreateResult(existing, manifest);
            }
            catch (Exception exception) when (IsCaptureSequenceConflict(exception))
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw CreateCaptureSequenceConflict(exception);
            }
            catch (Exception exception) when (IsPersistenceRace(exception))
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                if (attempt == maximumAttempts - 1)
                {
                    throw;
                }
                await DelayPersistenceRetryAsync(manifest.ArtifactId, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ArtifactIngestConflictException("The artifact could not be reconciled because of sustained concurrency.");
    }

    private async Task<ArtifactIngestResult> PersistAsync(
        DeviceRegistration registration,
        ArtifactIngestManifest manifest,
        string storageReference,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 10;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await dbContext.CentralArtifacts
                    .Include(artifact => artifact.IngestIdentities)
                    .Include(artifact => artifact.Sources)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
                    .AsSplitQuery()
                    .SingleOrDefaultAsync(
                        artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                            || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey), cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    EnsureManifestMatches(existing, manifest);
                    EnrichSceneProvenance(existing.Frame!, manifest);
                    if (existing.ObjectState != CentralArtifactObjectState.Available)
                    {
                        existing.StorageReference = storageReference;
                    }
                    var integrityReason = await VerifyStoredObjectAsync(existing, cancellationToken).ConfigureAwait(false);
                    if (integrityReason is not null)
                    {
                        QuarantineObject(existing, integrityReason);
                        await InvalidateDependentsAsync(dbContext, existing, cancellationToken).ConfigureAwait(false);
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                        throw new ArtifactIntegrityException("Stored artifact object is missing or inconsistent with SQL metadata.");
                    }
                    existing.ObjectState = CentralArtifactObjectState.Available;
                    existing.ReconciledAtUtc = receivedAtUtc;
                    await TryResolvePendingReferenceAsync(existing, manifest, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                    await ResolveWaitingSourcesAsync(existing, existing.Frame!.DevicePublicId, cancellationToken).ConfigureAwait(false);
                    if (ShouldScheduleDerivatives(existing))
                    {
                        await derivativeJobScheduler.EnsureRequiredJobsAsync(existing, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                    }
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                    return CreateResult(existing, manifest);
                }

                var devicePublicId = registration.DevicePublicId!.Value;
                var frame = await dbContext.CentralFrames
                    .Include(item => item.Artifacts).ThenInclude(artifact => artifact.IngestIdentities)
                    .Include(item => item.Timing)
                    .Include(item => item.Control)
                    .Include(item => item.Profiles)
                    .SingleOrDefaultAsync(
                    item => item.DevicePublicId == devicePublicId && item.FrameId == manifest.FrameId,
                    cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    frame = new CentralFrame
                    {
                        RegistrationId = registration.Id,
                        DevicePublicId = devicePublicId,
                        ObservatoryId = registration.ObservatoryId,
                        AgentId = manifest.AgentId,
                        FrameId = manifest.FrameId,
                        CapturedAtUtc = manifest.CapturedAtUtc,
                        FirstReceivedAtUtc = receivedAtUtc,
                        RigProfileVersion = manifest.IsReconstructable ? null : registration.CurrentRigProfileVersion,
                        SceneProvenanceJson = SerializeScene(manifest)
                    };
                    dbContext.CentralFrames.Add(frame);
                }
                else
                {
                    EnsureFrameMatches(frame, registration, manifest);
                    var crossSchemaArtifact = frame.Artifacts.FirstOrDefault(candidate =>
                        candidate.ArtifactId == manifest.ArtifactId
                        && candidate.ManifestSchemaVersion != manifest.SchemaVersion);
                    if (crossSchemaArtifact is not null)
                    {
                        EnsureCompatibleArtifactMatches(crossSchemaArtifact, manifest);
                        EnsureSceneProvenanceMatches(frame, manifest);
                        EnrichSceneProvenance(frame, manifest);
                        if (!crossSchemaArtifact.IngestIdentities.Any(identity =>
                            identity.IdempotencyKey == manifest.IdempotencyKey))
                        {
                            var alias = new CentralArtifactIngestIdentity
                            {
                                CentralArtifactId = crossSchemaArtifact.Id,
                                Artifact = crossSchemaArtifact,
                                ManifestSchemaVersion = manifest.SchemaVersion,
                                IdempotencyKey = manifest.IdempotencyKey.ToUpperInvariant()
                            };
                            dbContext.CentralArtifactIngestIdentities.Add(alias);
                        }
                        if (manifest.IsReconstructable)
                        {
                            await ApplyReconstructionAsync(
                                frame, crossSchemaArtifact, manifest, devicePublicId, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                        }
                        if (crossSchemaArtifact.ObjectState != CentralArtifactObjectState.Available)
                        {
                            crossSchemaArtifact.StorageReference = storageReference;
                        }
                        var integrityReason = await VerifyStoredObjectAsync(crossSchemaArtifact, cancellationToken).ConfigureAwait(false);
                        if (integrityReason is not null)
                        {
                            QuarantineObject(crossSchemaArtifact, integrityReason);
                            await InvalidateDependentsAsync(dbContext, crossSchemaArtifact, cancellationToken).ConfigureAwait(false);
                            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                            throw new ArtifactIntegrityException("Stored artifact object is missing or inconsistent with SQL metadata.");
                        }
                        crossSchemaArtifact.ObjectState = CentralArtifactObjectState.Available;
                        await TryResolvePendingReferenceAsync(
                            crossSchemaArtifact, manifest, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                        await ResolveWaitingSourcesAsync(crossSchemaArtifact, devicePublicId, cancellationToken).ConfigureAwait(false);
                        crossSchemaArtifact.ReconciledAtUtc = receivedAtUtc;
                        if (ShouldScheduleDerivatives(crossSchemaArtifact))
                        {
                            await derivativeJobScheduler.EnsureRequiredJobsAsync(
                                crossSchemaArtifact, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                        }
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                        return CreateResult(crossSchemaArtifact, manifest);
                    }
                    EnsureNoLogicalArtifactConflict(frame, manifest);
                }

                var artifact = CreateArtifact(frame, manifest, storageReference, receivedAtUtc);
                artifact.DevicePublicId = devicePublicId;
                dbContext.CentralArtifacts.Add(artifact);
                await ApplyReconstructionAsync(frame, artifact, manifest, devicePublicId, receivedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
                await ResolveWaitingSourcesAsync(artifact, devicePublicId, cancellationToken).ConfigureAwait(false);
                if (ShouldScheduleDerivatives(artifact))
                {
                    await derivativeJobScheduler.EnsureRequiredJobsAsync(artifact, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                }
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                var ready = artifact.ReconstructionState is CentralReconstructionState.Complete or CentralReconstructionState.LegacyIncomplete;
                return new ArtifactIngestResult(
                    new DeviceUploadResult(registration.Id, registration.ObservatoryId, storageReference, receivedAtUtc), ready);
            }
            catch (Exception exception) when (IsCaptureSequenceConflict(exception))
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw CreateCaptureSequenceConflict(exception);
            }
            catch (Exception exception) when (IsPersistenceRace(exception))
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                if (attempt == maximumAttempts - 1)
                {
                    var concurrent = await dbContext.CentralArtifacts.Include(item => item.IngestIdentities).Include(item => item.Frame).SingleOrDefaultAsync(
                        item => item.IdempotencyKey == manifest.IdempotencyKey
                            || item.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey), cancellationToken).ConfigureAwait(false);
                    if (concurrent is not null)
                    {
                        EnsureManifestMatches(concurrent, manifest);
                        return CreateResult(concurrent, manifest);
                    }
                    throw;
                }
                await DelayPersistenceRetryAsync(manifest.ArtifactId, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ArtifactIngestConflictException("The frame or artifact identity is already associated with different metadata.");
    }

    private static CentralArtifact CreateArtifact(
        CentralFrame frame,
        ArtifactIngestManifest manifest,
        string storageReference,
        DateTimeOffset receivedAtUtc)
    {
        var artifact = new CentralArtifact
        {
            CentralFrameId = frame.Id,
            Frame = frame,
            ArtifactId = manifest.ArtifactId,
            Role = manifest.Role,
            RecipeVersion = manifest.RecipeVersion,
            ManifestSchemaVersion = manifest.SchemaVersion,
            MediaType = manifest.MediaType,
            ByteLength = manifest.ByteLength,
            ChecksumSha256 = manifest.ChecksumSha256.ToUpperInvariant(),
            StorageReference = storageReference,
            ReceivedAtUtc = receivedAtUtc,
            IdempotencyKey = manifest.IdempotencyKey.ToUpperInvariant()
        };
        artifact.IngestIdentities.Add(new CentralArtifactIngestIdentity
        {
            ManifestSchemaVersion = manifest.SchemaVersion,
            IdempotencyKey = manifest.IdempotencyKey.ToUpperInvariant()
        });
        return artifact;
    }

    private static bool ShouldScheduleDerivatives(CentralArtifact artifact)
        => artifact.ReconstructionState is CentralReconstructionState.Complete or CentralReconstructionState.LegacyIncomplete
            && (artifact.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion
                || artifact.Role == FrameArtifactRole.Raw);

    private async Task ApplyReconstructionAsync(
        CentralFrame frame,
        CentralArtifact artifact,
        ArtifactIngestManifest manifest,
        Guid devicePublicId,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        artifact.ObjectState = CentralArtifactObjectState.Available;
        artifact.ReconciledAtUtc = receivedAtUtc;
        if (manifest.Descriptor is not { } descriptor)
        {
            artifact.ReconstructionState = CentralReconstructionState.LegacyIncomplete;
            artifact.StateReasonCode = "manifest.legacy-incomplete";
            return;
        }

        var capture = descriptor.Capture;
        var cycleEvidenceJson = descriptor.CycleEvidence is null ? null : JsonSerializer.Serialize(descriptor.CycleEvidence);
        if (frame.CaptureSequence.HasValue)
        {
            EnsureCaptureFactsMatch(frame, descriptor, cycleEvidenceJson);
        }
        frame.RigId = capture.RigId;
        frame.CaptureSequence = capture.CaptureSequence;
        frame.CycleEvidenceJson ??= cycleEvidenceJson;
        frame.Timing ??= new CentralCaptureTiming
        {
            RequestedStartUtc = descriptor.Timing.RequestedStartUtc,
            ExposureStartedUtc = descriptor.Timing.ExposureStartedUtc,
            ExposureEndedUtc = descriptor.Timing.ExposureEndedUtc,
            ReadoutCompletedUtc = descriptor.Timing.ReadoutCompletedUtc,
            DurableIngressUtc = descriptor.Timing.DurableIngressUtc,
            SetpointAppliedUtc = descriptor.Timing.SetpointAppliedUtc
        };
        frame.Control ??= new CentralCaptureControl
        {
            RequestedExposureTicks = descriptor.Controls.RequestedExposure.Ticks,
            EffectiveExposureTicks = descriptor.Controls.EffectiveExposure.Ticks,
            RequestedGain = descriptor.Controls.RequestedGain,
            EffectiveGain = descriptor.Controls.EffectiveGain,
            RequestedOffset = descriptor.Controls.RequestedOffset,
            EffectiveOffset = descriptor.Controls.EffectiveOffset,
            TemperatureSetpointC = descriptor.Controls.TemperatureSetpointC,
            EffectiveTemperatureC = descriptor.Controls.EffectiveTemperatureC
        };

        var rigIdentity = descriptor.Profiles.Rig;
        var rigProfile = await HistoricalRigProfileResolver.ResolveAsync(
            dbContext, devicePublicId, rigIdentity, descriptor.Timing.ExposureStartedUtc, cancellationToken).ConfigureAwait(false);
        if (rigProfile is not null)
        {
            frame.DeviceRigProfileId = rigProfile.Id;
            frame.DeviceRigProfile = rigProfile;
            frame.RigProfileVersion = rigProfile.Version;
        }

        if (frame.Profiles.Count == 0)
        {
            AddProfile(frame, CentralProfileKind.Rig, descriptor.Profiles.Rig, rigProfile);
            AddProfile(frame, CentralProfileKind.Calibration, descriptor.Profiles.Calibration);
            AddProfile(frame, CentralProfileKind.Mask, descriptor.Profiles.Mask);
            AddProfile(frame, CentralProfileKind.Sensor, descriptor.Profiles.Sensor);
            AddProfile(frame, CentralProfileKind.Processing, descriptor.Profiles.Processing);
        }

        artifact.SourceId = descriptor.Artifact.SourceId;
        artifact.Variant = descriptor.Artifact.Variant;
        artifact.CreatedUtc = descriptor.Artifact.CreatedUtc;
        artifact.Layout = new CentralArtifactLayout
        {
            Width = descriptor.Layout.Width,
            Height = descriptor.Layout.Height,
            StrideBytes = descriptor.Layout.StrideBytes,
            PixelFormat = descriptor.Layout.PixelFormat.ToString(),
            ByteOrder = descriptor.Layout.ByteOrder.ToString(),
            SampleDepthBits = descriptor.Layout.SampleDepthBits,
            ContainerDepthBits = descriptor.Layout.ContainerDepthBits,
            Packing = descriptor.Layout.Packing.ToString(),
            CfaPattern = descriptor.Layout.CfaPattern.ToString(),
            BlackLevel = descriptor.Layout.BlackLevel,
            WhiteLevel = descriptor.Layout.WhiteLevel,
            ByteLength = descriptor.Layout.ByteLength
        };
        artifact.Recipe = new CentralArtifactRecipe
        {
            Name = descriptor.Artifact.Recipe.Name,
            SemanticVersion = descriptor.Artifact.Recipe.SemanticVersion,
            ImplementationVersion = descriptor.Artifact.Recipe.ImplementationVersion,
            OptionsJson = JsonSerializer.Serialize(CaptureContractJson.Canonicalize(descriptor.Artifact.Recipe.Options)),
            OptionsSha256 = descriptor.Artifact.Recipe.OptionsSha256.ToUpperInvariant()
        };
        for (var ordinal = 0; ordinal < descriptor.Artifact.SourceArtifactIds.Count; ordinal++)
        {
            var sourceArtifactId = descriptor.Artifact.SourceArtifactIds[ordinal];
            var resolved = frame.Artifacts.FirstOrDefault(candidate => candidate.ArtifactId == sourceArtifactId
                    && IsUsableLineageSource(candidate))
                ?? await dbContext.CentralArtifacts.FirstOrDefaultAsync(candidate =>
                    candidate.ArtifactId == sourceArtifactId
                    && candidate.DevicePublicId == devicePublicId
                    && candidate.ObjectState == CentralArtifactObjectState.Available
                    && candidate.ReconstructionState == CentralReconstructionState.Complete,
                    cancellationToken).ConfigureAwait(false);
            artifact.Sources.Add(new CentralArtifactSource
            {
                Ordinal = ordinal,
                SourceArtifactId = sourceArtifactId,
                ResolvedCentralArtifactId = resolved?.Id,
                ResolvedArtifact = resolved
            });
        }
        SetReconstructionState(artifact, rigProfile is not null);
        AddIfDetached(frame.Timing);
        AddIfDetached(frame.Control);
        foreach (var profile in frame.Profiles)
        {
            AddIfDetached(profile);
        }
        AddIfDetached(artifact.Layout);
        AddIfDetached(artifact.Recipe);
        foreach (var source in artifact.Sources)
        {
            AddIfDetached(source);
        }

        void AddIfDetached(object? entity)
        {
            if (entity is not null && dbContext.Entry(entity).State == EntityState.Detached)
            {
                dbContext.Add(entity);
            }
        }
    }

    private async Task ResolveWaitingSourcesAsync(
        CentralArtifact artifact,
        Guid devicePublicId,
        CancellationToken cancellationToken)
    {
        if (!IsUsableLineageSource(artifact))
        {
            return;
        }

        var waitingSources = await dbContext.CentralArtifactSources
            .Include(source => source.Artifact)!.ThenInclude(sourceArtifact => sourceArtifact!.Frame)
            .Include(source => source.Artifact)!.ThenInclude(sourceArtifact => sourceArtifact!.Sources)
            .Where(source => source.SourceArtifactId == artifact.ArtifactId
                && source.ResolvedCentralArtifactId == null
                && source.Artifact!.DevicePublicId == devicePublicId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var waitingSource in waitingSources)
        {
            waitingSource.ResolvedCentralArtifactId = artifact.Id;
            waitingSource.ResolvedArtifact = artifact;
            var waitingArtifact = waitingSource.Artifact!;
            SetReconstructionState(
                waitingArtifact,
                waitingArtifact.Frame!.DeviceRigProfileId.HasValue,
                waitingArtifact.Sources.Any(source => source != waitingSource && source.ResolvedCentralArtifactId == null));
        }
    }

    private static void EnsureCaptureFactsMatch(
        CentralFrame frame,
        ReconstructionDescriptor descriptor,
        string? cycleEvidenceJson)
    {
        var timing = frame.Timing;
        var control = frame.Control;
        var profiles = frame.Profiles.ToDictionary(profile => profile.Kind);
        if (frame.CaptureSequence != descriptor.Capture.CaptureSequence
            || !string.Equals(frame.RigId, descriptor.Capture.RigId, StringComparison.Ordinal)
            || timing is null
            || timing.RequestedStartUtc != descriptor.Timing.RequestedStartUtc
            || timing.ExposureStartedUtc != descriptor.Timing.ExposureStartedUtc
            || timing.ExposureEndedUtc != descriptor.Timing.ExposureEndedUtc
            || timing.ReadoutCompletedUtc != descriptor.Timing.ReadoutCompletedUtc
            || timing.DurableIngressUtc != descriptor.Timing.DurableIngressUtc
            || timing.SetpointAppliedUtc != descriptor.Timing.SetpointAppliedUtc
            || control is null
            || control.RequestedExposureTicks != descriptor.Controls.RequestedExposure.Ticks
            || control.EffectiveExposureTicks != descriptor.Controls.EffectiveExposure.Ticks
            || control.RequestedGain != descriptor.Controls.RequestedGain
            || control.EffectiveGain != descriptor.Controls.EffectiveGain
            || control.RequestedOffset != descriptor.Controls.RequestedOffset
            || control.EffectiveOffset != descriptor.Controls.EffectiveOffset
            || control.TemperatureSetpointC != descriptor.Controls.TemperatureSetpointC
            || control.EffectiveTemperatureC != descriptor.Controls.EffectiveTemperatureC
            || !string.Equals(frame.CycleEvidenceJson, cycleEvidenceJson, StringComparison.Ordinal)
            || !ProfileMatches(profiles, CentralProfileKind.Rig, descriptor.Profiles.Rig)
            || !ProfileMatches(profiles, CentralProfileKind.Calibration, descriptor.Profiles.Calibration)
            || !ProfileMatches(profiles, CentralProfileKind.Mask, descriptor.Profiles.Mask)
            || !ProfileMatches(profiles, CentralProfileKind.Sensor, descriptor.Profiles.Sensor)
            || !ProfileMatches(profiles, CentralProfileKind.Processing, descriptor.Profiles.Processing))
        {
            throw new ArtifactIngestConflictException("The frame identity is already associated with different capture-time facts.");
        }
    }

    private static bool ProfileMatches(
        Dictionary<CentralProfileKind, CentralCaptureProfile> profiles,
        CentralProfileKind kind,
        ProfileIdentityDescriptor identity)
        => profiles.TryGetValue(kind, out var persisted)
            && persisted.Name == identity.Name
            && persisted.Version == identity.Version
            && string.Equals(persisted.Sha256, identity.Sha256, StringComparison.OrdinalIgnoreCase);

    private static void AddProfile(
        CentralFrame frame,
        CentralProfileKind kind,
        ProfileIdentityDescriptor identity,
        DeviceRigProfile? rigProfile = null)
        => frame.Profiles.Add(new CentralCaptureProfile
        {
            Kind = kind,
            Name = identity.Name,
            Version = identity.Version,
            Sha256 = identity.Sha256.ToUpperInvariant(),
            DeviceRigProfileId = rigProfile?.Id,
            DeviceRigProfile = rigProfile
        });

    private async Task TryResolvePendingReferenceAsync(
        CentralArtifact artifact,
        ArtifactIngestManifest manifest,
        DateTimeOffset reconciledAtUtc,
        CancellationToken cancellationToken)
    {
        if (manifest.Descriptor is not { } descriptor)
        {
            if (artifact.ManifestSchemaVersion == ArtifactManifestV2.CurrentSchemaVersion)
            {
                SetReconstructionState(artifact, artifact.Frame!.DeviceRigProfileId.HasValue);
            }
            else
            {
                artifact.ReconstructionState = CentralReconstructionState.LegacyIncomplete;
                artifact.StateReasonCode = "manifest.legacy-incomplete";
            }
            return;
        }
        var frame = artifact.Frame ?? throw new InvalidOperationException("The central artifact frame was not loaded.");
        if (!frame.DeviceRigProfileId.HasValue)
        {
            var rig = descriptor.Profiles.Rig;
            var profile = await HistoricalRigProfileResolver.ResolveAsync(
                dbContext, frame.DevicePublicId, rig, descriptor.Timing.ExposureStartedUtc, cancellationToken).ConfigureAwait(false);
            if (profile is not null)
            {
                frame.DeviceRigProfileId = profile.Id;
                frame.DeviceRigProfile = profile;
                frame.RigProfileVersion = profile.Version;
                var rigReference = frame.Profiles.Single(profileReference => profileReference.Kind == CentralProfileKind.Rig);
                rigReference.DeviceRigProfileId = profile.Id;
                rigReference.DeviceRigProfile = profile;
            }
        }

        var unavailableSource = false;
        foreach (var source in artifact.Sources.Where(source => source.ResolvedCentralArtifactId != null))
        {
            var usable = await dbContext.CentralArtifacts.AnyAsync(candidate =>
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
            var resolved = await dbContext.CentralArtifacts.FirstOrDefaultAsync(candidate =>
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
        SetReconstructionState(artifact, frame.DeviceRigProfileId.HasValue, unavailableSource: unavailableSource);
        artifact.ReconciledAtUtc = reconciledAtUtc;
    }

    private async Task<string?> VerifyStoredObjectAsync(CentralArtifact artifact, CancellationToken cancellationToken)
    {
        var prefix = $"minio://{Bucket}/";
        if (!artifact.StorageReference.StartsWith(prefix, StringComparison.Ordinal))
        {
            telemetry.RecordChecksum("stored-object", "reference-invalid");
            return "object.reference-invalid";
        }
        var objectKey = artifact.StorageReference[prefix.Length..];
        long bytesRead = 0;
        string? checksum = null;
        try
        {
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(Bucket)
                .WithObject(objectKey)
                .WithCallbackStream(stream =>
                {
                    using var verifying = new HashingReadStream(stream);
                    verifying.CopyTo(Stream.Null);
                    bytesRead = verifying.BytesRead;
                    checksum = verifying.GetChecksumSha256();
                }), cancellationToken).ConfigureAwait(false);
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            telemetry.RecordChecksum("stored-object", "missing");
            return "object.missing";
        }
        if (bytesRead != artifact.ByteLength)
        {
            telemetry.RecordChecksum("stored-object", "length-mismatch");
            return "object.length-mismatch";
        }
        var matches = string.Equals(checksum, artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
        telemetry.RecordChecksum("stored-object", matches ? "matched" : "checksum-mismatch");
        return matches ? null : "object.checksum-mismatch";
    }

    private void QuarantineObject(CentralArtifact artifact, string reasonCode)
    {
        artifact.ObjectState = CentralArtifactObjectState.Quarantined;
        artifact.ReconstructionState = CentralReconstructionState.Quarantined;
        artifact.StateReasonCode = reasonCode;
        telemetry.RecordQuarantine(reasonCode);
    }

    private async Task CommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordSqlCommit("completed", timeProvider.GetElapsedTime(started));
        }
        catch
        {
            telemetry.RecordSqlCommit("failed", timeProvider.GetElapsedTime(started));
            throw;
        }
    }

    private static void SetReconstructionState(
        CentralArtifact artifact,
        bool hasRigProfile,
        bool? hasUnresolvedSource = null,
        bool unavailableSource = false)
    {
        if (!hasRigProfile)
        {
            ResetReferenceRetryIfNewlyPending(artifact);
            artifact.ReconstructionState = CentralReconstructionState.PendingReference;
            artifact.StateReasonCode = "profile.rig-not-found";
            return;
        }
        if (hasUnresolvedSource ?? artifact.Sources.Any(source => source.ResolvedCentralArtifactId == null))
        {
            ResetReferenceRetryIfNewlyPending(artifact);
            artifact.ReconstructionState = CentralReconstructionState.PendingReference;
            artifact.StateReasonCode = unavailableSource
                ? "lineage.source-unavailable"
                : "lineage.source-not-found";
            return;
        }
        artifact.ReconstructionState = CentralReconstructionState.Complete;
        artifact.StateReasonCode = null;
        artifact.ReferenceRetryCount = 0;
        artifact.ReferenceRetryAtUtc = null;
    }

    private static void ResetReferenceRetryIfNewlyPending(CentralArtifact artifact)
    {
        if (artifact.ReconstructionState != CentralReconstructionState.PendingReference)
        {
            artifact.ReferenceRetryCount = 0;
            artifact.ReferenceRetryAtUtc = null;
        }
    }

    private static bool IsUsableLineageSource(CentralArtifact artifact)
        => artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete;

    internal static async Task InvalidateDependentsAsync(
        ApplicationDbContext dbContext,
        CentralArtifact sourceArtifact,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
            : null;
        var invalidatedAtUtc = DateTimeOffset.UtcNow;
        var pending = new Queue<Guid>();
        var visited = new HashSet<Guid>();
        var invalidatedJobs = new HashSet<Guid>();
        pending.Enqueue(sourceArtifact.Id);
        while (pending.TryDequeue(out var sourceId))
        {
            if (!visited.Add(sourceId))
            {
                continue;
            }
            var reverseReferences = await dbContext.CentralArtifactSources
                .Include(source => source.Artifact)
                .Where(source => source.ResolvedCentralArtifactId == sourceId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var reverseReference in reverseReferences)
            {
                reverseReference.ResolvedCentralArtifactId = null;
                reverseReference.ResolvedArtifact = null;
                var dependent = reverseReference.Artifact!;
                dependent.ReconstructionState = CentralReconstructionState.PendingReference;
                dependent.StateReasonCode = "lineage.source-unavailable";
                dependent.ReconciledAtUtc = null;
                dependent.ReferenceRetryCount = 0;
                dependent.ReferenceRetryAtUtc = null;
                pending.Enqueue(dependent.Id);
            }
            var jobs = await dbContext.CentralDerivativeJobs
                .Include(job => job.Attempts)
                .Include(job => job.InputRequirements)
                .Include(job => job.Inputs)
                .Where(job => (job.SourceCentralArtifactId == sourceId
                        || job.Inputs.Any(input => input.CentralArtifactId == sourceId)
                        || job.ResultCentralArtifactId == sourceId)
                    && (job.Status == CentralDerivativeJobStatus.Waiting
                        || job.Status == CentralDerivativeJobStatus.Pending
                        || job.Status == CentralDerivativeJobStatus.RetryableFailure
                        || job.Status == CentralDerivativeJobStatus.Leased
                        || job.Status == CentralDerivativeJobStatus.Completed))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var publishedJobIds = (await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
                .Where(evidence => jobs.Select(job => job.Id).Contains(evidence.CentralDerivativeJobId))
                .Select(evidence => evidence.CentralDerivativeJobId)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            foreach (var job in jobs)
            {
                if (!invalidatedJobs.Add(job.Id))
                {
                    continue;
                }
                var invalidatedInput = job.Inputs.SingleOrDefault(input => input.CentralArtifactId == sourceId);
                var isWindow = job.ResolutionStartedAtUtc.HasValue;
                var reResolveWindow = invalidatedInput is not null && isWindow
                    && job.ResultCentralArtifactId != sourceId
                    && !publishedJobIds.Contains(job.Id);
                var wakeFrozenWindowAfterRepair = invalidatedInput is not null
                    && (!isWindow || publishedJobIds.Contains(job.Id))
                    && job.SourceCentralArtifactId != sourceId;
                var preserveWindowResolution = job.Status == CentralDerivativeJobStatus.Waiting
                    && isWindow
                    && job.InputSetIdentitySha256 is null;
                var invalidationReason = job.SourceCentralArtifactId == sourceId
                    ? sourceId == sourceArtifact.Id
                        ? sourceArtifact.StateReasonCode ?? CentralDerivativeJobScheduler.SourceInvalidatedReason
                        : CentralDerivativeJobScheduler.SourceInvalidatedReason
                    : CentralDerivativeJobScheduler.ResultInvalidatedReason;
                if (await dbContext.CentralTransientValidationJobs.AsNoTracking().AnyAsync(validation =>
                        validation.CentralDerivativeJobId == job.Id && validation.CommittedAtUtc != null,
                        cancellationToken).ConfigureAwait(false))
                {
                    await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                        dbContext, job, invalidationReason, invalidatedAtUtc, cancellationToken).ConfigureAwait(false);
                }
                var activeAttempt = job.Attempts.SingleOrDefault(attempt =>
                    attempt.Outcome == CentralDerivativeAttemptOutcome.Leased);
                if (activeAttempt is not null)
                {
                    activeAttempt.Outcome = sourceId == sourceArtifact.Id
                        && sourceArtifact.ObjectState == CentralArtifactObjectState.Quarantined
                            ? CentralDerivativeAttemptOutcome.Quarantined
                            : CentralDerivativeAttemptOutcome.RetryableFailure;
                    activeAttempt.ReasonCode = invalidationReason;
                    activeAttempt.EndedAtUtc = invalidatedAtUtc;
                }
                job.Status = reResolveWindow || preserveWindowResolution
                    ? CentralDerivativeJobStatus.Waiting
                    : CentralDerivativeJobStatus.RetryableFailure;
                job.AvailableAtUtc = wakeFrozenWindowAfterRepair ? invalidatedAtUtc : null;
                job.LeaseOwner = null;
                job.LeaseToken = null;
                job.LeaseAcquiredAtUtc = null;
                job.LeaseExpiresAtUtc = null;
                job.LastFailedAtUtc = invalidatedAtUtc;
                job.LastError = job.SourceCentralArtifactId == sourceId
                    ? CentralDerivativeJobScheduler.SourceInvalidatedReason
                    : CentralDerivativeJobScheduler.ResultInvalidatedReason;
                if (reResolveWindow)
                {
                    var requirement = job.InputRequirements.Single(item => item.Id
                        == invalidatedInput!.CentralDerivativeJobInputRequirementId);
                    dbContext.CentralDerivativeJobInputs.Remove(invalidatedInput!);
                    requirement.ResolutionState = CentralDerivativeInputResolutionState.Waiting;
                    requirement.ResolutionReasonCode = CentralDerivativeJobScheduler.SourceInvalidatedReason;
                    requirement.ResolvedAtUtc = null;
                    job.InputSetIdentitySha256 = null;
                    var timeout = job.ResolutionDeadlineUtc.HasValue && job.ResolutionStartedAtUtc.HasValue
                        ? job.ResolutionDeadlineUtc.Value - job.ResolutionStartedAtUtc.Value
                        : TimeSpan.FromMinutes(5);
                    job.ResolutionStartedAtUtc = invalidatedAtUtc;
                    job.ResolutionCompletedAtUtc = null;
                    job.ResolutionDeadlineUtc = invalidatedAtUtc
                        + (timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMinutes(5));
                    job.StateReasonCode = CentralDerivativeWindowReasonCodes.WaitingRequiredInput;
                }
                else if (preserveWindowResolution)
                {
                    job.StateReasonCode = CentralDerivativeWindowReasonCodes.WaitingRequiredInput;
                }
                job.UpdatedAtUtc = invalidatedAtUtc;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryRollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // SQL Server already rolled back a deadlock victim.
        }
    }

    private static Task DelayPersistenceRetryAsync(
        Guid artifactId,
        int attempt,
        CancellationToken cancellationToken)
    {
        var jitterMilliseconds = artifactId.ToByteArray()[0] % 17;
        var delayMilliseconds = Math.Min(250, 10 * (attempt + 1) + jitterMilliseconds);
        return Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken);
    }

    private async Task RemoveUncommittedObjectAsync(string storageReference, string objectKey)
    {
        bool committedObject;
        try
        {
            dbContext.ChangeTracker.Clear();
            committedObject = await dbContext.CentralArtifacts.AsNoTracking().AnyAsync(
                artifact => artifact.StorageReference == storageReference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (committedObject)
        {
            return;
        }

        try
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey), CancellationToken.None).ConfigureAwait(false);
        }
        catch (MinioException)
        {
            // Cleanup is compensating; preserve the original ingest failure.
        }
        catch (HttpRequestException)
        {
            // Cleanup is compensating; preserve the original ingest failure.
        }
        catch (IOException)
        {
            // Cleanup is compensating; preserve the original ingest failure.
        }
    }

    private async Task TryRemoveObjectAsync(string objectKey)
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey), CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (MinioException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
            if (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
    }

    private static void EnsureManifestMatches(CentralArtifact existing, ArtifactIngestManifest manifest)
    {
        var frame = existing.Frame ?? throw new InvalidOperationException("The central artifact frame was not loaded.");
        if (existing.ManifestSchemaVersion != manifest.SchemaVersion
            && existing.IngestIdentities.Any(identity => string.Equals(
                identity.IdempotencyKey, manifest.IdempotencyKey, StringComparison.OrdinalIgnoreCase)))
        {
            EnsureCompatibleArtifactMatches(existing, manifest);
            return;
        }
        if (existing.ArtifactId != manifest.ArtifactId
            || frame.AgentId != manifest.AgentId
            || frame.FrameId != manifest.FrameId
            || frame.CapturedAtUtc != manifest.CapturedAtUtc
            || existing.Role != manifest.Role
            || existing.RecipeVersion != manifest.RecipeVersion
            || existing.ManifestSchemaVersion != manifest.SchemaVersion
            || existing.MediaType != manifest.MediaType
            || existing.ByteLength != manifest.ByteLength
            || !string.Equals(existing.ChecksumSha256, manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactIngestConflictException("The idempotency key is already associated with different artifact metadata.");
        }
        EnsureSceneProvenanceMatches(frame, manifest);
    }

    private static void EnsureCompatibleArtifactMatches(CentralArtifact existing, ArtifactIngestManifest manifest)
    {
        var frame = existing.Frame ?? throw new InvalidOperationException("The central artifact frame was not loaded.");
        if (existing.ArtifactId != manifest.ArtifactId
            || frame.AgentId != manifest.AgentId
            || frame.FrameId != manifest.FrameId
            || frame.CapturedAtUtc != manifest.CapturedAtUtc
            || existing.Role != manifest.Role
            || existing.MediaType != manifest.MediaType
            || existing.ByteLength != manifest.ByteLength
            || !string.Equals(existing.ChecksumSha256, manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArtifactIngestConflictException("The artifact identity is already associated with different immutable metadata.");
        }
    }

    private static void EnsureFrameMatches(
        CentralFrame frame,
        DeviceRegistration registration,
        ArtifactIngestManifest manifest)
    {
        if (frame.RegistrationId != registration.Id
            || frame.DevicePublicId != registration.DevicePublicId
            || frame.ObservatoryId != registration.ObservatoryId
            || frame.AgentId != manifest.AgentId
            || frame.FrameId != manifest.FrameId
            || frame.CapturedAtUtc != manifest.CapturedAtUtc)
        {
            throw new ArtifactIngestConflictException("The frame identity is already associated with different capture metadata.");
        }
        EnsureSceneProvenanceMatches(frame, manifest);
        EnrichSceneProvenance(frame, manifest);
    }

    private static void EnsureNoLogicalArtifactConflict(CentralFrame frame, ArtifactIngestManifest manifest)
    {
        var existing = frame.Artifacts.FirstOrDefault(artifact =>
            artifact.ArtifactId == manifest.ArtifactId
            || !manifest.IsReconstructable
                && artifact.Role == manifest.Role
                && artifact.RecipeVersion == manifest.RecipeVersion);
        if (existing is null)
        {
            return;
        }
        if (manifest.IsReconstructable && existing.ArtifactId == manifest.ArtifactId)
        {
            EnsureCompatibleArtifactMatches(existing, manifest);
        }
        else
        {
            EnsureManifestMatches(existing, manifest);
        }
    }

    private static void EnsureSceneProvenanceMatches(CentralFrame frame, ArtifactIngestManifest manifest)
    {
        var scene = SerializeScene(manifest);
        if (frame.SceneProvenanceJson is not null && scene is not null
            && !string.Equals(frame.SceneProvenanceJson, scene, StringComparison.Ordinal))
        {
            throw new ArtifactIngestConflictException("The frame identity is already associated with different scene provenance.");
        }
    }

    private static void EnrichSceneProvenance(CentralFrame frame, ArtifactIngestManifest manifest)
        => frame.SceneProvenanceJson ??= SerializeScene(manifest);

    private static string? SerializeScene(ArtifactIngestManifest manifest)
        => manifest.Scene is null ? null : JsonSerializer.Serialize(manifest.Scene);

    private static ArtifactIngestResult CreateResult(CentralArtifact artifact, ArtifactIngestManifest manifest)
    {
        var frame = artifact.Frame ?? throw new InvalidOperationException("The central artifact frame was not loaded.");
        var ready = artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState is CentralReconstructionState.Complete or CentralReconstructionState.LegacyIncomplete;
        return new ArtifactIngestResult(
            new DeviceUploadResult(frame.RegistrationId, frame.ObservatoryId, artifact.StorageReference, artifact.ReceivedAtUtc),
            ready || !manifest.IsReconstructable);
    }

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _checksumRead;

        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Append(buffer.AsSpan(offset, read));
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Append(buffer[..read]);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Append(buffer.Span[..read]);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public string GetChecksumSha256()
        {
            if (_checksumRead)
            {
                throw new InvalidOperationException("The payload checksum has already been read.");
            }

            _checksumRead = true;
            return Convert.ToHexString(_hash.GetHashAndReset());
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
        private void Append(ReadOnlySpan<byte> bytes)
        {
            _hash.AppendData(bytes);
            BytesRead += bytes.Length;
        }
    }

    private sealed class ArtifactGate
    {
        private readonly Lock _sync = new();
        private int _references;
        private bool _retired;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }
                _references++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _references--;
                if (_references != 0)
                {
                    return false;
                }
                _retired = true;
                return true;
            }
        }
    }

    private enum ExistingArtifactReconciliationMode
    {
        MultipartDuplicate,
        StatusAcknowledgement
    }
}

internal sealed class ArtifactIntegrityException : Exception
{
    public ArtifactIntegrityException()
    {
    }

    public ArtifactIntegrityException(string message) : base(message)
    {
    }

    public ArtifactIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal sealed class ArtifactIngestConflictException : Exception
{
    public ArtifactIngestConflictException()
    {
    }

    public ArtifactIngestConflictException(string message) : base(message)
    {
    }

    public ArtifactIngestConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
