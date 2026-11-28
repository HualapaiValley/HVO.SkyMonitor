using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

internal sealed record SiloArchiveMarker(string Format, string StoreId, string Bucket, string SourceBindingId, string RetentionPolicy);

internal sealed record SiloArchiveExpiry(string Format, string StoreId, string Bucket, string Key, string VersionId,
    string Disposition, DateTimeOffset ObservedUtc);

internal sealed partial class SiloArchiveStore(IAmazonS3 client, SiloArchiveMarker marker, TimeProvider clock,
    IDisposable transport) : IDisposable
{
    internal const string MarkerKey = "meta/archive.v1.json";
    internal const string MarkerFormat = "hvo.cameraagent.silo.archive/1";
    internal const string ExpiryFormat = "hvo.cameraagent.silo.archive-expiry/1";
    internal const string RetentionPolicy = "archive-owner-exact-version-v1";
    internal const string Custody = "archive-owner";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal SiloArchiveMarker Marker { get; } = marker;

    internal SiloS3ObjectStore Objects { get; } = new(client,
        new(marker.StoreId, marker.Bucket, SiloStoreClass.Archive, SiloRetrieval.Online, PersistenceQualified: true), RequestTimeout);

    internal static SiloArchiveMarker ReadMarker(IConfigurationSection section)
    {
        var id = Required(section, "StoreId");
        if (!id.StartsWith("silo-archive-", StringComparison.Ordinal) || !Guid.TryParseExact(id.AsSpan(13), "N", out var archiveId) ||
            archiveId == Guid.Empty || id != $"silo-archive-{archiveId:N}")
            throw new ArgumentException("The archive store identity must name its independently provisioned bucket.");
        var bucket = Required(section, "Bucket");
        if (!BucketPattern().IsMatch(bucket) || bucket.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("The archive bucket name is invalid.");
        var sourceBinding = Required(section, "SourceBindingId");
        SiloHash.Require(sourceBinding, nameof(sourceBinding));
        return new(MarkerFormat, id, bucket, sourceBinding, RetentionPolicy);
    }

    internal static SiloArchiveStore Create(IConfigurationSection section, TimeProvider clock) =>
        Create(section, ReadMarker(section), clock);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The returned store owns the S3 client.")]
    internal static SiloArchiveStore Create(IConfigurationSection section, SiloArchiveMarker marker, TimeProvider clock)
    {
        if (!Uri.TryCreate(Required(section, "Endpoint"), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The archive endpoint must be an HTTPS service origin.");
        var accessKey = Required(section, "AccessKey");
        var secretKey = Required(section, "SecretKey");
        if (!AccessKeyPattern().IsMatch(accessKey) || !SecretPattern().IsMatch(secretKey))
            throw new ArgumentException("The archive reader credentials have an invalid format.");

        var transport = new SiloArchiveTransport(Required(section, "AuthorityPem"));
        try
        {
            return new(SiloS3ObjectStore.CreateClient(endpoint, accessKey, secretKey, transport), marker, clock, transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    private static string Required(IConfigurationSection section, string name) => section[name] is { Length: > 0 } value ? value :
        throw new ArgumentException("The archive configuration is incomplete.");

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9]{3,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccessKeyPattern();

    [GeneratedRegex(@"\A[\x21-\x7e]{8,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    internal static SiloArchiveException TlsFailure() => new("failed", "archive-tls-failed");

    internal async ValueTask VerifyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var versioning = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = Marker.Bucket },
                deadline.Token).ConfigureAwait(false);
            var locking = await client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = Marker.Bucket },
                deadline.Token).ConfigureAwait(false);
            if (versioning.VersioningConfig?.Status != VersionStatus.Enabled ||
                locking.ObjectLockConfiguration?.ObjectLockEnabled != ObjectLockEnabled.Enabled ||
                locking.ObjectLockConfiguration.Rule is not null)
                throw new SiloArchiveException("blocked", "archive-versioning-or-lock-profile");

            await RequireNoLifecycleAsync(deadline.Token).ConfigureAwait(false);
            var current = await SiloVerifier.ReadCurrentAsync(Objects, MarkerKey, 4096, deadline.Token).ConfigureAwait(false);
            if (current is null || JsonSerializer.Deserialize<SiloArchiveMarker>(current.Value.Bytes, SiloJson.Options) != Marker)
                throw new SiloArchiveException("blocked", "archive-binding-mismatch");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SiloArchiveException("blocked", "archive-credentials-refused");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SiloArchiveException("blocked", "archive-bucket-or-lock-missing");
        }
        catch (Exception exception) when (SiloS3ObjectStore.IsTlsFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw TlsFailure();
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or
            IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SiloArchiveException("blocked", "archive-unreachable");
        }
        catch (Exception exception) when (exception is JsonException or SiloDataException)
        {
            throw new SiloArchiveException("failed", "archive-marker-invalid");
        }
    }

    private async ValueTask RequireNoLifecycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = Marker.Bucket },
                cancellationToken).ConfigureAwait(false);
            if (response.Configuration?.Rules is not { Count: > 0 }) return;
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchLifecycleConfiguration")
        {
            return;
        }
        throw new SiloArchiveException("blocked", "archive-lifecycle-profile-changed");
    }

    internal SiloExactLocator ReplicaOf(SiloExactLocator source) => source with { StoreId = Marker.StoreId, Bucket = Marker.Bucket };

    internal SiloExactLocator SegmentLocator(SiloCommittedSegment segment) =>
        new(Marker.StoreId, Marker.Bucket, SiloObjectNamespace.SegmentKey(segment.Position), segment.VersionId);

    internal async ValueTask<SiloJournalSegment> ReadSegmentAsync(SiloCommittedSegment segment, CancellationToken cancellationToken)
    {
        var bytes = await SiloVerifier.ReadExactMetadataAsync(Objects, SegmentLocator(segment), cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            throw new SiloArchiveException("pending", "archive-journal-version-pending");
        if (SiloHash.Sha256(bytes) != segment.Sha256)
            throw new SiloArchiveException("failed", "archive-journal-digest-mismatch");
        var journal = SiloJson.ReadSegment(bytes, "archive journal");
        if (journal.StorageBindingId != Marker.SourceBindingId || journal.Position != segment.Position)
            throw new SiloArchiveException("failed", "archive-journal-binding-mismatch");
        return journal;
    }

    internal async ValueTask<SiloProbe> ProbeAsync(SiloObjectIdentity identity, SiloExactLocator locator, bool captureBytes,
        CancellationToken cancellationToken)
    {
        if (locator.StoreId != Marker.StoreId || locator.Bucket != Marker.Bucket ||
            captureBytes && identity.Length > SiloJson.MaximumMetadataObjectBytes || identity.Length > SiloS3ObjectStore.MaximumPutBytes)
            throw new SiloArchiveException("failed", "archive-copy-identity-mismatch");
        return await SiloVerifier.ProbeAsync(Objects, locator, identity.Length, identity.Sha256, clock, captureBytes,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<SiloArchiveRetention> ReadRetentionAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var response = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = locator.Bucket,
                Key = locator.Key,
                VersionId = locator.VersionId
            }, deadline.Token).ConfigureAwait(false);
            if (response.VersionId != locator.VersionId)
                throw new SiloArchiveException("failed", "archive-retention-version-mismatch");
            var mode = response.ObjectLockMode?.Value;
            DateTimeOffset? retainUntilUtc = null;
            if (mode is not null)
            {
                var date = response.ObjectLockRetainUntilDate;
                if (!date.HasValue)
                    throw new SiloArchiveException("failed", "archive-retention-invalid");
                retainUntilUtc = date.Value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(date.Value, DateTimeKind.Utc) : date.Value.ToUniversalTime();
            }
            return new(response.ObjectLockLegalHoldStatus == ObjectLockLegalHoldStatus.On, mode, retainUntilUtc);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SiloArchiveException("pending", "archive-retention-version-pending");
        }
        catch (Exception exception) when (SiloS3ObjectStore.IsTlsFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw TlsFailure();
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or
            IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SiloArchiveException("blocked", "archive-retention-unavailable");
        }
    }

    internal async ValueTask<bool> HasLegalHoldAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        if (locator.StoreId != Marker.StoreId || locator.Bucket != Marker.Bucket) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var result = await client.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest
            {
                BucketName = locator.Bucket,
                Key = locator.Key,
                VersionId = locator.VersionId
            }, deadline.Token).ConfigureAwait(false);
            return result.LegalHold?.Status == ObjectLockLegalHoldStatus.On;
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or
            IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    internal async ValueTask<bool> TryPlaceLegalHoldAsync(IConfigurationSection settings, SiloExactLocator locator,
        CancellationToken cancellationToken)
    {
        if (locator.StoreId != Marker.StoreId || locator.Bucket != Marker.Bucket ||
            settings["CustodyAccessKey"] is not { Length: > 0 } key || settings["CustodySecretKey"] is not { Length: > 0 } secret ||
            !AccessKeyPattern().IsMatch(key) || !SecretPattern().IsMatch(secret) ||
            !Uri.TryCreate(settings["Endpoint"], UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps) return false;

        using var authority = new SiloArchiveTransport(Required(settings, "AuthorityPem"));
        using var custody = SiloS3ObjectStore.CreateClient(endpoint, key, secret, authority);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            await custody.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
            {
                BucketName = locator.Bucket,
                Key = locator.Key,
                VersionId = locator.VersionId,
                LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On }
            }, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or HttpRequestException or
            IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    internal async ValueTask<bool> WasExpiredAsync(SiloExactLocator locator, CancellationToken cancellationToken)
    {
        if (!locator.Key.StartsWith("p/", StringComparison.Ordinal)) return false;
        var identity = Encoding.UTF8.GetBytes(string.Join('\n', locator.StoreId, locator.Bucket, locator.Key, locator.VersionId));
        var key = "meta/archive-expiry/" + SiloHash.Sha256(identity) + ".json";
        var current = await SiloVerifier.ReadCurrentAsync(Objects, key, 4096, cancellationToken).ConfigureAwait(false);
        if (current is null) return false;
        SiloArchiveExpiry? record;
        try
        {
            record = JsonSerializer.Deserialize<SiloArchiveExpiry>(current.Value.Bytes, SiloJson.Options);
        }
        catch (JsonException)
        {
            throw new SiloArchiveException("failed", "archive-expiry-disposition-invalid");
        }
        if (record is null || record.Format != ExpiryFormat || record.StoreId != locator.StoreId ||
            record.Bucket != locator.Bucket || record.Key != locator.Key || record.VersionId != locator.VersionId ||
            record.Disposition != "policy-expired" || record.ObservedUtc.Offset != TimeSpan.Zero ||
            record.ObservedUtc == default)
            throw new SiloArchiveException("failed", "archive-expiry-disposition-invalid");
        return true;
    }

    public void Dispose()
    {
        client.Dispose();
        transport.Dispose();
    }
}

