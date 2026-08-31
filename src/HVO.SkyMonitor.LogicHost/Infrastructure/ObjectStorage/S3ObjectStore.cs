using System.Net;
using System.Runtime.ExceptionServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

internal sealed partial class S3ObjectStore(
    IAmazonS3 client,
    ObjectStoreTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<S3ObjectStore> logger) : IObjectStore
{
    public async Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken)
    {
        try
        {
            _ = await ExecuteAsync(
                "bucket-exists",
                bucket,
                token => client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    MaxKeys = 1
                }, token),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingBucket)
        {
            return false;
        }
    }

    public Task PutAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!content.CanRead)
        {
            throw new ArgumentException("Object content must be readable.", nameof(content));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        if (contentLength > IObjectStore.MaximumPutContentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentLength),
                contentLength,
                $"Object content cannot exceed {IObjectStore.MaximumPutContentLength} bytes.");
        }

        return ExecuteStreamAsync(
            "put",
            "write",
            bucket,
            contentLength,
            async token =>
            {
                var requestStream = content.CanSeek
                    ? content
                    : new KnownLengthReadStream(content, contentLength);
                var request = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = requestStream,
                    ContentType = contentType,
                    AutoCloseStream = false,
                    AutoResetStreamPosition = false,
                    // SkyMonitor verifies SHA-256 end to end; retain SigV4 signing without a second SDK checksum pass.
                    DisableDefaultChecksumValidation = true
                };
                request.Headers.ContentLength = contentLength;
                _ = await client.PutObjectAsync(request, token).ConfigureAwait(false);
            },
            cancellationToken,
            ambiguousOnTransportFailure: true);
    }

    public async Task<ObjectStoreObjectMetadata> StatAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        GetObjectMetadataResponse response;
        try
        {
            response = await ExecuteAsync(
                "stat",
                bucket,
                token => client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key
                }, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        {
            if (!await BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingBucket, "stat", exception);
            }
            throw;
        }
        var generation = NormalizeGeneration(response.ETag);
        if (string.IsNullOrWhiteSpace(generation))
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.Unsupported, "stat");
        }
        return new ObjectStoreObjectMetadata(
            key,
            response.Headers.ContentLength,
            response.ContentType ?? "application/octet-stream",
            generation,
            new DateTimeOffset(response.LastModified.GetValueOrDefault().ToUniversalTime()));
    }

    public Task ReadAsync(
        string bucket,
        string key,
        string? generation,
        Func<Stream, CancellationToken, Task> reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ReadCoreAsync(bucket, key, generation, reader, cancellationToken);
    }

    public Task CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            "copy",
            bucket,
            async token =>
            {
                _ = await client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = bucket,
                    DestinationKey = destinationKey
                }, token).ConfigureAwait(false);
            },
            cancellationToken,
            ambiguousOnTransportFailure: true);

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        => DeleteCoreAsync(bucket, key, cancellationToken);

    public async IAsyncEnumerable<ObjectStoreItem> ListAsync(
        string bucket,
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        string? startAfter = null)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry.StartOperation("list", bucket);
        string? continuationToken = null;
        var outcomeRecorded = false;
        try
        {
            do
            {
                ListObjectsV2Response response;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    response = await client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = prefix,
                        ContinuationToken = continuationToken,
                        StartAfter = continuationToken is null ? startAfter : null,
                        MaxKeys = 1000
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    telemetry.RecordOperation("list", "canceled", bucket, timeProvider.GetElapsedTime(started));
                    outcomeRecorded = true;
                    throw;
                }
                catch (Exception exception)
                {
                    var normalized = S3ObjectStoreExceptionMapper.Normalize(exception, "list", false);
                    RecordFailure(normalized, bucket, started);
                    outcomeRecorded = true;
                    throw normalized;
                }
                foreach (var item in response.S3Objects ?? [])
                {
                    yield return new ObjectStoreItem(
                        item.Key,
                        item.Size.GetValueOrDefault(),
                        new DateTimeOffset(item.LastModified.GetValueOrDefault().ToUniversalTime()));
                }
                continuationToken = response.IsTruncated == true
                    ? response.NextContinuationToken
                    : null;
            }
            while (!string.IsNullOrEmpty(continuationToken));
            telemetry.RecordOperation("list", "success", bucket, timeProvider.GetElapsedTime(started));
            outcomeRecorded = true;
        }
        finally
        {
            if (!outcomeRecorded)
            {
                telemetry.RecordOperation("list", "success", bucket, timeProvider.GetElapsedTime(started));
            }
        }
    }

    private async Task ReadCoreAsync(
        string bucket,
        string key,
        string? generation,
        Func<Stream, CancellationToken, Task> reader,
        CancellationToken cancellationToken)
    {
        var bytes = 0L;
        ExceptionDispatchInfo? readerFailure = null;
        try
        {
            await ExecuteStreamAsync(
                "get",
                "read",
                bucket,
                null,
                async token =>
                {
                    var request = new GetObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        EtagToMatch = string.IsNullOrWhiteSpace(generation) ? null : QuoteGeneration(generation)
                    };
                    using var response = await client.GetObjectAsync(request, token).ConfigureAwait(false);
                    var countingStream = new CountingReadStream(response.ResponseStream);
                    try
                    {
                        await reader(countingStream, token).ConfigureAwait(false);
                        bytes = countingStream.BytesRead;
                    }
                    catch (Exception exception)
                    {
                        readerFailure = ExceptionDispatchInfo.Capture(exception);
                        throw new ReaderCallbackException();
                    }
                },
                cancellationToken,
                () => bytes).ConfigureAwait(false);
        }
        catch when (readerFailure is not null)
        {
            readerFailure.Throw();
            throw;
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        {
            if (!await BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingBucket, "get", exception);
            }
            throw;
        }
    }

    private async Task DeleteCoreAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(
                "delete",
                bucket,
                async token =>
                {
                    _ = await client.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = bucket,
                        Key = key
                    }, token).ConfigureAwait(false);
                },
                cancellationToken,
                ambiguousOnTransportFailure: true).ConfigureAwait(false);
        }
        catch (ObjectStoreException exception) when (exception.Kind == ObjectStoreFailureKind.MissingObject)
        {
        }
    }

    private async Task ExecuteStreamAsync(
        string operation,
        string direction,
        string bucket,
        long? expectedBytes,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        Func<long>? observedBytes = null,
        bool ambiguousOnTransportFailure = false)
    {
        telemetry.ChangeActiveStreams(direction, 1);
        try
        {
            await ExecuteAsync(
                operation,
                bucket,
                action,
                cancellationToken,
                ambiguousOnTransportFailure).ConfigureAwait(false);
            telemetry.RecordBytes(operation, direction, bucket, observedBytes?.Invoke() ?? expectedBytes ?? 0);
        }
        finally
        {
            telemetry.ChangeActiveStreams(direction, -1);
        }
    }

    private async Task ExecuteAsync(
        string operation,
        string bucket,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool ambiguousOnTransportFailure = false)
    {
        await ExecuteAsync<object?>(operation, bucket, async token =>
        {
            await action(token).ConfigureAwait(false);
            return null;
        }, cancellationToken, ambiguousOnTransportFailure).ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(
        string operation,
        string bucket,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        bool ambiguousOnTransportFailure = false)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = telemetry.StartOperation(operation, bucket);
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            telemetry.RecordOperation(operation, "success", bucket, timeProvider.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.RecordOperation(operation, "canceled", bucket, timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (ReaderCallbackException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var normalized = S3ObjectStoreExceptionMapper.Normalize(
                exception, operation, ambiguousOnTransportFailure);
            RecordFailure(normalized, bucket, started);
            throw normalized;
        }
    }

    private void RecordFailure(ObjectStoreException exception, string bucket, long started)
    {
        var outcome = ObjectStoreException.GetOutcome(exception.Kind);
        telemetry.RecordOperation(exception.Operation, outcome, bucket, timeProvider.GetElapsedTime(started));
        if (exception.IsRetryable)
        {
            var recovery = exception.Kind == ObjectStoreFailureKind.Ambiguous ? "reconcile" : "caller-retry";
            telemetry.RecordRetry(exception.Operation, recovery, bucket);
            if (exception.Kind == ObjectStoreFailureKind.Ambiguous)
            {
                Log.AmbiguousResult(logger, exception.Operation, outcome, recovery);
            }
            else
            {
                Log.RetryRequired(logger, exception.Operation, outcome, recovery);
            }
        }
        else
        {
            Log.OperationFailed(
                logger,
                exception.Operation,
                outcome,
                false,
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);
        }
    }

    private static string NormalizeGeneration(string generation)
        => generation.Length >= 2 && generation[0] == '"' && generation[^1] == '"'
            ? generation[1..^1]
            : generation;

    private static string QuoteGeneration(string generation) => $"\"{generation}\"";

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class KnownLengthReadStream(Stream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _position += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _position += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ReaderCallbackException : Exception
    {
        public ReaderCallbackException()
        {
        }

        public ReaderCallbackException(string message)
            : base(message)
        {
        }

        public ReaderCallbackException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private static partial class Log
    {
        [LoggerMessage(2181, LogLevel.Warning,
            "Object storage operation requires caller recovery: Operation={Operation} Reason={Reason} Recovery={Recovery}")]
        public static partial void RetryRequired(ILogger logger, string operation, string reason, string recovery);

        [LoggerMessage(2182, LogLevel.Error,
            "Object storage operation failed: Operation={Operation} Outcome={Outcome} Retryable={Retryable} Exception={Exception}")]
        public static partial void OperationFailed(
            ILogger logger,
            string operation,
            string outcome,
            bool retryable,
            string exception);

        [LoggerMessage(2183, LogLevel.Warning,
            "Object storage operation returned an ambiguous result: Operation={Operation} Reason={Reason} Recovery={Recovery}")]
        public static partial void AmbiguousResult(ILogger logger, string operation, string reason, string recovery);
    }
}

internal static class S3ObjectStoreExceptionMapper
{
    public static ObjectStoreException Normalize(
        Exception exception,
        string operation,
        bool ambiguousOnTransportFailure)
    {
        if (exception is ObjectStoreException normalized)
        {
            return normalized;
        }
        if (exception is TimeoutException or TaskCanceledException)
        {
            return Create(ambiguousOnTransportFailure
                ? ObjectStoreFailureKind.Ambiguous
                : ObjectStoreFailureKind.Timeout);
        }
        if (exception is OperationCanceledException)
        {
            return Create(ObjectStoreFailureKind.Canceled);
        }
        if (exception is NotSupportedException or System.NotImplementedException)
        {
            return Create(ObjectStoreFailureKind.Unsupported);
        }
        if (exception is HttpRequestException or IOException)
        {
            return Create(ambiguousOnTransportFailure
                ? ObjectStoreFailureKind.Ambiguous
                : ObjectStoreFailureKind.Transient);
        }
        if (exception is AmazonS3Exception s3Exception)
        {
            var code = s3Exception.ErrorCode;
            var status = s3Exception.StatusCode;
            var kind = code switch
            {
                "NoSuchKey" or "NoSuchObject" or "NotFound" => ObjectStoreFailureKind.MissingObject,
                "NoSuchBucket" => ObjectStoreFailureKind.MissingBucket,
                "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "ExpiredToken"
                    => ObjectStoreFailureKind.Authentication,
                "AccessDenied" => ObjectStoreFailureKind.Authorization,
                "PreconditionFailed" or "ConditionalRequestConflict" => ObjectStoreFailureKind.Precondition,
                "SlowDown" or "Throttling" or "ThrottlingException" => ObjectStoreFailureKind.Throttled,
                "NotImplemented" or "UnsupportedOperation" => ObjectStoreFailureKind.Unsupported,
                "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException" or "IncorrectEndpoint"
                    or "InvalidArgument" or "InvalidBucketName" or "InvalidRequest" or "PermanentRedirect"
                    => ObjectStoreFailureKind.Unsupported,
                "RequestTimeout" => ambiguousOnTransportFailure
                    ? ObjectStoreFailureKind.Ambiguous
                    : ObjectStoreFailureKind.Timeout,
                "InternalError" or "ServiceUnavailable" => ambiguousOnTransportFailure
                    ? ObjectStoreFailureKind.Ambiguous
                    : ObjectStoreFailureKind.Transient,
                _ => status switch
                {
                    HttpStatusCode.NotFound => operation == "bucket-exists"
                        ? ObjectStoreFailureKind.MissingBucket
                        : ObjectStoreFailureKind.MissingObject,
                    HttpStatusCode.Unauthorized => ObjectStoreFailureKind.Authentication,
                    HttpStatusCode.Forbidden => ObjectStoreFailureKind.Authorization,
                    HttpStatusCode.PreconditionFailed => ObjectStoreFailureKind.Precondition,
                    HttpStatusCode.TooManyRequests => ObjectStoreFailureKind.Throttled,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ambiguousOnTransportFailure
                        ? ObjectStoreFailureKind.Ambiguous
                        : ObjectStoreFailureKind.Timeout,
                    HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                        => ObjectStoreFailureKind.Unsupported,
                    HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                        => ambiguousOnTransportFailure
                            ? ObjectStoreFailureKind.Ambiguous
                            : ObjectStoreFailureKind.Transient,
                    _ when (int)status >= 500 => ambiguousOnTransportFailure
                        ? ObjectStoreFailureKind.Ambiguous
                        : ObjectStoreFailureKind.Transient,
                    _ when (int)status >= 400 => ObjectStoreFailureKind.Unsupported,
                    _ => ObjectStoreFailureKind.Transient
                }
            };
            return new ObjectStoreException(kind, operation);
        }
        if (exception is AmazonClientException)
        {
            return Create(ObjectStoreFailureKind.Authentication);
        }
        return Create(ObjectStoreFailureKind.Transient);

        ObjectStoreException Create(ObjectStoreFailureKind kind)
            => new(kind, operation);
    }
}
