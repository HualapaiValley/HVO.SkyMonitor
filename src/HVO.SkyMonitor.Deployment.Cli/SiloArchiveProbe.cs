using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace HVO.SkyMonitor.Deployment;

/// <summary>What a read-only probe of the optional archive Silo found (#1966).</summary>
internal enum SiloArchiveState
{
    /// <summary>TLS chained to the configured authority for the endpoint's name, the credential authenticated, and the bucket is versioned.</summary>
    Verified,

    /// <summary>The archive could not be reached or answered with a server error; local capture is unaffected (degraded).</summary>
    TemporarilyUnavailable,

    /// <summary>The archive answered but is not the configured, trusted, authorized and versioned store.</summary>
    Refused
}

/// <summary>The outcome of one archive probe: a fixed reason code, never server text, and the certificate it saw.</summary>
internal sealed record SiloArchiveObservation(SiloArchiveState State, string Reason, string? ServerCertificateSha256);

/// <summary>
/// The optional archive configuration of a Silo-layout instance at the #1966 boundary: an HTTPS endpoint whose
/// certificate must chain to an operator-supplied authority for the endpoint's own name, a versioned bucket, and a
/// credential authenticated with AWS Signature Version 4. The probe only reads the bucket's versioning state; it sets up
/// no replication, hold or copy, which belong to #1969. Plain HTTP is refused; there is no lab exception.
/// </summary>
internal static partial class SiloArchiveProbe
{
    public const string Region = "us-east-1";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    private const string EmptyPayloadSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const int MaximumAuthorityBytes = 64 * 1024;
    private const int MaximumCredentialBytes = 4 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Checks the endpoint and bucket text; a usage error names what is wrong.</summary>
    public static Uri ValidateEndpoint(string endpoint, string bucket)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
        {
            throw new InstallUsageException(
                "--silo-archive-endpoint must be an https:// URL with a host and optional port only; plain HTTP is not supported.");
        }
        if (!BucketPattern().IsMatch(bucket) || bucket.Contains("..", StringComparison.Ordinal))
        {
            throw new InstallUsageException("--silo-archive-bucket must be a 3 to 63 character S3 bucket name.");
        }
        return uri;
    }

    /// <summary>The certificate authorities in a PEM file, read whole and bounded.</summary>
    public static X509Certificate2Collection ReadAuthority(string path) => ParseAuthority(ReadAuthorityText(path));

    /// <summary>
    /// A PEM authority file's text, read whole, bounded and strictly UTF-8. Admission, the copy the installer keeps and
    /// <c>silo-verify</c> all hash this text (<see cref="AuthoritySha256"/>), so a byte-order mark cannot make them differ.
    /// </summary>
    public static string ReadAuthorityText(string path)
    {
        using var stream = SafeFileSystem.OpenRegularFileRead(path);
        if (stream.Length is 0 or > MaximumAuthorityBytes)
            throw InstallerException.Refused("The archive certificate authority file is empty or too large.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw InstallerException.Refused("The archive certificate authority file is not UTF-8 text.", exception);
        }
    }

    /// <summary>The SHA-256 of an authority's text as the installer keeps it: UTF-8 without a byte-order mark.</summary>
    public static string AuthoritySha256(string pem) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pem)));

    /// <summary>The certificates a PEM text holds; at least one is required.</summary>
    public static X509Certificate2Collection ParseAuthority(string pem)
    {
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (CryptographicException exception)
        {
            throw InstallerException.Refused("The archive certificate authority file is not PEM certificates.", exception);
        }
        if (collection.Count == 0)
            throw InstallerException.Refused("The archive certificate authority file holds no certificate.");
        return collection;
    }

    /// <summary>The archive credential from an owner-only file: the access key ID and the secret on two lines.</summary>
    public static (string AccessKey, string SecretKey) ReadCredential(string path)
    {
        using var stream = OpenCredential(path);
        if (stream.Length is 0 or > MaximumCredentialBytes)
            throw InstallerException.Refused("The archive credential file is empty or too large.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var lines = reader.ReadToEnd().Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length != 0).ToArray();
        if (lines.Length != 2 || !AccessKeyPattern().IsMatch(lines[0]) || !SecretPattern().IsMatch(lines[1]))
            throw InstallerException.Refused("The archive credential file does not hold an access key ID and a secret on two lines.");
        return (lines[0], lines[1]);
    }

    private static FileStream OpenCredential(string path)
    {
        try
        {
            return SafeFileSystem.OpenOwnerFileRead(path);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused("The archive credential file must be a single-link file of this user, mode 0600 or 0400.", exception);
        }
    }

    /// <summary>
    /// Reads the bucket's versioning state over TLS. Never throws for an archive's answer or absence: it reports a state
    /// and a fixed reason. Proxies, redirects and system trust are not used; only the configured authority is trusted.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The handler is disposed by the using declaration that owns it.")]
    public static async Task<SiloArchiveObservation> ProbeAsync(
        Uri endpoint,
        string bucket,
        X509Certificate2Collection authority,
        (string AccessKey, string SecretKey) credential,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Func<SslClientAuthenticationOptions, HttpMessageHandler>? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(timeProvider);
        string? certificateSha256 = null;
        var tlsRejected = false;
        var ssl = new SslClientAuthenticationOptions
        {
            TargetHost = endpoint.IdnHost,
            // The platform's current protocols; no legacy version is enabled.
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, peerChain, errors) =>
            {
                var trusted = Trusted(certificate, errors, authority, PeerCertificates(peerChain), out var sha256);
                certificateSha256 = sha256;
                tlsRejected = !trusted;
                return trusted;
            }
        };
        using var handler = handlerFactory?.Invoke(ssl) ?? new SocketsHttpHandler
        {
            SslOptions = ssl,
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Budget, MaxResponseContentBufferSize = 64 * 1024 };
        var target = new Uri(endpoint, $"/{bucket}?versioning=");
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var host = target.IsDefaultPort ? target.IdnHost : $"{target.IdnHost}:{target.Port.ToString(CultureInfo.InvariantCulture)}";
        var (signature, signedHeaders) = Sign(
            "GET", $"/{bucket}", "versioning=",
            [("host", host), ("x-amz-content-sha256", EmptyPayloadSha256), ("x-amz-date", amzDate)],
            EmptyPayloadSha256, amzDate, Region, "s3", credential.SecretKey);
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", EmptyPayloadSha256);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={credential.AccessKey}/{amzDate[..8]}/{Region}/s3/aws4_request, SignedHeaders={signedHeaders}, Signature={signature}");
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => VersioningEnabled(body)
                    ? new(SiloArchiveState.Verified, "archive-verified", certificateSha256)
                    : new(SiloArchiveState.Refused, "archive-versioning-not-enabled", certificateSha256),
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                    new(SiloArchiveState.Refused, "archive-credential-refused", certificateSha256),
                HttpStatusCode.NotFound => new(SiloArchiveState.Refused, "archive-bucket-missing", certificateSha256),
                >= HttpStatusCode.InternalServerError => new(SiloArchiveState.TemporarilyUnavailable, "archive-server-error", certificateSha256),
                _ => new(SiloArchiveState.Refused, "archive-unexpected-response", certificateSha256)
            };
        }
        catch (HttpRequestException) when (tlsRejected)
        {
            return new(SiloArchiveState.Refused, "archive-tls-untrusted", certificateSha256);
        }
        catch (HttpRequestException exception) when (exception.InnerException is AuthenticationException)
        {
            return new(SiloArchiveState.Refused, "archive-tls-failed", certificateSha256);
        }
        catch (HttpRequestException)
        {
            return new(SiloArchiveState.TemporarilyUnavailable, "archive-unreachable", certificateSha256);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(SiloArchiveState.TemporarilyUnavailable, "archive-timeout", certificateSha256);
        }
    }

    /// <summary>
    /// Whether the server's certificate is for the endpoint's name and chains to the configured authority alone.
    /// </summary>
    internal static bool Trusted(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2Collection authority, out string? sha256)
        => Trusted(certificate, errors, authority, peerCertificates: null, out sha256);

    /// <summary>
    /// Whether the server's certificate is for the endpoint's name and chains to the configured authority alone. The
    /// certificates the server sent with its own may complete that chain as untrusted intermediates (#1966 review): an
    /// authority file that holds only the root still verifies a server that presents its intermediate. Only the configured
    /// authority is ever a trust anchor; a root the server sends is not.
    /// </summary>
    internal static bool Trusted(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        X509Certificate2Collection authority,
        X509Certificate2Collection? peerCertificates,
        out string? sha256)
    {
        sha256 = null;
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var leaf = new X509Certificate2(certificate);
        sha256 = Convert.ToHexStringLower(SHA256.HashData(leaf.RawData));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = DateTime.Now;
        // The certificate must be good for TLS server authentication: a leaf (or an intermediate) that limits itself to
        // other purposes, such as client authentication, is not an archive server's certificate.
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        if (peerCertificates is not null)
            chain.ChainPolicy.ExtraStore.AddRange(peerCertificates);
        return chain.Build(leaf);
    }

    /// <summary>
    /// The certificates the server presented besides its own, as the handshake's chain carries them: the platform's extra
    /// store and the chain elements above the leaf. They are only chain inputs; trust stays with the configured authority.
    /// </summary>
    internal static X509Certificate2Collection? PeerCertificates(X509Chain? peerChain)
    {
        if (peerChain is null) return null;
        var certificates = new X509Certificate2Collection();
        certificates.AddRange(peerChain.ChainPolicy.ExtraStore);
        for (var index = 1; index < peerChain.ChainElements.Count; index++)
            certificates.Add(peerChain.ChainElements[index].Certificate);
        return certificates;
    }

    /// <summary>AWS Signature Version 4 over a request with no body: the signature and the signed header list.</summary>
    internal static (string Signature, string SignedHeaders) Sign(
        string method,
        string canonicalUri,
        string canonicalQuery,
        IReadOnlyList<(string Name, string Value)> headers,
        string payloadSha256,
        string amzDate,
        string region,
        string service,
        string secretKey)
    {
        // Header names are given in their canonical lowercase form.
        var ordered = headers.Select(static header => (header.Name, Value: header.Value.Trim()))
            .OrderBy(static header => header.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Any(static header => header.Name.Any(char.IsAsciiLetterUpper)))
            throw new ArgumentException("Signed header names must be lowercase.", nameof(headers));
        var signedHeaders = string.Join(';', ordered.Select(static header => header.Name));
        var canonicalRequest = string.Join('\n',
            method, canonicalUri, canonicalQuery,
            string.Concat(ordered.Select(static header => $"{header.Name}:{header.Value}\n")),
            signedHeaders, payloadSha256);
        var date = amzDate[..8];
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        return (Convert.ToHexStringLower(Hmac(key, stringToSign)), signedHeaders);
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    internal static bool VersioningEnabled(string body)
    {
        try
        {
            var document = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64 * 1024
            });
            document.Load(reader);
            return document.DocumentElement?.LocalName == "VersioningConfiguration" &&
                   document.DocumentElement.ChildNodes.OfType<XmlElement>()
                       .Any(static element => element.LocalName == "Status" && element.InnerText == "Enabled");
        }
        catch (XmlException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex("^[A-Za-z0-9]{3,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccessKeyPattern();

    [GeneratedRegex("^[\\x21-\\x7e]{8,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
