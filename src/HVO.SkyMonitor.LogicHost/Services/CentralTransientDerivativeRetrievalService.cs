using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum CentralTransientDerivativeLookupStatus
{
    Found,
    NotFound,
    Gone,
    Unavailable,
    IntegrityFailure
}

internal sealed record CentralTransientDerivativeContent(
    CentralTransientDerivativeLookupStatus Status,
    Guid ArtifactId = default,
    string? MediaType = null,
    string? ChecksumSha256 = null,
    long ByteLength = 0,
    string? ObjectKey = null,
    string? StorageETag = null,
    CentralObjectApplicationLock? ObjectLock = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ObjectLock?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal interface ICentralTransientDerivativeRetrievalService
{
    Task<CentralTransientDerivativeContent> GetAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        Guid derivativeId,
        CancellationToken cancellationToken);

    Task CopyToAsync(
        CentralTransientDerivativeContent content,
        Stream destination,
        CentralArtifactByteRange? range,
        CancellationToken cancellationToken);
}

internal sealed class CentralTransientDerivativeRetrievalService(
    ApplicationDbContext dbContext,
    IMinioClient minio,
    CentralObjectStorageNames? storageNames = null) : ICentralTransientDerivativeRetrievalService
{
    private readonly CentralObjectStorageNames _storageNames = storageNames ?? new();
    private string Bucket => _storageNames.ArtifactBucket;
    private string BucketPrefix => _storageNames.ArtifactPrefix;

    public async Task<CentralTransientDerivativeContent> GetAsync(
        ClaimsPrincipal principal,
        Guid centralTransientEventId,
        Guid derivativeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var isAdmin = CentralArtifactCredentialAccess.HasScope(principal, "api.admin");
        var ownerId = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (!isAdmin && (string.IsNullOrWhiteSpace(ownerId) ||
                !CentralArtifactCredentialAccess.HasOwnerCredential(principal)))
        {
            return new(CentralTransientDerivativeLookupStatus.NotFound);
        }
        var canAccess = await CentralTransientEventReadService.ApplyAccess(
                dbContext,
                dbContext.CentralTransientEventCurrent.AsNoTracking(),
                isAdmin,
                ownerId,
                CentralArtifactCredentialAccess.GetObservatoryScope(principal))
            .AnyAsync(item => item.CentralTransientEventId == centralTransientEventId, cancellationToken)
            .ConfigureAwait(false);
        if (!canAccess)
        {
            return new(CentralTransientDerivativeLookupStatus.NotFound);
        }
        var intent = await dbContext.CentralTransientDerivatives.AsNoTracking()
            .Where(item => item.CentralTransientEventId == centralTransientEventId &&
                item.DerivativeId == derivativeId)
            .Select(item => item.OutputIntent)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (intent is null || intent.CommittedAtUtc is null)
        {
            return new(CentralTransientDerivativeLookupStatus.NotFound);
        }
        if (intent.ObjectState == CentralArtifactObjectState.Expired)
        {
            return new(CentralTransientDerivativeLookupStatus.Gone);
        }
        if (intent.ObjectState != CentralArtifactObjectState.Available ||
            !intent.StorageReference.StartsWith(BucketPrefix, StringComparison.Ordinal) ||
            intent.ByteLength < 1 || string.IsNullOrWhiteSpace(intent.StorageETag))
        {
            return new(CentralTransientDerivativeLookupStatus.Unavailable);
        }

        CentralObjectApplicationLock? objectLock = null;
        try
        {
#pragma warning disable CA2000 // Ownership transfers to the returned content and is disposed by the controller.
            objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, intent.StorageReference, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2000
            var current = await dbContext.CentralTransientDerivativeOutputIntents.AsNoTracking()
                .Where(item => item.Id == intent.Id)
                .Select(item => new { item.ObjectState, item.StorageETag })
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            if (current.ObjectState == CentralArtifactObjectState.Expired)
            {
                return new(CentralTransientDerivativeLookupStatus.Gone);
            }
            if (current.ObjectState != CentralArtifactObjectState.Available ||
                string.IsNullOrWhiteSpace(current.StorageETag))
            {
                return new(CentralTransientDerivativeLookupStatus.Unavailable);
            }
            var objectKey = intent.StorageReference[BucketPrefix.Length..];
            var stat = await minio.StatObjectAsync(new StatObjectArgs().WithBucket(Bucket).WithObject(objectKey),
                cancellationToken).ConfigureAwait(false);
            if (stat.Size != intent.ByteLength ||
                !string.Equals(stat.ETag, current.StorageETag, StringComparison.Ordinal))
            {
                return new(CentralTransientDerivativeLookupStatus.IntegrityFailure);
            }
            var transferredLock = objectLock;
            objectLock = null;
            return new(
                CentralTransientDerivativeLookupStatus.Found,
                intent.ArtifactId,
                intent.MediaType,
                intent.ChecksumSha256,
                intent.ByteLength,
                objectKey,
                current.StorageETag,
                transferredLock);
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return new(CentralTransientDerivativeLookupStatus.Unavailable);
        }
        finally
        {
            if (objectLock is not null)
            {
                await objectLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task CopyToAsync(
        CentralTransientDerivativeContent content,
        Stream destination,
        CentralArtifactByteRange? range,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (content.Status != CentralTransientDerivativeLookupStatus.Found ||
            string.IsNullOrWhiteSpace(content.ObjectKey) || string.IsNullOrWhiteSpace(content.StorageETag))
        {
            throw new CentralArtifactStorageException("Transient derivative content is not streamable.");
        }
        await minio.GetObjectAsync(new GetObjectArgs().WithBucket(Bucket).WithObject(content.ObjectKey)
            .WithMatchETag(content.StorageETag).WithCallbackStream(async (stream, token) =>
            {
                var buffer = new byte[81920];
                if (range is not null)
                {
                    var skip = range.Start;
                    while (skip > 0)
                    {
                        var read = await stream.ReadAsync(
                            buffer.AsMemory(0, (int)Math.Min(buffer.Length, skip)), token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new EndOfStreamException("Derivative ended before the requested range.");
                        }
                        skip -= read;
                    }
                }
                var remaining = range?.Length ?? content.ByteLength;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Derivative ended while streaming content.");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    remaining -= read;
                }
            }), cancellationToken).ConfigureAwait(false);
    }
}
