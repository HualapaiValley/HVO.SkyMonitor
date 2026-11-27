using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

/// <summary>
/// One instance's private, versioned Silo bucket. Writes use a single conditional PUT, including empty objects.
/// The single-PUT ceiling is 5 GiB. This adapter never initiates a multipart upload, so its retries and crashes
/// cannot leave any multipart upload to abort. The publication layer independently reads back and hashes the version.
/// </summary>
internal sealed class SiloS3ObjectStore(
    IAmazonS3 client, SiloStoreDescriptor descriptor, TimeSpan requestTimeout) : ISiloObjectStore
{
    public const long MaximumPutBytes = 5L * 1024 * 1024 * 1024;
    private const int PageSize = 32;

    public SiloStoreDescriptor Descriptor { get; } = descriptor;

    public static AmazonS3Client CreateClient(Uri endpoint, string accessKey, string secretKey, HttpClientFactory? transport = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("Silo requires an HTTP(S) service origin without embedded credentials.", nameof(endpoint));
        }

        return new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint.AbsoluteUri,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            HttpClientFactory = transport,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }

    public async ValueTask<bool> HasVersioningAsync(CancellationToken cancellationToken) =>
        await SendAsync("verify bucket versioning", async token =>
        {
            var response = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest
            {
                BucketName = Descriptor.Bucket
            }, token).ConfigureAwait(false);
            return response.VersioningConfig?.Status == VersionStatus.Enabled;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Length, MaximumPutBytes);
        SiloHash.Require(request.Sha256, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var input = await request.OpenContent(cancellationToken).ConfigureAwait(false);
        await using var inputLease = input.ConfigureAwait(false);
        if (!input.CanSeek || input.Length - input.Position != request.Length)
        {
            throw new SiloChecksumRejectedException(request.Key);
        }

        var put = new PutObjectRequest
        {
            BucketName = Descriptor.Bucket,
            Key = request.Key,
            IfNoneMatch = "*",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(request.Sha256)),
            InputStream = input,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            UseChunkEncoding = false
        };
        foreach (var (name, value) in request.Metadata)
        {
            put.Metadata.Add(name, value);
        }

        return await SendAsync("create object", async token =>
        {
            try
            {
                var response = await client.PutObjectAsync(put, token).ConfigureAwait(false);
                return new SiloPutResult(true, RequireVersion(response.VersionId, "create object", writeMayHaveLanded: true));
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                return new SiloPutResult(false, null);
            }
            catch (AmazonS3Exception exception) when (
                exception.ErrorCode is "BadDigest" or "XAmzContentSHA256Mismatch" or "XAmzContentChecksumMismatch")
            {
                throw new SiloChecksumRejectedException(request.Key);
            }
        }, cancellationToken, writeMayHaveLanded: true).ConfigureAwait(false);
    }

    public async ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        if (versionId == "null")
        {
            throw new ArgumentException("A versioned Silo locator is required.", nameof(versionId));
        }

        return await SendAsync("open exact version", async token =>
        {
            try
            {
                var response = await client.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = Descriptor.Bucket,
                    Key = key,
                    VersionId = versionId
                }, token).ConfigureAwait(false);
                if (!string.Equals(response.VersionId, versionId, StringComparison.Ordinal) || response.ResponseStream is null)
                {
                    response.Dispose();
                    throw Unavailable("open exact version", writeMayHaveLanded: false);
                }
                return new SiloOpenResult(SiloOpenStatus.Found, new SiloS3ResponseStream(response, Descriptor.StoreId, requestTimeout));
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound &&
                exception.ErrorCode is "NoSuchKey" or "NoSuchVersion")
            {
                return new SiloOpenResult(exception.ErrorCode == "NoSuchVersion" ? SiloOpenStatus.NoSuchVersion : SiloOpenStatus.NoSuchKey, null);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken) =>
        await SendAsync("locate current version", async token =>
        {
            try
            {
                var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = Descriptor.Bucket,
                    Key = key
                }, token).ConfigureAwait(false);
                return RequireVersion(response.VersionId, "locate current version", writeMayHaveLanded: false);
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound &&
                exception.ErrorCode != "NoSuchBucket")
            {
                // HEAD has no error body. A bare 404 cannot distinguish an absent key from an absent bucket.
                var bucket = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest
                {
                    BucketName = Descriptor.Bucket
                }, token).ConfigureAwait(false);
                if (bucket.VersioningConfig?.Status != VersionStatus.Enabled)
                {
                    throw Unavailable("locate current version", writeMayHaveLanded: false);
                }
                return null;
            }
        }, cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<string> ListKeysAsync(
        string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = Descriptor.Bucket,
            Prefix = prefix,
            StartAfter = startAfter,
            MaxKeys = PageSize
        };
        string? previous = startAfter;
        while (true)
        {
            var page = await SendAsync("list keys", token => client.ListObjectsV2Async(request, token), cancellationToken)
                .ConfigureAwait(false);
            foreach (var entry in page.S3Objects ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                    string.CompareOrdinal(entry.Key, previous) <= 0)
                {
                    throw Unavailable("list ordered keys", writeMayHaveLanded: false);
                }
                previous = entry.Key;
                yield return entry.Key;
            }

            if (page.IsTruncated != true)
            {
                yield break;
            }
            if (string.IsNullOrEmpty(page.NextContinuationToken) || page.NextContinuationToken == request.ContinuationToken)
            {
                throw Unavailable("continue listing keys", writeMayHaveLanded: false);
            }
            request.StartAfter = null;
            request.ContinuationToken = page.NextContinuationToken;
        }
    }

    private string RequireVersion(string? versionId, string operation, bool writeMayHaveLanded) =>
        !string.IsNullOrWhiteSpace(versionId) && versionId != "null"
            ? versionId
            : throw Unavailable(operation, writeMayHaveLanded);

    private async Task<T> SendAsync<T>(
        string operation, Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken, bool writeMayHaveLanded = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            return await send(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or
            OperationCanceledException or IOException and not (SiloChecksumRejectedException or SiloStoreUnavailableException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Provider errors can contain endpoint details or untrusted response data.
            throw Unavailable(operation, writeMayHaveLanded);
        }
    }

    private SiloStoreUnavailableException Unavailable(string operation, bool writeMayHaveLanded) =>
        new(Descriptor.StoreId, operation, writeMayHaveLanded);
}