/// <summary>Trust only the operator's archive CA and the configured HTTPS origin.</summary>
internal sealed class SiloArchiveTransport : HttpClientFactory, IDisposable
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private readonly X509Certificate2Collection _authority = [];
    private readonly HttpClient _client;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "HttpClient owns its socket handler.")]
    public SiloArchiveTransport(string pem)
    {
        if (pem.Length is 0 or > 65536) throw new ArgumentException("The archive authority must be a bounded PEM file.", nameof(pem));
        try
        {
            _authority.ImportFromPem(pem);
            if (_authority.Count == 0) throw new ArgumentException("The archive authority contains no certificates.", nameof(pem));
            var chain = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            };
            chain.CustomTrustStore.AddRange(_authority);
            chain.ApplicationPolicy.Add(new Oid(ServerAuth));
            _client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateChainPolicy = chain,
                    RemoteCertificateValidationCallback = static (_, certificate, _, errors) =>
                        errors == SslPolicyErrors.None && certificate is X509Certificate2 leaf &&
                        leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension =>
                            extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ServerAuth))
                }
            });
        }
        catch (CryptographicException exception)
        {
            foreach (var certificate in _authority) certificate.Dispose();
            throw new ArgumentException("The archive authority could not be parsed.", nameof(pem), exception);
        }
    }

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client;
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    public void Dispose()
    {
        _client.Dispose();
        foreach (var certificate in _authority) certificate.Dispose();
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Archive attempts expose fixed operator-facing states and reason codes.")]
internal sealed class SiloArchiveException(string state, string reason) : InvalidOperationException(reason)
{
    internal string State { get; } = state;
    internal string Reason { get; } = reason;
}
