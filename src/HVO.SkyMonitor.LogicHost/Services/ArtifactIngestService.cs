using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IArtifactIngestService
{
    Task<DeviceUploadResult> IngestAsync(ArtifactUploadManifest manifest, Stream payload, CancellationToken cancellationToken);
}

/// <summary>Streams a versioned artifact into MinIO and records an idempotent metadata row.</summary>
internal sealed class ArtifactIngestService(
    ApplicationDbContext dbContext,
    IMinioClient minio,
    TimeProvider timeProvider) : IArtifactIngestService
{
    private const string Bucket = "skymonitor-artifacts";
    private const string ObjectContentType = "application/octet-stream";

    public async Task<DeviceUploadResult> IngestAsync(ArtifactUploadManifest manifest, Stream payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        manifest.Validate();

        var existing = await dbContext.CentralArtifacts.Include(artifact => artifact.Frame).SingleOrDefaultAsync(
            artifact => artifact.IdempotencyKey == manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureManifestMatches(existing, manifest);
            EnrichSceneProvenance(existing.Frame!, manifest);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return CreateResult(existing);
        }

        var registration = await dbContext.DeviceRegistrations.SingleOrDefaultAsync(
            item => item.DeviceId == manifest.AgentId && item.Status == DeviceRegistrationStatus.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Agent is not registered or active.");
        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Agent is not fully activated.");
        }

        var existingFrame = await dbContext.CentralFrames.Include(frame => frame.Artifacts).SingleOrDefaultAsync(
            frame => frame.DevicePublicId == registration.DevicePublicId.Value && frame.FrameId == manifest.FrameId,
            cancellationToken).ConfigureAwait(false);
        if (existingFrame is not null)
        {
            EnsureFrameMatches(existingFrame, registration, manifest);
            EnsureNoLogicalArtifactConflict(existingFrame, manifest);
            dbContext.ChangeTracker.Clear();
        }

        var bucketExists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false);
        if (!bucketExists)
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false);
        }

        var objectKey = $"{manifest.AgentId}/{manifest.CapturedAtUtc:yyyy/MM/dd}/{manifest.IdempotencyKey}-{manifest.ChecksumSha256.ToUpperInvariant()}.bin";
        var stagingKey = $"staging/{Guid.NewGuid():N}";
        try
        {
            using var verifyingPayload = new HashingReadStream(payload);
            await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(stagingKey).WithStreamData(verifyingPayload).WithObjectSize(manifest.ByteLength)
                .WithContentType(ObjectContentType), cancellationToken).ConfigureAwait(false);
            if (verifyingPayload.BytesRead != manifest.ByteLength
                || !string.Equals(verifyingPayload.GetChecksumSha256(), manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArtifactIntegrityException("Payload length or checksum does not match the artifact manifest.");
            }
            var source = new CopySourceObjectArgs().WithBucket(Bucket).WithObject(stagingKey);
            await minio.CopyObjectAsync(new CopyObjectArgs().WithBucket(Bucket).WithObject(objectKey).WithCopyObjectSource(source), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await TryRemoveObjectAsync(stagingKey).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow();
        var storageReference = $"minio://{Bucket}/{objectKey}";
        try
        {
            return await PersistAsync(registration, manifest, storageReference, now, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveUncommittedObjectAsync(storageReference, objectKey).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private async Task<DeviceUploadResult> PersistAsync(
        DeviceRegistration registration,
        ArtifactUploadManifest manifest,
        string storageReference,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var existing = await dbContext.CentralArtifacts.Include(artifact => artifact.Frame).SingleOrDefaultAsync(
                artifact => artifact.IdempotencyKey == manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureManifestMatches(existing, manifest);
                EnrichSceneProvenance(existing.Frame!, manifest);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return CreateResult(existing);
            }

            var devicePublicId = registration.DevicePublicId!.Value;
            var frame = await dbContext.CentralFrames.Include(item => item.Artifacts).SingleOrDefaultAsync(
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
                    RigProfileVersion = registration.CurrentRigProfileVersion,
                    SceneProvenanceJson = SerializeScene(manifest)
                };
                dbContext.CentralFrames.Add(frame);
            }
            else
            {
                EnsureFrameMatches(frame, registration, manifest);
                EnsureNoLogicalArtifactConflict(frame, manifest);
            }

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
            dbContext.CentralArtifacts.Add(artifact);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new DeviceUploadResult(registration.Id, registration.ObservatoryId, storageReference, receivedAtUtc);
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                dbContext.ChangeTracker.Clear();
                var concurrent = await dbContext.CentralArtifacts.Include(item => item.Frame).SingleOrDefaultAsync(
                    item => item.IdempotencyKey == manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                if (concurrent is not null)
                {
                    EnsureManifestMatches(concurrent, manifest);
                    EnrichSceneProvenance(concurrent.Frame!, manifest);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return CreateResult(concurrent);
                }
                if (attempt == maximumAttempts - 1)
                {
                    throw new ArtifactIngestConflictException(
                        "The frame or artifact identity is already associated with different metadata.", exception);
                }
            }
        }

        throw new ArtifactIngestConflictException("The frame or artifact identity is already associated with different metadata.");
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

    private static void EnsureManifestMatches(CentralArtifact existing, ArtifactUploadManifest manifest)
    {
        var frame = existing.Frame ?? throw new InvalidOperationException("The central artifact frame was not loaded.");
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

    private static void EnsureFrameMatches(
        CentralFrame frame,
        DeviceRegistration registration,
        ArtifactUploadManifest manifest)
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

    private static void EnsureNoLogicalArtifactConflict(CentralFrame frame, ArtifactUploadManifest manifest)
    {
        var existing = frame.Artifacts.FirstOrDefault(artifact =>
            artifact.Role == manifest.Role && artifact.RecipeVersion == manifest.RecipeVersion
            || artifact.ArtifactId == manifest.ArtifactId);
        if (existing is null)
        {
            return;
        }
        EnsureManifestMatches(existing, manifest);
    }

    private static void EnsureSceneProvenanceMatches(CentralFrame frame, ArtifactUploadManifest manifest)
    {
        var scene = SerializeScene(manifest);
        if (frame.SceneProvenanceJson is not null && scene is not null
            && !string.Equals(frame.SceneProvenanceJson, scene, StringComparison.Ordinal))
        {
            throw new ArtifactIngestConflictException("The frame identity is already associated with different scene provenance.");
        }
    }

    private static void EnrichSceneProvenance(CentralFrame frame, ArtifactUploadManifest manifest)
        => frame.SceneProvenanceJson ??= SerializeScene(manifest);

    private static string? SerializeScene(ArtifactUploadManifest manifest)
        => manifest.Scene is null ? null : JsonSerializer.Serialize(manifest.Scene);

    private static DeviceUploadResult CreateResult(CentralArtifact artifact)
    {
        var frame = artifact.Frame ?? throw new InvalidOperationException("The central artifact frame was not loaded.");
        return new DeviceUploadResult(frame.RegistrationId, frame.ObservatoryId, artifact.StorageReference, artifact.ReceivedAtUtc);
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
