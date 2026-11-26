using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Operations)]
public sealed class SmtpNotificationTransportTests
{
    private static readonly OperatorNotificationNetworkPolicy LoopbackTestPolicy = new(isDevelopment: false)
    {
        AllowLoopbackForTesting = true
    };
    private static readonly string[] ExpectedDualRecipients = ["<ops1@example.test>", "<ops2@example.test>"];

    [TestMethod]
    public async Task SendAsync_PlaintextRelay_DeliversDotStuffedMessageAndHeadersAsync()
    {
        var server = await InProcessSmtpCaptureServer.StartAsync(new SmtpServerBehavior()).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System);

            var request = new SmtpDeliveryRequest(
                "127.0.0.1",
                server.Port,
                SmtpSecurityMode.None,
                "camera@example.test",
                "North Camera",
                ["ops1@example.test", "ops2@example.test"],
                Username: null,
                Password: null,
                Subject: "[HVO CameraAgent] Disk pressure",
                Body: "Line one\n.Leading dot line\n..Two dots\nFinal line",
                EventSlug: "disk_pressure",
                Timeout: TimeSpan.FromSeconds(5));

            var result = await transport.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.AreEqual(NotificationReasonCodes.Sent, result.ReasonCode);
            Assert.HasCount(1, server.CapturedMessages);
            var captured = server.CapturedMessages[0];
            Assert.AreEqual("<camera@example.test>", captured.MailFrom);
            CollectionAssert.AreEqual(ExpectedDualRecipients, captured.Recipients.ToArray());
            StringAssert.Contains(captured.Data, "Subject: [HVO CameraAgent] Disk pressure", StringComparison.Ordinal);
            StringAssert.Contains(captured.Data, "X-HVO-Notification-Event: disk_pressure", StringComparison.Ordinal);
            StringAssert.Contains(captured.Data, "\r\n..Leading dot line\r\n", StringComparison.Ordinal);
            StringAssert.Contains(captured.Data, "\r\n...Two dots\r\n", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SendAsync_AuthPlainAndStartTls_UpgradesReEhlosAndAuthenticatesAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var expectedThumbprint = cert.Thumbprint;
        var behavior = new SmtpServerBehavior
        {
            SupportsStartTls = true,
            ServerCertificate = cert,
            AuthMechanisms = "PLAIN LOGIN",
            ExpectedUsername = "smtp-user-alpha",
            ExpectedPassword = "synthetic-smtp-password-plain"
        };
        var server = await InProcessSmtpCaptureServer.StartAsync(behavior).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, expectedThumbprint, StringComparison.Ordinal)
            };

            var request = new SmtpDeliveryRequest(
                "127.0.0.1",
                server.Port,
                SmtpSecurityMode.StartTls,
                "camera@example.test",
                "CameraAgent",
                ["ops@example.test"],
                behavior.ExpectedUsername,
                behavior.ExpectedPassword,
                "Test STARTTLS",
                "Encrypted body",
                "test",
                TimeSpan.FromSeconds(5));

            var result = await transport.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.HasCount(1, server.CapturedMessages);
            Assert.IsTrue(server.CapturedMessages[0].UsedTls);
            Assert.AreEqual("PLAIN", server.CapturedMessages[0].AuthMechanismUsed);
            Assert.AreEqual(2, server.CapturedMessages[0].EhloCount);
        }
    }

    [TestMethod]
    public async Task SendAsync_AuthLoginAzureSmtpPattern_AuthenticatesOverStartTlsAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var expectedThumbprint = cert.Thumbprint;
        var behavior = new SmtpServerBehavior
        {
            SupportsStartTls = true,
            ServerCertificate = cert,
            AuthMechanisms = "LOGIN",
            ExpectedUsername = "acs-resource.guid-app.guid-tenant",
            ExpectedPassword = "synthetic-azure-smtp-secret-value"
        };
        var server = await InProcessSmtpCaptureServer.StartAsync(behavior).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, expectedThumbprint, StringComparison.Ordinal)
            };

            var request = new SmtpDeliveryRequest(
                "127.0.0.1",
                server.Port,
                SmtpSecurityMode.StartTls,
                "DoNotReply@example.test",
                "CameraAgent",
                ["ops@example.test"],
                behavior.ExpectedUsername,
                behavior.ExpectedPassword,
                "Azure ACS SMTP Test",
                "Hello from CameraAgent",
                "daily_heartbeat",
                TimeSpan.FromSeconds(5));

            var result = await transport.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.HasCount(1, server.CapturedMessages);
            Assert.IsTrue(server.CapturedMessages[0].UsedTls);
            Assert.AreEqual("LOGIN", server.CapturedMessages[0].AuthMechanismUsed);
        }
    }

    [TestMethod]
    public async Task SendAsync_ImplicitTls_NegotiatesTlsImmediatelyAndDeliversAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var expectedThumbprint = cert.Thumbprint;
        var behavior = new SmtpServerBehavior
        {
            ImplicitTls = true,
            ServerCertificate = cert,
            AuthMechanisms = "PLAIN",
            ExpectedUsername = "implicit-user",
            ExpectedPassword = "synthetic-implicit-password"
        };
        var server = await InProcessSmtpCaptureServer.StartAsync(behavior).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, expectedThumbprint, StringComparison.Ordinal)
            };

            var request = new SmtpDeliveryRequest(
                "127.0.0.1",
                server.Port,
                SmtpSecurityMode.ImplicitTls,
                "camera@example.test",
                "CameraAgent",
                ["ops@example.test"],
                behavior.ExpectedUsername,
                behavior.ExpectedPassword,
                "Implicit TLS Test",
                "Body",
                "capture_stalled",
                TimeSpan.FromSeconds(5));

            var result = await transport.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.Succeeded, result.ReasonCode);
            Assert.HasCount(1, server.CapturedMessages);
            Assert.IsTrue(server.CapturedMessages[0].UsedTls);
        }
    }

    [TestMethod]
    public async Task SendAsync_StartTlsMissingOnServer_FailsClosedWithoutSendingAuthAsync()
    {
        var behavior = new SmtpServerBehavior
        {
            SupportsStartTls = false,
            AuthMechanisms = "PLAIN"
        };
        var server = await InProcessSmtpCaptureServer.StartAsync(behavior).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System);

            var request = new SmtpDeliveryRequest(
                "127.0.0.1",
                server.Port,
                SmtpSecurityMode.StartTls,
                "camera@example.test",
                "CameraAgent",
                ["ops@example.test"],
                "secret-user-must-not-be-sent",
                "secret-pass-must-not-be-sent",
                "Should Fail Closed",
                "Body",
                "test",
                TimeSpan.FromSeconds(5));

            var result = await transport.SendAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.Transient);
            Assert.AreEqual(NotificationReasonCodes.TlsUnsupported, result.ReasonCode);
            Assert.IsEmpty(server.CapturedMessages);
            Assert.IsFalse(server.ReceivedAnyAuthCommand);
        }
    }

    [TestMethod]
    public async Task SendAsync_TransientAndAuthErrors_MapToSanitizedReasonCodesWithoutLeakingSecretsAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var expectedThumbprint = cert.Thumbprint;
        var badAuthBehavior = new SmtpServerBehavior
        {
            SupportsStartTls = true,
            ServerCertificate = cert,
            AuthMechanisms = "PLAIN",
            ExpectedUsername = "right-user",
            ExpectedPassword = "right-password"
        };
        var authServer = await InProcessSmtpCaptureServer.StartAsync(badAuthBehavior).ConfigureAwait(false);
        await using (authServer.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, expectedThumbprint, StringComparison.Ordinal)
            };

            const string wrongPassword = "wrong-secret-password-98765";
            var authResult = await transport.SendAsync(new SmtpDeliveryRequest(
                "127.0.0.1", authServer.Port, SmtpSecurityMode.StartTls,
                "camera@example.test", "Camera", ["ops@example.test"],
                "right-user", wrongPassword, "Subj", "Body", "test", TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(authResult.Succeeded);
            Assert.IsFalse(authResult.Transient);
            Assert.AreEqual(NotificationReasonCodes.AuthFailed, authResult.ReasonCode);
            Assert.DoesNotContain(wrongPassword, authResult.ToString(), StringComparison.Ordinal);

            var transientServer = await InProcessSmtpCaptureServer.StartAsync(new SmtpServerBehavior { GreetingCode = 421 }).ConfigureAwait(false);
            await using (transientServer.ConfigureAwait(false))
            {
                var transientResult = await transport.SendAsync(new SmtpDeliveryRequest(
                    "127.0.0.1", transientServer.Port, SmtpSecurityMode.None,
                    "camera@example.test", "Camera", ["ops@example.test"],
                    null, null, "Subj", "Body", "test", TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(transientResult.Succeeded);
                Assert.IsTrue(transientResult.Transient);
                Assert.AreEqual(NotificationReasonCodes.ServerError, transientResult.ReasonCode);
            }
        }
    }

    [TestMethod]
    public async Task SendAsync_SecurityModeNoneWithCredentials_FailsClosedWithTlsRequiredAndNeverConnectsAsync()
    {
        var server = await InProcessSmtpCaptureServer.StartAsync(new SmtpServerBehavior
        {
            AuthMechanisms = "PLAIN LOGIN",
            ExpectedUsername = "user",
            ExpectedPassword = "synthetic-password"
        }).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var dnsCalled = false;
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
            {
                DnsResolver = (_, _) =>
                {
                    dnsCalled = true;
                    return Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]);
                }
            };

            var publicResult = await transport.SendAsync(new SmtpDeliveryRequest(
                "smtp.office365.com", 25, SmtpSecurityMode.None,
                "camera@example.test", "Camera", ["ops@example.test"],
                "user", "synthetic-password", "Subj", "Body", "test", TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(publicResult.Succeeded);
            Assert.IsFalse(publicResult.Transient);
            Assert.AreEqual(NotificationReasonCodes.TlsRequired, publicResult.ReasonCode);
            Assert.IsFalse(dnsCalled);

            var loopbackResult = await transport.SendAsync(new SmtpDeliveryRequest(
                "127.0.0.1", server.Port, SmtpSecurityMode.None,
                "camera@example.test", "Camera", ["ops@example.test"],
                "user", "synthetic-password", "Subj", "Body", "test", TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(loopbackResult.Succeeded);
            Assert.IsFalse(loopbackResult.Transient);
            Assert.AreEqual(NotificationReasonCodes.TlsRequired, loopbackResult.ReasonCode);
            Assert.IsFalse(server.ReceivedAnyAuthCommand);
            Assert.IsEmpty(server.CapturedMessages);
        }
    }

    [TestMethod]
    public async Task SendAsync_UnauthenticatedNoneToPublicIpOrPublicDns_FailsClosedWithTlsRequiredAsync()
    {
        var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
        {
            DnsResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")])
        };

        var literalResult = await transport.SendAsync(new SmtpDeliveryRequest(
            "93.184.216.34", 25, SmtpSecurityMode.None,
            "camera@example.test", "Camera", ["ops@example.test"],
            null, null, "Subj", "Body", "test", TimeSpan.FromSeconds(2)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(literalResult.Succeeded);
        Assert.IsFalse(literalResult.Transient);
        Assert.AreEqual(NotificationReasonCodes.TlsRequired, literalResult.ReasonCode);

        var dnsResult = await transport.SendAsync(new SmtpDeliveryRequest(
            "public-relay.example.test", 25, SmtpSecurityMode.None,
            "camera@example.test", "Camera", ["ops@example.test"],
            null, null, "Subj", "Body", "test", TimeSpan.FromSeconds(2)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(dnsResult.Succeeded);
        Assert.IsFalse(dnsResult.Transient);
        Assert.AreEqual(NotificationReasonCodes.TlsRequired, dnsResult.ReasonCode);
    }

    [TestMethod]
    public async Task SendAsync_UnknownSecurityMode_FailsClosedWithInvalidConfigurationAsync()
    {
        var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System);
        var result = await transport.SendAsync(new SmtpDeliveryRequest(
            "127.0.0.1", 25, (SmtpSecurityMode)99,
            "camera@example.test", "Camera", ["ops@example.test"],
            null, null, "Subj", "Body", "test", TimeSpan.FromSeconds(2)), CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Transient);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, result.ReasonCode);
    }

    [TestMethod]
    public async Task SendAsync_ServerWithoutAuthCapability_NeverReceivesAuthCommandAsync()
    {
        using var cert = CreateEphemeralLoopbackCertificate();
        var expectedThumbprint = cert.Thumbprint;
        var behavior = new SmtpServerBehavior
        {
            SupportsStartTls = true,
            ServerCertificate = cert,
            AuthMechanisms = null
        };
        var server = await InProcessSmtpCaptureServer.StartAsync(behavior).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System)
            {
                CertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, expectedThumbprint, StringComparison.Ordinal)
            };

            var result = await transport.SendAsync(new SmtpDeliveryRequest(
                "127.0.0.1", server.Port, SmtpSecurityMode.StartTls,
                "camera@example.test", "Camera", ["ops@example.test"],
                "must-not-send-user", "must-not-send-pass", "Subj", "Body", "test", TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.Transient);
            Assert.AreEqual(NotificationReasonCodes.AuthFailed, result.ReasonCode);
            Assert.IsFalse(server.ReceivedAnyAuthCommand, "Client must never emit AUTH when server did not advertise AUTH PLAIN/LOGIN.");
            Assert.IsEmpty(server.CapturedMessages);
        }
    }

    [TestMethod]
    public void BuildRfc5322Message_EncodesNonAsciiFromWithoutQuotesFoldsToAndRespects8BitMime()
    {
        var recipients = Enumerable.Range(1, 6).Select(i => $"operator-recipient-{i:D2}@observatory.example.test").ToArray();
        var request = new SmtpDeliveryRequest(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls,
            "camera@example.test",
            "Observatory — Cañón",
            recipients,
            null, null,
            "Capture stalled — dome café",
            "Alert with non-ASCII temperature: 12°C — résumé",
            "capture_stalled",
            TimeSpan.FromSeconds(5));

        var rawNo8Bit = SmtpNotificationTransport.BuildRfc5322Message(
            request, new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero), serverSupports8BitMime: false, out var uses8BitNo);
        Assert.IsFalse(uses8BitNo);
        Assert.IsTrue(rawNo8Bit.All(static b => b <= 127), "Message must stay pure 7-bit ASCII when server lacks 8BITMIME.");
        var textNo8Bit = Encoding.ASCII.GetString(rawNo8Bit);
        Assert.DoesNotContain("From: \"=?utf-8?", textNo8Bit, StringComparison.Ordinal);
        StringAssert.Contains(textNo8Bit, "From: =?utf-8?B?", StringComparison.Ordinal);
        StringAssert.Contains(textNo8Bit, "Content-Transfer-Encoding: quoted-printable\r\n", StringComparison.Ordinal);
        StringAssert.Contains(textNo8Bit, ",\r\n <operator-recipient-", StringComparison.Ordinal);

        var rawWith8Bit = SmtpNotificationTransport.BuildRfc5322Message(
            request, new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero), serverSupports8BitMime: true, out var uses8BitYes);
        Assert.IsTrue(uses8BitYes);
        var textWith8Bit = Encoding.UTF8.GetString(rawWith8Bit);
        StringAssert.Contains(textWith8Bit, "Content-Transfer-Encoding: 8bit\r\n", StringComparison.Ordinal);

        var asciiRequest = request with { FromDisplayName = "North Camera", Subject = "Disk pressure", Body = "Pure ASCII body" };
        var rawAscii = SmtpNotificationTransport.BuildRfc5322Message(
            asciiRequest, new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero), serverSupports8BitMime: false, out var uses8BitAscii);
        Assert.IsFalse(uses8BitAscii);
        StringAssert.Contains(Encoding.ASCII.GetString(rawAscii), "Content-Transfer-Encoding: 7bit\r\n", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SendAsync_StalledServer_HonorsExplicitTimeoutAsync()
    {
        var stalledServer = await InProcessSmtpCaptureServer.StartAsync(new SmtpServerBehavior { HangOnConnect = true }).ConfigureAwait(false);
        await using (stalledServer.ConfigureAwait(false))
        {
            var transport = new SmtpNotificationTransport(LoopbackTestPolicy, TimeProvider.System);

            var result = await transport.SendAsync(new SmtpDeliveryRequest(
                "127.0.0.1", stalledServer.Port, SmtpSecurityMode.None,
                "camera@example.test", "Camera", ["ops@example.test"],
                null, null, "Subj", "Body", "test", TimeSpan.FromMilliseconds(250)), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Transient);
            Assert.AreEqual(NotificationReasonCodes.Timeout, result.ReasonCode);
        }
    }

    [TestMethod]
    public async Task SendAsync_ProductionPolicy_RejectsMailpitHostBeforeConnectingAsync()
    {
        var transport = new SmtpNotificationTransport(new OperatorNotificationNetworkPolicy(isDevelopment: false), TimeProvider.System);
        var result = await transport.SendAsync(new SmtpDeliveryRequest(
            "mailpit", 1025, SmtpSecurityMode.None,
            "camera@example.test", "Camera", ["ops@example.test"],
            null, null, "Subj", "Body", "test", TimeSpan.FromSeconds(2)), CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Transient);
        Assert.AreEqual(NotificationReasonCodes.MailpitProhibited, result.ReasonCode);
    }

    private static X509Certificate2 CreateEphemeralLoopbackCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    internal sealed class SmtpServerBehavior
    {
        public int GreetingCode { get; init; } = 220;
        public bool HangOnConnect { get; init; }
        public bool SupportsStartTls { get; init; }
        public bool ImplicitTls { get; init; }
        public X509Certificate2? ServerCertificate { get; init; }
        public string? AuthMechanisms { get; init; }
        public string? ExpectedUsername { get; init; }
        public string? ExpectedPassword { get; init; }
    }

    internal sealed record CapturedSmtpMessage(
        string MailFrom,
        IReadOnlyList<string> Recipients,
        string Data,
        bool UsedTls,
        string? AuthMechanismUsed,
        int EhloCount);

    internal sealed class InProcessSmtpCaptureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly SmtpServerBehavior _behavior;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly List<CapturedSmtpMessage> _messages = [];
        private readonly object _gate = new();

        public int Port { get; }
        public bool ReceivedAnyAuthCommand { get; private set; }

        public IReadOnlyList<CapturedSmtpMessage> CapturedMessages
        {
            get { lock (_gate) return [.. _messages]; }
        }

        private InProcessSmtpCaptureServer(TcpListener listener, SmtpServerBehavior behavior)
        {
            _listener = listener;
            _behavior = behavior;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static Task<InProcessSmtpCaptureServer> StartAsync(SmtpServerBehavior behavior)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new InProcessSmtpCaptureServer(listener, behavior));
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
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
                    if (_behavior.HangOnConnect)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    Stream stream = client.GetStream();
                    SslStream? ssl = null;
                    var usedTls = false;
                    if (_behavior.ImplicitTls && _behavior.ServerCertificate is not null)
                    {
                        ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                        await ssl.AuthenticateAsServerAsync(_behavior.ServerCertificate).ConfigureAwait(false);
                        stream = ssl;
                        usedTls = true;
                    }

                    try
                    {
                        var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

                        await writer.WriteLineAsync($"{_behavior.GreetingCode} test-smtp.local ESMTP").ConfigureAwait(false);
                        if (_behavior.GreetingCode != 220)
                        {
                            return;
                        }

                        var mailFrom = string.Empty;
                        var recipients = new List<string>();
                        string? authUsed = null;
                        var ehloCount = 0;

                        while (true)
                        {
                            var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                            if (line is null) break;

                            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                            {
                                ehloCount++;
                                var caps = new List<string> { "test-smtp.local" };
                                if (_behavior.SupportsStartTls && !usedTls) caps.Add("STARTTLS");
                                if (!string.IsNullOrEmpty(_behavior.AuthMechanisms)) caps.Add($"AUTH {_behavior.AuthMechanisms}");
                                for (var i = 0; i < caps.Count; i++)
                                {
                                    var sep = i == caps.Count - 1 ? ' ' : '-';
                                    await writer.WriteLineAsync($"250{sep}{caps[i]}").ConfigureAwait(false);
                                }
                            }
                            else if (line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase) && _behavior.SupportsStartTls && _behavior.ServerCertificate is not null)
                            {
                                await writer.WriteLineAsync("220 Ready to start TLS").ConfigureAwait(false);
                                ssl = new SslStream(stream, leaveInnerStreamOpen: true);
                                await ssl.AuthenticateAsServerAsync(_behavior.ServerCertificate).ConfigureAwait(false);
                                stream = ssl;
                                usedTls = true;
                                reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                                writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                            }
                            else if (line.StartsWith("AUTH PLAIN ", StringComparison.OrdinalIgnoreCase))
                            {
                                ReceivedAnyAuthCommand = true;
                                var payload = Encoding.UTF8.GetString(Convert.FromBase64String(line[11..].Trim()));
                                var parts = payload.Split('\0');
                                var ok = parts.Length >= 3 &&
                                         parts[1] == _behavior.ExpectedUsername &&
                                         parts[2] == _behavior.ExpectedPassword;
                                if (ok)
                                {
                                    authUsed = "PLAIN";
                                    await writer.WriteLineAsync("235 Authentication succeeded").ConfigureAwait(false);
                                }
                                else
                                {
                                    await writer.WriteLineAsync("535 Authentication credentials invalid").ConfigureAwait(false);
                                }
                            }
                            else if (line.Equals("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
                            {
                                ReceivedAnyAuthCommand = true;
                                await writer.WriteLineAsync("334 VXNlcm5hbWU6").ConfigureAwait(false);
                                var uLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                                await writer.WriteLineAsync("334 UGFzc3dvcmQ6").ConfigureAwait(false);
                                var pLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                                var user = Encoding.UTF8.GetString(Convert.FromBase64String(uLine ?? string.Empty));
                                var pass = Encoding.UTF8.GetString(Convert.FromBase64String(pLine ?? string.Empty));
                                if (user == _behavior.ExpectedUsername && pass == _behavior.ExpectedPassword)
                                {
                                    authUsed = "LOGIN";
                                    await writer.WriteLineAsync("235 Authentication succeeded").ConfigureAwait(false);
                                }
                                else
                                {
                                    await writer.WriteLineAsync("535 Authentication credentials invalid").ConfigureAwait(false);
                                }
                            }
                            else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                            {
                                mailFrom = line[10..].Trim();
                                await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                            }
                            else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                            {
                                recipients.Add(line[8..].Trim());
                                await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                            }
                            else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                                var sb = new StringBuilder();
                                while (true)
                                {
                                    var dataLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                                    if (dataLine is null || dataLine == ".") break;
                                    sb.Append(dataLine).Append("\r\n");
                                }
                                lock (_gate)
                                {
                                    _messages.Add(new CapturedSmtpMessage(mailFrom, recipients.ToArray(), sb.ToString(), usedTls, authUsed, ehloCount));
                                }
                                await writer.WriteLineAsync("250 Queued").ConfigureAwait(false);
                            }
                            else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                                break;
                            }
                            else
                            {
                                await writer.WriteLineAsync("500 Unrecognized").ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        if (ssl is not null) await ssl.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException or FormatException)
                {
                    // Test server disconnects cleanly on client abort/timeout.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            _cts.Dispose();
        }
    }
}
