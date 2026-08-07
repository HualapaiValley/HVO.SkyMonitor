using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
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
    ReconstructionDescriptor? Descriptor,
    CaptureManifestCompleteness Completeness)
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
                legacy.RecipeVersion, legacy.IdempotencyKey, legacy.Scene, null, document.Completeness);
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
            descriptor,
            document.Completeness);
    }
}

/// <summary>Streams a versioned artifact into MinIO and records an idempotent metadata row.</summary>
internal sealed partial class ArtifactIngestService(
    ApplicationDbContext dbContext,
    IMinioClient minio,
    ICentralArtifactObjectReader objectReader,
    TimeProvider timeProvider,
    ICentralDerivativeJobScheduler derivativeJobScheduler,
    CentralIngestTelemetry telemetry,
    DeploymentLocationTelemetry deploymentLocationTelemetry,
    ILogger<ArtifactIngestService> logger,
    CentralObjectStorageNames? storageNames = null) : IArtifactIngestService
{
    private readonly CentralObjectStorageNames _storageNames = storageNames ?? new();
    private string Bucket => _storageNames.ArtifactBucket;
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
            .Include(frame => frame.Timing)
            .Include(frame => frame.Control)
            .Include(frame => frame.Profiles)
            .Include(frame => frame.Location)
            .AsSplitQuery()
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
                    dbContext.ChangeTracker.Clear();
                    var enriched = await ReconcileExistingAsync(
                        manifest,
                        timeProvider.GetUtcNow(),
                        ExistingArtifactReconciliationMode.CrossSchemaCompatibility,
                        cancellationToken,
                        compatibilityArtifact.Id,
                        registration).ConfigureAwait(false);
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

        var storageReference = CreateCanonicalStorageReference(devicePublicId, manifest, Bucket);
        var objectKey = storageReference[$"minio://{Bucket}/".Length..];
        var now = timeProvider.GetUtcNow();
        var persisted = false;
        try
        {
            var result = await PublishAndPersistUnderObjectLockAsync().ConfigureAwait(false);
            persisted = true;
            await ScheduleDerivativesAfterObjectLockAsync(
                registration.DevicePublicId ?? throw new InvalidOperationException(
                    "An active device registration has no public identity."),
                manifest.ArtifactId,
                now,
                cancellationToken).ConfigureAwait(false);
            telemetry.RecordRequest(
                manifest.SchemaVersion,
                result.ReadyForAcknowledgement ? "accepted" : "pending-reference",
                manifest.ByteLength,
                timeProvider.GetElapsedTime(started));
            return result;
        }
        catch (ExistingArtifactRequiresCurrentObjectLockException exception)
        {
            if (exception.RemovePublishedObject)
            {
                await RemoveUncommittedObjectWithLockAsync(storageReference, objectKey).ConfigureAwait(false);
            }
            var result = await ReconcileExistingAsync(
                manifest,
                now,
                exception.Mode,
                cancellationToken,
                exception.CentralArtifactId,
                exception.Mode == ExistingArtifactReconciliationMode.CrossSchemaCompatibility
                    ? registration
                    : null).ConfigureAwait(false);
            telemetry.RecordDuplicate(manifest.SchemaVersion);
            telemetry.RecordRequest(
                manifest.SchemaVersion,
                result.ReadyForAcknowledgement ? "duplicate" : "pending-reference",
                manifest.ByteLength,
                timeProvider.GetElapsedTime(started));
            return result;
        }
        catch
        {
            if (persisted)
            {
                throw;
            }
            try
            {
                if (await CentralObjectOwnershipFence.IsRetiredAsync(
                        dbContext, storageReference, CancellationToken.None, $"minio://{Bucket}/").ConfigureAwait(false))
                {
                    await ExpireRejectedIntentAsync(manifest, storageReference).ConfigureAwait(false);
                }
            }
            catch (DbException)
            {
                // Cleanup is compensating; preserve the original ingest failure.
            }
            catch (DbUpdateException)
            {
                // Cleanup is compensating; preserve the original ingest failure.
            }
            catch (InvalidOperationException)
            {
                // Cleanup is compensating; preserve the original ingest failure.
            }
            catch (TimeoutException)
            {
                // Cleanup is compensating; preserve the original ingest failure.
            }
            catch (OperationCanceledException)
            {
                // Cleanup is compensating; preserve the original ingest failure.
            }
            await RemoveUncommittedObjectWithLockAsync(storageReference, objectKey).ConfigureAwait(false);
            throw;
        }

        async Task<ArtifactIngestResult> PublishAndPersistUnderObjectLockAsync()
        {
            await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, storageReference, cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureStorageReferenceNotRetiredAsync(storageReference, cancellationToken).ConfigureAwait(false);
            }
            catch (ArtifactIngestConflictException)
            {
                await ExpireRejectedIntentAsync(manifest, storageReference).ConfigureAwait(false);
                throw;
            }
            if (manifest.IsReconstructable)
            {
                await EnsureV2IntentAsync(
                    registration, manifest, storageReference, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
            }
            try
            {
                await EnsureStorageReferenceNotRetiredAsync(storageReference, cancellationToken).ConfigureAwait(false);
            }
            catch (ArtifactIngestConflictException)
            {
                await ExpireRejectedIntentAsync(manifest, storageReference).ConfigureAwait(false);
                throw;
            }
            var source = new CopySourceObjectArgs().WithBucket(Bucket).WithObject(stagingKey);
            var copyStarted = timeProvider.GetTimestamp();
            try
            {
                await minio.CopyObjectAsync(new CopyObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                    .WithCopyObjectSource(source), cancellationToken).ConfigureAwait(false);
                telemetry.RecordObjectWrite("canonical-copy", "completed", timeProvider.GetElapsedTime(copyStarted));
            }
            catch
            {
                telemetry.RecordObjectWrite("canonical-copy", "failed", timeProvider.GetElapsedTime(copyStarted));
                throw;
            }
            var result = await PersistAsync(
                registration, manifest, storageReference, objectLock, now, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result.Upload.StorageReference, storageReference, StringComparison.Ordinal))
            {
                await RemoveUncommittedObjectUnderLockAsync(storageReference, objectKey).ConfigureAwait(false);
            }
            return result;
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

    internal static string CreateCanonicalStorageReference(
        Guid devicePublicId,
        ArtifactIngestManifest manifest,
        string bucket = Configuration.CentralObjectStorageOptions.DefaultArtifactBucket)
    {
        var mediaTypeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.MediaType)));
        return $"minio://{bucket}/artifacts/{devicePublicId:N}/{manifest.CapturedAtUtc:yyyy/MM/dd}/{manifest.IdempotencyKey}-{manifest.ChecksumSha256.ToUpperInvariant()}-{mediaTypeKey}.bin";
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
        await AcquireArtifactIdentityLocksAsync(
            registration.DevicePublicId!.Value,
            manifest.Descriptor?.Artifact.SourceArtifactIds.Append(manifest.ArtifactId) ?? [manifest.ArtifactId],
            cancellationToken).ConfigureAwait(false);
        await AcquireFrameIdentityLockAsync(
            registration.DevicePublicId.Value, manifest.FrameId, cancellationToken).ConfigureAwait(false);
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
            .Include(item => item.Location)
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
                LogicalCameraInstallationId = await ResolveCaptureInstallationAsync(
                    registration.Id, manifest.CapturedAtUtc, cancellationToken).ConfigureAwait(false),
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

    private static bool IsSqlDeadlock(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1205 })
            {
                return true;
            }
        }
        return false;
    }

    private static int? GetSqlErrorNumber(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException sqlException)
            {
                return sqlException.Number;
            }
        }
        return null;
    }

    private Task<Guid?> ResolveCaptureInstallationAsync(
        Guid registrationId,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
        => dbContext.LogicalCameraInstallations.AsNoTracking()
            .Where(installation => installation.RegistrationId == registrationId
                && installation.AssignedAtUtc <= capturedAtUtc
                && (installation.RetiredAtUtc == null || installation.RetiredAtUtc > capturedAtUtc))
            .Select(installation => (Guid?)installation.Id)
            .SingleOrDefaultAsync(cancellationToken);

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
        CancellationToken cancellationToken,
        Guid? knownArtifactId = null,
        DeviceRegistration? compatibilityRegistration = null)
    {
        const int maximumAttempts = 10;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                var target = knownArtifactId.HasValue
                    ? await dbContext.CentralArtifacts.AsNoTracking()
                        .Where(artifact => artifact.Id == knownArtifactId.Value)
                        .Select(artifact => new ExistingArtifactTarget(artifact.Id, artifact.StorageReference))
                        .SingleAsync(cancellationToken).ConfigureAwait(false)
                    : await dbContext.CentralArtifacts.AsNoTracking()
                        .Where(artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                            || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey))
                        .Select(artifact => new ExistingArtifactTarget(artifact.Id, artifact.StorageReference))
                        .SingleAsync(cancellationToken).ConfigureAwait(false);
                ArtifactIngestResult result;
                await using (var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                    dbContext, target.StorageReference, cancellationToken).ConfigureAwait(false))
                {
                    dbContext.ChangeTracker.Clear();
                    var currentReference = await dbContext.CentralArtifacts.AsNoTracking()
                        .Where(artifact => artifact.Id == target.ArtifactId)
                        .Select(artifact => artifact.StorageReference)
                        .SingleAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentReference, target.StorageReference, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    result = await ReconcileExistingUnderObjectLockAsync(
                        target.ArtifactId,
                        target.StorageReference,
                        objectLock,
                        manifest,
                        receivedAtUtc,
                        mode,
                        compatibilityRegistration,
                        cancellationToken).ConfigureAwait(false);
                }
                await ScheduleDerivativesAfterObjectLockAsync(
                    target.ArtifactId, receivedAtUtc, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (ExistingArtifactVerificationStaleException)
            {
                dbContext.ChangeTracker.Clear();
                if (attempt < maximumAttempts - 1)
                {
                    await DelayPersistenceRetryAsync(manifest.ArtifactId, attempt, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsPersistenceRace(exception))
            {
                dbContext.ChangeTracker.Clear();
                if (attempt >= maximumAttempts - 1)
                {
                    throw;
                }
                if (IsSqlDeadlock(exception))
                {
                    telemetry.RecordReconciliationConcurrency("sql-deadlock-retry");
                }
                await DelayPersistenceRetryAsync(manifest.ArtifactId, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ArtifactIngestConflictException(
            "The artifact object generation changed repeatedly during verification.");
    }

    private async Task<ArtifactIngestResult> ReconcileExistingUnderObjectLockAsync(
        Guid centralArtifactId,
        string lockedStorageReference,
        CentralObjectApplicationLock objectLock,
        ArtifactIngestManifest manifest,
        DateTimeOffset receivedAtUtc,
        ExistingArtifactReconciliationMode mode,
        DeviceRegistration? compatibilityRegistration,
        CancellationToken cancellationToken)
    {
        var preparation = await ReserveExistingVerificationAsync(
            centralArtifactId,
            lockedStorageReference,
            manifest,
            receivedAtUtc,
            mode,
            compatibilityRegistration,
            cancellationToken).ConfigureAwait(false);
        if (preparation.Result is not null)
        {
            return preparation.Result;
        }
        var reservation = preparation.Reservation
            ?? throw new InvalidOperationException("Artifact verification preparation omitted its reservation.");
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        var verificationStarted = timeProvider.GetTimestamp();
        ExistingObjectVerification verification;
        var probe = reservation.CreateProbe();
        try
        {
            var objectSnapshot = await objectReader.VerifyAsync(probe, cancellationToken).ConfigureAwait(false);
            verification = new ExistingObjectVerification(null, objectSnapshot.StorageETag);
            telemetry.RecordChecksum("stored-object", "matched");
            telemetry.RecordVerificationDuration(
                "stream", "matched", timeProvider.GetElapsedTime(verificationStarted));
        }
        catch (CentralArtifactMissingException)
        {
            verification = new ExistingObjectVerification("object.missing", null);
            telemetry.RecordChecksum("stored-object", "missing");
            telemetry.RecordVerificationDuration(
                "stream", "missing", timeProvider.GetElapsedTime(verificationStarted));
        }
        catch (CentralArtifactIntegrityException exception)
        {
            if (exception.StorageETag is { Length: > 0 }
                && !await objectReader.IsCurrentGenerationAsync(
                    probe, exception.StorageETag, cancellationToken).ConfigureAwait(false))
            {
                throw new ExistingArtifactVerificationStaleException();
            }
            verification = new ExistingObjectVerification(exception.ReasonCode, exception.StorageETag);
            telemetry.RecordChecksum("stored-object", exception.ReasonCode switch
            {
                "object.length-mismatch" => "length-mismatch",
                "object.checksum-mismatch" => "checksum-mismatch",
                _ => "integrity-failed"
            });
            telemetry.RecordVerificationDuration(
                "stream", "invalid", timeProvider.GetElapsedTime(verificationStarted));
        }
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        return await FinalizeExistingVerificationAsync(
            reservation,
            verification,
            manifest,
            receivedAtUtc,
            objectLock,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExistingVerificationPreparation> ReserveExistingVerificationAsync(
        Guid centralArtifactId,
        string lockedStorageReference,
        ArtifactIngestManifest manifest,
        DateTimeOffset receivedAtUtc,
        ExistingArtifactReconciliationMode mode,
        DeviceRegistration? compatibilityRegistration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = await dbContext.CentralArtifacts.AsNoTracking()
                .Where(artifact => artifact.Id == centralArtifactId)
                .Select(artifact => new { artifact.DevicePublicId, artifact.Frame!.FrameId })
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            var verificationDevicePublicId = identity.DevicePublicId ?? compatibilityRegistration?.DevicePublicId;
            if (verificationDevicePublicId.HasValue)
            {
                await AcquireArtifactIdentityLocksAsync(
                    verificationDevicePublicId.Value,
                    manifest.Descriptor?.Artifact.SourceArtifactIds.Append(manifest.ArtifactId) ?? [manifest.ArtifactId],
                    cancellationToken).ConfigureAwait(false);
                await AcquireFrameIdentityLockAsync(
                    verificationDevicePublicId.Value, identity.FrameId, cancellationToken).ConfigureAwait(false);
            }
            var existing = await LoadExistingArtifactAsync(centralArtifactId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.StorageReference, lockedStorageReference, StringComparison.Ordinal))
            {
                throw new ExistingArtifactVerificationStaleException();
            }
            await EnsureStorageReferenceNotRetiredAsync(lockedStorageReference, cancellationToken).ConfigureAwait(false);
            if (existing.ObjectState == CentralArtifactObjectState.Expired)
            {
                throw new ArtifactIngestConflictException("The immutable artifact object key has been permanently retired.");
            }
            if (existing.ObjectState == CentralArtifactObjectState.Quarantined
                && mode == ExistingArtifactReconciliationMode.StatusAcknowledgement)
            {
                throw new ArtifactIntegrityException("Stored artifact object is quarantined.");
            }

            if (mode == ExistingArtifactReconciliationMode.CrossSchemaCompatibility)
            {
                var registration = compatibilityRegistration
                    ?? throw new InvalidOperationException("Cross-schema verification requires the device registration.");
                EnsureCompatibleArtifactMatches(existing, manifest);
                if (existing.DevicePublicId is null
                    && await dbContext.CentralArtifacts.AnyAsync(candidate =>
                        candidate.Id != existing.Id
                        && candidate.DevicePublicId == registration.DevicePublicId
                        && candidate.ArtifactId == manifest.ArtifactId,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new ArtifactIngestConflictException(
                        "The artifact identity already has a canonical row for this device.");
                }
                existing.DevicePublicId ??= registration.DevicePublicId!.Value;
                EnsureSceneProvenanceMatches(existing.Frame!, manifest);
                EnrichSceneProvenance(existing.Frame!, manifest);
                if (!existing.IngestIdentities.Any(identity => string.Equals(
                    identity.IdempotencyKey, manifest.IdempotencyKey, StringComparison.OrdinalIgnoreCase)))
                {
                    dbContext.CentralArtifactIngestIdentities.Add(new CentralArtifactIngestIdentity
                    {
                        CentralArtifactId = existing.Id,
                        Artifact = existing,
                        ManifestSchemaVersion = manifest.SchemaVersion,
                        IdempotencyKey = manifest.IdempotencyKey.ToUpperInvariant()
                    });
                }
                if (manifest.IsReconstructable)
                {
                    var devicePublicId = registration.DevicePublicId
                        ?? throw new DeviceRegistrationException("Agent is not fully activated.");
                    await ApplyReconstructionAsync(
                        existing.Frame!,
                        existing,
                        manifest,
                        devicePublicId,
                        receivedAtUtc,
                        cancellationToken).ConfigureAwait(false);
                }
                await TryResolvePendingReferenceAsync(
                    existing, manifest, receivedAtUtc, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                EnsureManifestMatches(existing, manifest);
                EnrichSceneProvenance(existing.Frame!, manifest);
                await TryResolvePendingReferenceAsync(
                    existing, manifest, receivedAtUtc, cancellationToken).ConfigureAwait(false);
            }

            if (existing.ReconstructionState == CentralReconstructionState.PendingReference
                && existing.ObjectVerificationToken is null)
            {
                if (mode != ExistingArtifactReconciliationMode.StatusAcknowledgement)
                {
                    existing.ObjectState = CentralArtifactObjectState.Available;
                }
                existing.ReconciledAtUtc = receivedAtUtc;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var result = CreateResult(existing, manifest);
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                return new ExistingVerificationPreparation(null, result);
            }

            existing.ObjectVerificationToken = Guid.NewGuid();
            existing.ObjectVerificationRequestedAtUtc = timeProvider.GetUtcNow();
            existing.ObjectVerificationRetryCount = 0;
            existing.ObjectVerificationRetryAtUtc = null;
            existing.ObjectState = CentralArtifactObjectState.Pending;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var reservation = ExistingArtifactVerificationReservation.Create(existing);
            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            return new ExistingVerificationPreparation(reservation, null);
        }
        catch (Exception exception) when (IsSqlDeadlock(exception))
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsPersistenceRace(exception))
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            var reason = exception is DbUpdateConcurrencyException concurrency
                ? "concurrency:" + string.Join(',', concurrency.Entries.Select(static entry => entry.Metadata.ClrType.Name))
                : exception.Message;
            throw new ExistingArtifactVerificationStaleException(reason, exception);
        }
        catch (DbUpdateException exception)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Artifact verification reservation database update failed ({GetSqlErrorNumber(exception)}).",
                exception);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ArtifactIngestResult> FinalizeExistingVerificationAsync(
        ExistingArtifactVerificationReservation reservation,
        ExistingObjectVerification verification,
        ArtifactIngestManifest manifest,
        DateTimeOffset receivedAtUtc,
        CentralObjectApplicationLock objectLock,
        CancellationToken cancellationToken)
    {
        var probe = reservation.CreateProbe();
        if (verification.StorageETag is { Length: > 0 }
            && !await objectReader.IsCurrentGenerationAsync(
                probe, verification.StorageETag, cancellationToken).ConfigureAwait(false))
        {
            throw new ExistingArtifactVerificationStaleException("object-generation");
        }
        if (verification.ReasonCode == "object.missing"
            && !await IsStoredObjectMissingAsync(probe, cancellationToken).ConfigureAwait(false))
        {
            throw new ExistingArtifactVerificationStaleException("object-appeared");
        }
        await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
        var finalizeStarted = timeProvider.GetTimestamp();
        var integrityFailure = verification.ReasonCode is not null;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            integrityFailure
                ? IsolationLevel.Serializable
                : IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (reservation.DevicePublicId.HasValue)
            {
                await AcquireArtifactIdentityLocksAsync(
                    reservation.DevicePublicId.Value, [reservation.ArtifactId], cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            var existing = await LoadExistingArtifactAsync(reservation.CentralArtifactId, cancellationToken)
                .ConfigureAwait(false);
            var mismatch = reservation.GetMismatch(existing);
            if (mismatch is not null)
            {
                throw new ExistingArtifactVerificationStaleException(mismatch);
            }
            if (existing.ObjectState == CentralArtifactObjectState.Expired
                || await CentralObjectOwnershipFence.IsRetiredAsync(
                    dbContext, reservation.StorageReference, cancellationToken, $"minio://{Bucket}/").ConfigureAwait(false))
            {
                throw new ExistingArtifactVerificationStaleException("retired");
            }
            EnsureManifestMatches(existing, manifest);
            existing.ObjectVerifiedAtUtc = timeProvider.GetUtcNow();
            existing.ReconciledAtUtc = receivedAtUtc;
            existing.ObjectVerificationToken = null;
            existing.ObjectVerificationRequestedAtUtc = null;
            existing.ObjectVerificationRetryCount = 0;
            existing.ObjectVerificationRetryAtUtc = null;
            if (integrityFailure)
            {
                QuarantineObject(existing, verification.ReasonCode!);
                await InvalidateDependentsAsync(dbContext, existing, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                existing.ObjectState = CentralArtifactObjectState.Available;
                await ResolveWaitingSourcesAsync(
                    existing, existing.Frame!.DevicePublicId, cancellationToken).ConfigureAwait(false);
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await objectLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);
            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            telemetry.RecordVerificationDuration(
                "finalize",
                integrityFailure ? "quarantined" : "available",
                timeProvider.GetElapsedTime(finalizeStarted));
            if (integrityFailure)
            {
                throw new ArtifactIntegrityException(
                    "Stored artifact object is missing or inconsistent with SQL metadata.");
            }
            return CreateResult(existing, manifest);
        }
        catch (ArtifactIntegrityException)
        {
            throw;
        }
        catch (Exception exception) when (IsSqlDeadlock(exception))
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is ExistingArtifactVerificationStaleException
            || IsPersistenceRace(exception))
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            var reason = exception is DbUpdateConcurrencyException concurrency
                ? "concurrency:" + string.Join(',', concurrency.Entries.Select(static entry => entry.Metadata.ClrType.Name))
                : exception.Message;
            throw new ExistingArtifactVerificationStaleException(reason, exception);
        }
        catch (DbUpdateException exception)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Artifact verification finalization database update failed ({GetSqlErrorNumber(exception)}).",
                exception);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private Task<CentralArtifact> LoadExistingArtifactAsync(Guid centralArtifactId, CancellationToken cancellationToken)
        => dbContext.CentralArtifacts
            .Include(artifact => artifact.IngestIdentities)
            .Include(artifact => artifact.Layout)
            .Include(artifact => artifact.Recipe)
            .Include(artifact => artifact.Sources)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Control)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
            .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Location)
            .AsSplitQuery()
            .SingleAsync(artifact => artifact.Id == centralArtifactId, cancellationToken);

    private async Task<bool> IsStoredObjectMissingAsync(
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        var prefix = $"minio://{Bucket}/";
        if (!artifact.StorageReference.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            _ = await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(Bucket)
                .WithObject(artifact.StorageReference[prefix.Length..]), cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return true;
        }
    }

    private async Task<ArtifactIngestResult> PersistAsync(
        DeviceRegistration registration,
        ArtifactIngestManifest manifest,
        string storageReference,
        CentralObjectApplicationLock objectLock,
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
                await AcquireArtifactIdentityLocksAsync(
                    registration.DevicePublicId!.Value,
                    manifest.Descriptor?.Artifact.SourceArtifactIds.Append(manifest.ArtifactId) ?? [manifest.ArtifactId],
                    cancellationToken).ConfigureAwait(false);
                await AcquireFrameIdentityLockAsync(
                    registration.DevicePublicId.Value, manifest.FrameId, cancellationToken).ConfigureAwait(false);
                await EnsureStorageReferenceNotRetiredAsync(storageReference, cancellationToken).ConfigureAwait(false);
                var existing = await dbContext.CentralArtifacts
                    .Include(artifact => artifact.IngestIdentities)
                    .Include(artifact => artifact.Sources)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Artifacts)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
                    .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Location)
                    .AsSplitQuery()
                    .SingleOrDefaultAsync(
                        artifact => artifact.IdempotencyKey == manifest.IdempotencyKey
                            || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == manifest.IdempotencyKey), cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.ManifestSchemaVersion == manifest.SchemaVersion)
                    {
                        EnsureManifestMatches(existing, manifest);
                    }
                    else
                    {
                        EnsureCompatibleArtifactMatches(existing, manifest);
                        EnsureSceneProvenanceMatches(existing.Frame!, manifest);
                    }
                    if (!string.Equals(existing.StorageReference, storageReference, StringComparison.Ordinal))
                    {
                        if (existing.ObjectState == CentralArtifactObjectState.Available)
                        {
                            await TryRollbackAsync(transaction).ConfigureAwait(false);
                            dbContext.ChangeTracker.Clear();
                            throw new ExistingArtifactRequiresCurrentObjectLockException(
                                existing.Id,
                                existing.ManifestSchemaVersion == manifest.SchemaVersion
                                    ? ExistingArtifactReconciliationMode.MultipartDuplicate
                                    : ExistingArtifactReconciliationMode.CrossSchemaCompatibility,
                                removePublishedObject: true);
                        }
                        existing.StorageReference = storageReference;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await TryRollbackAsync(transaction).ConfigureAwait(false);
                    }
                    dbContext.ChangeTracker.Clear();
                    var reconciliationMode = existing.ManifestSchemaVersion == manifest.SchemaVersion
                        ? ExistingArtifactReconciliationMode.MultipartDuplicate
                        : ExistingArtifactReconciliationMode.CrossSchemaCompatibility;
                    try
                    {
                        return await ReconcileExistingUnderObjectLockAsync(
                            existing.Id,
                            storageReference,
                            objectLock,
                            manifest,
                            receivedAtUtc,
                            reconciliationMode,
                            reconciliationMode == ExistingArtifactReconciliationMode.CrossSchemaCompatibility
                                ? registration
                                : null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (ExistingArtifactVerificationStaleException)
                    {
                        throw new ExistingArtifactRequiresCurrentObjectLockException(
                            existing.Id, reconciliationMode, removePublishedObject: false);
                    }
                }

                var devicePublicId = registration.DevicePublicId!.Value;
                var frame = await dbContext.CentralFrames
                    .Include(item => item.Artifacts).ThenInclude(artifact => artifact.IngestIdentities)
                    .Include(item => item.Timing)
                    .Include(item => item.Control)
                    .Include(item => item.Profiles)
                    .Include(item => item.Location)
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
                        LogicalCameraInstallationId = await ResolveCaptureInstallationAsync(
                            registration.Id, manifest.CapturedAtUtc, cancellationToken).ConfigureAwait(false),
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
                        if (!string.Equals(
                            crossSchemaArtifact.StorageReference, storageReference, StringComparison.Ordinal))
                        {
                            if (crossSchemaArtifact.ObjectState == CentralArtifactObjectState.Available)
                            {
                                await TryRollbackAsync(transaction).ConfigureAwait(false);
                                dbContext.ChangeTracker.Clear();
                                throw new ExistingArtifactRequiresCurrentObjectLockException(
                                    crossSchemaArtifact.Id,
                                    ExistingArtifactReconciliationMode.CrossSchemaCompatibility,
                                    removePublishedObject: true);
                            }
                            crossSchemaArtifact.StorageReference = storageReference;
                            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await TryRollbackAsync(transaction).ConfigureAwait(false);
                        }
                        dbContext.ChangeTracker.Clear();
                        try
                        {
                            return await ReconcileExistingUnderObjectLockAsync(
                                crossSchemaArtifact.Id,
                                storageReference,
                                objectLock,
                                manifest,
                                receivedAtUtc,
                                ExistingArtifactReconciliationMode.CrossSchemaCompatibility,
                                registration,
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (ExistingArtifactVerificationStaleException)
                        {
                            throw new ExistingArtifactRequiresCurrentObjectLockException(
                                crossSchemaArtifact.Id,
                                ExistingArtifactReconciliationMode.CrossSchemaCompatibility,
                                removePublishedObject: false);
                        }
                    }
                    EnsureNoLogicalArtifactConflict(frame, manifest);
                }

                var artifact = CreateArtifact(frame, manifest, storageReference, receivedAtUtc);
                artifact.DevicePublicId = devicePublicId;
                dbContext.CentralArtifacts.Add(artifact);
                await ApplyReconstructionAsync(frame, artifact, manifest, devicePublicId, receivedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
                await ResolveWaitingSourcesAsync(artifact, devicePublicId, cancellationToken).ConfigureAwait(false);
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
                        throw new ExistingArtifactRequiresCurrentObjectLockException(
                            concurrent.Id,
                            concurrent.ManifestSchemaVersion == manifest.SchemaVersion
                                ? ExistingArtifactReconciliationMode.MultipartDuplicate
                                : ExistingArtifactReconciliationMode.CrossSchemaCompatibility,
                            removePublishedObject: true);
                    }
                    throw;
                }
                await DelayPersistenceRetryAsync(manifest.ArtifactId, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ArtifactIngestConflictException("The frame or artifact identity is already associated with different metadata.");
    }

    private async Task EnsureStorageReferenceNotRetiredAsync(
        string storageReference,
        CancellationToken cancellationToken)
    {
        if (await CentralObjectOwnershipFence.IsRetiredAsync(
                dbContext, storageReference, cancellationToken, $"minio://{Bucket}/").ConfigureAwait(false))
        {
            throw new ArtifactIngestConflictException(
                "The immutable artifact object key has been permanently retired.");
        }
    }

    private async Task ExpireRejectedIntentAsync(
        ArtifactIngestManifest manifest,
        string storageReference)
    {
        var idempotencyKey = manifest.IdempotencyKey.ToUpperInvariant();
        var now = timeProvider.GetUtcNow();
        await dbContext.CentralArtifacts.Where(artifact =>
                artifact.ArtifactId == manifest.ArtifactId
                && artifact.ObjectState == CentralArtifactObjectState.Pending
                && artifact.RetentionDeletionToken == null
                && (artifact.IdempotencyKey == idempotencyKey
                    || artifact.IngestIdentities.Any(identity => identity.IdempotencyKey == idempotencyKey))
                && EF.Functions.Collate(
                    artifact.StorageReference,
                    CentralObjectOwnershipFence.BinaryCollation) == storageReference)
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
        => artifact.ReconstructionState == CentralReconstructionState.Complete &&
            (artifact.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion ||
                artifact.Role == FrameArtifactRole.Raw);

    private async Task ScheduleDerivativesAfterObjectLockAsync(
        Guid devicePublicId,
        Guid artifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var shouldSchedule = await dbContext.CentralArtifacts.AsNoTracking().AnyAsync(artifact =>
            artifact.DevicePublicId == devicePublicId && artifact.ArtifactId == artifactId &&
            artifact.ReconstructionState == CentralReconstructionState.Complete &&
            (artifact.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion ||
                artifact.Role == FrameArtifactRole.Raw), cancellationToken).ConfigureAwait(false);
        if (shouldSchedule)
        {
            await derivativeJobScheduler.EnsureRequiredJobsAsync(
                devicePublicId, artifactId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScheduleDerivativesAfterObjectLockAsync(
        Guid centralArtifactId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var identity = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.Id == centralArtifactId &&
                artifact.ReconstructionState == CentralReconstructionState.Complete &&
                (artifact.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion ||
                    artifact.Role == FrameArtifactRole.Raw))
            .Select(artifact => new
            {
                DevicePublicId = artifact.DevicePublicId ?? artifact.Frame!.DevicePublicId,
                artifact.ArtifactId
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (identity is not null)
        {
            await derivativeJobScheduler.EnsureRequiredJobsAsync(
                identity.DevicePublicId, identity.ArtifactId, now, cancellationToken).ConfigureAwait(false);
        }
    }

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
            if (frame.Location is null
                && frame.LocationEvidenceState != CentralCaptureLocationEvidenceState.LegacyIncomplete)
            {
                await dbContext.Entry(frame).Reference(item => item.Location).LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            EnsureCaptureFactsMatch(frame, descriptor, cycleEvidenceJson);
        }
        frame.RigId = capture.RigId;
        frame.CaptureSequence = capture.CaptureSequence;
        frame.CycleEvidenceJson ??= cycleEvidenceJson;
        if (frame.Location is null && descriptor.Location is { } location)
        {
            await BindCaptureLocationAsync(frame, location, cancellationToken).ConfigureAwait(false);
        }
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
        artifact.Layout ??= new CentralArtifactLayout
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
            StoredCodeTransform = descriptor.Layout.StoredCodeTransform?.ToString(),
            LevelCodeSpace = descriptor.Layout.LevelCodeSpace?.ToString(),
            NativeWidth = descriptor.Layout.Readout?.NativeWidth,
            NativeHeight = descriptor.Layout.Readout?.NativeHeight,
            RoiX = descriptor.Layout.Readout?.RoiX,
            RoiY = descriptor.Layout.Readout?.RoiY,
            RoiWidth = descriptor.Layout.Readout?.RoiWidth,
            RoiHeight = descriptor.Layout.Readout?.RoiHeight,
            BinX = descriptor.Layout.Readout?.BinX,
            BinY = descriptor.Layout.Readout?.BinY,
            BinningAlgorithm = descriptor.Layout.Readout?.BinningAlgorithm.ToString(),
            CfaOriginX = descriptor.Layout.Readout?.CfaOriginX,
            CfaOriginY = descriptor.Layout.Readout?.CfaOriginY,
            ByteLength = descriptor.Layout.ByteLength
        };
        artifact.Recipe ??= new CentralArtifactRecipe
        {
            Name = descriptor.Artifact.Recipe.Name,
            SemanticVersion = descriptor.Artifact.Recipe.SemanticVersion,
            ImplementationVersion = descriptor.Artifact.Recipe.ImplementationVersion,
            OptionsJson = JsonSerializer.Serialize(CaptureContractJson.Canonicalize(descriptor.Artifact.Recipe.Options)),
            OptionsSha256 = descriptor.Artifact.Recipe.OptionsSha256.ToUpperInvariant()
        };
        await AcquireArtifactIdentityLocksAsync(
            devicePublicId, descriptor.Artifact.SourceArtifactIds, cancellationToken).ConfigureAwait(false);
        for (var ordinal = artifact.Sources.Count; ordinal < descriptor.Artifact.SourceArtifactIds.Count; ordinal++)
        {
            var sourceArtifactId = descriptor.Artifact.SourceArtifactIds[ordinal];
            var resolved = await dbContext.CentralArtifacts.FirstOrDefaultAsync(candidate =>
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
        SetReconstructionState(artifact, rigProfile is not null, manifestCompleteness: manifest.Completeness);
        AddIfDetached(frame.Timing);
        AddIfDetached(frame.Control);
        AddIfDetached(frame.Location);
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
            .Include(source => source.Artifact)!.ThenInclude(sourceArtifact => sourceArtifact!.Layout)
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
            dbContext.Entry(waitingArtifact).Property(candidate => candidate.ReconciledAtUtc).IsModified = true;
        }
    }

    private async Task AcquireArtifactIdentityLocksAsync(
        Guid devicePublicId,
        IEnumerable<Guid> artifactIds,
        CancellationToken cancellationToken)
    {
        foreach (var artifactId in artifactIds.Distinct().Order())
        {
            var resource = $"hvo-central-artifact-identity:{devicePublicId:N}:{artifactId:N}";
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = {resource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000;
                IF @result < 0
                    THROW 51008, 'Could not acquire the central artifact identity lock.', 1;
                """, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcquireFrameIdentityLockAsync(
        Guid devicePublicId,
        Guid frameId,
        CancellationToken cancellationToken)
    {
        var resource = CreateFrameIdentityLockResource(devicePublicId, frameId);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = {resource},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @result < 0
                THROW 51009, 'Could not acquire the central frame identity lock.', 1;
            """, cancellationToken).ConfigureAwait(false);
    }

    internal static string CreateFrameIdentityLockResource(Guid devicePublicId, Guid frameId)
        => $"hvo-central-frame-identity:{devicePublicId:N}:{frameId:N}";

    private static void EnsureCaptureFactsMatch(
        CentralFrame frame,
        ReconstructionDescriptor descriptor,
        string? cycleEvidenceJson)
    {
        if (!LocationMatches(frame.Location, descriptor.Location))
        {
            throw new ArtifactIngestConflictException(
                $"The frame identity is already associated with different capture-time location facts ({LocationMismatchField(frame.Location, descriptor.Location)}-{frame.LocationEvidenceState}).");
        }
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

    private async Task BindCaptureLocationAsync(
        CentralFrame frame,
        CaptureLocationProvenance location,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = DeploymentLocationTelemetry.ActivitySource.StartActivity(
            "deployment-location.ingest-bind");
        var localDeployments = dbContext.DeviceDeploymentLocationVersions.Local
            .Where(item =>
                item.RegistrationId == frame.RegistrationId
                && item.LocationId == location.LocationId
                && item.Version == location.Version)
            .ToArray();
        var deployments = await dbContext.DeviceDeploymentLocationVersions
            .Include(item => item.ObservatoryLocationVersion)
            .Where(item =>
                item.RegistrationId == frame.RegistrationId
                && item.LocationId == location.LocationId
                && item.Version == location.Version)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        deployments.AddRange(localDeployments.Where(local => deployments.All(item => item.Id != local.Id)));
        var deployment = deployments
            .Where(item => item.ObservatoryLocationVersion is { } version
                && ObservatoryLocationAuthority.AppliesAt(version, frame.CapturedAtUtc))
            .OrderByDescending(item => item.ObservatoryLocationVersionNumber)
            .FirstOrDefault();
        var appliesAtCapture = deployment is not null
            && DeploymentLocationAuthorityService.AppliesAt(deployment, frame.CapturedAtUtc);
        var exactMatch = appliesAtCapture && LocationMatches(deployment!, location);
        frame.Location = new CentralCaptureLocation
        {
            CentralFrame = frame,
            CentralFrameId = frame.Id,
            DeviceDeploymentLocationVersionId = exactMatch ? deployment!.Id : null,
            DeploymentLocation = exactMatch ? deployment : null,
            LocationId = location.LocationId,
            Version = location.Version,
            Source = location.Source,
            HorizontalAccuracyMeters = location.HorizontalAccuracyMeters,
            EffectiveFromUtc = location.EffectiveFromUtc,
            EffectiveUntilUtc = location.EffectiveUntilUtc
        };
        frame.LocationEvidenceState = deployment switch
        {
            null => CentralCaptureLocationEvidenceState.ReportedUnresolved,
            _ when !exactMatch => CentralCaptureLocationEvidenceState.Mismatch,
            { Status: DeploymentLocationResolutionStatus.Acknowledged } =>
                CentralCaptureLocationEvidenceState.ReportedResolved,
            _ => CentralCaptureLocationEvidenceState.Mismatch
        };
        if (exactMatch)
        {
            frame.ObservatoryId = deployment!.ObservatoryId;
        }
        var versionRelation = deployment is null
            ? "unknown"
            : !appliesAtCapture ? "outside-interval" : exactMatch ? "exact" : "conflict";
        var outcome = frame.LocationEvidenceState.ToString();
        activity?.SetTag("deployment.outcome", outcome);
        activity?.SetTag("deployment.reason", versionRelation);
        activity?.SetTag("deployment.source_kind", deployment?.SourceKind.ToString() ?? "Unknown");
        activity?.SetTag("deployment.version_relation", versionRelation);
        deploymentLocationTelemetry.RecordOperation(
            "ingest-bind", outcome, versionRelation, "capture", timeProvider.GetElapsedTime(started));
        LocationLog.IngestBinding(logger, outcome, versionRelation, deployment?.SourceKind.ToString() ?? "Unknown");
    }

    private static partial class LocationLog
    {
        [LoggerMessage(7410, LogLevel.Information,
            "Deployment location ingest binding completed: Outcome={Outcome}, VersionRelation={VersionRelation}, SourceKind={SourceKind}")]
        internal static partial void IngestBinding(
            ILogger logger,
            string outcome,
            string versionRelation,
            string sourceKind);
    }

    private static bool LocationMatches(CentralCaptureLocation? persisted, CaptureLocationProvenance? reported)
        => persisted is null && reported is null
            || persisted is not null && reported is not null
            && persisted.LocationId == reported.LocationId
            && persisted.Version == reported.Version
            && string.Equals(persisted.Source, reported.Source, StringComparison.Ordinal)
            && persisted.HorizontalAccuracyMeters == reported.HorizontalAccuracyMeters
            && persisted.EffectiveFromUtc == reported.EffectiveFromUtc
            && persisted.EffectiveUntilUtc == reported.EffectiveUntilUtc;

    private static string LocationMismatchField(
        CentralCaptureLocation? persisted,
        CaptureLocationProvenance? reported)
    {
        if (persisted is null || reported is null)
        {
            return persisted is null ? "persisted-missing" : "reported-missing";
        }
        if (persisted.LocationId != reported.LocationId)
        {
            return "identity";
        }
        if (persisted.Version != reported.Version)
        {
            return "version";
        }
        if (!string.Equals(persisted.Source, reported.Source, StringComparison.Ordinal))
        {
            return "source";
        }
        if (persisted.HorizontalAccuracyMeters != reported.HorizontalAccuracyMeters)
        {
            return "accuracy";
        }
        return persisted.EffectiveFromUtc != reported.EffectiveFromUtc ? "effective-from" : "effective-until";
    }

    private static bool LocationMatches(
        DeviceDeploymentLocationVersion persisted,
        CaptureLocationProvenance reported)
        => persisted.LocationId == reported.LocationId
            && persisted.Version == reported.Version
            && string.Equals(persisted.Source, reported.Source, StringComparison.Ordinal)
            && persisted.HorizontalAccuracyMeters == reported.HorizontalAccuracyMeters
            && persisted.EffectiveFromUtc == reported.EffectiveFromUtc
            && persisted.EffectiveUntilUtc == reported.EffectiveUntilUtc;

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
        SetReconstructionState(
            artifact,
            frame.DeviceRigProfileId.HasValue,
            unavailableSource: unavailableSource,
            manifestCompleteness: manifest.Completeness);
        artifact.ReconciledAtUtc = reconciledAtUtc;
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
        bool unavailableSource = false,
        CaptureManifestCompleteness? manifestCompleteness = null)
    {
        if (manifestCompleteness == CaptureManifestCompleteness.LegacyIncomplete ||
            artifact.Layout is { SampleDepthBits: var sampleDepth, ContainerDepthBits: var containerDepth } layout &&
            sampleDepth < containerDepth && (layout.StoredCodeTransform is null || layout.LevelCodeSpace is null))
        {
            artifact.ReconstructionState = CentralReconstructionState.LegacyIncomplete;
            artifact.StateReasonCode = "layout.stored-code-ambiguous";
            artifact.ReferenceRetryCount = 0;
            artifact.ReferenceRetryAtUtc = null;
            return;
        }
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

    private async Task RemoveUncommittedObjectWithLockAsync(string storageReference, string objectKey)
    {
        try
        {
            await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, storageReference, CancellationToken.None).ConfigureAwait(false);
            await RemoveUncommittedObjectUnderLockAsync(storageReference, objectKey).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // Cleanup is compensating; preserve the original ingest failure.
        }
        catch (InvalidOperationException)
        {
            // Cleanup is compensating; preserve the original ingest failure.
        }
    }

    private async Task RemoveUncommittedObjectUnderLockAsync(string storageReference, string objectKey)
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
        catch (TimeoutException)
        {
            // Cleanup is compensating; preserve the original ingest failure.
        }
        catch (OperationCanceledException)
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
            if (frame.CaptureSequence.HasValue && manifest.Descriptor is { } descriptor)
            {
                var cycleEvidenceJson = descriptor.CycleEvidence is null
                    ? null
                    : JsonSerializer.Serialize(descriptor.CycleEvidence);
                EnsureCaptureFactsMatch(frame, descriptor, cycleEvidenceJson);
            }
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
            ready);
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

    private sealed record ExistingArtifactTarget(Guid ArtifactId, string StorageReference);

    private sealed record ExistingVerificationPreparation(
        ExistingArtifactVerificationReservation? Reservation,
        ArtifactIngestResult? Result);

    private sealed record ExistingObjectVerification(string? ReasonCode, string? StorageETag);

    private sealed record ExistingArtifactVerificationReservation(
        Guid CentralArtifactId,
        Guid CentralFrameId,
        Guid ArtifactId,
        Guid? DevicePublicId,
        string StorageReference,
        long ByteLength,
        string ChecksumSha256,
        Guid Token,
        byte[] RowVersion)
    {
        internal static ExistingArtifactVerificationReservation Create(CentralArtifact artifact)
            => new(
                artifact.Id,
                artifact.CentralFrameId,
                artifact.ArtifactId,
                artifact.DevicePublicId,
                artifact.StorageReference,
                artifact.ByteLength,
                artifact.ChecksumSha256,
                artifact.ObjectVerificationToken
                    ?? throw new InvalidOperationException("Artifact verification token was not assigned."),
                [.. artifact.RowVersion]);

        internal CentralArtifact CreateProbe()
            => new()
            {
                Id = CentralArtifactId,
                CentralFrameId = CentralFrameId,
                ArtifactId = ArtifactId,
                DevicePublicId = DevicePublicId,
                StorageReference = StorageReference,
                ByteLength = ByteLength,
                ChecksumSha256 = ChecksumSha256
            };

        internal string? GetMismatch(CentralArtifact artifact)
        {
            if (artifact.Id != CentralArtifactId) return "central-artifact-id";
            if (artifact.CentralFrameId != CentralFrameId) return "central-frame-id";
            if (artifact.ArtifactId != ArtifactId) return "artifact-id";
            if (artifact.DevicePublicId != DevicePublicId) return "device-public-id";
            if (!string.Equals(artifact.StorageReference, StorageReference, StringComparison.Ordinal)) return "storage-reference";
            if (artifact.ByteLength != ByteLength) return "byte-length";
            if (!string.Equals(artifact.ChecksumSha256, ChecksumSha256, StringComparison.OrdinalIgnoreCase)) return "checksum";
            if (artifact.ObjectVerificationToken != Token) return "verification-token";
            return artifact.RowVersion.AsSpan().SequenceEqual(RowVersion) ? null : "row-version";
        }
    }

    private sealed class ExistingArtifactVerificationStaleException : Exception
    {
        public ExistingArtifactVerificationStaleException()
        {
        }

        public ExistingArtifactVerificationStaleException(string message)
            : base(message)
        {
        }

        public ExistingArtifactVerificationStaleException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        internal ExistingArtifactVerificationStaleException(Exception innerException)
            : base("The artifact verification reservation is stale.", innerException)
        {
        }
    }

    private sealed class ExistingArtifactRequiresCurrentObjectLockException : Exception
    {
        public ExistingArtifactRequiresCurrentObjectLockException()
        {
        }

        public ExistingArtifactRequiresCurrentObjectLockException(string message)
            : base(message)
        {
        }

        public ExistingArtifactRequiresCurrentObjectLockException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        internal ExistingArtifactRequiresCurrentObjectLockException(
            Guid centralArtifactId,
            ExistingArtifactReconciliationMode mode,
            bool removePublishedObject)
        {
            CentralArtifactId = centralArtifactId;
            Mode = mode;
            RemovePublishedObject = removePublishedObject;
        }

        internal Guid CentralArtifactId { get; }

        internal ExistingArtifactReconciliationMode Mode { get; }

        internal bool RemovePublishedObject { get; }
    }

    private enum ExistingArtifactReconciliationMode
    {
        MultipartDuplicate,
        StatusAcknowledgement,
        CrossSchemaCompatibility
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
