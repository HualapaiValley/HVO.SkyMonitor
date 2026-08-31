namespace HVO.SkyMonitor.LogicHost.Services;

internal interface IObjectStore
{
    const long MaximumPutContentLength = 100L * 1024 * 1024;

    Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken);

    Task PutAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken);

    Task<ObjectStoreObjectMetadata> StatAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken);

    Task ReadAsync(
        string bucket,
        string key,
        string? generation,
        Func<Stream, CancellationToken, Task> reader,
        CancellationToken cancellationToken);

    Task CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken);

    IAsyncEnumerable<ObjectStoreItem> ListAsync(
        string bucket,
        string prefix,
        CancellationToken cancellationToken,
        string? startAfter = null);
}

internal sealed record ObjectStoreObjectMetadata(
    string Key,
    long ContentLength,
    string ContentType,
    string Generation,
    DateTimeOffset LastModifiedUtc);

internal sealed record ObjectStoreItem(string Key, long ContentLength, DateTimeOffset LastModifiedUtc);

internal enum ObjectStoreFailureKind
{
    MissingObject,
    MissingBucket,
    Authentication,
    Authorization,
    Precondition,
    Throttled,
    Transient,
    Timeout,
    Canceled,
    Unsupported,
    Ambiguous
}

internal sealed class ObjectStoreException : Exception
{
    public ObjectStoreException()
        : this(ObjectStoreFailureKind.Transient, "unknown")
    {
    }

    public ObjectStoreException(string message)
        : base(message)
    {
        Kind = ObjectStoreFailureKind.Transient;
        Operation = "unknown";
    }

    public ObjectStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = ObjectStoreFailureKind.Transient;
        Operation = "unknown";
    }

    public ObjectStoreException(ObjectStoreFailureKind kind, string operation, Exception? innerException = null)
        : base($"Object storage operation '{operation}' failed with category '{GetOutcome(kind)}'.", innerException)
    {
        Kind = kind;
        Operation = operation;
    }

    public ObjectStoreFailureKind Kind { get; }

    public string Operation { get; }

    public bool IsRetryable => Kind is ObjectStoreFailureKind.Throttled
        or ObjectStoreFailureKind.Transient
        or ObjectStoreFailureKind.Timeout
        or ObjectStoreFailureKind.Ambiguous;

    public bool IsTerminal => Kind is ObjectStoreFailureKind.MissingBucket
        or ObjectStoreFailureKind.Authentication
        or ObjectStoreFailureKind.Authorization
        or ObjectStoreFailureKind.Unsupported;

    public static string GetOutcome(ObjectStoreFailureKind kind)
        => kind switch
        {
            ObjectStoreFailureKind.MissingObject => "missing-object",
            ObjectStoreFailureKind.MissingBucket => "missing-bucket",
            ObjectStoreFailureKind.Authentication => "authentication",
            ObjectStoreFailureKind.Authorization => "authorization",
            ObjectStoreFailureKind.Precondition => "precondition",
            ObjectStoreFailureKind.Throttled => "throttled",
            ObjectStoreFailureKind.Transient => "transient",
            ObjectStoreFailureKind.Timeout => "timeout",
            ObjectStoreFailureKind.Canceled => "canceled",
            ObjectStoreFailureKind.Unsupported => "unsupported",
            ObjectStoreFailureKind.Ambiguous => "ambiguous",
            _ => "transient"
        };
}
