using System.Security.Claims;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Ui")]
public sealed class NotificationsPageTests
{
    [TestMethod]
    public void Render_DefaultOff_ShowsDisabledStatusEmptySecretsAndEmptyHistory()
    {
        using var context = CreateContext(out _);

        var cut = context.Render<NotificationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Disabled", cut.Find("#notifications-master-state").TextContent.Trim());
            Assert.AreEqual("Disabled", cut.Find("#notifications-smtp-state").TextContent.Trim());
            Assert.AreEqual("Disabled", cut.Find("#notifications-webhook-state").TextContent.Trim());
            Assert.AreEqual(string.Empty, cut.Find("#smtp-username").GetAttribute("value") ?? string.Empty);
            Assert.AreEqual(string.Empty, cut.Find("#smtp-password").GetAttribute("value") ?? string.Empty);
            Assert.AreEqual(string.Empty, cut.Find("#webhook-url").GetAttribute("value") ?? string.Empty);
            Assert.AreEqual(string.Empty, cut.Find("#webhook-auth-token").GetAttribute("value") ?? string.Empty);
            Assert.IsNotNull(cut.Find("#notifications-history-empty"));
            Assert.IsNull(cut.Find("fieldset.notifications-fieldset").GetAttribute("disabled"));
            StringAssert.Contains(cut.Markup, "restores any host-configured webhook URL and host token", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "transport security mode", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void Render_ReadOnlyUser_DisablesFormAndTestButtons()
    {
        using var context = CreateContext(out var service);
        service.CurrentView = service.CurrentView with { CanMutate = false };

        var cut = context.Render<NotificationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find("fieldset.notifications-fieldset").GetAttribute("disabled"));
            Assert.IsNotNull(cut.Find("#notifications-save").GetAttribute("disabled"));
            Assert.IsNotNull(cut.Find("#notifications-test-all").GetAttribute("disabled"));
            Assert.IsNotNull(cut.Find("#notifications-test-smtp").GetAttribute("disabled"));
            Assert.IsNotNull(cut.Find("#notifications-test-webhook").GetAttribute("disabled"));
        });
    }

    [TestMethod]
    public void Save_SubmitsWriteOnlySecretsClearsInputsAndNeverLeaksSecretsIntoDom()
    {
        using var context = CreateContext(out var service);
        const string smtpUser = "ui-secret-smtp-user-11";
        const string smtpPass = "ui-secret-smtp-pass-22";
        const string webhookSecretPath = "ui-secret-webhook-path-33";
        const string webhookToken = "ui-secret-webhook-tok-44";
        const string ntfyTopicName = "ui-test-ntfy-topic-66";
        var fullWebhookUrl = $"https://hooks.example.test/{webhookSecretPath}?sig=55";

        var cut = context.Render<NotificationsPage>();
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("#notifications-enabled")));

        cut.Find("#notifications-enabled").Change(true);
        cut.Find("#smtp-enabled").Change(true);
        cut.Find("#smtp-host").Change("smtp.example.test");
        cut.Find("#smtp-port").Change("587");
        cut.Find("#smtp-from-address").Change("camera@example.test");
        cut.Find("#smtp-recipients").Change("ops@example.test");
        cut.Find("#smtp-username").Change(smtpUser);
        cut.Find("#smtp-password").Change(smtpPass);

        cut.Find("#webhook-enabled").Change(true);
        cut.Find("#webhook-format").Change(WebhookPayloadFormat.Ntfy.ToString());
        cut.Find("#webhook-url").Change(fullWebhookUrl);
        cut.Find("#webhook-auth-token").Change(webhookToken);
        cut.Find("#webhook-ntfy-topic").Change(ntfyTopicName);

        cut.Find("form.notifications-form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(service.LastSaveRequest);
            Assert.AreEqual("Notification settings saved.", cut.Find("#notifications-banner").TextContent.Trim());
            Assert.AreEqual("Enabled", cut.Find("#notifications-master-state").TextContent.Trim());
            StringAssert.Contains(cut.Find("#webhook-secret-status").TextContent, "https://hooks.example.test", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#webhook-secret-status").TextContent, "Topic: Protected store", StringComparison.Ordinal);
        });

        Assert.AreEqual(smtpUser, service.LastSaveRequest!.SmtpUsername);
        Assert.AreEqual(smtpPass, service.LastSaveRequest.SmtpPassword);
        Assert.AreEqual(fullWebhookUrl, service.LastSaveRequest.WebhookUrl);
        Assert.AreEqual(webhookToken, service.LastSaveRequest.WebhookAuthToken);
        Assert.AreEqual(ntfyTopicName, service.LastSaveRequest.WebhookNtfyTopic);

        // Negative DOM secret-leak verification: inputs cleared and stored secrets (including ntfy topic) NEVER rendered in markup!
        var html = cut.Markup;
        foreach (var canary in new[] { smtpUser, smtpPass, webhookSecretPath, "sig=55", webhookToken, ntfyTopicName })
        {
            Assert.DoesNotContain(canary, html, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void Render_CorruptStore_ShowsRecoveryAlertAndAllowsSavingReplacementSettings()
    {
        using var context = CreateContext(out var service);
        service.CurrentView = service.CurrentView with
        {
            Configuration = service.CurrentView.Configuration with
            {
                StoreCorrupt = true,
                StoreStatusReasonCode = NotificationReasonCodes.StoreCorrupt
            }
        };

        var cut = context.Render<NotificationsPage>();

        cut.WaitForAssertion(() =>
        {
            var corruptBanner = cut.Find("#notifications-corrupt-banner");
            StringAssert.Contains(corruptBanner.TextContent, "unreadable", StringComparison.OrdinalIgnoreCase);
            Assert.IsNull(cut.Find("fieldset.notifications-fieldset").GetAttribute("disabled"));
            Assert.IsNull(cut.Find("#notifications-save").GetAttribute("disabled"));
        });
    }

    [TestMethod]
    public void Render_InvalidHostConfiguration_ShowsInvalidConfigurationBannerAndAllowsOverrideSave()
    {
        using var context = CreateContext(out var service);
        service.CurrentView = service.CurrentView with
        {
            Configuration = service.CurrentView.Configuration with
            {
                StoreCorrupt = false,
                StoreStatusReasonCode = NotificationReasonCodes.InvalidConfiguration
            },
            Status = service.CurrentView.Status with
            {
                ConfigurationErrorReasonCode = NotificationReasonCodes.InvalidConfiguration
            }
        };

        var cut = context.Render<NotificationsPage>();

        cut.WaitForAssertion(() =>
        {
            var banner = cut.Find("#notifications-config-error-banner");
            StringAssert.Contains(banner.TextContent, NotificationReasonCodes.InvalidConfiguration, StringComparison.Ordinal);
            Assert.IsNull(cut.Find("fieldset.notifications-fieldset").GetAttribute("disabled"));
            Assert.IsNull(cut.Find("#notifications-save").GetAttribute("disabled"));
        });
    }

    [TestMethod]
    public void TestSend_DisplaysSanitizedPerChannelResultAndRefreshesAuditHistory()
    {
        using var context = CreateContext(out var service);
        service.TestHandler = target =>
        {
            service.CurrentView = service.CurrentView with
            {
                Status = service.CurrentView.Status with
                {
                    RecentDeliveries =
                    [
                        new OperatorNotificationAuditEntry(
                            new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero),
                            "test",
                            "smtp",
                            "sent",
                            NotificationReasonCodes.Sent,
                            1,
                            IsTest: true)
                    ]
                }
            };
            return ValueTask.FromResult(OperatorUiResult<OperatorNotificationTestResult>.Success(
                new OperatorNotificationTestResult(
                    RateLimited: false,
                    RetryAfterUtc: null,
                    Channels:
                    [
                        new NotificationChannelTestResult("smtp", Attempted: true, Succeeded: true, NotificationReasonCodes.Sent, "Delivered successfully.", 12.5)
                    ])));
        };

        var cut = context.Render<NotificationsPage>();
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("#notifications-test-smtp")));

        cut.Find("#notifications-test-smtp").Click();

        cut.WaitForAssertion(() =>
        {
            var resultText = cut.Find("#notifications-test-result").TextContent;
            StringAssert.Contains(resultText, "SMTP:", StringComparison.Ordinal);
            StringAssert.Contains(resultText, "Delivered successfully.", StringComparison.Ordinal);
            var rows = cut.FindAll("#notifications-history-table tbody tr");
            Assert.HasCount(1, rows);
        });
    }

    [TestMethod]
    public void UnauthorizedRead_RedirectsToAccessDenied()
    {
        using var context = CreateContext(out var service);
        service.GetOverride = () => ValueTask.FromResult(
            OperatorUiResult<NotificationsPageView>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        var cut = context.Render<NotificationsPage>();

        cut.WaitForAssertion(() =>
            StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Save_AgainstRealStoreWithInvalidHiddenHostSettings_NormalizesHiddenFieldsAndPersistsCleanlyAsync()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-ui-hidden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(stateDir, "dp-keys")));
            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":QueueCapacity"] = "2",
                    [OperatorNotificationsOptions.SectionKey + ":MaxRetryAttempts"] = "99",
                    [OperatorNotificationsOptions.SectionKey + ":RetryInitialDelaySeconds"] = "200",
                    [OperatorNotificationsOptions.SectionKey + ":RetryMaximumDelaySeconds"] = "50",
                    [OperatorNotificationsOptions.SectionKey + ":OutageCooldownSeconds"] = "1",
                    [OperatorNotificationsOptions.SectionKey + ":ConsecutiveOutageThreshold"] = "0",
                    [OperatorNotificationsOptions.SectionKey + ":EvaluationIntervalSeconds"] = "1",
                    [OperatorNotificationsOptions.SectionKey + ":CameraDisconnectedThresholdSeconds"] = "-5",
                    [OperatorNotificationsOptions.SectionKey + ":TestSendMinimumIntervalSeconds"] = "999"
                })
                .Build();

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = stateDir }),
                hostConfig,
                new OperatorNotificationNetworkPolicy(isDevelopment: false),
                NullLogger<OperatorNotificationStore>.Instance);
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                new NoOpSmtpTransport(),
                new NoOpWebhookTransport(),
                telemetry,
                TimeProvider.System);

            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "owner-1"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
                ],
                IdentityConstants.ApplicationScheme));

            var uiService = new CameraAgentNotificationsUiService(
                new StaticAuthStateProvider(owner),
                new AllowAllAuthorizationService(),
                dispatcher,
                NullLogger<CameraAgentNotificationsUiService>.Instance);

            using var context = new BunitContext();
            context.Services.AddSingleton<ICameraAgentNotificationsUiService>(uiService);

            var cut = context.Render<NotificationsPage>();
            cut.WaitForAssertion(() =>
            {
                Assert.IsNotNull(cut.Find("#notifications-config-error-banner"));
                Assert.IsNotNull(cut.Find("#notifications-enabled"));
            });

            // Operator unchecks master enable and saves via the real store: invalid hidden settings must NOT trap save.
            await cut.Find("#notifications-enabled").ChangeAsync(new ChangeEventArgs { Value = false }).ConfigureAwait(false);
            await cut.Find("form.notifications-form").SubmitAsync().ConfigureAwait(false);

            cut.WaitForAssertion(() =>
            {
                var banner = cut.Find("#notifications-banner");
                Assert.AreEqual("Notification settings saved.", banner.TextContent.Trim());
                Assert.AreEqual("status", banner.GetAttribute("role"));
                Assert.IsEmpty(cut.FindAll("#notifications-config-error-banner"));
                Assert.AreEqual("Disabled", cut.Find("#notifications-master-state").TextContent.Trim());
            });

            // Verify persisted envelope in the real store normalized all invalid hidden fields to valid defaults while preserving valid RetryInitialDelaySeconds=200.
            Assert.IsNull(store.ConfigurationErrorReasonCode);
            var persisted = await store.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(persisted.HasSavedOverrides);
            Assert.IsFalse(persisted.Options.Enabled);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultQueueCapacity, persisted.Options.QueueCapacity);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultMaxRetryAttempts, persisted.Options.MaxRetryAttempts);
            Assert.AreEqual(200, persisted.Options.RetryInitialDelaySeconds);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultRetryMaximumDelaySeconds, persisted.Options.RetryMaximumDelaySeconds);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultOutageCooldownSeconds, persisted.Options.OutageCooldownSeconds);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultConsecutiveOutageThreshold, persisted.Options.ConsecutiveOutageThreshold);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultEvaluationIntervalSeconds, persisted.Options.EvaluationIntervalSeconds);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultCameraDisconnectedThresholdSeconds, persisted.Options.CameraDisconnectedThresholdSeconds);
            Assert.AreEqual(OperatorNotificationsOptions.DefaultTestSendMinimumIntervalSeconds, persisted.Options.TestSendMinimumIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Save_AgainstRealStoreChangingWebhookOriginAndSmtpHost_DropsHostSecretsAndUpdatesUiPresenceAsync()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"hvo-notif-ui-dest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(stateDir, "dp-keys")));
            var hostConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperatorNotificationsOptions.SectionKey + ":Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Enabled"] = "true",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Host"] = "relay-a.example.test",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Port"] = "587",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode"] = "StartTls",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:FromAddress"] = "camera@example.test",
                    [OperatorNotificationsOptions.SectionKey + ":Smtp:Recipients:0"] = "ops@example.test",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpUsernameConfigKey] = "host-smtp-user",
                    [ConfigurationOperatorNotificationConfigProvider.SmtpPasswordConfigKey] = "host-smtp-pass",
                    [OperatorNotificationsOptions.SectionKey + ":Webhook:Enabled"] = "true",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookUrlConfigKey] = "https://hooks-a.example.test/alerts",
                    [ConfigurationOperatorNotificationConfigProvider.WebhookAuthTokenConfigKey] = "host-webhook-token"
                })
                .Build();

            using var store = new OperatorNotificationStore(
                dpProvider,
                Options.Create(new DeviceProvisioningOptions { StateDirectory = stateDir }),
                hostConfig,
                new OperatorNotificationNetworkPolicy(isDevelopment: false),
                NullLogger<OperatorNotificationStore>.Instance);
            using var telemetry = new OperatorNotificationTelemetry();
            using var dispatcher = new OperatorNotificationDispatcher(
                store,
                new NoOpSmtpTransport(),
                new NoOpWebhookTransport(),
                telemetry,
                TimeProvider.System);

            var owner = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "owner-1"),
                    new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
                ],
                IdentityConstants.ApplicationScheme));

            var uiService = new CameraAgentNotificationsUiService(
                new StaticAuthStateProvider(owner),
                new AllowAllAuthorizationService(),
                dispatcher,
                NullLogger<CameraAgentNotificationsUiService>.Instance);

            using var context = new BunitContext();
            context.Services.AddSingleton<ICameraAgentNotificationsUiService>(uiService);

            var cut = context.Render<NotificationsPage>();
            cut.WaitForAssertion(() =>
            {
                StringAssert.Contains(cut.Find("#smtp-secret-status").TextContent, "Password: Host configuration", StringComparison.Ordinal);
                StringAssert.Contains(cut.Find("#webhook-secret-status").TextContent, "https://hooks-a.example.test (config)", StringComparison.Ordinal);
                StringAssert.Contains(cut.Find("#webhook-secret-status").TextContent, "Token: Host configuration", StringComparison.Ordinal);
            });

            // Change both SMTP host and webhook origin in the UI without entering new credentials.
            await cut.Find("#smtp-host").ChangeAsync(new ChangeEventArgs { Value = "relay-b.example.test" }).ConfigureAwait(false);
            await cut.Find("#webhook-url").ChangeAsync(new ChangeEventArgs { Value = "https://hooks-b.example.test/new-alerts" }).ConfigureAwait(false);
            await cut.Find("form.notifications-form").SubmitAsync().ConfigureAwait(false);

            cut.WaitForAssertion(() =>
            {
                Assert.AreEqual("Notification settings saved.", cut.Find("#notifications-banner").TextContent.Trim());
                StringAssert.Contains(cut.Find("#smtp-secret-status").TextContent, "Username: Not set", StringComparison.Ordinal);
                StringAssert.Contains(cut.Find("#smtp-secret-status").TextContent, "Password: Not set", StringComparison.Ordinal);
                StringAssert.Contains(cut.Find("#webhook-secret-status").TextContent, "https://hooks-b.example.test (protected)", StringComparison.Ordinal);
                StringAssert.Contains(cut.Find("#webhook-secret-status").TextContent, "Token: Not set", StringComparison.Ordinal);
            });

            var effective = await store.GetEffectiveConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("relay-b.example.test", effective.Options.Smtp.Host);
            Assert.IsNull(effective.Secrets.SmtpUsername);
            Assert.IsNull(effective.Secrets.SmtpPassword);
            Assert.AreEqual("https://hooks-b.example.test/new-alerts", effective.Secrets.WebhookUrl);
            Assert.IsNull(effective.Secrets.WebhookAuthToken);
        }
        finally
        {
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, recursive: true);
            }
        }
    }

    private sealed class StaticAuthStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class AllowAllAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
            => Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
            => Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class NoOpSmtpTransport : ISmtpNotificationTransport
    {
        public Task<OperatorNotificationDeliveryResult> SendAsync(SmtpDeliveryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(OperatorNotificationDeliveryResult.Success(SmtpNotificationTransport.ChannelName, TimeSpan.Zero));
    }

    private sealed class NoOpWebhookTransport : IWebhookNotificationTransport
    {
        public Task<OperatorNotificationDeliveryResult> SendAsync(WebhookDeliveryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(OperatorNotificationDeliveryResult.Success(WebhookNotificationTransport.ChannelName, TimeSpan.Zero));
    }

    private static BunitContext CreateContext(out FakeNotificationsUiService service)
    {
        var context = new BunitContext();
        service = new FakeNotificationsUiService();
        context.Services.AddSingleton<ICameraAgentNotificationsUiService>(service);
        return context;
    }

    private sealed class FakeNotificationsUiService : ICameraAgentNotificationsUiService
    {
        public NotificationsPageView CurrentView { get; set; } = CreateDefaultView();
        public OperatorNotificationsUpdateRequest? LastSaveRequest { get; private set; }
        public Func<ValueTask<OperatorUiResult<NotificationsPageView>>>? GetOverride { get; set; }
        public Func<NotificationTestTarget, ValueTask<OperatorUiResult<OperatorNotificationTestResult>>>? TestHandler { get; set; }

        public ValueTask<OperatorUiResult<NotificationsPageView>> GetAsync(CancellationToken cancellationToken)
            => GetOverride is not null
                ? GetOverride()
                : ValueTask.FromResult(OperatorUiResult<NotificationsPageView>.Success(CurrentView));

        public ValueTask<OperatorUiResult<NotificationsSaveUiView>> SaveAsync(
            OperatorNotificationsUpdateRequest request,
            CancellationToken cancellationToken)
        {
            LastSaveRequest = request;
            var host = OperatorNotificationRedactor.FormatEndpointHost(request.WebhookUrl);
            var presence = new OperatorNotificationSecretPresence(
                !string.IsNullOrEmpty(request.SmtpUsername), NotificationSecretSource.ProtectedStore,
                !string.IsNullOrEmpty(request.SmtpPassword), NotificationSecretSource.ProtectedStore,
                !string.IsNullOrEmpty(request.WebhookUrl), host, NotificationSecretSource.ProtectedStore,
                !string.IsNullOrEmpty(request.WebhookAuthToken), NotificationSecretSource.ProtectedStore,
                !string.IsNullOrEmpty(request.WebhookUserKey), NotificationSecretSource.ProtectedStore,
                !string.IsNullOrEmpty(request.WebhookNtfyTopic), NotificationSecretSource.ProtectedStore);
            var config = new OperatorNotificationsConfigurationSnapshot(request.Options, presence, "v2", true, true);
            var status = CurrentView.Status with
            {
                Enabled = request.Options.Enabled,
                Smtp = CurrentView.Status.Smtp with
                {
                    Enabled = request.Options.Enabled && request.Options.Smtp.Enabled,
                    State = request.Options.Enabled && request.Options.Smtp.Enabled
                        ? NotificationChannelHealthState.Ready
                        : NotificationChannelHealthState.Disabled
                },
                Webhook = CurrentView.Status.Webhook with
                {
                    Enabled = request.Options.Enabled && request.Options.Webhook.Enabled,
                    State = request.Options.Enabled && request.Options.Webhook.Enabled
                        ? NotificationChannelHealthState.Ready
                        : NotificationChannelHealthState.Disabled
                }
            };
            CurrentView = CurrentView with { Configuration = config, Status = status };
            return ValueTask.FromResult(OperatorUiResult<NotificationsSaveUiView>.Success(
                new NotificationsSaveUiView(config, status, "Notification settings saved.")));
        }

        public ValueTask<OperatorUiResult<OperatorNotificationTestResult>> SendTestAsync(
            NotificationTestTarget target,
            CancellationToken cancellationToken)
            => TestHandler is not null
                ? TestHandler(target)
                : ValueTask.FromResult(OperatorUiResult<OperatorNotificationTestResult>.Success(
                    new OperatorNotificationTestResult(false, null, [])));

        private static NotificationsPageView CreateDefaultView()
        {
            var opts = new OperatorNotificationsOptions();
            var config = new OperatorNotificationsConfigurationSnapshot(
                opts, OperatorNotificationSecretPresence.Empty, "v1", false, true);
            var status = new OperatorNotificationDispatcherSnapshot(
                false, 0, 32, 0, 0, 0, 0, 0,
                new OperatorNotificationChannelStatus("smtp", false, NotificationChannelHealthState.Disabled, 0, 0, 0, null, null, null, null, null),
                new OperatorNotificationChannelStatus("webhook", false, NotificationChannelHealthState.Disabled, 0, 0, 0, null, null, null, null, null),
                []);
            return new NotificationsPageView(config, status, CanMutate: true);
        }
    }
}
