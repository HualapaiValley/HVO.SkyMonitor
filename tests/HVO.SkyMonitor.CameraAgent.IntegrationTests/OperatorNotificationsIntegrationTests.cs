using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OperatorNotificationsIntegrationTests
{
    private const string NotificationsPath = "/api/v1/operations/notifications";
    private const string NotificationsTestPath = "/api/v1/operations/notifications/test";
    private static readonly string[] IntegrationRecipients = ["ops@example.test"];

    [TestMethod]
    public async Task DefaultHostNotificationsAreOffHealthIsDisabledAndNoSecretFilesAreCreatedAsync()
    {
        using var anonymous = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var healthResponse = await anonymous.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        healthResponse.EnsureSuccessStatusCode();
        var healthJson = await healthResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(healthJson))
        {
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
            Assert.AreEqual("Healthy", check.GetProperty("status").GetString());
            Assert.AreEqual("Disabled", check.GetProperty("data").GetProperty("Availability").GetString());
        }

        // Anonymous request to operations notifications endpoint is rejected.
        using var anonApi = await anonymous.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonApi.StatusCode);

        // Authenticated owner sees disabled defaults.
        using var owner = CreateOwnerSessionClient(AssemblyHooks.Fixture);
        using var getResponse = await owner.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false);
        var body = await getResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode, body);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.IsFalse(doc.RootElement.GetProperty("configuration").GetProperty("options").GetProperty("enabled").GetBoolean());
            Assert.IsFalse(doc.RootElement.GetProperty("status").GetProperty("enabled").GetBoolean());
        }

        // Zero notification files created on disk while disabled.
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var provisioningDir = scope.ServiceProvider.GetRequiredService<IOptions<DeviceProvisioningOptions>>().Value.StateDirectory;
        Assert.IsFalse(File.Exists(Path.Combine(provisioningDir, OperatorNotificationStore.ConfigFileName)));
        Assert.IsFalse(File.Exists(Path.Combine(provisioningDir, OperatorNotificationStore.SecretsFileName)));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task AuthAndAntiforgeryEnforceOwnerReadMutateAndApiKeyRulesAsync()
    {
        string readKey;
        string readWriteKey;
        string ownerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!.Id;
            var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
            readKey = (await keys.CreateAsync(ownerId, "notif-read", ApiKeyAccessLevel.Read, CancellationToken.None).ConfigureAwait(false)).Token!;
            readWriteKey = (await keys.CreateAsync(ownerId, "notif-rw", ApiKeyAccessLevel.ReadWrite, CancellationToken.None).ConfigureAwait(false)).Token!;
        }

        try
        {
            // Cookie-like session without antiforgery token is blocked on PUT and POST /test.
            using var sessionClient = CreateOwnerSessionClient(AssemblyHooks.Fixture);
            using (var putNoAnti = await sessionClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = "any",
                options = new { enabled = false }
            }).ConfigureAwait(false))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, putNoAnti.StatusCode);
            }
            using (var testNoAnti = await sessionClient.PostAsJsonAsync(new Uri(NotificationsTestPath, UriKind.Relative), new { target = "AllEnabled" }).ConfigureAwait(false))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, testNoAnti.StatusCode);
            }

            // Read-only API key can GET, cannot PUT or POST /test.
            using var readClient = CreateApiKeyClient(AssemblyHooks.Fixture, readKey);
            using (var getOk = await readClient.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false))
            {
                Assert.AreEqual(HttpStatusCode.OK, getOk.StatusCode);
            }
            using (var putForbidden = await readClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = "any",
                options = new { enabled = false }
            }).ConfigureAwait(false))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, putForbidden.StatusCode);
            }
            using (var testForbidden = await readClient.PostAsJsonAsync(new Uri(NotificationsTestPath, UriKind.Relative), new { target = "AllEnabled" }).ConfigureAwait(false))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, testForbidden.StatusCode);
            }

            // Read-write API key passes antiforgery without a token (returns 409 Conflict for stale version, not 400 antiforgery).
            using var rwClient = CreateApiKeyClient(AssemblyHooks.Fixture, readWriteKey);
            using var stalePut = await rwClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = "stale-version-check",
                options = new { enabled = false }
            }).ConfigureAwait(false);
            var staleJson = await stalePut.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Conflict, stalePut.StatusCode, staleJson);
            StringAssert.Contains(staleJson, "notifications.version-conflict", StringComparison.Ordinal);
        }
        finally
        {
            using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _ = await CameraAgentApiKeyStore.RemoveAllAsync(db, ownerId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task EndToEndEncryptedSaveLoopbackSmtpAndWebhookDeliveryOutageNonInterferenceAndZeroSecretLeaksAsync()
    {
        var runId = Guid.NewGuid().ToString("N");
        var smtpUser = $"integ-user-{runId[..8]}";
        var smtpPass = $"integ-pass-{runId[8..16]}";
        var webhookPathSegment = $"integ-path-{runId[16..24]}";
        var webhookQueryValue = $"integ-query-{runId[24..]}";
        var webhookHeaderValue = $"integ-header-{runId[..12]}";

        using var logCollector = new InMemoryLogCollector();
        var smtpCapture = LoopbackSmtpSink.Start();
        var webhookCapture = LoopbackHttpSink.Start();
        await using (smtpCapture.ConfigureAwait(false))
        await using (webhookCapture.ConfigureAwait(false))
        {
            using var fixture = new CameraAgentIntegrationFixture();
            await fixture.InitializeAsync(configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton(new OperatorNotificationNetworkPolicy(isDevelopment: false)
                {
                    AllowLoopbackForTesting = true,
                    AllowInsecureHttpWebhookForTesting = true
                }));
                services.AddLogging(logging => logging.AddProvider(logCollector));
            }).ConfigureAwait(false);

            string readWriteKey;
            string provisioningDir;
            using (var scope = fixture.CreateCameraAgentScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var owner = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!;
                var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
                readWriteKey = (await keys.CreateAsync(owner.Id, "e2e-notif", ApiKeyAccessLevel.ReadWrite, CancellationToken.None).ConfigureAwait(false)).Token!;
                provisioningDir = scope.ServiceProvider.GetRequiredService<IOptions<DeviceProvisioningOptions>>().Value.StateDirectory;
            }

            using var client = CreateApiKeyClient(fixture, readWriteKey);

            // 1. Read initial version.
            var initialJson = await client.GetStringAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false);
            string version;
            using (var doc = JsonDocument.Parse(initialJson))
            {
                version = doc.RootElement.GetProperty("configuration").GetProperty("version").GetString()!;
            }

            // 2. Production policy rejects Mailpit even on this host (IsDevelopment == false in policy).
            using (var mailpitReject = await client.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = version,
                options = new
                {
                    enabled = true,
                    smtp = new
                    {
                        enabled = true,
                        host = "mailpit",
                        port = 1025,
                        securityMode = 2,
                        fromAddress = "camera@example.test",
                        recipients = IntegrationRecipients
                    }
                }
            }).ConfigureAwait(false))
            {
                var mailpitBody = await mailpitReject.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.BadRequest, mailpitReject.StatusCode, mailpitBody);
                StringAssert.Contains(mailpitBody, "Mailpit", StringComparison.OrdinalIgnoreCase);
            }

            // 3. Save valid opt-in SMTP + Generic Webhook configuration with secrets.
            var webhookUrl = $"http://127.0.0.1:{webhookCapture.Port}/{webhookPathSegment}?k={webhookQueryValue}";
            using (var saveResp = await client.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = version,
                options = new
                {
                    enabled = true,
                    requestTimeoutSeconds = 5,
                    maxRetryAttempts = 0,
                    consecutiveOutageThreshold = 1,
                    outageCooldownSeconds = 60,
                    testSendMinimumIntervalSeconds = 0,
                    smtp = new
                    {
                        enabled = true,
                        host = "127.0.0.1",
                        port = smtpCapture.Port,
                        securityMode = 2, // None (local loopback capture)
                        fromAddress = "camera@example.test",
                        fromDisplayName = "Integration Camera",
                        recipients = IntegrationRecipients
                    },
                    webhook = new
                    {
                        enabled = true,
                        format = 0, // Generic
                        authHeaderName = "Authorization"
                    }
                },
                smtpUsername = smtpUser,
                smtpPassword = smtpPass,
                webhookUrl,
                webhookAuthToken = webhookHeaderValue
            }).ConfigureAwait(false))
            {
                var saveBody = await saveResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, saveResp.StatusCode, saveBody);
                using var doc = JsonDocument.Parse(saveBody);
                version = doc.RootElement.GetProperty("configuration").GetProperty("version").GetString()!;
                Assert.AreEqual($"http://127.0.0.1:{webhookCapture.Port}",
                    doc.RootElement.GetProperty("configuration").GetProperty("secrets").GetProperty("webhookEndpointHost").GetString());
            }

            // 4. Verify encrypted secret file on disk contains zero plaintext secrets.
            var secretDisk = await File.ReadAllTextAsync(Path.Combine(provisioningDir, OperatorNotificationStore.SecretsFileName)).ConfigureAwait(false);
            foreach (var canary in new[] { smtpUser, smtpPass, webhookPathSegment, webhookQueryValue, webhookHeaderValue })
            {
                Assert.DoesNotContain(canary, secretDisk, StringComparison.Ordinal);
            }

            // 5. Trigger safe test-send and verify BOTH loopback SMTP and loopback Webhook received payloads.
            using (var testResp = await client.PostAsJsonAsync(new Uri(NotificationsTestPath, UriKind.Relative), new { target = "AllEnabled" }).ConfigureAwait(false))
            {
                var testBody = await testResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, testResp.StatusCode, testBody);
                using var doc = JsonDocument.Parse(testBody);
                var channels = doc.RootElement.GetProperty("channels").EnumerateArray().ToArray();
                Assert.HasCount(2, channels);
                Assert.IsTrue(channels.All(static c => c.GetProperty("succeeded").GetBoolean()), testBody);
            }

            Assert.HasCount(1, smtpCapture.Bodies);
            StringAssert.Contains(smtpCapture.Bodies[0], "Test notification", StringComparison.Ordinal);
            Assert.HasCount(1, webhookCapture.Requests);
            StringAssert.Contains(webhookCapture.Requests[0], $"Bearer {webhookHeaderValue}", StringComparison.Ordinal);
            StringAssert.Contains(webhookCapture.Requests[0], "Test notification", StringComparison.Ordinal);

            // 6. Simulate SMTP provider outage (stop SMTP listener), publish an event, verify outage backoff does NOT
            //    make CameraAgent unhealthy or break /api/v1/operations/summary, while Webhook still delivers!
            await smtpCapture.DisposeAsync().ConfigureAwait(false);
            using (var scope = fixture.CreateCameraAgentScope())
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<OperatorNotificationDispatcher>();
                Assert.IsTrue(dispatcher.TryPublish(new OperatorNotificationEvent(
                    OperatorNotificationEventKind.DiskPressure,
                    OperatorNotificationSeverity.Warning,
                    "disk_pressure:raw-ingress",
                    "Disk pressure (raw-ingress)",
                    "Storage volume raw-ingress is under disk pressure.",
                    DateTimeOffset.UtcNow,
                    "Integration Camera")));

                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (DateTimeOffset.UtcNow < deadline &&
                       (webhookCapture.Requests.Count < 2 || dispatcher.GetStatus().Smtp.State != NotificationChannelHealthState.OutageBackoff))
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            Assert.HasCount(2, webhookCapture.Requests, "Webhook must still deliver during SMTP outage.");

            // Health endpoint reports Degraded (never Unhealthy) for operator-notifications; operations summary stays 200 OK.
            var healthOutage = await client.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(healthOutage))
            {
                var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                    .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
                Assert.AreEqual("Degraded", check.GetProperty("status").GetString());
                Assert.AreEqual("OutageBackoff", check.GetProperty("data").GetProperty("SmtpState").GetString());
            }

            using var summaryResp = await client.GetAsync(new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
            var summaryBody = await summaryResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, summaryResp.StatusCode, summaryBody);

            // 7. Negative secret-leak sweep across ALL host surfaces: notifications API, operations summary, /health, and host logs!
            var notifApiBody = await client.GetStringAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false);
            var allSurfaces = string.Join("\n", notifApiBody, summaryBody, healthOutage, logCollector.Dump());
            foreach (var canary in new[] { smtpUser, smtpPass, webhookPathSegment, webhookQueryValue, webhookHeaderValue })
            {
                Assert.DoesNotContain(canary, allSurfaces, StringComparison.Ordinal);
            }
        }
    }

    private static HttpClient CreateOwnerSessionClient(CameraAgentIntegrationFixture fixture)
    {
        var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, fixture.OwnerId);
        return client;
    }

    private static HttpClient CreateApiKeyClient(CameraAgentIntegrationFixture fixture, string key)
    {
        var client = fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private sealed class InMemoryLogCollector : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly List<string> _entries = [];

        public ILogger CreateLogger(string categoryName) => new CollectorLogger(this);

        public string Dump()
        {
            lock (_gate)
            {
                return string.Join("\n", _entries);
            }
        }

        public void Dispose() { }

        private void Add(string line)
        {
            lock (_gate)
            {
                _entries.Add(line);
            }
        }

        private sealed class CollectorLogger(InMemoryLogCollector parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => parent.Add(formatter(state, exception));
        }
    }

    private sealed class LoopbackSmtpSink : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly object _gate = new();
        private readonly List<string> _bodies = [];
        private bool _disposed;

        public int Port { get; }
        public IReadOnlyList<string> Bodies { get { lock (_gate) return [.. _bodies]; } }

        private LoopbackSmtpSink(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public static LoopbackSmtpSink Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackSmtpSink(listener);
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                        await using (writer.ConfigureAwait(false))
                        {
                            await writer.WriteLineAsync("220 loopback-smtp.local ESMTP").ConfigureAwait(false);
                            while (true)
                            {
                                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                                if (line is null) break;
                                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                                {
                                    await writer.WriteLineAsync("250-loopback-smtp.local").ConfigureAwait(false);
                                    await writer.WriteLineAsync("250 AUTH PLAIN LOGIN").ConfigureAwait(false);
                                }
                                else if (line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase))
                                {
                                    await writer.WriteLineAsync("235 OK").ConfigureAwait(false);
                                }
                                else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) ||
                                         line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                                {
                                    await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                                }
                                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                                {
                                    await writer.WriteLineAsync("354 Go ahead").ConfigureAwait(false);
                                    var sb = new StringBuilder();
                                    while (true)
                                    {
                                        var dl = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                                        if (dl is null || dl == ".") break;
                                        sb.AppendLine(dl);
                                    }
                                    lock (_gate) _bodies.Add(sb.ToString());
                                    await writer.WriteLineAsync("250 Queued").ConfigureAwait(false);
                                }
                                else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                                {
                                    await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                                    break;
                                }
                                else
                                {
                                    await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Test server disconnects cleanly.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private sealed class LoopbackHttpSink : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly object _gate = new();
        private readonly List<string> _requests = [];
        private bool _disposed;

        public int Port { get; }
        public IReadOnlyList<string> Requests { get { lock (_gate) return [.. _requests]; } }

        private LoopbackHttpSink(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public static LoopbackHttpSink Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackHttpSink(listener);
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        var buffer = new byte[16384];
                        var total = 0;
                        var contentLength = 0;
                        var headerEnd = -1;
                        while (total < buffer.Length)
                        {
                            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), _cts.Token).ConfigureAwait(false);
                            if (read == 0) break;
                            total += read;
                            if (headerEnd < 0)
                            {
                                var text = Encoding.UTF8.GetString(buffer, 0, total);
                                var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                if (idx >= 0)
                                {
                                    headerEnd = idx + 4;
                                    foreach (var line in text[..idx].Split("\r\n"))
                                    {
                                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                            int.TryParse(line[15..].Trim(), out var parsed))
                                        {
                                            contentLength = parsed;
                                        }
                                    }
                                }
                            }
                            if (headerEnd >= 0 && total >= headerEnd + contentLength)
                            {
                                break;
                            }
                        }

                        lock (_gate) _requests.Add(Encoding.UTF8.GetString(buffer, 0, total));
                        var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
                        await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Test server disconnects cleanly.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}
