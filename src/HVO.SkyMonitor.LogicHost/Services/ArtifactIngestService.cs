using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

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
        if (manifest.SchemaVersion != "v1" || manifest.ByteLength < 0 || string.IsNullOrWhiteSpace(manifest.ChecksumSha256))
        {
            throw new ArgumentException("Artifact manifest is invalid.", nameof(manifest));
        }

        var existing = await dbContext.DeviceImageUploads.SingleOrDefaultAsync(upload => upload.IdempotencyKey == manifest.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
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

        var objectKey = $"{manifest.AgentId}/{manifest.CapturedAtUtc:yyyy/MM/dd}/{manifest.ArtifactId:N}-{manifest.Role}.bin";
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey).WithStreamData(payload).WithObjectSize(manifest.ByteLength)
            .WithContentType(manifest.MediaType), cancellationToken).ConfigureAwait(false);

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
            ArtifactRole = manifest.Role.ToString(),
            ChecksumSha256 = manifest.ChecksumSha256,
            ByteLength = manifest.ByteLength
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DeviceUploadResult(registration.Id, registration.ObservatoryId, storageReference, now);
    }
}
