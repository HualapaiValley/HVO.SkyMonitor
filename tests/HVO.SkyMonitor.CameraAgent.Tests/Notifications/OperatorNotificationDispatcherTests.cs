using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Operations)]
public sealed class OperatorNotificationDispatcherTests
{
    [TestMethod]
    public async Task DefaultOff_TryPublishReturnsFalseAndNeverCallsTransportsAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var config = new InMemoryConfigProvider(new OperatorNotificationsOptions { Enabled = false }, OperatorNotificationSecrets.Empty);
        var smtp = new FakeSmtpTransport();
        var webhook = new FakeWebhookTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, clock);

        var accepted = dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "stall", clock.GetUtcNow()));
        var processed = await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(accepted);
        Assert.AreEqual(0, processed);
        Assert.IsEmpty(smtp.Sent);
        Assert.IsEmpty(webhook.Sent);
        var status = dispatcher.GetStatus();
        Assert.IsFalse(status.Enabled);
        Assert.AreEqual(NotificationChannelHealthState.Disabled, status.Smtp.State);
        Assert.AreEqual(NotificationChannelHealthState.Disabled, status.Webhook.State);
    }

    [TestMethod]
    public async Task AntiStorm_Coalesces500DuplicatesEnforcesCooldownHourlyCapAndQueueBoundsAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            QueueCapacity = 4,
            MaxNotificationsPerHour = 6,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = false },
            Events = new OperatorNotificationEventsOptions
            {
                CaptureStalledCooldownMinutes = 30
            }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var smtp = new FakeSmtpTransport();
        var webhook = new FakeWebhookTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, clock);

        // 1. 500 identical faults coalesce into a single queued item -> 1 delivery.
        for (var i = 0; i < 500; i++)
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "capture_stalled", clock.GetUtcNow())));
        }
        Assert.AreEqual(1, dispatcher.GetStatus().QueueDepth);
        Assert.AreEqual(499, dispatcher.GetStatus().TotalCoalesced);

        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, smtp.Sent);
        StringAssert.Contains(smtp.Sent[0].Body, "Coalesced occurrences: 500", StringComparison.Ordinal);

        // 2. Inside 30-minute cooldown -> suppressed.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsFalse(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "capture_stalled", clock.GetUtcNow())));
        Assert.IsFalse(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "capture_stalled", clock.GetUtcNow())));
        Assert.AreEqual(2, dispatcher.GetStatus().TotalSuppressedCooldown);

        // 3. Past 30-minute cooldown -> delivers again with suppressed count reported.
        clock.Advance(TimeSpan.FromMinutes(25));
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "capture_stalled", clock.GetUtcNow())));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(2, smtp.Sent);
        StringAssert.Contains(smtp.Sent[1].Body, "Suppressed during cooldown: 2", StringComparison.Ordinal);

        // 4. Bounded queue capacity (QueueCapacity=4): 5th distinct event drops without blocking.
        for (var i = 0; i < 4; i++)
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, $"disk-{i}", clock.GetUtcNow())));
        }
        // Note: hourly cap is 6 (2 already sent + 4 queued = 6), so next hits hourly cap; let's test hourly cap first!
        Assert.IsFalse(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk-cap", clock.GetUtcNow())));
        Assert.AreEqual(1, dispatcher.GetStatus().TotalSuppressedRateLimit);

        // Raise hourly cap to test pure queue-full drop.
        config.UpdateOptions(new OperatorNotificationsOptions
        {
            Enabled = true,
            QueueCapacity = 4,
            MaxNotificationsPerHour = 20,
            Smtp = options.Smtp,
            Webhook = options.Webhook,
            Events = options.Events
        });
        Assert.IsFalse(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk-overflow", clock.GetUtcNow())));
        Assert.AreEqual(1, dispatcher.GetStatus().TotalDroppedQueueFull);
    }

    [TestMethod]
    public async Task ChannelOutageIsolation_HungOrFailingSmtpNeverBlocksCallerOrWebhookAndEntersCircuitBackoffAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxRetryAttempts = 2,
            RetryInitialDelaySeconds = 10,
            RetryMaximumDelaySeconds = 60,
            ConsecutiveOutageThreshold = 3,
            OutageCooldownSeconds = 300,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Generic
            }
        };
        var secrets = new OperatorNotificationSecrets(WebhookUrl: "https://webhook.example.test/ok");
        var config = new InMemoryConfigProvider(options, secrets);
        var smtp = new FakeSmtpTransport
        {
            Handler = (_, _) => Task.FromResult(OperatorNotificationDeliveryResult.Failure(
                SmtpNotificationTransport.ChannelName, NotificationReasonCodes.ConnectFailed, transient: true, TimeSpan.FromMilliseconds(5)))
        };
        var webhook = new FakeWebhookTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, clock);

        var sw = Stopwatch.StartNew();
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CameraDisconnected, "cam-1", clock.GetUtcNow())));
        sw.Stop();
        Assert.IsLessThan(200, sw.ElapsedMilliseconds, "TryPublish must remain O(1) non-blocking.");

        // Attempt 1: SMTP fails (1), Webhook succeeds (1) independently!
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, webhook.Sent);
        Assert.AreEqual(NotificationChannelHealthState.Ready, dispatcher.GetStatus().Webhook.State);
        Assert.AreEqual(NotificationChannelHealthState.Degraded, dispatcher.GetStatus().Smtp.State);

        // Advance past attempt-1 backoff (10s) -> SMTP fails (2), schedules retry in 20s.
        clock.Advance(TimeSpan.FromSeconds(11));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, dispatcher.GetStatus().Smtp.ConsecutiveFailures);

        // Advance past attempt-2 backoff (20s) -> SMTP fails (3) -> enters OutageBackoff (300s)!
        clock.Advance(TimeSpan.FromSeconds(21));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var status = dispatcher.GetStatus();
        Assert.AreEqual(NotificationChannelHealthState.OutageBackoff, status.Smtp.State);
        Assert.AreEqual(NotificationChannelHealthState.Ready, status.Webhook.State);
        Assert.AreEqual(0, status.QueueDepth);
        Assert.HasCount(3, smtp.Sent);

        // New event while SMTP is in outage backoff: SMTP is skipped (no socket call), Webhook still delivers!
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk-1", clock.GetUtcNow())));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(3, smtp.Sent, "SMTP in outage backoff must not hammer failing provider.");
        Assert.HasCount(2, webhook.Sent, "Webhook must keep delivering independently during SMTP outage.");
    }

    [TestMethod]
    public async Task SendTestAsync_EnforcesRateLimitAndReportsPerChannelOutcomesAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = false,
            TestSendMinimumIntervalSeconds = 10,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = true }
        };
        var config = new InMemoryConfigProvider(options, new OperatorNotificationSecrets(WebhookUrl: "https://webhook.example.test/test"));
        var smtp = new FakeSmtpTransport();
        var webhook = new FakeWebhookTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, clock);

        var first = await dispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(first.RateLimited);
        Assert.HasCount(2, first.Channels);
        Assert.IsTrue(first.Channels.All(static c => c.Attempted && c.Succeeded));

        var second = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(second.RateLimited);
        Assert.IsNotNull(second.RetryAfterUtc);

        clock.Advance(TimeSpan.FromSeconds(11));
        var third = await dispatcher.SendTestAsync(NotificationTestTarget.Smtp, "owner-1", CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(third.RateLimited);
        Assert.HasCount(1, third.Channels);
        Assert.AreEqual("smtp", third.Channels[0].Channel);
    }

    [TestMethod]
    public async Task NegativeSecretLeak_LogsTelemetryAndStatusNeverContainStoredCredentialsAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        const string smtpUser = "canary-smtp-user-8811";
        const string smtpPass = "canary-smtp-pass-9922";
        const string recipient = "canary-ops-recipient@example.test";
        const string webhookPathSecret = "canary-webhook-path-3344";
        const string webhookToken = "canary-webhook-bearer-5566";
        const string webhookUserKey = "canary-pushover-user-7788";

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxRetryAttempts = 0,
            ConsecutiveOutageThreshold = 1,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = [recipient]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = true,
                Format = WebhookPayloadFormat.Pushover
            }
        };
        var secrets = new OperatorNotificationSecrets(
            smtpUser,
            smtpPass,
            $"https://webhook.example.test/{webhookPathSecret}?key=canary-query-9900",
            webhookToken,
            webhookUserKey);

        var config = new InMemoryConfigProvider(options, secrets);
        var smtp = new FakeSmtpTransport
        {
            Handler = (_, _) => Task.FromResult(OperatorNotificationDeliveryResult.Failure(
                SmtpNotificationTransport.ChannelName, NotificationReasonCodes.AuthFailed, transient: false, TimeSpan.FromMilliseconds(8)))
        };
        var webhook = new FakeWebhookTransport();
        var logger = new CapturingLogger<OperatorNotificationDispatcher>();
        var observedTags = new List<string>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OperatorNotificationTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => RecordTags(tags, observedTags));
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) => RecordTags(tags, observedTags));
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OperatorNotificationTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                foreach (var tag in activity.TagObjects)
                {
                    observedTags.Add($"{tag.Key}={tag.Value}");
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, clock, logger);

        await dispatcher.UpdateConfigurationAsync(new OperatorNotificationsUpdateRequest("v1", options, smtpUser, false, smtpPass, false, secrets.WebhookUrl, false, webhookToken, false, webhookUserKey, false), "owner-1", CancellationToken.None).ConfigureAwait(false);
        dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk", clock.GetUtcNow()));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        await dispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);

        var statusText = dispatcher.GetStatus().ToString();
        var allObserved = string.Join("\n", logger.Messages.Concat(observedTags).Append(statusText));

        foreach (var canary in new[] { smtpUser, smtpPass, recipient, webhookPathSecret, "canary-query-9900", webhookToken, webhookUserKey })
        {
            Assert.DoesNotContain(canary, allObserved, StringComparison.Ordinal);
        }
    }

    private static void RecordTags(ReadOnlySpan<KeyValuePair<string, object?>> tags, List<string> target)
    {
        foreach (var tag in tags)
        {
            target.Add($"{tag.Key}={tag.Value}");
        }
    }

    private static OperatorNotificationEvent MakeEvent(OperatorNotificationEventKind kind, string key, DateTimeOffset utc) =>
        new(kind, OperatorNotificationSeverity.Warning, key, OperatorNotificationEvent.EventDisplayName(kind), "Summary text", utc, "North Camera");

    private sealed class ManualNotificationClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class InMemoryConfigProvider(OperatorNotificationsOptions options, OperatorNotificationSecrets secrets)
        : IOperatorNotificationConfigProvider
    {
        private OperatorNotificationsOptions _options = options;
        private OperatorNotificationSecrets _secrets = secrets;

        public OperatorNotificationsOptions CurrentOptions => _options;

        public void UpdateOptions(OperatorNotificationsOptions next) => _options = next;

        public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(_secrets, OperatorNotificationSecrets.Empty);
            return ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(_options, presence, "v1", true, true));
        }

        public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult((_options, _secrets));

        public ValueTask<OperatorNotificationsSaveResult> SaveAsync(OperatorNotificationsUpdateRequest request, string actor, CancellationToken cancellationToken)
        {
            _options = request.Options;
            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(_secrets, OperatorNotificationSecrets.Empty);
            var snapshot = new OperatorNotificationsConfigurationSnapshot(_options, presence, "v2", true, true);
            return ValueTask.FromResult(new OperatorNotificationsSaveResult(OperatorNotificationsSaveOutcome.Applied, snapshot, "Saved"));
        }
    }

    private sealed class FakeSmtpTransport : ISmtpNotificationTransport
    {
        public List<SmtpDeliveryRequest> Sent { get; } = [];
        public Func<SmtpDeliveryRequest, CancellationToken, Task<OperatorNotificationDeliveryResult>>? Handler { get; init; }

        public Task<OperatorNotificationDeliveryResult> SendAsync(SmtpDeliveryRequest request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Handler is not null
                ? Handler(request, cancellationToken)
                : Task.FromResult(OperatorNotificationDeliveryResult.Success(SmtpNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(5)));
        }
    }

    private sealed class FakeWebhookTransport : IWebhookNotificationTransport
    {
        public List<WebhookDeliveryRequest> Sent { get; } = [];
        public Func<WebhookDeliveryRequest, CancellationToken, Task<OperatorNotificationDeliveryResult>>? Handler { get; init; }

        public Task<OperatorNotificationDeliveryResult> SendAsync(WebhookDeliveryRequest request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Handler is not null
                ? Handler(request, cancellationToken)
                : Task.FromResult(OperatorNotificationDeliveryResult.Success(WebhookNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(5)));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
            {
                foreach (var kv in kvs)
                {
                    Messages.Add($"{kv.Key}={kv.Value}");
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
