using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
using Microsoft.Data.Sqlite;
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
        Assert.IsFalse(File.Exists(Path.Combine(provisioningDir, FileOperatorNotificationHeartbeatStore.HeartbeatFileName)));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task AuthAntiforgeryAndUndefinedTestTargetEnforceOwnerRulesWithoutConsumingRateLimitAsync()
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
            using (var stalePut = await rwClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = "stale-version-check",
                options = new { enabled = false }
            }).ConfigureAwait(false))
            {
                var staleJson = await stalePut.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Conflict, stalePut.StatusCode, staleJson);
                StringAssert.Contains(staleJson, "notifications.version-conflict", StringComparison.Ordinal);
            }

            // Undefined numeric test-send target returns 400 Bad Request BEFORE consuming the test-send rate-limit slot.
            using (var badTarget = await rwClient.PostAsJsonAsync(new Uri(NotificationsTestPath, UriKind.Relative), new { target = "99" }).ConfigureAwait(false))
            {
                var badBody = await badTarget.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.BadRequest, badTarget.StatusCode, badBody);
                StringAssert.Contains(badBody, "notifications.invalid-target", StringComparison.Ordinal);
            }
            using (var validAfterBadTarget = await rwClient.PostAsJsonAsync(new Uri(NotificationsTestPath, UriKind.Relative), new { target = "AllEnabled" }).ConfigureAwait(false))
            {
                var okBody = await validAfterBadTarget.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, validAfterBadTarget.StatusCode, okBody);
            }
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
    public async Task InvalidNotificationConfigurationNeverFailsHostStartupAndIsolatesFailureToNotificationHealthAsync()
    {
        // Case 1: Disabled notifications with out-of-range timeout -> CameraAgent starts cleanly and notifications stay Disabled.
        using (var disabledFixture = new CameraAgentIntegrationFixture())
        {
            await disabledFixture.InitializeAsync(settings: new Dictionary<string, string?>
            {
                ["CameraAgent:Notifications:Enabled"] = "false",
                ["CameraAgent:Notifications:RequestTimeoutSeconds"] = "999"
            }).ConfigureAwait(false);

            using var anon = disabledFixture.CreateCameraAgentClient();
            var healthJson = await anon.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(healthJson);
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
            Assert.AreEqual("Healthy", check.GetProperty("status").GetString());
            Assert.AreEqual("Disabled", check.GetProperty("data").GetProperty("Availability").GetString());

            using var owner = CreateOwnerSessionClient(disabledFixture);
            using var summaryResp = await owner.GetAsync(new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, summaryResp.StatusCode);
        }

        // Case 2: Enabled notifications with invalid timeout -> CameraAgent STILL boots cleanly, capture/summary work,
        //         notification health is Degraded (not Unhealthy), and outbound send fails closed with invalid-configuration.
        using (var enabledInvalidFixture = new CameraAgentIntegrationFixture())
        {
            await enabledInvalidFixture.InitializeAsync(settings: new Dictionary<string, string?>
            {
                ["CameraAgent:Notifications:Enabled"] = "true",
                ["CameraAgent:Notifications:RequestTimeoutSeconds"] = "999",
                ["CameraAgent:Notifications:Smtp:Enabled"] = "true"
            }).ConfigureAwait(false);

            using var anon = enabledInvalidFixture.CreateCameraAgentClient();
            var healthJson = await anon.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(healthJson);
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
            Assert.AreEqual("Degraded", check.GetProperty("status").GetString());
            Assert.AreEqual("invalid-configuration", check.GetProperty("data").GetProperty("ReasonCode").GetString());

            using var owner = CreateOwnerSessionClient(enabledInvalidFixture);
            using var summaryResp = await owner.GetAsync(new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, summaryResp.StatusCode);

            using var scope = enabledInvalidFixture.CreateCameraAgentScope();
            var svc = scope.ServiceProvider.GetRequiredService<IOperatorNotificationService>();
            var testRes = await svc.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, testRes.Channels);
            Assert.IsFalse(testRes.Channels[0].Succeeded);
            Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, testRes.Channels[0].ReasonCode);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("CameraAgent:Notifications:Smtp:SecurityMode", "SSL")]
    [DataRow("CameraAgent:Notifications:RequestTimeoutSeconds", "ten")]
    [DataRow("CameraAgent:Notifications:Smtp:Port", "submission")]
    [DataRow("CameraAgent:Notifications:Webhook:Format", "Teams")]
    public async Task UnparseableNotificationConfigWithEnabledFalseStartsCameraAgentCleanlyAndAllowsDiagnosisAndOverrideAsync(
        string key,
        string malformedValue)
    {
        using var fixture = new CameraAgentIntegrationFixture();
        await fixture.InitializeAsync(settings: new Dictionary<string, string?>
        {
            ["CameraAgent:Notifications:Enabled"] = "false",
            [key] = malformedValue
        }).ConfigureAwait(false);

        // /health MUST return HTTP 200 with operator-notifications Degraded (invalid-configuration, NOT store-corrupt).
        using var anon = fixture.CreateCameraAgentClient();
        using var healthResp = await anon.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        var healthJson = await healthResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, healthResp.StatusCode, healthJson);
        using (var doc = JsonDocument.Parse(healthJson))
        {
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
            Assert.AreEqual("Degraded", check.GetProperty("status").GetString());
            Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, check.GetProperty("data").GetProperty("ReasonCode").GetString());
        }

        // Operations summary and notifications GET endpoints stay 200 OK (NOT 503!) so operators can diagnose and override.
        string readWriteKey;
        using (var scope = fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!;
            var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
            readWriteKey = (await keys.CreateAsync(owner.Id, "malformed-cfg-override", ApiKeyAccessLevel.ReadWrite, CancellationToken.None).ConfigureAwait(false)).Token!;
        }

        using var rwClient = CreateApiKeyClient(fixture, readWriteKey);
        using (var summaryResp = await rwClient.GetAsync(new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.OK, summaryResp.StatusCode);
        }

        string version;
        using (var getResp = await rwClient.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false))
        {
            var getBody = await getResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode, getBody);
            using var doc = JsonDocument.Parse(getBody);
            var cfg = doc.RootElement.GetProperty("configuration");
            Assert.IsFalse(cfg.GetProperty("storeCorrupt").GetBoolean());
            Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, cfg.GetProperty("storeStatusReasonCode").GetString());
            Assert.IsTrue(cfg.GetProperty("canPersist").GetBoolean());
            version = cfg.GetProperty("version").GetString()!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(version));
        }

        // Explicit operator override clears invalid-configuration and restores Healthy status.
        using (var putResp = await rwClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
        {
            expectedVersion = version,
            options = new { enabled = false }
        }).ConfigureAwait(false))
        {
            var putBody = await putResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, putResp.StatusCode, putBody);
        }

        using var healthAfterResp = await anon.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        var healthAfterJson = await healthAfterResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, healthAfterResp.StatusCode, healthAfterJson);
        using (var doc = JsonDocument.Parse(healthAfterJson))
        {
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
            Assert.AreEqual("Healthy", check.GetProperty("status").GetString());
            Assert.AreEqual("Disabled", check.GetProperty("data").GetProperty("Availability").GetString());
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DamagedOrUnreadableStorePathSuspendsWithoutHostFallbackReportsDegradedAndSupportsResetRecoveryAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new CameraAgentIntegrationFixture();
        await fixture.InitializeAsync(settings: new Dictionary<string, string?>
        {
            ["CameraAgent:Notifications:Enabled"] = "true",
            ["CameraAgent:Notifications:Smtp:Enabled"] = "true",
            ["CameraAgent:Notifications:Smtp:Host"] = "fallback-relay.example.test",
            ["CameraAgent:Notifications:Smtp:FromAddress"] = "fallback@example.test",
            ["CameraAgent:Notifications:Smtp:Recipients:0"] = "ops@example.test",
            [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-fallback-user",
            [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-fallback-pass"
        }).ConfigureAwait(false);

        string readWriteKey;
        string provisioningDir;
        using (var scope = fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!;
            var keys = scope.ServiceProvider.GetRequiredService<CameraAgentApiKeyStore>();
            readWriteKey = (await keys.CreateAsync(owner.Id, "store-recovery", ApiKeyAccessLevel.ReadWrite, CancellationToken.None).ConfigureAwait(false)).Token!;
            provisioningDir = scope.ServiceProvider.GetRequiredService<IOptions<DeviceProvisioningOptions>>().Value.StateDirectory;
        }

        var storePath = Path.Combine(provisioningDir, OperatorNotificationStore.ConfigFileName);
        using var rwClient = CreateApiKeyClient(fixture, readWriteKey);
        using var anon = fixture.CreateCameraAgentClient();

        // Part A: Directory at envelope path must NEVER reactivate host credentials and must report Degraded / store-unreadable.
        Directory.CreateDirectory(storePath);
        try
        {
            var dirHealthJson = await anon.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(dirHealthJson))
            {
                var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                    .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
                Assert.AreEqual("Degraded", check.GetProperty("status").GetString());
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, check.GetProperty("data").GetProperty("ReasonCode").GetString());
            }

            using (var dirGetResp = await rwClient.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false))
            {
                var dirGetBody = await dirGetResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, dirGetResp.StatusCode, dirGetBody);
                using var doc = JsonDocument.Parse(dirGetBody);
                var cfg = doc.RootElement.GetProperty("configuration");
                Assert.IsTrue(cfg.GetProperty("storeCorrupt").GetBoolean());
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, cfg.GetProperty("storeStatusReasonCode").GetString());
                Assert.IsFalse(cfg.GetProperty("secrets").GetProperty("hasSmtpPassword").GetBoolean(),
                    "Directory at envelope path must never fall back to host SMTP credentials.");
            }
        }
        finally
        {
            if (Directory.Exists(storePath))
            {
                Directory.Delete(storePath, recursive: true);
            }
        }

        // Part B: Save a valid envelope, make it unreadable (0000) at runtime, verify Degraded health, 200 GET, and PUT reset recovery.
        string currentVersion;
        using (var initialGet = await rwClient.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false))
        {
            var body = await initialGet.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, initialGet.StatusCode, body);
            using var doc = JsonDocument.Parse(body);
            currentVersion = doc.RootElement.GetProperty("configuration").GetProperty("version").GetString()!;
        }

        using (var saveValid = await rwClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
        {
            expectedVersion = currentVersion,
            options = new
            {
                enabled = true,
                smtp = new
                {
                    enabled = true,
                    host = "smtp.example.test",
                    port = 587,
                    securityMode = 0,
                    fromAddress = "camera@example.test",
                    recipients = IntegrationRecipients
                }
            },
            smtpUsername = "saved-user",
            smtpPassword = "saved-password"
        }).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.OK, saveValid.StatusCode, await saveValid.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        try
        {
            File.SetUnixFileMode(storePath, UnixFileMode.None);

            // Health IMMEDIATELY reports Degraded (store-unreadable) rather than staying green.
            var unreadableHealthJson = await anon.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(unreadableHealthJson))
            {
                var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                    .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
                Assert.AreEqual("Degraded", check.GetProperty("status").GetString());
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, check.GetProperty("data").GetProperty("ReasonCode").GetString());
            }

            // GET returns 200 OK (not 503) with a valid recovery version and zero host secret fallback.
            string recoveryVersion;
            using (var unreadableGet = await rwClient.GetAsync(new Uri(NotificationsPath, UriKind.Relative)).ConfigureAwait(false))
            {
                var body = await unreadableGet.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, unreadableGet.StatusCode, body);
                using var doc = JsonDocument.Parse(body);
                var cfg = doc.RootElement.GetProperty("configuration");
                Assert.IsTrue(cfg.GetProperty("storeCorrupt").GetBoolean());
                Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, cfg.GetProperty("storeStatusReasonCode").GetString());
                Assert.IsTrue(cfg.GetProperty("canPersist").GetBoolean());
                Assert.IsFalse(cfg.GetProperty("secrets").GetProperty("hasSmtpPassword").GetBoolean());
                recoveryVersion = cfg.GetProperty("version").GetString()!;
                Assert.IsFalse(string.IsNullOrWhiteSpace(recoveryVersion));
            }

            // PUT with the recovery version atomically replaces the 0000 file with a 0600 envelope and restores Healthy.
            using (var resetPut = await rwClient.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = recoveryVersion,
                options = new { enabled = false }
            }).ConfigureAwait(false))
            {
                var body = await resetPut.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, resetPut.StatusCode, body);
                using var doc = JsonDocument.Parse(body);
                Assert.IsFalse(doc.RootElement.GetProperty("configuration").GetProperty("storeCorrupt").GetBoolean());
            }

            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(storePath));
            var recoveredHealthJson = await anon.GetStringAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(recoveredHealthJson))
            {
                var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                    .Single(c => c.GetProperty("name").GetString() == "operator-notifications");
                Assert.AreEqual("Healthy", check.GetProperty("status").GetString());
                Assert.AreEqual("Disabled", check.GetProperty("data").GetProperty("Availability").GetString());
            }
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.SetUnixFileMode(storePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task EndToEndEncryptedSaveLoopbackTlsSmtpAndWebhookDeliveryCaptureProgressDuringHeldOutageAndZeroSecretLeaksAsync()
    {
        var runId = Guid.NewGuid().ToString("N");
        var smtpUser = $"integ-user-{runId[..8]}";
        var smtpPass = $"integ-pass-{runId[8..16]}";
        var webhookPathSegment = $"integ-path-{runId[16..24]}";
        var webhookQueryValue = $"integ-query-{runId[24..]}";
        var webhookHeaderValue = $"integ-header-{runId[..12]}";
        var ntfyTopicSecret = $"integ-topic-{runId[12..24]}";

        using var cert = CreateEphemeralLoopbackCertificate();
        var certThumbprint = cert.Thumbprint;
        using var logCollector = new InMemoryLogCollector();
        var smtpCapture = LoopbackStartTlsSmtpSink.Start(cert);
        var webhookCapture = LoopbackHttpsSink.Start(cert);
        ObservingSmtpNotificationTransport? smtpObserver = null;
        await using (smtpCapture.ConfigureAwait(false))
        await using (webhookCapture.ConfigureAwait(false))
        {
            using var fixture = new CameraAgentIntegrationFixture();
            await fixture.InitializeAsync(configureServices: services =>
            {
                var loopbackPolicy = new OperatorNotificationNetworkPolicy(isDevelopment: false)
                {
                    AllowLoopbackForTesting = true
                };
                services.Replace(ServiceDescriptor.Singleton(loopbackPolicy));
                services.Replace(ServiceDescriptor.Singleton<ISmtpNotificationTransport>(sp =>
                {
                    var inner = new SmtpNotificationTransport(loopbackPolicy, sp.GetRequiredService<TimeProvider>())
                    {
                        CertificateValidationCallback = (_, presented, _, _) =>
                            presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, certThumbprint, StringComparison.Ordinal)
                    };
                    smtpObserver = new ObservingSmtpNotificationTransport(inner);
                    return smtpObserver;
                }));
                services.Replace(ServiceDescriptor.Singleton<IWebhookNotificationTransport>(sp =>
                    new WebhookNotificationTransport(loopbackPolicy, sp.GetRequiredService<TimeProvider>())
                    {
                        CertificateValidationCallback = (_, presented, _, _) =>
                            presented is X509Certificate2 x509 && string.Equals(x509.Thumbprint, certThumbprint, StringComparison.Ordinal)
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

            // 3. Save valid opt-in STARTTLS SMTP + HTTPS Webhook configuration with atomic protected secrets.
            var webhookUrl = $"https://127.0.0.1:{webhookCapture.Port}/{webhookPathSegment}?k={webhookQueryValue}";
            using (var saveResp = await client.PutAsJsonAsync(new Uri(NotificationsPath, UriKind.Relative), new
            {
                expectedVersion = version,
                options = new
                {
                    enabled = true,
                    requestTimeoutSeconds = 15,
                    maxRetryAttempts = 0,
                    consecutiveOutageThreshold = 1,
                    outageCooldownSeconds = 60,
                    testSendMinimumIntervalSeconds = 0,
                    smtp = new
                    {
                        enabled = true,
                        host = "127.0.0.1",
                        port = smtpCapture.Port,
                        securityMode = 0, // StartTls (required with credentials)
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
                webhookAuthToken = webhookHeaderValue,
                webhookNtfyTopic = ntfyTopicSecret
            }).ConfigureAwait(false))
            {
                var saveBody = await saveResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, saveResp.StatusCode, saveBody);
                using var doc = JsonDocument.Parse(saveBody);
                version = doc.RootElement.GetProperty("configuration").GetProperty("version").GetString()!;
                Assert.AreEqual($"https://127.0.0.1:{webhookCapture.Port}",
                    doc.RootElement.GetProperty("configuration").GetProperty("secrets").GetProperty("webhookEndpointHost").GetString());
                Assert.IsTrue(doc.RootElement.GetProperty("configuration").GetProperty("secrets").GetProperty("hasWebhookNtfyTopic").GetBoolean());
            }

            // 4. Verify single atomic envelope file on disk contains zero plaintext secrets.
            var storeDisk = await File.ReadAllTextAsync(Path.Combine(provisioningDir, OperatorNotificationStore.ConfigFileName)).ConfigureAwait(false);
            foreach (var canary in new[] { smtpUser, smtpPass, webhookPathSegment, webhookQueryValue, webhookHeaderValue, ntfyTopicSecret })
            {
                Assert.DoesNotContain(canary, storeDisk, StringComparison.Ordinal);
            }

            // 5. Trigger safe test-send and verify BOTH STARTTLS loopback SMTP and HTTPS loopback Webhook received payloads.
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

            // 6. Wait for initial virtual-camera durable capture, then hold an SMTP provider send OPEN and verify:
            //    (a) Webhook delivers immediately while the client-side SMTP attempt is still actively pending,
            //    (b) Virtual-camera durable capture sequence numbers advance AFTER the hold starts AND BEFORE the
            //        client-side 15s SMTP timeout expires or the hold is released (strict overlap invariant), and
            //    (c) Once SMTP times out into OutageBackoff, /health reports Degraded (never Unhealthy) and summary stays 200 OK.
            _ = await WaitForCaptureSequenceAsync(fixture.StorageRoot, minimumSequence: 1, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Assert.IsNotNull(smtpObserver);
            Assert.AreEqual(1, smtpObserver.CompletedSendCount);
            var smtpHoldGate = smtpCapture.ArmHoldNextConnection();

            try
            {
                using var scope = fixture.CreateCameraAgentScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OperatorNotificationDispatcher>();
                Assert.IsTrue(dispatcher.TryPublish(new OperatorNotificationEvent(
                    OperatorNotificationEventKind.DiskPressure,
                    OperatorNotificationSeverity.Warning,
                    "disk_pressure:raw-ingress",
                    "Disk pressure (raw-ingress)",
                    "Storage volume raw-ingress is under disk pressure.",
                    DateTimeOffset.UtcNow,
                    "Integration Camera")));

                // Confirm SMTP worker has connected and the loopback server is actively holding the connection open.
                await smtpHoldGate.ConnectionHeld.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Assert.AreEqual(1, smtpObserver.ActiveSendCount, "Client-side SMTP send must be actively pending while held open.");
                Assert.AreEqual(1, smtpObserver.CompletedSendCount, "Held SMTP attempt must not have timed out or completed yet.");

                // Sample baseline committed capture_sequence AFTER the SMTP connection is actively held open!
                var baselineDuringHold = await WaitForCaptureSequenceAsync(
                    fixture.StorageRoot,
                    minimumSequence: 1,
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // Independent Webhook channel delivers promptly while client-side SMTP send is still actively pending!
                var webhookDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (DateTimeOffset.UtcNow < webhookDeadline && webhookCapture.Requests.Count < 2)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
                Assert.HasCount(2, webhookCapture.Requests, "Webhook must deliver independently while SMTP send is held open.");
                Assert.AreEqual(1, smtpObserver.ActiveSendCount, "Client-side SMTP send must still be actively pending after webhook delivery.");

                // Real capture-progress acceptance check: capture_sequence MUST advance WHILE the client-side SMTP attempt
                // is STILL actively pending (before releasing the hold and before the 15s client timeout expires)!
                var sequenceDuringOutage = await WaitForCaptureSequenceAsync(
                    fixture.StorageRoot,
                    minimumSequence: baselineDuringHold + 2,
                    TimeSpan.FromSeconds(13)).ConfigureAwait(false);
                Assert.IsGreaterThanOrEqualTo(baselineDuringHold + 2, sequenceDuringOutage,
                    "Virtual-camera durable capture sequence must continue advancing while client-side SMTP delivery is actively pending.");
                Assert.IsFalse(smtpHoldGate.ReleaseTask.IsCompleted, "SMTP server hold must still be open when capture advancement completes.");
                Assert.AreEqual(1, smtpObserver.ActiveSendCount,
                    "Client-side SMTP attempt must STILL be actively pending (not timed out) when capture_sequence advanced.");
                Assert.AreEqual(1, smtpObserver.CompletedSendCount,
                    "Client-side SMTP attempt must NOT have finished before capture_sequence advanced.");
                Assert.AreNotEqual(NotificationChannelHealthState.OutageBackoff, dispatcher.GetStatus().Smtp.State,
                    "SMTP must not have already timed out into OutageBackoff before capture_sequence advanced.");

                // Now wait for the held SMTP attempt to hit its 15s RequestTimeoutSeconds and transition SMTP to OutageBackoff.
                var backoffDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (DateTimeOffset.UtcNow < backoffDeadline &&
                       dispatcher.GetStatus().Smtp.State != NotificationChannelHealthState.OutageBackoff)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
                Assert.AreEqual(NotificationChannelHealthState.OutageBackoff, dispatcher.GetStatus().Smtp.State);
                Assert.AreEqual(0, smtpObserver.ActiveSendCount);
                Assert.AreEqual(2, smtpObserver.CompletedSendCount);
            }
            finally
            {
                smtpHoldGate.Release();
            }

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
            foreach (var canary in new[] { smtpUser, smtpPass, webhookPathSegment, webhookQueryValue, webhookHeaderValue, ntfyTopicSecret })
            {
                Assert.DoesNotContain(canary, allSurfaces, StringComparison.Ordinal);
            }
        }
    }

    private static async Task<long> WaitForCaptureSequenceAsync(string storageRoot, long minimumSequence, TimeSpan timeout)
    {
        var dbPath = Path.Combine(storageRoot, "journal", "raw-ingress.db");
        var deadline = DateTimeOffset.UtcNow + timeout;
        long latest = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(dbPath))
            {
                try
                {
                    using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                        DefaultTimeout = 1
                    }.ToString());
                    await conn.OpenAsync().ConfigureAwait(false);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COALESCE(MAX(capture_sequence), 0) FROM raw_captures WHERE state = 'committed';";
                    var scalar = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (scalar is long seq)
                    {
                        latest = seq;
                        if (latest >= minimumSequence)
                        {
                            return latest;
                        }
                    }
                }
                catch (SqliteException)
                {
                    // Table or DB still initializing.
                }
            }
            await Task.Delay(100).ConfigureAwait(false);
        }

        Assert.Fail($"Expected durable committed capture sequence >= {minimumSequence} within {timeout.TotalSeconds:0.0}s, observed {latest}.");
        return latest;
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

    private sealed class SmtpHoldGate
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectionHeld => _held.Task;
        public Task ReleaseTask => _release.Task;
        public void MarkHeld() => _held.TrySetResult();
        public void Release() => _release.TrySetResult();
    }

    private sealed class ObservingSmtpNotificationTransport(ISmtpNotificationTransport inner) : ISmtpNotificationTransport
    {
        private int _activeSendCount;
        private int _completedSendCount;

        public int ActiveSendCount => Volatile.Read(ref _activeSendCount);
        public int CompletedSendCount => Volatile.Read(ref _completedSendCount);

        public async Task<OperatorNotificationDeliveryResult> SendAsync(SmtpDeliveryRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeSendCount);
            try
            {
                return await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSendCount);
                Interlocked.Increment(ref _completedSendCount);
            }
        }
    }

    private sealed class LoopbackStartTlsSmtpSink : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly object _gate = new();
        private readonly List<string> _bodies = [];
        private SmtpHoldGate? _pendingHold;
        private bool _disposed;

        public int Port { get; }
        public IReadOnlyList<string> Bodies { get { lock (_gate) return [.. _bodies]; } }

        private LoopbackStartTlsSmtpSink(TcpListener listener, X509Certificate2 cert)
        {
            _listener = listener;
            _cert = cert;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public static LoopbackStartTlsSmtpSink Start(X509Certificate2 cert)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackStartTlsSmtpSink(listener, cert);
        }

        public SmtpHoldGate ArmHoldNextConnection()
        {
            var gate = new SmtpHoldGate();
            lock (_gate)
            {
                _pendingHold = gate;
            }
            return gate;
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
                    SmtpHoldGate? holdGate;
                    lock (_gate)
                    {
                        holdGate = _pendingHold;
                        _pendingHold = null;
                    }
                    if (holdGate is not null)
                    {
                        holdGate.MarkHeld();
                        await holdGate.ReleaseTask.WaitAsync(_cts.Token).ConfigureAwait(false);
                        return;
                    }

                    var networkStream = client.GetStream();
                    await using (networkStream.ConfigureAwait(false))
                    {
                        var upgradedToTls = await RunSmtpLoopAsync(networkStream, sendGreeting: true, usedTls: false).ConfigureAwait(false);
                        if (upgradedToTls)
                        {
                            var ssl = new SslStream(networkStream, leaveInnerStreamOpen: true);
                            await using (ssl.ConfigureAwait(false))
                            {
                                await ssl.AuthenticateAsServerAsync(_cert).ConfigureAwait(false);
                                _ = await RunSmtpLoopAsync(ssl, sendGreeting: false, usedTls: true).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
                {
                    // Test server disconnects cleanly.
                }
            }
        }

        private async Task<bool> RunSmtpLoopAsync(Stream stream, bool sendGreeting, bool usedTls)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await using (writer.ConfigureAwait(false))
            {
                if (sendGreeting)
                {
                    await writer.WriteLineAsync("220 loopback-smtp.local ESMTP").ConfigureAwait(false);
                }

                while (true)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (line is null) return false;
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-loopback-smtp.local").ConfigureAwait(false);
                        if (!usedTls)
                        {
                            await writer.WriteLineAsync("250-STARTTLS").ConfigureAwait(false);
                        }
                        await writer.WriteLineAsync("250 AUTH PLAIN LOGIN").ConfigureAwait(false);
                    }
                    else if (line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase) && !usedTls)
                    {
                        await writer.WriteLineAsync("220 Ready to start TLS").ConfigureAwait(false);
                        return true;
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
                        return false;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                    }
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

    private sealed class LoopbackHttpsSink : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly object _gate = new();
        private readonly List<string> _requests = [];
        private bool _disposed;

        public int Port { get; }
        public IReadOnlyList<string> Requests { get { lock (_gate) return [.. _requests]; } }

        private LoopbackHttpsSink(TcpListener listener, X509Certificate2 cert)
        {
            _listener = listener;
            _cert = cert;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptAsync);
        }

        public static LoopbackHttpsSink Start(X509Certificate2 cert)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackHttpsSink(listener, cert);
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
                    var raw = client.GetStream();
                    await using (raw.ConfigureAwait(false))
                    {
                        var ssl = new SslStream(raw, leaveInnerStreamOpen: true);
                        await using (ssl.ConfigureAwait(false))
                        {
                            await ssl.AuthenticateAsServerAsync(_cert).ConfigureAwait(false);
                            var buffer = new byte[16384];
                            var total = 0;
                            var contentLength = 0;
                            var headerEnd = -1;
                            while (total < buffer.Length)
                            {
                                var read = await ssl.ReadAsync(buffer.AsMemory(total, buffer.Length - total), _cts.Token).ConfigureAwait(false);
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
                                                int.TryParse(line[15..].Trim(), CultureInfo.InvariantCulture, out var parsed))
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
                            await ssl.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                            await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
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
