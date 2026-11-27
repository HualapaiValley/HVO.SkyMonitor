using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Operations)]
public sealed class WebhookNotificationTransportTests
{
    private static readonly OperatorNotificationNetworkPolicy ProductionPolicy = new(isDevelopment: false);
    private static readonly OperatorNotificationNetworkPolicy LoopbackTlsTestPolicy = new(isDevelopment: false)
    {
        AllowLoopbackForTesting = true
    };

    private static OperatorNotificationEvent SampleEvent(string? title = null, string? summary = null) => new(
        OperatorNotificationEventKind.CaptureStalled,
        OperatorNotificationSeverity.Warning,
        "capture_stalled",
        title ?? "Capture stalled",
        summary ?? "No successful capture in 20m.",
        new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero),
        "North Camera",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["elapsedMinutes"] = "20.0" });

    [TestMethod]
    public async Task SendAsync_GenericProfile_SendsStructuredJsonAndBearerHeaderOverPinnedTlsAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var server = InProcessHttpsWebhookServer.Start(cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (server.ConfigureAwait(false))
        {
            using var transport = CreateLoopbackTlsTransport(cert);
            var result = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/alerts/synthetic-path",
                WebhookPayloadFormat.Generic,
                "Authorization",
                null,
                AllowPrivateNetwork: false,
                AuthToken: "synthetic-bearer-token-123",
                UserKey: null,
                SampleEvent(),
                CoalescedCount: 3,
                SuppressedDuringCooldown: 2,
                IsTest: false,
                Timeout: TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.HasCount(1, server.Captured);
            var captured = server.Captured[0];
            Assert.AreEqual("POST", captured.Method);
            Assert.AreEqual("/alerts/synthetic-path", captured.PathAndQuery);
            Assert.AreEqual("Bearer synthetic-bearer-token-123", captured.Header("Authorization"));
            StringAssert.StartsWith(captured.Header("Content-Type")!, "application/json", StringComparison.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(captured.Body);
            Assert.AreEqual("capture_stalled", doc.RootElement.GetProperty("event").GetString());
            Assert.AreEqual("North Camera", doc.RootElement.GetProperty("camera").GetString());
            Assert.AreEqual(3, doc.RootElement.GetProperty("coalescedCount").GetInt32());
            Assert.AreEqual(2, doc.RootElement.GetProperty("suppressedCount").GetInt32());
            StringAssert.Contains(doc.RootElement.GetProperty("text").GetString()!, "Capture stalled", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SendAsync_SlackNtfyAndPushoverProfiles_ProduceOfficialPayloadsAndEscapeSlackMrkdwnAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var server = InProcessHttpsWebhookServer.Start(cert, _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (server.ConfigureAwait(false))
        {
            using var transport = CreateLoopbackTlsTransport(cert);

            // 1. Slack with mrkdwn mention injection attempts (<!channel>, <!here>, <!everyone>, <@U12345>)
            var mentionEvent = SampleEvent(
                title: "Fault <!channel> & <!here>",
                summary: "Check <!everyone> and <@U12345> now");
            var slack = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/services/synthetic-hook",
                WebhookPayloadFormat.Slack,
                "Authorization",
                null,
                false, null, null, mentionEvent, 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(slack.Succeeded, slack.ReasonCode);
            using (var doc = JsonDocument.Parse(server.Captured[0].Body))
            {
                var topText = doc.RootElement.GetProperty("text").GetString()!;
                var sectionText = doc.RootElement.GetProperty("blocks")[1].GetProperty("text").GetProperty("text").GetString()!;
                Assert.DoesNotContain("<!channel>", topText, StringComparison.Ordinal);
                Assert.DoesNotContain("<!here>", topText, StringComparison.Ordinal);
                Assert.DoesNotContain("<!everyone>", topText, StringComparison.Ordinal);
                Assert.DoesNotContain("<@U12345>", topText, StringComparison.Ordinal);
                Assert.DoesNotContain("<!everyone>", sectionText, StringComparison.Ordinal);
                Assert.DoesNotContain("<@U12345>", sectionText, StringComparison.Ordinal);
                StringAssert.Contains(topText, "&lt;!channel&gt;", StringComparison.Ordinal);
                StringAssert.Contains(sectionText, "&lt;!everyone&gt;", StringComparison.Ordinal);
            }

            // 2. ntfy root URL (/) + protected topic -> JSON envelope with topic
            var ntfyRoot = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/",
                WebhookPayloadFormat.Ntfy,
                "X-Custom-Token",
                "hvo-observatory",
                false, "synthetic-ntfy-token", null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(ntfyRoot.Succeeded, ntfyRoot.ReasonCode);
            Assert.AreEqual("synthetic-ntfy-token", server.Captured[1].Header("X-Custom-Token"));
            StringAssert.StartsWith(server.Captured[1].Header("Content-Type")!, "application/json", StringComparison.OrdinalIgnoreCase);
            using (var doc = JsonDocument.Parse(server.Captured[1].Body))
            {
                Assert.AreEqual("hvo-observatory", doc.RootElement.GetProperty("topic").GetString());
                Assert.AreEqual(4, doc.RootElement.GetProperty("priority").GetInt32());
            }

            // 3. ntfy topic-path URL (/secret-topic) -> plain-text body with Title, Priority, Tags headers (NOT JSON)
            var ntfyPath = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/my-secret-topic",
                WebhookPayloadFormat.Ntfy,
                "Authorization",
                null,
                false, "synthetic-ntfy-bearer", null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(ntfyPath.Succeeded, ntfyPath.ReasonCode);
            var ntfyPathReq = server.Captured[2];
            StringAssert.StartsWith(ntfyPathReq.Header("Content-Type")!, "text/plain", StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual("[North Camera] Capture stalled", ntfyPathReq.Header("Title"));
            Assert.AreEqual("4", ntfyPathReq.Header("Priority"));
            Assert.AreEqual("cameraagent,warning,capture_stalled", ntfyPathReq.Header("Tags"));
            Assert.IsFalse(ntfyPathReq.Body.TrimStart().StartsWith('{'), "Topic-path ntfy publish must send plain-text body, not literal JSON.");
            StringAssert.Contains(ntfyPathReq.Body, "No successful capture in 20m.", StringComparison.Ordinal);

            // 4. ntfy root URL with empty topic -> fails closed before connecting
            var ntfyRootMissing = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/",
                WebhookPayloadFormat.Ntfy,
                "Authorization",
                string.Empty,
                false, null, null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(ntfyRootMissing.Succeeded);
            Assert.AreEqual(NotificationReasonCodes.MissingSecret, ntfyRootMissing.ReasonCode);
            Assert.HasCount(3, server.Captured);

            // 5. Pushover
            var pushover = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/1/messages.json",
                WebhookPayloadFormat.Pushover,
                "Authorization",
                null,
                false, "synthetic-app-tok-99", "synthetic-user-key-88", SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(pushover.Succeeded, pushover.ReasonCode);
            Assert.IsNull(server.Captured[3].Header("Authorization"));
            using (var doc = JsonDocument.Parse(server.Captured[3].Body))
            {
                Assert.AreEqual("synthetic-app-tok-99", doc.RootElement.GetProperty("token").GetString());
                Assert.AreEqual("synthetic-user-key-88", doc.RootElement.GetProperty("user").GetString());
                Assert.AreEqual(0, doc.RootElement.GetProperty("priority").GetInt32());
            }
        }
    }

    [TestMethod]
    public async Task SendAsync_RedirectsRateLimitsAndServerErrors_AreClassifiedSafelyAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();

        // 302 redirect refused
        var redirectServer = InProcessHttpsWebhookServer.Start(cert, _ =>
            "HTTP/1.1 302 Found\r\nLocation: https://evil.example.test/redirect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using (redirectServer.ConfigureAwait(false))
        {
            using var transport = CreateLoopbackTlsTransport(cert);
            var res = await transport.SendAsync(CreateGenericRequest($"https://127.0.0.1:{redirectServer.Port}/hook"), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded);
            Assert.IsFalse(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.RedirectBlocked, res.ReasonCode);
        }

        // 429 with Retry-After
        var rateServer = InProcessHttpsWebhookServer.Start(cert, _ =>
            "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 45\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using (rateServer.ConfigureAwait(false))
        {
            using var transport = CreateLoopbackTlsTransport(cert);
            var res = await transport.SendAsync(CreateGenericRequest($"https://127.0.0.1:{rateServer.Port}/hook"), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded);
            Assert.IsTrue(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.RateLimited, res.ReasonCode);
            Assert.AreEqual(TimeSpan.FromSeconds(45), res.RetryAfter);
        }

        // 401 permanent (no secret echo)
        var authServer = InProcessHttpsWebhookServer.Start(cert, _ =>
            "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using (authServer.ConfigureAwait(false))
        {
            using var transport = CreateLoopbackTlsTransport(cert);
            var marker = Guid.NewGuid().ToString("N");
            var pathMarker = $"path-{marker[..8]}";
            var headerMarker = $"header-{marker[8..16]}";
            var res = await transport.SendAsync(CreateGenericRequest($"https://127.0.0.1:{authServer.Port}/{pathMarker}", headerMarker), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded);
            Assert.IsFalse(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.AuthFailed, res.ReasonCode);
            Assert.DoesNotContain(pathMarker, res.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(headerMarker, res.ToString(), StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SendAsync_ConnectTimeDnsPinning_BlocksLoopbackImdsAlibabaMetadataAndEmbeddedIpv6WithZeroSocketsAsync()
    {
        var connectCalls = 0;
        var blockedTargets = new[]
        {
            IPAddress.Loopback,
            IPAddress.IPv6Loopback,
            IPAddress.Parse("169.254.169.254"),
            IPAddress.Parse("100.100.100.200"),
            IPAddress.Parse("100.64.0.10"),
            IPAddress.Parse("fd00:ec2::254"),
            IPAddress.Parse("::127.0.0.1"),
            IPAddress.Parse("::ffff:0:169.254.169.254"),
            IPAddress.Parse("2002:a9fe:a9fe::1"),
            IPAddress.Parse("64:ff9b:1::127.0.0.1"),
            IPAddress.Parse("100::1"),
            IPAddress.Parse("192.88.99.1")
        };

        foreach (var allowPrivate in new[] { false, true })
        {
            foreach (var resolvedIp in blockedTargets)
            {
                using var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System)
                {
                    DnsResolver = (_, _) => Task.FromResult<IPAddress[]>([resolvedIp]),
                    StreamConnector = (_, _, _) =>
                    {
                        Interlocked.Increment(ref connectCalls);
                        throw new InvalidOperationException("Socket connection must never occur for SSRF-blocked target.");
                    }
                };

                var res = await transport.SendAsync(new WebhookDeliveryRequest(
                    "https://rebind.example.test/services/T1/B1/secret",
                    WebhookPayloadFormat.Generic,
                    "Authorization",
                    null,
                    allowPrivate,
                    null,
                    null,
                    SampleEvent(),
                    1,
                    0,
                    false,
                    TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(res.Succeeded, $"Resolved IP {resolvedIp} (allowPrivate={allowPrivate}) must be blocked.");
                Assert.IsFalse(res.Transient, $"Resolved IP {resolvedIp} (allowPrivate={allowPrivate}) must fail non-transiently.");
                Assert.AreEqual(NotificationReasonCodes.SsrfBlocked, res.ReasonCode, $"Resolved IP {resolvedIp} (allowPrivate={allowPrivate})");
            }
        }

        Assert.AreEqual(0, connectCalls, "Zero socket connections may be attempted for SSRF-blocked resolutions.");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task SendAsync_IgnoresHttpsProxyAndAllProxyEnvironmentVariablesAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var targetServer = InProcessHttpsWebhookServer.Start(cert, _ =>
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        var decoyProxyListener = new TcpListener(IPAddress.Loopback, 0);
        decoyProxyListener.Start();
        var decoyProxyPort = ((IPEndPoint)decoyProxyListener.LocalEndpoint).Port;
        var proxyConnections = 0;
        using var proxyCts = new CancellationTokenSource();
        var proxyLoop = Task.Run(async () =>
        {
            try
            {
                while (!proxyCts.IsCancellationRequested)
                {
                    using var accepted = await decoyProxyListener.AcceptTcpClientAsync(proxyCts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref proxyConnections);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Expected on shutdown.
            }
        });

        var priorHttpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var priorAllProxy = Environment.GetEnvironmentVariable("ALL_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{decoyProxyPort}"));
            Environment.SetEnvironmentVariable("ALL_PROXY", string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{decoyProxyPort}"));

            using var transport = new WebhookNotificationTransport(LoopbackTlsTestPolicy, TimeProvider.System)
            {
                DnsResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]),
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
            };

            var result = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://hooks.example.test:{targetServer.Port}/notify",
                WebhookPayloadFormat.Generic,
                "Authorization",
                null,
                false,
                null,
                null,
                SampleEvent(),
                1,
                0,
                false,
                TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.HasCount(1, targetServer.Captured);
            Assert.AreEqual(0, Volatile.Read(ref proxyConnections), "Webhook transport must connect directly and ignore HTTPS_PROXY/ALL_PROXY.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", priorHttpsProxy);
            Environment.SetEnvironmentVariable("ALL_PROXY", priorAllProxy);
            await proxyCts.CancelAsync().ConfigureAwait(false);
            decoyProxyListener.Stop();
            await proxyLoop.ConfigureAwait(false);
            await targetServer.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task SendAsync_RealTlsDelivery_NeverLeaksCredentialPathIntoSystemNetHttpDiagnosticsOrEventsAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var server = InProcessHttpsWebhookServer.Start(cert, _ =>
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
        await using (server.ConfigureAwait(false))
        {
            var marker = Guid.NewGuid().ToString("N");
            var pathSecretCanary = $"T000/B000/SECRET-{marker[..12]}";
            var queryCanary = $"sig-{marker[12..20]}";
            var tokenCanary = $"tok-{marker[20..]}";

            var observedEvents = new ConcurrentBag<string>();
            using var eventListener = new SystemNetHttpEventCollector(observedEvents);
            using var diagnosticSubscription = DiagnosticListener.AllListeners.Subscribe(
                new HttpDiagnosticCollector(observedEvents));

            using var transport = CreateLoopbackTlsTransport(cert);
            var result = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/services/{pathSecretCanary}?k={queryCanary}",
                WebhookPayloadFormat.Slack,
                "Authorization",
                null,
                AllowPrivateNetwork: false,
                AuthToken: tokenCanary,
                UserKey: null,
                SampleEvent(),
                1,
                0,
                false,
                TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.HasCount(1, server.Captured);

            var allEmitted = string.Join("\n", observedEvents.Append(result.ToString()));
            Assert.DoesNotContain(pathSecretCanary, allEmitted, StringComparison.Ordinal);
            Assert.DoesNotContain(marker[..12], allEmitted, StringComparison.Ordinal);
            Assert.DoesNotContain(queryCanary, allEmitted, StringComparison.Ordinal);
            Assert.DoesNotContain(tokenCanary, allEmitted, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SendAsync_SendTimeHeaderValidation_RejectsBadHeaderNamesAndControlCharsWithZeroSocketsAsync()
    {
        var connectCalls = 0;
        using var transport = new WebhookNotificationTransport(LoopbackTlsTestPolicy, TimeProvider.System)
        {
            StreamConnector = (_, _, _) =>
            {
                Interlocked.Increment(ref connectCalls);
                throw new InvalidOperationException("Should never open socket when headers are invalid.");
            }
        };

        var badHeaderName = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://127.0.0.1:443/hook",
            WebhookPayloadFormat.Generic,
            "Host",
            null,
            false,
            "valid-token",
            null,
            SampleEvent(),
            1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(badHeaderName.Succeeded);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, badHeaderName.ReasonCode);

        var crlfInToken = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://127.0.0.1:443/hook",
            WebhookPayloadFormat.Generic,
            "X-Api-Key",
            null,
            false,
            "token\r\nX-Injected: true",
            null,
            SampleEvent(),
            1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(crlfInToken.Succeeded);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, crlfInToken.ReasonCode);

        var crlfInPushoverKey = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://127.0.0.1:443/hook",
            WebhookPayloadFormat.Pushover,
            "Authorization",
            null,
            false,
            "app-token",
            "user\r\nkey",
            SampleEvent(),
            1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(crlfInPushoverKey.Succeeded);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, crlfInPushoverKey.ReasonCode);

        Assert.AreEqual(0, connectCalls);
    }

    [TestMethod]
    public async Task SendAsync_StalledServer_ReturnsTimeoutPromptlyAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var server = InProcessHttpsWebhookServer.Start(cert, _ => null, hang: true);
        await using (server.ConfigureAwait(false))
        {
            using var transport = CreateLoopbackTlsTransport(cert);
            var res = await transport.SendAsync(new WebhookDeliveryRequest(
                $"https://127.0.0.1:{server.Port}/slow",
                WebhookPayloadFormat.Generic,
                "Authorization",
                null,
                false, null, null, SampleEvent(), 1, 0, false, TimeSpan.FromMilliseconds(250)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(res.Succeeded);
            Assert.IsTrue(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.Timeout, res.ReasonCode);
        }
    }

    private static WebhookNotificationTransport CreateLoopbackTlsTransport(X509Certificate2 cert) =>
        new(LoopbackTlsTestPolicy, TimeProvider.System)
        {
            CertificateValidationCallback = (_, presented, _, _) =>
                presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, cert.Thumbprint, StringComparison.Ordinal)
        };

    private static WebhookDeliveryRequest CreateGenericRequest(string url, string? token = null) => new(
        url, WebhookPayloadFormat.Generic, "Authorization", null, false, token, null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5));

    internal static X509Certificate2 CreateEphemeralLoopbackCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        san.AddDnsName("hooks.example.test");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    internal sealed record CapturedHttp(
        string Method,
        string PathAndQuery,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var val) ? val : null;
    }

    internal sealed class InProcessHttpsWebhookServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly Func<CapturedHttp, string?> _responder;
        private readonly bool _hang;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly object _gate = new();
        private readonly List<CapturedHttp> _captured = [];

        public int Port { get; }
        public IReadOnlyList<CapturedHttp> Captured { get { lock (_gate) return [.. _captured]; } }

        private InProcessHttpsWebhookServer(TcpListener listener, X509Certificate2 cert, Func<CapturedHttp, string?> responder, bool hang)
        {
            _listener = listener;
            _cert = cert;
            _responder = responder;
            _hang = hang;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static InProcessHttpsWebhookServer Start(X509Certificate2 cert, Func<CapturedHttp, string?> responder, bool hang = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new InProcessHttpsWebhookServer(listener, cert, responder, hang);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    if (_hang)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    var raw = client.GetStream();
                    await using (raw.ConfigureAwait(false))
                    {
                        var ssl = new SslStream(raw, leaveInnerStreamOpen: true);
                        await using (ssl.ConfigureAwait(false))
                        {
                            await ssl.AuthenticateAsServerAsync(_cert).ConfigureAwait(false);
                            var buffer = new byte[32768];
                            var total = 0;
                            var headerEnd = -1;
                            var contentLength = 0;
                            while (total < buffer.Length)
                            {
                                var read = await ssl.ReadAsync(buffer.AsMemory(total, buffer.Length - total), _cts.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                total += read;
                                if (headerEnd < 0)
                                {
                                    var text = Encoding.ASCII.GetString(buffer, 0, total);
                                    var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                    if (idx >= 0)
                                    {
                                        headerEnd = idx + 4;
                                        foreach (var line in text[..idx].Split("\r\n"))
                                        {
                                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                                int.TryParse(line[15..].Trim(), CultureInfo.InvariantCulture, out var parsedLen))
                                            {
                                                contentLength = parsedLen;
                                            }
                                        }
                                    }
                                }
                                if (headerEnd >= 0 && total >= headerEnd + contentLength) break;
                            }

                            var headText = headerEnd >= 4 ? Encoding.ASCII.GetString(buffer, 0, headerEnd - 4) : string.Empty;
                            var lines = headText.Split("\r\n");
                            var requestParts = lines.Length > 0 ? lines[0].Split(' ') : [];
                            var method = requestParts.Length > 0 ? requestParts[0] : string.Empty;
                            var path = requestParts.Length > 1 ? requestParts[1] : string.Empty;
                            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            for (var i = 1; i < lines.Length; i++)
                            {
                                var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
                                if (colon > 0)
                                {
                                    headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                                }
                            }
                            var body = headerEnd >= 0 && total > headerEnd
                                ? Encoding.UTF8.GetString(buffer, headerEnd, Math.Min(contentLength, total - headerEnd))
                                : string.Empty;

                            var captured = new CapturedHttp(method, path, headers, body);
                            lock (_gate) _captured.Add(captured);

                            var response = _responder(captured);
                            if (response is not null)
                            {
                                var bytes = Encoding.ASCII.GetBytes(response);
                                await ssl.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                                await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
                {
                    // Expected on timeout or client disconnect.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            finally
            {
                _listener.Stop();
                _cts.Dispose();
            }
        }
    }

    private sealed class SystemNetHttpEventCollector : EventListener
    {
        private readonly ConcurrentBag<string> _entries;

        public SystemNetHttpEventCollector(ConcurrentBag<string> entries)
        {
            _entries = entries;
            foreach (var source in EventSource.GetSources())
            {
                if (source.Name.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase))
                {
                    EnableEvents(source, EventLevel.Verbose);
                }
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase))
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var payload = eventData.Payload is null
                ? string.Empty
                : string.Join(", ", eventData.Payload.Select(static p => p?.ToString() ?? string.Empty));
            _entries.Add($"{eventData.EventSource.Name}:{eventData.EventName}:{payload}");
        }
    }

    private sealed class HttpDiagnosticCollector(ConcurrentBag<string> entries)
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly List<IDisposable> _subscriptions = [];

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, "HttpHandlerDiagnosticListener", StringComparison.Ordinal))
            {
                _subscriptions.Add(value.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
            => entries.Add($"diag:{value.Key}:{value.Value}");

        public void OnError(Exception error) { }
        public void OnCompleted()
        {
            foreach (var sub in _subscriptions) sub.Dispose();
        }
    }
}
