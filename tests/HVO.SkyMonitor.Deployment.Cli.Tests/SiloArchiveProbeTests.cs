using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The optional archive configuration's read-only probe (#1966): HTTPS only, the server's certificate must be for the
/// endpoint's name and chain to the operator's authority alone, the request is AWS Signature Version 4 with no secret on
/// the wire, and every answer or absence maps to a fixed state and reason, never server text. It reads only the bucket's
/// versioning; replication and holds are #1969's.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloArchiveProbeTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Secret = "archiveSECRET0123456789";
    private static readonly DateTimeOffset At = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Sign_MatchesTheAwsSignatureVersion4TestSuite()
    {
        // aws-sig-v4-test-suite get-vanilla.
        var (signature, signed) = SiloArchiveProbe.Sign("GET", "/", string.Empty,
            [("host", "example.amazonaws.com"), ("x-amz-date", "20150830T123600Z")],
            EmptySha256, "20150830T123600Z", "us-east-1", "service", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");

        Assert.AreEqual("5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", signature);
        Assert.AreEqual("host;x-amz-date", signed);
    }

    [TestMethod]
    public void Sign_TheVersioningReadMatchesAnIndependentImplementation()
    {
        var (signature, signed) = SiloArchiveProbe.Sign("GET", "/hvo-archive", "versioning=",
            [("x-amz-date", "20261122T120000Z"), ("host", "archive.example:9443"), ("x-amz-content-sha256", EmptySha256)],
            EmptySha256, "20261122T120000Z", "us-east-1", "s3", Secret);

        Assert.AreEqual("3ce99607f92cbb7088ea377c954f6c4493de2db6bbee9d49dd2e4f3f9a9fba2a", signature);
        Assert.AreEqual("host;x-amz-content-sha256;x-amz-date", signed);
        Assert.ThrowsExactly<ArgumentException>(() => SiloArchiveProbe.Sign("GET", "/", string.Empty, [("Host", "x")],
            EmptySha256, "20261122T120000Z", "us-east-1", "s3", Secret));
    }

    [TestMethod]
    [DataRow("https://archive.example", "hvo-archive", true)]
    [DataRow("https://archive.example:9443/", "hvo.archive-2", true)]
    [DataRow("http://archive.example:9000", "hvo-archive", false)]
    [DataRow("https://user@archive.example", "hvo-archive", false)]
    [DataRow("https://archive.example/bucket", "hvo-archive", false)]
    [DataRow("https://archive.example/?x=1", "hvo-archive", false)]
    [DataRow("https://archive.example/#f", "hvo-archive", false)]
    [DataRow("archive.example:9443", "hvo-archive", false)]
    [DataRow("https://archive.example", "HVO-archive", false)]
    [DataRow("https://archive.example", "ab", false)]
    [DataRow("https://archive.example", "-archive", false)]
    [DataRow("https://archive.example", "hvo..archive", false)]
    public void ValidateEndpoint_AdmitsOnlyAnHttpsAuthorityAndAnS3BucketName(string endpoint, string bucket, bool admitted)
    {
        if (admitted)
        {
            Assert.AreEqual(Uri.UriSchemeHttps, SiloArchiveProbe.ValidateEndpoint(endpoint, bucket).Scheme);
            return;
        }
        Assert.ThrowsExactly<InstallUsageException>(() => SiloArchiveProbe.ValidateEndpoint(endpoint, bucket));
    }

    [TestMethod]
    public void ReadCredential_TakesAnOwnerOnlyFileOfTwoLinesAndRefusesAnythingElse()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("credential", "AKIAHVO123\narchiveSECRET0123456789\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.AreEqual(("AKIAHVO123", Secret), SiloArchiveProbe.ReadCredential(path));

        foreach (var (content, mode) in new[]
                 {
                     ("AKIAHVO123\narchiveSECRET0123456789\n", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead),
                     ("AKIAHVO123\n", UnixFileMode.UserRead | UnixFileMode.UserWrite),
                     ("AKIAHVO123\nshort\n", UnixFileMode.UserRead | UnixFileMode.UserWrite),
                     ("AKIA HVO\narchiveSECRET0123456789\n", UnixFileMode.UserRead | UnixFileMode.UserWrite),
                     ("AKIAHVO123\narchiveSECRET0123456789\nthird\n", UnixFileMode.UserRead | UnixFileMode.UserWrite),
                     (string.Empty, UnixFileMode.UserRead | UnixFileMode.UserWrite)
                 })
        {
            var refused = directory.File($"credential-{Guid.NewGuid():N}", content, mode);
            Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => SiloArchiveProbe.ReadCredential(refused)).IsRefusal, content);
        }
    }

    [TestMethod]
    public void ReadAuthority_TakesPemCertificatesAndHashesTheTextAsKept()
    {
        using var directory = new TemporaryDirectory();
        using var authority = Authority("HVO archive test authority");
        var pem = authority.ExportCertificatePem() + "\n";
        var plain = directory.File("ca.pem", pem, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var withMark = Path.Combine(directory.Path, "ca-bom.pem");
        File.WriteAllBytes(withMark, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(pem)]);

        Assert.HasCount(1, SiloArchiveProbe.ReadAuthority(plain));
        // A byte-order mark is not part of the text, so the copy the installer keeps hashes as admitted.
        Assert.AreEqual(SiloArchiveProbe.AuthoritySha256(SiloArchiveProbe.ReadAuthorityText(plain)),
            SiloArchiveProbe.AuthoritySha256(SiloArchiveProbe.ReadAuthorityText(withMark)));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(plain))),
            SiloArchiveProbe.AuthoritySha256(SiloArchiveProbe.ReadAuthorityText(plain)));

        foreach (var content in new[] { "not a certificate", "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n" })
        {
            var refused = directory.File($"ca-{Guid.NewGuid():N}.pem", content, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => SiloArchiveProbe.ReadAuthority(refused)).IsRefusal, content);
        }
        var empty = directory.File("empty.pem", string.Empty, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => SiloArchiveProbe.ReadAuthority(empty)).IsRefusal);
        var large = directory.File("large.pem", new string('x', 64 * 1024 + 1), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => SiloArchiveProbe.ReadAuthority(large)).IsRefusal);
        var invalid = Path.Combine(directory.Path, "invalid.pem");
        File.WriteAllBytes(invalid, [0x41, 0xC3, 0x28]);
        Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => SiloArchiveProbe.ReadAuthority(invalid)).IsRefusal);
    }

    [TestMethod]
    public void VersioningEnabled_ReadsOnlyAnEnabledStatusAndRefusesADocumentType()
    {
        Assert.IsTrue(SiloArchiveProbe.VersioningEnabled(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Status>Enabled</Status></VersioningConfiguration>"));
        Assert.IsFalse(SiloArchiveProbe.VersioningEnabled("<VersioningConfiguration><Status>Suspended</Status></VersioningConfiguration>"));
        Assert.IsFalse(SiloArchiveProbe.VersioningEnabled("<VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"/>"));
        Assert.IsFalse(SiloArchiveProbe.VersioningEnabled("<Other><Status>Enabled</Status></Other>"));
        Assert.IsFalse(SiloArchiveProbe.VersioningEnabled("not xml"));
        Assert.IsFalse(SiloArchiveProbe.VersioningEnabled(
            "<!DOCTYPE v [<!ENTITY e \"Enabled\">]><VersioningConfiguration><Status>&e;</Status></VersioningConfiguration>"));
    }

    [TestMethod]
    [DataRow(200, "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>", "Verified", "archive-verified")]
    [DataRow(200, "<VersioningConfiguration><Status>Suspended</Status></VersioningConfiguration>", "Refused", "archive-versioning-not-enabled")]
    [DataRow(200, "<VersioningConfiguration/>", "Refused", "archive-versioning-not-enabled")]
    [DataRow(403, "<Error><Code>AccessDenied</Code></Error>", "Refused", "archive-credential-refused")]
    [DataRow(401, "", "Refused", "archive-credential-refused")]
    [DataRow(404, "<Error><Code>NoSuchBucket</Code></Error>", "Refused", "archive-bucket-missing")]
    [DataRow(500, "", "TemporarilyUnavailable", "archive-server-error")]
    [DataRow(503, "<Error><Code>SlowDown</Code></Error>", "TemporarilyUnavailable", "archive-server-error")]
    [DataRow(301, "", "Refused", "archive-unexpected-response")]
    [DataRow(400, "<Error><Code>InvalidRequest</Code></Error>", "Refused", "archive-unexpected-response")]
    public async Task Probe_ClassifiesEveryAnswerWithAFixedReasonAndSignsAReadOnlyRequest(int status, string body, string state, string reason)
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });

        var observed = await ProbeAsync(handler);

        Assert.AreEqual(Enum.Parse<SiloArchiveState>(state), observed.State);
        Assert.AreEqual(reason, observed.Reason);
        var request = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Get, request.Method);
        Assert.AreEqual("https://archive.example:9443/hvo-archive?versioning=", request.RequestUri!.AbsoluteUri);
        Assert.AreEqual("archive.example:9443", request.Headers.Host);
        Assert.AreEqual("20261122T120000Z", request.Headers.GetValues("x-amz-date").Single());
        Assert.AreEqual(EmptySha256, request.Headers.GetValues("x-amz-content-sha256").Single());
        Assert.AreEqual(
            "AWS4-HMAC-SHA256 Credential=AKIAHVO123/20261122/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=3ce99607f92cbb7088ea377c954f6c4493de2db6bbee9d49dd2e4f3f9a9fba2a",
            request.Headers.GetValues("Authorization").Single());
        Assert.IsFalse(request.ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.IsNull(request.Content);
    }

    [TestMethod]
    public async Task Probe_AnArchiveItCannotReachIsTemporarilyUnavailableNeverRefused()
    {
        using var refusing = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        using var slow = new RecordingHandler(_ => throw new TaskCanceledException("budget"));
        var unreachable = await ProbeAsync(refusing);
        var timedOut = await ProbeAsync(slow);

        Assert.AreEqual(new SiloArchiveObservation(SiloArchiveState.TemporarilyUnavailable, "archive-unreachable", null), unreachable);
        Assert.AreEqual(new SiloArchiveObservation(SiloArchiveState.TemporarilyUnavailable, "archive-timeout", null), timedOut);
    }

    [TestMethod]
    public void Trusted_RequiresTheEndpointsNameAndAChainToTheConfiguredAuthorityAlone()
    {
        using var authority = Authority("HVO archive test authority");
        using var other = Authority("Another authority");
        using var leaf = Leaf(authority, "archive.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var expired = Leaf(authority, "archive.example", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var trusted = new X509Certificate2Collection(authority);

        // The platform reports a chain error for an authority it does not know; only the configured one decides.
        Assert.IsTrue(SiloArchiveProbe.Trusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors, trusted, out var sha256));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(leaf.RawData)), sha256);
        Assert.IsFalse(SiloArchiveProbe.Trusted(leaf, SslPolicyErrors.None, new X509Certificate2Collection(other), out _));
        Assert.IsFalse(SiloArchiveProbe.Trusted(leaf, SslPolicyErrors.RemoteCertificateNameMismatch, trusted, out _));
        Assert.IsFalse(SiloArchiveProbe.Trusted(null, SslPolicyErrors.RemoteCertificateNotAvailable, trusted, out _));
        Assert.IsFalse(SiloArchiveProbe.Trusted(expired, SslPolicyErrors.None, trusted, out _));
        Assert.IsFalse(SiloArchiveProbe.Trusted(authority, SslPolicyErrors.None, new X509Certificate2Collection(other), out _));
    }

    [TestMethod]
    [DataRow("server", true)]
    [DataRow("client-only", false)]
    [DataRow("no-usage", true)]
    [DataRow("server-and-client", true)]
    public void Trusted_RequiresTheServerAuthenticationPurposeWhenTheCertificateStatesOne(string usage, bool trusted)
    {
        using var authority = Authority("HVO archive test authority");
        OidCollection? purposes = usage switch
        {
            "server" => [new Oid("1.3.6.1.5.5.7.3.1")],
            "client-only" => [new Oid("1.3.6.1.5.5.7.3.2")],
            "server-and-client" => [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
            _ => null
        };
        using var leaf = Leaf(authority, "archive.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), purposes);

        Assert.AreEqual(trusted, SiloArchiveProbe.Trusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors,
            new X509Certificate2Collection(authority), out _), usage);
    }

    [TestMethod]
    public async Task Probe_OverRealTls_VerifiesOnlyTheConfiguredAuthorityAndTheEndpointsName()
    {
        using var authority = Authority("HVO archive test authority");
        using var other = Authority("Another authority");
        using var leaf = Leaf(authority, "localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var misnamed = Leaf(authority, "archive.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        const string enabled = "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>";

        await using (var server = await TlsServer.StartAsync(leaf, enabled, TestContext.CancellationToken))
        {
            var verified = await RealProbeAsync(server.Port, new X509Certificate2Collection(authority));
            Assert.AreEqual(SiloArchiveState.Verified, verified.State);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(leaf.RawData)), verified.ServerCertificateSha256);
            StringAssert.StartsWith(server.Requests.Single(), "GET /hvo-archive?versioning= HTTP/1.1", StringComparison.Ordinal);

            var untrusted = await RealProbeAsync(server.Port, new X509Certificate2Collection(other));
            Assert.AreEqual(SiloArchiveState.Refused, untrusted.State);
            Assert.AreEqual("archive-tls-untrusted", untrusted.Reason);
            Assert.HasCount(1, server.Requests, "Nothing is sent to a server the authority does not vouch for.");
        }
        await using (var server = await TlsServer.StartAsync(misnamed, enabled, TestContext.CancellationToken))
        {
            var wrongName = await RealProbeAsync(server.Port, new X509Certificate2Collection(authority));
            Assert.AreEqual(SiloArchiveState.Refused, wrongName.State);
            Assert.AreEqual("archive-tls-untrusted", wrongName.Reason);
            Assert.IsEmpty(server.Requests);
        }

        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var unreachable = await RealProbeAsync(port, new X509Certificate2Collection(authority));
        Assert.AreEqual(SiloArchiveState.TemporarilyUnavailable, unreachable.State);
        Assert.AreEqual("archive-unreachable", unreachable.Reason);
    }

    /// <summary>
    /// #1966 review: an authority file holding only the root verifies a server whose certificate needs an intermediate,
    /// when the server presents that intermediate. The peer's certificates only complete the chain: a root the server sends
    /// is never trusted, so another hierarchy's leaf is refused even with its whole chain presented.
    /// </summary>
    [TestMethod]
    public void Trusted_CompletesTheChainWithTheServersIntermediateButTrustsOnlyTheConfiguredRoot()
    {
        using var root = Authority("HVO archive test root");
        using var intermediate = Intermediate(root, "HVO archive test intermediate");
        using var leaf = Leaf(intermediate, "archive.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var rogueRoot = Authority("Rogue root");
        using var rogueIntermediate = Intermediate(rogueRoot, "Rogue intermediate");
        using var rogueLeaf = Leaf(rogueIntermediate, "archive.example", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var rootOnly = new X509Certificate2Collection(root);

        Assert.IsTrue(SiloArchiveProbe.Trusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors, rootOnly,
            new X509Certificate2Collection(intermediate), out _), "The presented intermediate completes the chain to the root.");
        Assert.IsFalse(SiloArchiveProbe.Trusted(leaf, SslPolicyErrors.RemoteCertificateChainErrors, rootOnly, [], out _),
            "Without the intermediate the leaf does not chain to the root alone.");
        Assert.IsFalse(SiloArchiveProbe.Trusted(rogueLeaf, SslPolicyErrors.RemoteCertificateChainErrors, rootOnly,
            new X509Certificate2Collection(new[] { rogueIntermediate, rogueRoot }), out _), "A root the server sends is never a trust anchor.");
    }

    [TestMethod]
    public async Task Probe_OverRealTls_AcceptsAServerThatPresentsItsIntermediateWithARootOnlyAuthority()
    {
        using var root = Authority("HVO archive test root");
        using var intermediate = Intermediate(root, "HVO archive test intermediate");
        using var leaf = Leaf(intermediate, "localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var rogueRoot = Authority("Rogue root");
        using var rogueIntermediate = Intermediate(rogueRoot, "Rogue intermediate");
        using var rogueLeaf = Leaf(rogueIntermediate, "localhost", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        const string enabled = "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>";
        var rootOnly = new X509Certificate2Collection(root);

        await using (var server = await TlsServer.StartAsync(leaf, enabled, TestContext.CancellationToken, new X509Certificate2Collection(intermediate)))
        {
            var verified = await RealProbeAsync(server.Port, rootOnly);
            Assert.AreEqual(SiloArchiveState.Verified, verified.State, verified.Reason);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(leaf.RawData)), verified.ServerCertificateSha256);
            Assert.HasCount(1, server.Requests);
        }
        await using (var server = await TlsServer.StartAsync(leaf, enabled, TestContext.CancellationToken))
        {
            var incomplete = await RealProbeAsync(server.Port, rootOnly);
            Assert.AreEqual(SiloArchiveState.Refused, incomplete.State);
            Assert.AreEqual("archive-tls-untrusted", incomplete.Reason);
            Assert.IsEmpty(server.Requests, "A chain the server does not complete is not trusted, and nothing is sent.");
        }
        await using (var server = await TlsServer.StartAsync(rogueLeaf, enabled, TestContext.CancellationToken,
                         new X509Certificate2Collection(new[] { rogueIntermediate, rogueRoot })))
        {
            var rogue = await RealProbeAsync(server.Port, rootOnly);
            Assert.AreEqual(SiloArchiveState.Refused, rogue.State);
            Assert.AreEqual("archive-tls-untrusted", rogue.Reason);
            Assert.IsEmpty(server.Requests, "A server presenting another hierarchy with its own root is refused before anything is sent.");
        }
    }

    private Task<SiloArchiveObservation> RealProbeAsync(int port, X509Certificate2Collection authority)
        => SiloArchiveProbe.ProbeAsync(new Uri($"https://localhost:{port}/"), "hvo-archive", authority, ("AKIAHVO123", Secret),
            TimeProvider.System, TestContext.CancellationToken);

    private Task<SiloArchiveObservation> ProbeAsync(RecordingHandler handler)
        => SiloArchiveProbe.ProbeAsync(new Uri("https://archive.example:9443/"), "hvo-archive", [], ("AKIAHVO123", Secret),
            new FixedTime(At), TestContext.CancellationToken, _ => handler);

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(60));
    }

    private static X509Certificate2 Leaf(X509Certificate2 authority, string dnsName, DateTimeOffset notBefore, DateTimeOffset notAfter)
        => Leaf(authority, dnsName, notBefore, notAfter, [new Oid("1.3.6.1.5.5.7.3.1")]);

    /// <summary>An intermediate certificate authority under <paramref name="root"/>, holding its private key to issue leaves.</summary>
    private static X509Certificate2 Intermediate(X509Certificate2 root, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var signed = request.Create(root, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(45), RandomNumberGenerator.GetBytes(8));
        return signed.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 Leaf(
        X509Certificate2 authority, string dnsName, DateTimeOffset notBefore, DateTimeOffset notAfter, OidCollection? purposes)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        if (purposes is not null)
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, false));
        using var signed = request.Create(authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));
        return signed.CopyWithPrivateKey(key);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A loopback HTTPS server that answers every request with one body and records each request line.</summary>
    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private CancellationTokenSource? linked;
        private Task serving = Task.CompletedTask;

        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        /// <summary>A server presenting <paramref name="certificate"/>, and <paramref name="intermediates"/> after it when given.</summary>
        public static Task<TlsServer> StartAsync(
            X509Certificate2 certificate, string body, CancellationToken cancellationToken, X509Certificate2Collection? intermediates = null)
        {
            var server = new TlsServer();
            server.listener.Start();
            server.linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, server.stopping.Token);
            var token = server.linked.Token;
            var context = SslStreamCertificateContext.Create(certificate, intermediates ?? new X509Certificate2Collection(), offline: true);
            server.serving = Task.Run(() => server.ServeAsync(context, body, token), CancellationToken.None);
            return Task.FromResult(server);
        }

        private async Task ServeAsync(SslStreamCertificateContext certificate, string body, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                using (client)
                {
                    try
                    {
                        await using var tls = new SslStream(client.GetStream());
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = certificate }, cancellationToken);
                        using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                        var first = await reader.ReadLineAsync(cancellationToken);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                        {
                        }
                        if (first is not null)
                            Requests.Enqueue(first);
                        var content = Encoding.ASCII.GetBytes(body);
                        await tls.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), cancellationToken);
                        await tls.WriteAsync(content, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
                    {
                        // A client that refuses the certificate closes the handshake.
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Dispose();
            await serving;
            linked?.Dispose();
            stopping.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("hvo-silo-archive-").FullName;

        public string File(string name, string content, UnixFileMode mode)
        {
            var path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(path, content);
            System.IO.File.SetUnixFileMode(path, mode);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
