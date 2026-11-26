using System.Net;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Common.Options;

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
        var hostOptions = new CameraAgentHostOptions();
        var options = hostOptions.Notifications;

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.Smtp.Enabled);
        Assert.IsFalse(options.Webhook.Enabled);
        Assert.AreEqual(SmtpSecurityMode.StartTls, options.Smtp.SecurityMode);
        Assert.AreEqual(SmtpNotificationOptions.DefaultPort, options.Smtp.Port);
        Assert.AreEqual(WebhookPayloadFormat.Generic, options.Webhook.Format);
        Assert.IsFalse(options.Webhook.AllowPrivateNetwork);

        var problems = OperatorNotificationValidation.Validate(options, OperatorNotificationSecrets.Empty, ProductionPolicy);
        Assert.IsEmpty(problems);
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
            Path.Combine(repoRoot, "deploy", "docker-compose.apps.yml")
        };

        foreach (var file in productionFiles)
        {
            if (!File.Exists(file))
            {
                continue;
            }
            var content = File.ReadAllText(file);
            Assert.IsFalse(
                content.Contains("mailpit", StringComparison.OrdinalIgnoreCase),
                $"Production file '{Path.GetRelativePath(repoRoot, file)}' must not reference Mailpit.");
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
    [DataRow("https://[fd00:ec2::254]/latest/meta-data/", true)]
    [DataRow("https://[::ffff:169.254.169.254]/meta", true)]
    [DataRow("https://[64:ff9b::169.254.169.254]/meta", true)]
    [DataRow("https://224.0.0.1/notify", true)]
    [DataRow("https://192.0.2.1/notify", true)]
    [DataRow("https://10.0.0.5/notify", false)]
    [DataRow("https://172.16.10.2/notify", false)]
    [DataRow("https://192.168.1.50/notify", false)]
    [DataRow("https://100.64.0.10/notify", false)]
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
    public void Validate_Webhook_RejectsReservedHeadersAndEnforcesPushoverKeys()
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
            WebhookUserKey: "synthetic-key-delta");
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
            secretUrl, WebhookPayloadFormat.Generic, "Authorization", string.Empty, false,
            secrets.WebhookAuthToken, secrets.WebhookUserKey, ev, 1, 0, false, TimeSpan.FromSeconds(5));
        var webhookStr = webhookRequest.ToString();
        Assert.DoesNotContain("synthetic-secret-segment", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-query-token", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token-charlie", webhookStr, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-key-delta", webhookStr, StringComparison.Ordinal);

        var cleaned = OperatorNotificationRedactor.SanitizeOperationalText(
            "Fault https://secret.example.test/tok Authorization: Bearer synthetic-hdr password=synthetic-pw\r\nline2",
            200);
        Assert.DoesNotContain("secret.example.test", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-hdr", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-pw", cleaned, StringComparison.Ordinal);
        Assert.IsFalse(cleaned.Contains('\r', StringComparison.Ordinal) || cleaned.Contains('\n', StringComparison.Ordinal));
    }

    [TestMethod]
    public void IsBlockedSmtpAddress_AllowsLanRelaysButBlocksImdsAndMulticast()
    {
        Assert.IsFalse(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("192.168.1.25")));
        Assert.IsFalse(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("10.0.0.25")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("169.254.169.254")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("0.0.0.0")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("224.0.0.1")));
        Assert.IsTrue(OperatorNotificationValidation.IsBlockedSmtpAddress(IPAddress.Parse("fd00:ec2::254")));
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
