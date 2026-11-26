using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

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
