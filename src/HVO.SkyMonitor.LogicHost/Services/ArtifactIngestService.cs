using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
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

    public async Task<DeviceUploadResult> IngestAsync(ArtifactUploadManifest manifest, Stream payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        manifest.Validate();

        var existing = await dbContext.DeviceImageUploads.SingleOrDefaultAsync(upload => upload.IdempotencyKey == manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureManifestMatches(existing, manifest);
            return new DeviceUploadResult(existing.RegistrationId, existing.ObservatoryId, existing.StorageReference, existing.ReceivedAtUtc);
        }

        var registration = await dbContext.DeviceRegistrations.SingleOrDefaultAsync(
            item => item.DeviceId == manifest.AgentId && item.Status == DeviceRegistrationStatus.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceRegistrationException("Agent is not registered or active.");
        if (registration.DevicePublicId is null)
        {
            throw new DeviceRegistrationException("Agent is not fully activated.");
        }

        var bucketExists = await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false);
        if (!bucketExists)
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), cancellationToken).ConfigureAwait(false);
        }

        var objectKey = $"{manifest.AgentId}/{manifest.CapturedAtUtc:yyyy/MM/dd}/{manifest.IdempotencyKey}-{manifest.ChecksumSha256.ToUpperInvariant()}.bin";
        using var verifyingPayload = new HashingReadStream(payload);
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey).WithStreamData(verifyingPayload).WithObjectSize(manifest.ByteLength)
            .WithContentType(manifest.MediaType), cancellationToken).ConfigureAwait(false);
        if (verifyingPayload.BytesRead != manifest.ByteLength
            || !string.Equals(verifyingPayload.GetChecksumSha256(), manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey), cancellationToken).ConfigureAwait(false);
            throw new ArtifactIntegrityException("Payload length or checksum does not match the artifact manifest.");
        }

        var now = timeProvider.GetUtcNow();
        var storageReference = $"minio://{Bucket}/{objectKey}";
        dbContext.DeviceImageUploads.Add(new DeviceImageUpload
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = registration.ObservatoryId,
            RigProfileVersion = registration.CurrentRigProfileVersion,
            CapturedAtUtc = manifest.CapturedAtUtc,
            ReceivedAtUtc = now,
            ContentType = manifest.MediaType,
            FileName = null,
            PayloadBase64Length = 0,
            StorageReference = storageReference,
            IdempotencyKey = manifest.IdempotencyKey,
            ArtifactId = manifest.ArtifactId,
            FrameId = manifest.FrameId,
            ArtifactRole = manifest.Role.ToString(),
            RecipeVersion = manifest.RecipeVersion,
            ManifestSchemaVersion = manifest.SchemaVersion,
            ChecksumSha256 = manifest.ChecksumSha256,
            ByteLength = manifest.ByteLength,
            AgentId = manifest.AgentId,
            SceneProvenanceJson = manifest.Scene is null ? null : JsonSerializer.Serialize(manifest.Scene)
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            dbContext.ChangeTracker.Clear();
            var concurrent = await dbContext.DeviceImageUploads.SingleOrDefaultAsync(
                upload => upload.IdempotencyKey == manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (concurrent is null)
            {
                throw;
            }

            try
            {
                EnsureManifestMatches(concurrent, manifest);
            }
            catch (ArtifactIngestConflictException)
            {
                if (!string.Equals(concurrent.StorageReference, storageReference, StringComparison.Ordinal))
                {
                    await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey), cancellationToken).ConfigureAwait(false);
                }
                throw;
            }
            return new DeviceUploadResult(concurrent.RegistrationId, concurrent.ObservatoryId, concurrent.StorageReference, concurrent.ReceivedAtUtc);
        }
        catch
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey), cancellationToken).ConfigureAwait(false);
            throw;
        }
        return new DeviceUploadResult(registration.Id, registration.ObservatoryId, storageReference, now);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    private static void EnsureManifestMatches(DeviceImageUpload existing, ArtifactUploadManifest manifest)
    {
        if (existing.ArtifactId != manifest.ArtifactId
            || existing.AgentId != manifest.AgentId
            || existing.ArtifactRole != manifest.Role.ToString()
            || existing.ByteLength != manifest.ByteLength
            || !string.Equals(existing.ChecksumSha256, manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            || existing.FrameId is { } frameId && frameId != manifest.FrameId
            || existing.RecipeVersion is { } recipe && recipe != manifest.RecipeVersion
            || existing.ManifestSchemaVersion is { } schema && schema != manifest.SchemaVersion)
        {
            throw new ArtifactIngestConflictException("The idempotency key is already associated with different artifact metadata.");
        }
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
