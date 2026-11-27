using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Operations)]
public sealed class OperatorNotificationValidationTests
{
    private static readonly OperatorNotificationNetworkPolicy ProductionPolicy = new(isDevelopment: false);
    private static readonly OperatorNotificationNetworkPolicy DevelopmentPolicy = new(isDevelopment: true);

    [TestMethod]
    public void Defaults_AreStrictlyOffByDefault()
    {
        var options = new OperatorNotificationsOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.Smtp.Enabled);
        Assert.IsFalse(options.Webhook.Enabled);
        Assert.AreEqual(SmtpSecurityMode.StartTls, options.Smtp.SecurityMode);
        Assert.AreEqual(SmtpNotificationOptions.DefaultPort, options.Smtp.Port);
        Assert.AreEqual(WebhookPayloadFormat.Generic, options.Webhook.Format);
        Assert.IsFalse(options.Webhook.AllowPrivateNetwork);

        var problems = OperatorNotificationValidation.Validate(options, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsEmpty(problems);

        var emptyConfig = new ConfigurationBuilder().Build();
        var bound = ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(emptyConfig, out var bindingError);
        Assert.IsNull(bindingError);
        Assert.IsFalse(bound.Enabled);
    }

    [TestMethod]
    [DataRow("Smtp:SecurityMode", "SSL")]
    [DataRow("Smtp:Port", "submission")]
    [DataRow("Webhook:Format", "Teams")]
    [DataRow("RequestTimeoutSeconds", "ten")]
    public void ReadOptionsFromConfiguration_UnparseableValue_DegradesAsInvalidConfigurationWithoutThrowing(
        string relativeKey,
        string malformedValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "false",
                [OperatorNotificationsOptions.SectionKey + ":" + relativeKey] = malformedValue
            })
            .Build();

        var options = ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(config, out var bindingError);

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, bindingError);

        using var provider = new ConfigurationOperatorNotificationConfigProvider(config, ProductionPolicy);
        Assert.IsFalse(provider.CurrentOptions.Enabled);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, provider.ConfigurationErrorReasonCode);
    }

    [TestMethod]
    public void Validate_SmtpEnabled_RequiresHostFromAndRecipientsAndRejectsHeaderInjection()
    {
        var missing = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions { Enabled = true }
        };

        var problems = OperatorNotificationValidation.Validate(missing, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsTrue(problems.ContainsKey("smtp.host"));
        Assert.IsTrue(problems.ContainsKey("smtp.fromAddress"));
        Assert.IsTrue(problems.ContainsKey("smtp.recipients"));

        var injected = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.org",
                Port = 587,
                FromAddress = "camera@example.org",
                FromDisplayName = "Camera\r\nBcc: evil@example.org",
                Recipients = ["ops@example.org"]
            }
        };
        var secrets = new OperatorNotificationSecrets(
            SmtpUsername: "user\r\nAUTH",
            SmtpPassword: "synthetic-pass-val");

        var injectedProblems = OperatorNotificationValidation.Validate(injected, secrets, ProductionPolicy);
        Assert.IsTrue(injectedProblems.ContainsKey("smtp.fromDisplayName"));
        Assert.IsTrue(injectedProblems.ContainsKey("smtp.username"));
    }

    [TestMethod]
    public void Validate_SmtpPasswordWithoutUsername_IsRejected()
    {
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.azurecomm.net",
                Port = 587,
                SecurityMode = SmtpSecurityMode.StartTls,
                FromAddress = "alerts@example.org",
                Recipients = ["operator@example.org"]
            }
        };

        var problems = OperatorNotificationValidation.Validate(
            options,
            new OperatorNotificationSecrets(SmtpPassword: "synthetic-only-password"),
            ProductionPolicy);

        Assert.IsTrue(problems.ContainsKey("smtp.username"));
    }

    [TestMethod]
    public void Validate_SmtpSecurityModeNone_RejectsCredentialsAndPublicLiteralIpsAndUnknownModes()
    {
        // Public host + SecurityMode.None + credentials MUST be rejected.
        var publicWithCreds = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.office365.com",
                Port = 25,
                SecurityMode = SmtpSecurityMode.None,
                FromAddress = "alerts@example.org",
                Recipients = ["ops@example.org"]
            }
        };
        var credsProblems = OperatorNotificationValidation.Validate(
            publicWithCreds,
            new OperatorNotificationSecrets(SmtpUsername: "user@example.org", SmtpPassword: "synthetic-password"),
            ProductionPolicy);
        Assert.IsTrue(credsProblems.ContainsKey("smtp.securityMode"));

        // Local LAN relay + SecurityMode.None + credentials MUST also be rejected (TLS required whenever credentials exist).
        var lanWithCreds = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "192.168.1.25",
                Port = 25,
                SecurityMode = SmtpSecurityMode.None,
                FromAddress = "alerts@example.org",
                Recipients = ["ops@example.org"]
            }
        };
        var lanCredsProblems = OperatorNotificationValidation.Validate(
            lanWithCreds,
            new OperatorNotificationSecrets(SmtpUsername: "lan-user", SmtpPassword: "synthetic-password"),
            ProductionPolicy);
        Assert.IsTrue(lanCredsProblems.ContainsKey("smtp.securityMode"));

        // Unauthenticated SecurityMode.None to public IP MUST be rejected.
        var publicIpUnauth = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "93.184.216.34",
                Port = 25,
                SecurityMode = SmtpSecurityMode.None,
                FromAddress = "alerts@example.org",
                Recipients = ["ops@example.org"]
            }
        };
        var publicIpProblems = OperatorNotificationValidation.Validate(
            publicIpUnauth,
            OperatorNotificationSecrets.Empty,
            ProductionPolicy);
        Assert.IsTrue(publicIpProblems.ContainsKey("smtp.securityMode"));

        // Unauthenticated SecurityMode.None to RFC 1918 LAN relay is permitted.
        var lanUnauth = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "192.168.1.25",
                Port = 25,
                SecurityMode = SmtpSecurityMode.None,
                FromAddress = "alerts@example.org",
                Recipients = ["ops@example.org"]
            }
        };
        var lanProblems = OperatorNotificationValidation.Validate(lanUnauth, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsEmpty(lanProblems);

        // Unknown security mode fails validation.
        var unknownMode = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.org",
                Port = 587,
                SecurityMode = (SmtpSecurityMode)99,
                FromAddress = "alerts@example.org",
                Recipients = ["ops@example.org"]
            }
        };
        var unknownProblems = OperatorNotificationValidation.Validate(unknownMode, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsTrue(unknownProblems.ContainsKey("smtp.securityMode"));
    }

    [TestMethod]
    public void Validate_AllDeclaredOptionRanges_EnforceBounds()
    {
        var outOfRangeLow = new OperatorNotificationsOptions
        {
            ConsecutiveOutageThreshold = 0,
            EvaluationIntervalSeconds = 4,
            TestSendMinimumIntervalSeconds = -1
        };
        var lowProblems = OperatorNotificationValidation.Validate(outOfRangeLow, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsTrue(lowProblems.ContainsKey("consecutiveOutageThreshold"));
        Assert.IsTrue(lowProblems.ContainsKey("evaluationIntervalSeconds"));
        Assert.IsTrue(lowProblems.ContainsKey("testSendMinimumIntervalSeconds"));

        var outOfRangeHigh = new OperatorNotificationsOptions
        {
            ConsecutiveOutageThreshold = 11,
            EvaluationIntervalSeconds = 601,
            TestSendMinimumIntervalSeconds = 301
        };
        var highProblems = OperatorNotificationValidation.Validate(outOfRangeHigh, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsTrue(highProblems.ContainsKey("consecutiveOutageThreshold"));
        Assert.IsTrue(highProblems.ContainsKey("evaluationIntervalSeconds"));
        Assert.IsTrue(highProblems.ContainsKey("testSendMinimumIntervalSeconds"));
    }

    [TestMethod]
    [DataRow("mailpit")]
    [DataRow("skymonitor-mailpit")]
    [DataRow("mailpit.local")]
    [DataRow("smtp.mailpit.internal")]
    [DataRow("mailhog")]
    public void Validate_MailpitHost_IsRejectedInProductionAndAllowedOnlyInDevelopment(string host)
    {
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = host,
                Port = 1025,
                SecurityMode = SmtpSecurityMode.None,
                FromAddress = "camera@example.org",
                Recipients = ["ops@example.org"]
            }
        };

        var prodProblems = OperatorNotificationValidation.Validate(options, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsTrue(prodProblems.ContainsKey("smtp.host"), $"Expected {host} to be rejected in production.");
        StringAssert.Contains(prodProblems["smtp.host"], "Mailpit", StringComparison.OrdinalIgnoreCase);

        var devProblems = OperatorNotificationValidation.Validate(options, OperatorNotificationSecrets.Empty, DevelopmentPolicy);
        Assert.IsFalse(devProblems.ContainsKey("smtp.host"), $"Expected {host} to be permitted in Development.");
    }

    [TestMethod]
    public void ProductionConfigurationAndDeploymentFiles_ExcludeMailpit()
    {
        var repoRoot = FindRepositoryRoot();
        var productionFiles = new[]
        {
            Path.Combine(repoRoot, "src", "HVO.SkyMonitor.CameraAgent", "appsettings.json"),
            Path.Combine(repoRoot, "src", "HVO.SkyMonitor.CameraAgent", "Dockerfile"),
            Path.Combine(repoRoot, "docker-compose.apps.yml"),
            Path.Combine(repoRoot, "deploy", "split-host", "compose.cameraagent.yml"),
            Path.Combine(repoRoot, "src", "HVO.SkyMonitor.Deployment.Cli", "ComposeDeployment.cs"),
            Path.Combine(repoRoot, "src", "HVO.SkyMonitor.Deployment.Cli", "CameraAgentStateLayout.cs")
        };

        foreach (var file in productionFiles)
        {
            Assert.IsTrue(File.Exists(file), $"Expected release/deployment inventory file is missing: '{file}'.");
            var content = File.ReadAllText(file);
            Assert.IsFalse(
                content.Contains("mailpit", StringComparison.OrdinalIgnoreCase),
                $"Production file '{Path.GetRelativePath(repoRoot, file)}' must not reference Mailpit.");
            Assert.IsFalse(
                content.Contains("mailhog", StringComparison.OrdinalIgnoreCase),
                $"Production file '{Path.GetRelativePath(repoRoot, file)}' must not reference MailHog.");
        }
    }

    [TestMethod]
    [DataRow("http://hooks.example.test/notify", false)]
    [DataRow("https://user:pass@hooks.example.test/notify", false)]
    [DataRow("https://hooks.example.test/notify#fragment", false)]
    [DataRow("https://localhost/notify", false)]
    [DataRow("https://sub.localhost/notify", false)]
    [DataRow("https://metadata.google.internal/computeMetadata/v1/", false)]
    [DataRow("https://127.0.0.1/notify", false)]
    [DataRow("https://[::1]/notify", false)]
    [DataRow("https://0.0.0.0/notify", false)]
    [DataRow("https://169.254.169.254/latest/meta-data/", false)]
    [DataRow("https://169.254.169.254/latest/meta-data/", true)]
    [DataRow("https://100.100.100.200/latest/meta-data/", false)]
    [DataRow("https://100.100.100.200/latest/meta-data/", true)]
    [DataRow("https://100.64.0.10/notify", false)]
    [DataRow("https://100.64.0.10/notify", true)]
    [DataRow("https://[fd00:ec2::254]/latest/meta-data/", true)]
    [DataRow("https://[::ffff:169.254.169.254]/meta", true)]
    [DataRow("https://[::ffff:100.100.100.200]/meta", true)]
    [DataRow("https://[64:ff9b::169.254.169.254]/meta", true)]
    [DataRow("https://[64:ff9b::100.100.100.200]/meta", true)]
    [DataRow("https://[::127.0.0.1]/notify", false)]
    [DataRow("https://[::127.0.0.1]/notify", true)]
    [DataRow("https://[::ffff:0:127.0.0.1]/notify", false)]
    [DataRow("https://[::ffff:0:169.254.169.254]/notify", true)]
    [DataRow("https://[2002:7f00:0001::1]/notify", true)]
    [DataRow("https://[2002:a9fe:a9fe::1]/notify", true)]
    [DataRow("https://[64:ff9b:1::127.0.0.1]/notify", true)]
    [DataRow("https://[100::1]/notify", true)]
    [DataRow("https://[2001::1]/notify", true)]
    [DataRow("https://192.88.99.1/notify", true)]
    [DataRow("https://224.0.0.1/notify", true)]
    [DataRow("https://192.0.2.1/notify", true)]
    [DataRow("https://10.0.0.5/notify", false)]
    [DataRow("https://172.16.10.2/notify", false)]
    [DataRow("https://192.168.1.50/notify", false)]
    [DataRow("https://[fd12:3456::1]/notify", false)]
    public void TryValidateWebhookUrl_BlocksUnsafeAndSsrfTargets(string candidate, bool allowPrivateNetwork)
    {
        var valid = OperatorNotificationValidation.TryValidateWebhookUrl(
            candidate,
            allowPrivateNetwork,
            ProductionPolicy,
            out _,
            out var reasonCode,
            out var problem);

        Assert.IsFalse(valid, $"URL '{candidate}' (allowPrivate={allowPrivateNetwork}) should be rejected.");
        Assert.IsNotNull(reasonCode);
        Assert.IsNotNull(problem);
    }

    [TestMethod]
    [DataRow("https://hooks.example.test/webhook/item", false)]
    [DataRow("https://ntfy.example.test/camera-alerts", false)]
    [DataRow("https://192.168.1.50/ntfy", true)]
    [DataRow("https://10.10.0.25:8443/hook", true)]
    [DataRow("https://[fd12:3456::1]/hook", true)]
    public void TryValidateWebhookUrl_AllowsSafeHttpsAndOptInPrivateLan(string candidate, bool allowPrivateNetwork)
    {
        var valid = OperatorNotificationValidation.TryValidateWebhookUrl(
            candidate,
            allowPrivateNetwork,
            ProductionPolicy,
            out var uri,
            out _,
            out _);

        Assert.IsTrue(valid, $"URL '{candidate}' (allowPrivate={allowPrivateNetwork}) should be permitted.");
        Assert.IsNotNull(uri);
    }

    [TestMethod]
    public void Validate_Webhook_RejectsReservedHeadersEnforcesPushoverKeysAndEnforcesNtfyRootTopic()
    {
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover,
                AuthHeaderName = "Host"
            }
        };

        var problems = OperatorNotificationValidation.Validate(
            options,
            new OperatorNotificationSecrets(WebhookUrl: "https://api.pushover.example.test/1/messages.json"),
            ProductionPolicy);

        Assert.IsTrue(problems.ContainsKey("webhook.authHeaderName"));
        Assert.IsTrue(problems.ContainsKey("webhook.authToken"));
        Assert.IsTrue(problems.ContainsKey("webhook.userKey"));

        // Root ntfy URL without topic MUST fail validation.
        var ntfyOptions = new OperatorNotificationsOptions
        {
            Enabled = true,
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Ntfy
            }
        };
        var ntfyRootMissing = OperatorNotificationValidation.Validate(
            ntfyOptions,
            new OperatorNotificationSecrets(WebhookUrl: "https://ntfy.example.test/"),
            ProductionPolicy);
        Assert.IsTrue(ntfyRootMissing.ContainsKey("webhook.ntfyTopic"));

        // Root ntfy URL WITH protected topic passes.
        var ntfyRootOk = OperatorNotificationValidation.Validate(
            ntfyOptions,
            new OperatorNotificationSecrets(WebhookUrl: "https://ntfy.example.test/", WebhookNtfyTopic: "secret-observatory-topic"),
            ProductionPolicy);
        Assert.IsEmpty(ntfyRootOk);

        // Topic-path ntfy URL without separate topic passes.
        var ntfyPathOk = OperatorNotificationValidation.Validate(
            ntfyOptions,
            new OperatorNotificationSecrets(WebhookUrl: "https://ntfy.example.test/my-topic"),
            ProductionPolicy);
        Assert.IsEmpty(ntfyPathOk);
    }

    [TestMethod]
    public void RedactorAndContracts_NeverExposeCredentialsOrSecretPaths()
    {
        const string secretUrl = "https://hooks.example.test:8443/services/synthetic-secret-segment?token=synthetic-query-token#frag";
        Assert.AreEqual("https://hooks.example.test:8443", OperatorNotificationRedactor.FormatEndpointHost(secretUrl));
        Assert.AreEqual("https://hooks.example.test", OperatorNotificationRedactor.FormatEndpointHost("https://hooks.example.test/very/secret/path"));

        var secrets = new OperatorNotificationSecrets(
            SmtpUsername: "synthetic-user-alpha",
            SmtpPassword: "synthetic-pass-bravo",
            WebhookUrl: secretUrl,
            WebhookAuthToken: "synthetic-token-charlie",
            WebhookUserKey: "synthetic-key-delta",
            WebhookNtfyTopic: "synthetic-ntfy-topic-echo");
        Assert.AreEqual("[REDACTED]", secrets.ToString());

        var smtpRequest = new SmtpDeliveryRequest(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls,
            "from@example.test", "Camera", ["ops@example.test"],
            secrets.SmtpUsername, secrets.SmtpPassword, "Subj", "Body", "capture_stalled", TimeSpan.FromSeconds(5));
        var smtpStr = smtpRequest.ToString();
        Assert.DoesNotContain("synthetic-user-alpha", smtpStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-pass-bravo", smtpStr, StringComparison.Ordinal);

        var ev = new OperatorNotificationEvent(
            OperatorNotificationEventKind.DiskPressure,
            OperatorNotificationSeverity.Warning,
            "disk", "Disk", "Summary", DateTimeOffset.UtcNow);
        var webhookRequest = new WebhookDeliveryRequest(
            secretUrl, WebhookPayloadFormat.Ntfy, "Authorization", secrets.WebhookNtfyTopic, false,
            secrets.WebhookAuthToken, secrets.WebhookUserKey, ev, 1, 0, false, TimeSpan.FromSeconds(5));
        var webhookStr = webhookRequest.ToString();
        Assert.DoesNotContain("synthetic-secret-segment", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-query-token", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token-charlie", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-key-delta", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-ntfy-topic-echo", webhookStr, StringComparison.Ordinal);

        var cleaned = OperatorNotificationRedactor.SanitizeOperationalText(
            "Fault https://secret.example.test/tok Authorization: Bearer synthetic-hdr password=synthetic-pw\r\nline2",
            200);
        Assert.DoesNotContain("secret.example.test", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-hdr", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-pw", cleaned, StringComparison.Ordinal);
        Assert.IsFalse(cleaned.Contains('\r', StringComparison.Ordinal) || cleaned.Contains('\n', StringComparison.Ordinal));
    }

    [TestMethod]
    public void IsBlockedAndPrivateSmtpAddress_AllowLanRelaysButBlockImdsMetadataAndPublicForNone()
    {
        Assert.IsFalse(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("192.168.1.25")));
        Assert.IsFalse(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("10.0.0.25")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("169.254.169.254")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("100.100.100.200")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("0.0.0.0")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("224.0.0.1")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("fd00:ec2::254")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("::127.0.0.1")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("2002:a9fe:a9fe::1")));

        Assert.IsTrue(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("127.0.0.1")));
        Assert.IsTrue(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("::1")));
        Assert.IsTrue(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("192.168.1.25")));
        Assert.IsTrue(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("10.0.0.5")));
        Assert.IsTrue(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("172.16.0.5")));
        Assert.IsTrue(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("fd12:3456::1")));
        Assert.IsFalse(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("93.184.216.34")));
        Assert.IsFalse(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("100.100.100.200")));
        Assert.IsFalse(OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(IPAddress.Parse("fd00:ec2::254")));
    }

    [TestMethod]
    public async Task InvalidIdnWebhookHost_IsRejectedCleanlyWithoutThrowingUriFormatExceptionAsync()
    {
        const string invalidIdnUrl = "https://bad\u200Dhost.example.test/alerts";

        Assert.IsFalse(OperatorNotificationValidation.TryValidateWebhookUrl(
            invalidIdnUrl,
            allowPrivateNetwork: false,
            ProductionPolicy,
            out var parsed,
            out var reasonCode,
            out var problem));
        Assert.IsNull(parsed);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, reasonCode);
        Assert.IsNotNull(problem);

        Assert.IsNull(OperatorNotificationRedactor.FormatEndpointHost(invalidIdnUrl));
        Assert.IsNull(ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(invalidIdnUrl));
        Assert.IsFalse(ConfigurationOperatorNotificationConfigProvider.HasMatchingWebhookOrigin(invalidIdnUrl, invalidIdnUrl));
        Assert.IsNull(ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(invalidIdnUrl));
        Assert.IsFalse(ConfigurationOperatorNotificationConfigProvider.HasMatchingWebhookDestination(invalidIdnUrl, invalidIdnUrl));

        using var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System);
        var delivery = await transport.SendAsync(new WebhookDeliveryRequest(
            invalidIdnUrl,
            WebhookPayloadFormat.Generic,
            WebhookNotificationOptions.DefaultAuthHeaderName,
            null,
            false,
            null,
            null,
            new OperatorNotificationEvent(
                OperatorNotificationEventKind.DailyHeartbeat,
                OperatorNotificationSeverity.Info,
                "idn-test",
                "IDN test",
                "Invalid IDN host should fail cleanly.",
                DateTimeOffset.UtcNow),
            1, 0, true, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(delivery.Succeeded);
        Assert.AreEqual(NotificationReasonCodes.InvalidConfiguration, delivery.ReasonCode);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate HVO.SkyMonitor.v9.slnx repository root.");
    }
}
