using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>A loopback HTTPS archive with real SDK requests and test-controlled exact versions.</summary>
internal sealed class SiloArchiveFixture : IAsyncDisposable
{
    internal const string SettingsPath = "CameraAgent:SiloArchive";
    internal const string StoreId = "silo-archive-11111111111111111111111111111111";
    internal const string Bucket = "archive-bucket";
    internal const string MarkerVersion = "marker-version";
    internal static readonly byte[] Payload = "verified payload"u8.ToArray();

    private readonly ConcurrentDictionary<(string Key, string Version), byte[]> _versions = new();
    private readonly ConcurrentDictionary<string, int> _gets = new(StringComparer.Ordinal);
    private readonly WebApplication _app;
    private readonly X509Certificate2 _authority;
    private readonly X509Certificate2 _server;
    private readonly SiloArchiveMarker _marker;

    internal ConfigurationManager Configuration { get; } = new();
    internal IConfigurationSection Settings => Configuration.GetSection(SettingsPath);
    internal SiloArchiveMarker Marker => _marker;
    internal string? UnavailableKey { get; set; }
    internal string? StallKey { get; set; }
    internal TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SiloArchiveFixture(string binding, string certificateProfile)
    {
        var now = DateTimeOffset.UtcNow;
        _authority = MakeAuthority(now);
        using var leafKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=Silo fixture", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(certificateProfile == "wrong-host" ? IPAddress.IPv6Loopback : IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        if (certificateProfile != "no-eku")
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var expiry = certificateProfile == "expired" ? now.AddHours(-1) : now.AddHours(1);
        using var signed = request.Create(_authority, now.AddHours(-2), expiry, RandomNumberGenerator.GetBytes(16));
        _server = signed.CopyWithPrivateKey(leafKey);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_server)));
        _app = builder.Build();
        _app.Run(RespondAsync);

        _marker = new(SiloArchiveStore.MarkerFormat, StoreId, Bucket, binding, SiloArchiveStore.RetentionPolicy);
        Put(SiloArchiveStore.MarkerKey, MarkerVersion, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_marker, SiloJson.Options));
        Settings["StoreId"] = StoreId;
        Settings["Bucket"] = Bucket;
        Settings["SourceBindingId"] = binding;
        Settings["AccessKey"] = "TestArchiveReader";
        Settings["SecretKey"] = "test-archive-secret";
        Settings["ReplicationEnabled"] = "true";
        if (certificateProfile == "untrusted")
        {
            using var other = MakeAuthority(now);
            Settings["AuthorityPem"] = other.ExportCertificatePem();
        }
        else
        {
            Settings["AuthorityPem"] = _authority.ExportCertificatePem();
        }
    }

    internal static async Task<SiloArchiveFixture> StartAsync(string binding, CancellationToken token, string certificateProfile = "valid")
    {
        var fixture = new SiloArchiveFixture(binding, certificateProfile);
        try
        {
            await fixture._app.StartAsync(token).ConfigureAwait(false);
            fixture.Settings["Endpoint"] = fixture._app.Urls.Single();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static X509Certificate2 MakeAuthority(DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Silo test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
    }

    internal void Put(string key, string version, byte[] bytes) => _versions[(key, version)] = bytes;

    internal void Delete(SiloExactLocator locator) => _versions.TryRemove((locator.Key, locator.VersionId), out _);

    internal void Replicate(SiloTestStore store, SiloExactLocator locator) =>
        Put(locator.Key, locator.VersionId, store.Read(locator.Key, locator.VersionId));

    internal void Replicate(SiloTestStore store, SiloCommittedSegment segment)
    {
        var key = SiloObjectNamespace.SegmentKey(segment.Position);
        Put(key, segment.VersionId, store.Read(key, segment.VersionId));
    }

    internal int Gets(string key) => _gets.GetValueOrDefault(key);

    internal void WriteExpiry(SiloExactLocator locator, DateTimeOffset observed, bool tampered = false)
    {
        var key = "meta/archive-expiry/" + SiloHash.Sha256(Encoding.UTF8.GetBytes(
            string.Join('\n', StoreId, Bucket, locator.Key, locator.VersionId))) + ".json";
        var record = new SiloArchiveExpiry(SiloArchiveStore.ExpiryFormat, StoreId, Bucket, locator.Key,
            tampered ? "other-version" : locator.VersionId, "policy-expired", observed);
        Put(key, "expiry", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, SiloJson.Options));
    }

    private async Task RespondAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var key = request.Path.Value![(Bucket.Length + 1)..].TrimStart('/');
        if (key.Length == 0)
        {
            switch (request.Query.Keys.Single())
            {
                case "versioning":
                    await response.WriteAsync("<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>").ConfigureAwait(false);
                    return;
                case "object-lock":
                    await response.WriteAsync("<ObjectLockConfiguration><ObjectLockEnabled>Enabled</ObjectLockEnabled></ObjectLockConfiguration>").ConfigureAwait(false);
                    return;
                case "lifecycle":
                    await response.WriteAsync("<LifecycleConfiguration/>").ConfigureAwait(false);
                    return;
            }
        }
        if (HttpMethods.IsGet(request.Method))
            _gets.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if (key == StallKey)
        {
            Stalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
        }
        if (key == UnavailableKey)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var version = request.Query.TryGetValue("versionId", out var exact) ? exact.ToString() :
            _versions.Keys.Where(candidate => candidate.Key == key).Select(static candidate => candidate.Version).SingleOrDefault();
        if (version is null || !_versions.TryGetValue((key, version), out var bytes))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            await response.WriteAsync("<Error><Code>NoSuchVersion</Code></Error>").ConfigureAwait(false);
            return;
        }
        response.Headers["x-amz-version-id"] = version;
        response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(request.Method))
            await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        _server.Dispose();
        _authority.Dispose();
    }
}
