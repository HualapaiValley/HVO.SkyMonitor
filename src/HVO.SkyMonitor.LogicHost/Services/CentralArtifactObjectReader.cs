using System.Security.Cryptography;
using HVO.SkyMonitor.LogicHost.Data;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralArtifactObjectReader
{
    Task<CentralArtifactObjectSnapshot> VerifyAsync(CentralArtifact artifact, CancellationToken cancellationToken);

    Task<bool> IsCurrentGenerationAsync(
        CentralArtifact artifact,
        string storageETag,
        CancellationToken cancellationToken);

    Task CopyToAsync(
        CentralArtifactObjectSnapshot snapshot,
        Stream destination,
        CentralArtifactByteRange? range,
        CancellationToken cancellationToken);
}

internal sealed partial class CentralArtifactObjectReader(
    IObjectStore objectStore,
    CentralArtifactRetrievalTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralArtifactObjectReader> logger,
    CentralObjectStorageNames? storageNames = null) : ICentralArtifactObjectReader
{
    private readonly CentralObjectStorageNames _storageNames = storageNames ?? new();
    private string Bucket => _storageNames.ArtifactBucket;
    private string BucketPrefix => _storageNames.ArtifactPrefix;

    public async Task<CentralArtifactObjectSnapshot> VerifyAsync(
        CentralArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.StorageReference.StartsWith(BucketPrefix, StringComparison.Ordinal))
        {
            throw new CentralArtifactIntegrityException("object.reference-invalid");
        }
        var objectKey = artifact.StorageReference[BucketPrefix.Length..];
        var started = timeProvider.GetTimestamp();
        long bytesRead = 0;
        try
        {
            var stat = await objectStore.StatAsync(Bucket, objectKey, cancellationToken).ConfigureAwait(false);
            if (stat.ContentLength != artifact.ByteLength)
            {
                throw new CentralArtifactIntegrityException("object.length-mismatch", stat.Generation);
            }

            string? checksum = null;
            await objectStore.ReadAsync(Bucket, objectKey, stat.Generation, async (stream, token) =>
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
            }, cancellationToken).ConfigureAwait(false);
            if (bytesRead != artifact.ByteLength)
            {
                throw new CentralArtifactIntegrityException("object.length-mismatch", stat.Generation);
            }
            if (!string.Equals(checksum, artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CentralArtifactIntegrityException("object.checksum-mismatch", stat.Generation);
            }
            telemetry.RecordVerification("matched");
            telemetry.RecordObjectRead("verify", "completed", bytesRead, timeProvider.GetElapsedTime(started));
            return new CentralArtifactObjectSnapshot(objectKey, stat.Generation, artifact.ByteLength);
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        {
            telemetry.RecordObjectRead("verify", "missing", 0, timeProvider.GetElapsedTime(started));
            throw new CentralArtifactMissingException(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.RecordObjectRead("verify", "cancelled", bytesRead, timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (CentralArtifactIntegrityException)
        {
            telemetry.RecordObjectRead("verify", "failed", bytesRead, timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            telemetry.RecordObjectRead("verify", "failed", bytesRead, timeProvider.GetElapsedTime(started));
            throw new CentralArtifactStorageException(exception);
        }
    }

    public async Task<bool> IsCurrentGenerationAsync(
        CentralArtifact artifact,
        string storageETag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageETag);
        if (!artifact.StorageReference.StartsWith(BucketPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            var stat = await objectStore.StatAsync(
                Bucket, artifact.StorageReference[BucketPrefix.Length..], cancellationToken).ConfigureAwait(false);
            return string.Equals(stat.Generation, storageETag, StringComparison.Ordinal);
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            throw new CentralArtifactStorageException(exception);
        }
    }

    public async Task CopyToAsync(
        CentralArtifactObjectSnapshot snapshot,
        Stream destination,
        CentralArtifactByteRange? range,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        var started = timeProvider.GetTimestamp();
        long objectBytesRead = 0;
        try
        {
            await objectStore.ReadAsync(Bucket, snapshot.ObjectKey, snapshot.StorageETag, async (stream, token) =>
            {
                objectBytesRead = range is null
                    ? await CopyFullAsync(stream, destination, token).ConfigureAwait(false)
                    : await CopyRangeAsync(stream, destination, range, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            telemetry.RecordObjectRead("serve", "completed", objectBytesRead, timeProvider.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.RecordObjectRead("serve", "cancelled", objectBytesRead, timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            telemetry.RecordObjectRead("serve", "failed", objectBytesRead, timeProvider.GetElapsedTime(started));
            Log.ReadFailed(logger, exception.GetType().Name);
            throw new CentralArtifactStorageException(exception);
        }
    }

    private static async Task<long> CopyFullAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
        return total;
    }

    private static async Task<long> CopyRangeAsync(
        Stream source,
        Stream destination,
        CentralArtifactByteRange range,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        var skip = range.Start;
        while (skip > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, skip)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Artifact ended before the requested range.");
            }
            skip -= read;
            total += read;
        }
        var remaining = range.Length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Artifact ended during the requested range.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
            total += read;
        }
        return total;
    }

    private static partial class Log
    {
        [LoggerMessage(2110, LogLevel.Warning, "Central artifact object read failed: FailureType={FailureType}")]
        public static partial void ReadFailed(ILogger logger, string failureType);
    }

    private static bool IsStorageFailure(Exception exception)
        => exception is ObjectStoreException or HttpRequestException or IOException or TimeoutException
            or OperationCanceledException;
}

internal sealed record CentralArtifactObjectSnapshot(string ObjectKey, string StorageETag, long ByteLength);

internal sealed class CentralArtifactMissingException : Exception
{
    public CentralArtifactMissingException()
        : base("The central artifact object is missing.")
    {
    }

    public CentralArtifactMissingException(string message) : base(message)
    {
    }

    public CentralArtifactMissingException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public CentralArtifactMissingException(Exception innerException)
        : this("The central artifact object is missing.", innerException)
    {
    }
}

internal sealed class CentralArtifactIntegrityException : Exception
{
    public CentralArtifactIntegrityException()
        : this("object.integrity-failed")
    {
    }

    public CentralArtifactIntegrityException(string reasonCode)
        : base("The central artifact object failed integrity verification.")
    {
        ReasonCode = reasonCode;
    }

    public CentralArtifactIntegrityException(string reasonCode, string storageETag)
        : this(reasonCode)
    {
        StorageETag = storageETag;
    }

    public CentralArtifactIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
        ReasonCode = "object.integrity-failed";
    }

    public string ReasonCode { get; }

    public string? StorageETag { get; }
}

internal sealed class CentralArtifactStorageException : Exception
{
    public CentralArtifactStorageException()
        : base("Central artifact storage is unavailable.")
    {
    }

    public CentralArtifactStorageException(string message) : base(message)
    {
    }

    public CentralArtifactStorageException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public CentralArtifactStorageException(Exception innerException)
        : this("Central artifact storage is unavailable.", innerException)
    {
    }
}
