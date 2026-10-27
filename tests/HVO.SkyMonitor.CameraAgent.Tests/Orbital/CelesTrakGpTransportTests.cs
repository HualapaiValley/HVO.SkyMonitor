using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using static HVO.SkyMonitor.CameraAgent.Tests.Orbital.CelesTrakGpTestData;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// Drives the production <see cref="CelesTrakGpSource.CreatePrimaryHandler"/> over real TLS and HTTP/1.1 framing. The
/// connection is an in-memory duplex supplied through <see cref="SocketsHttpHandler.ConnectCallback"/>: no socket, DNS
/// lookup or network access, and the certificate is generated in the test.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CelesTrakGpTransportTests
{
    private static readonly TimeSpan ServerDeadline = TimeSpan.FromSeconds(30);
    private static readonly string[] ExpectedRequestHeaders = ["ACCEPT", "ACCEPT-ENCODING", "HOST", "USER-AGENT"];

    [TestMethod]
    public async Task PlatformCertificateValidationRejectsAnUntrustedServer()
    {
        using var server = new InMemoryHttpsServer(_ => Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));
        var client = server.CreateClient(pinCertificate: false);

        var result = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TransportFailure, result.Disposition);
        Assert.AreEqual("orbital.transport-tls", result.ReasonCode);
        Assert.IsNull(result.HttpStatusCode);
        Assert.IsNull(result.RawBytes);
        Assert.IsEmpty(server.Requests);
        Assert.DoesNotContain("celestrak.org", result.Detail, StringComparison.OrdinalIgnoreCase);
        await server.DrainAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RedirectIsAHaltAndItsCookieIsNeverReturned()
    {
        var gzip = Gzip(ReadFixture(ValidFixture));
        using var server = new InMemoryHttpsServer(index => index == 0
            ? Encoding.ASCII.GetBytes("HTTP/1.1 301 Moved Permanently\r\nLocation: https://celestrak.org/elsewhere\r\n" +
                "Set-Cookie: hvo=synthetic; Path=/; Secure\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            : [.. Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\n" +
                $"Content-Length: {gzip.Length}\r\nETag: \"synthetic-wire\"\r\nConnection: close\r\n\r\n"), .. gzip]);
        var client = server.CreateClient(pinCertificate: true);

        var redirected = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.HttpStatusHalt, redirected.Disposition);
        Assert.AreEqual(301, redirected.HttpStatusCode);
        Assert.AreEqual("orbital.non-200-response", redirected.ReasonCode);
        Assert.HasCount(1, server.Requests, "The redirect must not be followed.");

        var retrieved = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.Retrieved, retrieved.Disposition, retrieved.Detail);
        CollectionAssert.AreEqual(ReadFixture(ValidFixture), retrieved.RawBytes);
        Assert.AreEqual("gzip", retrieved.ContentEncoding);
        Assert.AreEqual(gzip.LongLength, retrieved.WireLengthBytes);
        Assert.AreEqual("\"synthetic-wire\"", retrieved.EntityTag);
        Assert.HasCount(2, server.Requests);
        foreach (var request in server.Requests)
        {
            Assert.AreEqual("GET /NORAD/elements/gp.php?GROUP=active&FORMAT=JSON HTTP/1.1", request.RequestLine);
            CollectionAssert.AreEquivalent(ExpectedRequestHeaders,
                request.Headers.Select(static h => h.Name.ToUpperInvariant()).ToArray(),
                string.Join(" | ", request.Headers.Select(static h => h.Name)));
            Assert.AreEqual("celestrak.org", request.Header("Host"));
            Assert.AreEqual(UserAgent, request.Header("User-Agent"));
            Assert.AreEqual("application/json", request.Header("Accept"));
            Assert.AreEqual("gzip", request.Header("Accept-Encoding"));
        }
        await server.DrainAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task OversizedResponseHeadersAreATransportLimit()
    {
        using var server = new InMemoryHttpsServer(_ => Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nX-Pad: " + new string('a', 40 * 1024) +
            "\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]"));
        var client = server.CreateClient(pinCertificate: true);

        var result = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalFetchDisposition.TransportFailure, result.Disposition);
        Assert.AreEqual("orbital.transport-limit", result.ReasonCode, result.Detail);
        Assert.IsNull(result.RawBytes);
        await server.DrainAsync().ConfigureAwait(false);
    }

    private sealed record WireRequest(string RequestLine, IReadOnlyList<(string Name, string Value)> Headers)
    {
        public string Header(string name) =>
            Headers.Single(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>One TLS HTTP/1.1 exchange per connection; responses are produced per accepted request index.</summary>
    private sealed class InMemoryHttpsServer : IDisposable
    {
        private readonly Func<int, byte[]> respond;
        private readonly X509Certificate2 certificate;
        private readonly ConcurrentQueue<WireRequest> requests = new();
        private readonly ConcurrentQueue<Task> connections = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly List<HttpClient> clients = [];
        private int requestCount;

        public InMemoryHttpsServer(Func<int, byte[]> respond)
        {
            this.respond = respond;
            certificate = CreateCertificate();
        }

        public IReadOnlyList<WireRequest> Requests => [.. requests];

        public CelesTrakGpClient CreateClient(bool pinCertificate)
        {
            var handler = (SocketsHttpHandler)CelesTrakGpSource.CreatePrimaryHandler();
            handler.UseProxy = false;
            handler.ConnectCallback = (_, _) =>
            {
                var (clientSide, serverSide) = DuplexStream.CreatePair();
                connections.Enqueue(Task.Run(() => ServeAsync(serverSide), CancellationToken.None));
                return ValueTask.FromResult<Stream>(clientSide);
            };
            if (pinCertificate)
            {
                var pinned = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is not null && string.Equals(presented.GetCertHashString(HashAlgorithmName.SHA256), pinned,
                        StringComparison.Ordinal);
            }
            var httpClient = new HttpClient(handler);
            clients.Add(httpClient);
            return new CelesTrakGpClient(httpClient, TimeProvider.System, DefaultSettings);
        }

        public async Task DrainAsync()
        {
            foreach (var connection in connections)
            {
                await connection.WaitAsync(ServerDeadline).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            foreach (var httpClient in clients)
            {
                httpClient.Dispose();
            }
            stopping.Cancel();
            stopping.Dispose();
            certificate.Dispose();
        }

        private async Task ServeAsync(DuplexStream transport)
        {
            var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
            await using var sslScope = ssl.ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                deadline.CancelAfter(ServerDeadline);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    ClientCertificateRequired = false
                }, deadline.Token).ConfigureAwait(false);
                var request = await ReadRequestAsync(ssl, deadline.Token).ConfigureAwait(false);
                requests.Enqueue(request);
                var response = respond(Interlocked.Increment(ref requestCount) - 1);
                await ssl.WriteAsync(response, deadline.Token).ConfigureAwait(false);
                await ssl.FlushAsync(deadline.Token).ConfigureAwait(false);
                await ssl.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException
                or OperationCanceledException or ObjectDisposedException)
            {
                // The client rejected the certificate or closed after a limit. The client-side outcome is asserted.
            }
        }

        private static async Task<WireRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("request-ended");
                }
                length += read;
                var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
                    var headers = lines.Skip(1).Select(static line =>
                    {
                        var colon = line.IndexOf(':', StringComparison.Ordinal);
                        return (line[..colon], line[(colon + 1)..].Trim());
                    }).ToArray();
                    return new WireRequest(lines[0], headers);
                }
                if (length == buffer.Length)
                {
                    throw new IOException("request-head-too-large");
                }
            }
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=celestrak.org", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("celestrak.org");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var now = DateTimeOffset.UtcNow;
            using var generated = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        }
    }

    /// <summary>A connected in-memory byte stream pair. Disposing one side ends the other side's reads.</summary>
    private sealed class DuplexStream(PipeReader input, PipeWriter output) : Stream
    {
        private readonly Stream reader = input.AsStream();
        private readonly Stream writer = output.AsStream();

        public static (DuplexStream Client, DuplexStream Server) CreatePair()
        {
            var options = new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024);
            var toServer = new Pipe(options);
            var toClient = new Pipe(options);
            return (new DuplexStream(toClient.Reader, toServer.Writer), new DuplexStream(toServer.Reader, toClient.Writer));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            reader.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            reader.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            writer.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            writer.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => writer.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                output.Complete();
                input.Complete();
                reader.Dispose();
                writer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
