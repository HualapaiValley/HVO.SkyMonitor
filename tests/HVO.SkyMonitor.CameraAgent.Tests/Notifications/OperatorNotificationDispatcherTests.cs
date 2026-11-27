using System.Collections.Concurrent;
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

        // 4. Bounded queue capacity (QueueCapacity=4): 4 distinct events fill queue and hit hourly cap (2 + 4 = 6).
        for (var i = 0; i < 4; i++)
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, $"disk-{i}", clock.GetUtcNow())));
        }
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
    public async Task IndependentChannelWorkers_HungSmtpNeverDelaysWebhookDeliveryAsync()
    {
        var smtpEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSmtp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var webhookDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
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
        var config = new InMemoryConfigProvider(options, new OperatorNotificationSecrets(WebhookUrl: "https://webhook.example.test/ok"));
        var smtp = new FakeSmtpTransport
        {
            Handler = async (_, ct) =>
            {
                smtpEntered.TrySetResult();
                await releaseSmtp.Task.WaitAsync(ct).ConfigureAwait(false);
                return OperatorNotificationDeliveryResult.Success(SmtpNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(50));
            }
        };
        var webhook = new FakeWebhookTransport
        {
            Handler = (_, _) =>
            {
                webhookDelivered.TrySetResult();
                return Task.FromResult(OperatorNotificationDeliveryResult.Success(WebhookNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(5)));
            }
        };

        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, TimeProvider.System);
        await dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk-hung-smtp", DateTimeOffset.UtcNow)));

            await smtpEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // Webhook MUST complete while SMTP is still held open!
            await webhookDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(releaseSmtp.Task.IsCompleted);
            Assert.HasCount(1, webhook.Sent);
        }
        finally
        {
            releaseSmtp.TrySetResult();
            await dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task FailedDeliveries_CommitAccountingOnAdmissionSoRepeatedFailuresCannotEscapeHourlyCapOrCooldownAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxNotificationsPerHour = 1,
            MaxRetryAttempts = 0,
            ConsecutiveOutageThreshold = 5,
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
            },
            Events = new OperatorNotificationEventsOptions
            {
                DiskPressureCooldownMinutes = 30
            }
        };
        var config = new InMemoryConfigProvider(options, new OperatorNotificationSecrets(WebhookUrl: "https://webhook.example.test/fail"));
        var smtp = new FakeSmtpTransport
        {
            Handler = (_, _) => Task.FromResult(OperatorNotificationDeliveryResult.Failure(
                SmtpNotificationTransport.ChannelName, NotificationReasonCodes.ConnectFailed, transient: false, TimeSpan.FromMilliseconds(5)))
        };
        var webhook = new FakeWebhookTransport
        {
            Handler = (_, _) => Task.FromResult(OperatorNotificationDeliveryResult.Failure(
                WebhookNotificationTransport.ChannelName, NotificationReasonCodes.ServerError, transient: false, TimeSpan.FromMilliseconds(5)))
        };
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, webhook, telemetry, clock);

        // First fault admitted and BOTH channels fail terminally.
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk-1", clock.GetUtcNow())));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, smtp.Sent);
        Assert.HasCount(1, webhook.Sent);

        // Second identical fault MUST be suppressed by cooldown despite prior failure!
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsFalse(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk-1", clock.GetUtcNow())));
        Assert.AreEqual(1, dispatcher.GetStatus().TotalSuppressedCooldown);

        // Second distinct fault within same hour MUST be suppressed by MaxNotificationsPerHour=1 despite prior failure!
        Assert.IsFalse(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "stall-1", clock.GetUtcNow())));
        Assert.AreEqual(1, dispatcher.GetStatus().TotalSuppressedRateLimit);
    }

    [TestMethod]
    public async Task DisabledEventCategoryDuringRetryBackoff_RetiresCleanlyWithoutRetryingAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxRetryAttempts = 3,
            RetryInitialDelaySeconds = 10,
            RetryMaximumDelaySeconds = 60,
            ConsecutiveOutageThreshold = 5,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Events = new OperatorNotificationEventsOptions { CaptureStalledEnabled = true }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var smtp = new FakeSmtpTransport
        {
            Handler = (_, _) => Task.FromResult(OperatorNotificationDeliveryResult.Failure(
                SmtpNotificationTransport.ChannelName, NotificationReasonCodes.ServerError, transient: true, TimeSpan.FromMilliseconds(5)))
        };
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, new FakeWebhookTransport(), telemetry, clock);

        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "stall-revoke", clock.GetUtcNow())));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, smtp.Sent);
        Assert.AreEqual(1, dispatcher.GetStatus().QueueDepth);

        // Disable CaptureStalled while item is waiting in retry backoff.
        config.UpdateOptions(new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxRetryAttempts = 3,
            RetryInitialDelaySeconds = 10,
            RetryMaximumDelaySeconds = 60,
            ConsecutiveOutageThreshold = 5,
            Smtp = options.Smtp,
            Events = new OperatorNotificationEventsOptions { CaptureStalledEnabled = false }
        });

        clock.Advance(TimeSpan.FromSeconds(15));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(1, smtp.Sent, "Revoked event category must NOT be retried after being disabled.");
        Assert.AreEqual(0, dispatcher.GetStatus().QueueDepth);
    }

    [TestMethod]
    public async Task CoalescedRepeatsDuringInFlightDelivery_ArePreservedInsteadOfDroppedAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var smtpEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSmtp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Events = new OperatorNotificationEventsOptions { CaptureStalledCooldownMinutes = 10 }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var firstCall = true;
        var smtp = new FakeSmtpTransport
        {
            Handler = async (_, ct) =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    smtpEntered.TrySetResult();
                    await releaseSmtp.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                return OperatorNotificationDeliveryResult.Success(SmtpNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(5));
            }
        };
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, new FakeWebhookTransport(), telemetry, clock);

        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "inflight-key", clock.GetUtcNow())));
        var dispatchTask = dispatcher.ProcessPendingOnceAsync(CancellationToken.None);
        await smtpEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Publish 3 identical repeats WHILE the first attempt is in flight.
        for (var i = 0; i < 3; i++)
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "inflight-key", clock.GetUtcNow())));
        }

        releaseSmtp.TrySetResult();
        await dispatchTask.ConfigureAwait(false);
        Assert.HasCount(1, smtp.Sent);

        // Advance past cooldown and publish again -> the 3 in-flight repeats were preserved as suppressed occurrences!
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "inflight-key", clock.GetUtcNow())));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, smtp.Sent);
        StringAssert.Contains(smtp.Sent[1].Body, "Suppressed during cooldown: 3", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CoalescedRepeatsDuringTerminallyFailedInFlightDelivery_ArePreservedInNextNotificationAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var smtpEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSmtp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxRetryAttempts = 0,
            ConsecutiveOutageThreshold = 5,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Events = new OperatorNotificationEventsOptions { CaptureStalledCooldownMinutes = 10 }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var attempt = 0;
        var smtp = new FakeSmtpTransport
        {
            Handler = async (_, ct) =>
            {
                if (++attempt == 1)
                {
                    smtpEntered.TrySetResult();
                    await releaseSmtp.Task.WaitAsync(ct).ConfigureAwait(false);
                    return OperatorNotificationDeliveryResult.Failure(
                        SmtpNotificationTransport.ChannelName,
                        NotificationReasonCodes.ServerError,
                        transient: false,
                        TimeSpan.FromMilliseconds(5));
                }
                return OperatorNotificationDeliveryResult.Success(SmtpNotificationTransport.ChannelName, TimeSpan.FromMilliseconds(5));
            }
        };
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, new FakeWebhookTransport(), telemetry, clock);

        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "inflight-fail-key", clock.GetUtcNow())));
        var dispatchTask = dispatcher.ProcessPendingOnceAsync(CancellationToken.None);
        await smtpEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Accept 3 identical repeats while the first SMTP send is held open before failing terminally.
        for (var i = 0; i < 3; i++)
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "inflight-fail-key", clock.GetUtcNow())));
        }

        releaseSmtp.TrySetResult();
        await dispatchTask.ConfigureAwait(false);
        Assert.HasCount(1, smtp.Sent);

        // Advance past cooldown and send next notification -> all 3 repeats accepted during the failed send MUST be carried forward!
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.CaptureStalled, "inflight-fail-key", clock.GetUtcNow())));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(2, smtp.Sent);
        StringAssert.Contains(smtp.Sent[1].Body, "Suppressed during cooldown: 3", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ExpiredCooldownEntries_AreEvictedOnBackgroundWorkerAndTryPublishInvokesLoggerOutsideLockAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            MaxNotificationsPerHour = 60,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Events = new OperatorNotificationEventsOptions { DiskPressureCooldownMinutes = 5 }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var smtp = new FakeSmtpTransport();
        var separateThreadProbes = 0;
        var observedLockHeldFromOtherThread = 0;
        OperatorNotificationDispatcher? dispatcherRef = null;
        var probeLogger = new CallbackLogger<OperatorNotificationDispatcher>(() =>
        {
            if (dispatcherRef is not null)
            {
                // Probe _gate from a SEPARATE thread (not re-entrant on the caller thread!) so a mutant
                // moving logging inside _gate immediately sees lock contention and fails the test.
                var unlockedOnOtherThread = Task.Run(() => dispatcherRef.TryProbeGateUnlockedForTesting())
                    .GetAwaiter().GetResult();
                if (!unlockedOnOtherThread)
                {
                    Interlocked.Increment(ref observedLockHeldFromOtherThread);
                }
                Interlocked.Increment(ref separateThreadProbes);
            }
        });

        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, new FakeWebhookTransport(), telemetry, clock, probeLogger);
        dispatcherRef = dispatcher;

        for (var i = 0; i < 5; i++)
        {
            Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, $"evict-{i}", clock.GetUtcNow())));
        }
        Assert.AreEqual(5, dispatcher.TrackedCooldownCount);
        Assert.IsGreaterThanOrEqualTo(5, separateThreadProbes);
        Assert.AreEqual(0, observedLockHeldFromOtherThread, "TryPublish must never invoke logging while holding the dispatcher lock.");

        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);

        // Advance past cooldown window: TryPublish itself does NOT scan/prune cooldowns; background worker pass evicts them.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "evict-new", clock.GetUtcNow())));
        Assert.AreEqual(6, dispatcher.TrackedCooldownCount, "Cooldown pruning must stay off TryPublish.");

        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, dispatcher.TrackedCooldownCount, "Expired cooldown entries must be evicted on the background worker.");
    }

    [TestMethod]
    public void TryPublish_Under300msContention_ReturnsImmediatelyWithoutWaitingAndAccountsContentionSeparately()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var logger = new CapturingLogger<OperatorNotificationDispatcher>();
        var droppedReasons = new ConcurrentBag<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OperatorNotificationTelemetry.MeterName &&
                instrument.Name == "camera_agent.notifications.dropped")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    droppedReasons.Add(reason);
                }
            }
        });
        meterListener.Start();

        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, new FakeSmtpTransport(), new FakeWebhookTransport(), telemetry, clock, logger);

        using var hold = dispatcher.HoldGateForTesting();
        var holdRelease = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            hold.Dispose();
        });

        var sw = Stopwatch.StartNew();
        var accepted = dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "contended-key", clock.GetUtcNow()));
        sw.Stop();

        Assert.IsFalse(accepted, "Contended TryPublish must drop without waiting on the dispatcher gate.");
        Assert.IsLessThan(150, sw.ElapsedMilliseconds, $"TryPublish blocked for {sw.ElapsedMilliseconds}ms instead of returning immediately.");

        holdRelease.GetAwaiter().GetResult();
        var status = dispatcher.GetStatus();
        Assert.AreEqual(0, status.TotalDroppedQueueFull,
            "Lock contention must NOT increment TotalDroppedQueueFull.");
        Assert.AreEqual(1, status.TotalDroppedContended,
            "Lock contention must increment TotalDroppedContended.");
        Assert.IsTrue(droppedReasons.Contains("admission-contended"),
            "Dropped metric must tag contention with reason=admission-contended.");
        Assert.IsTrue(logger.Entries.Any(static e =>
                e.Level == LogLevel.Debug &&
                e.EventId.Id == 16213 &&
                e.Message.Contains("admission-contended", StringComparison.Ordinal)),
            "Lock contention must log at Debug with admission-contended.");
        Assert.IsFalse(logger.Entries.Any(static e =>
                e.EventId.Id == 16203 ||
                e.Message.Contains("bounded queue is full", StringComparison.OrdinalIgnoreCase)),
            "Lock contention must NOT emit a queue-full warning.");
        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "after-hold-key", clock.GetUtcNow())));
    }

    [TestMethod]
    public async Task Cooldown24HourWindow_1025DistinctKeysAt61SecondIntervals_NeverEvictsUnexpiredFirstKeyEarlyAsync()
    {
        var clock = new ManualNotificationClock(new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            QueueCapacity = 256,
            MaxNotificationsPerHour = 60,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Events = new OperatorNotificationEventsOptions
            {
                DiskPressureCooldownMinutes = 1440 // 24 hours
            }
        };
        var config = new InMemoryConfigProvider(options, OperatorNotificationSecrets.Empty);
        var smtp = new FakeSmtpTransport();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(config, smtp, new FakeWebhookTransport(), telemetry, clock);

        // Admit 1,025 distinct keys at 61-second intervals (total elapsed: 1024 * 61s = ~17.35 hours).
        for (var i = 0; i < 1025; i++)
        {
            if (i > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(61));
            }
            Assert.IsTrue(
                dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, $"key-{i}", clock.GetUtcNow())),
                $"Expected distinct key-{i} to be admitted at {clock.GetUtcNow():O}.");
            if ((i + 1) % 100 == 0)
            {
                await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1025, dispatcher.TrackedCooldownCount);

        // At ~17.37h, key-0 is still well inside its 24-hour cooldown and MUST stay suppressed.
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.IsFalse(
            dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "key-0", clock.GetUtcNow())),
            "key-0 must remain suppressed at 17.37h under a 24-hour cooldown.");

        // At 23h 59m from t=0, key-0 MUST STILL be suppressed.
        var almost24h = new DateTimeOffset(2026, 11, 26, 23, 59, 0, TimeSpan.Zero);
        clock.Advance(almost24h - clock.GetUtcNow());
        Assert.IsFalse(
            dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "key-0", clock.GetUtcNow())),
            "key-0 must remain suppressed at 23h 59m under a 24-hour cooldown.");

        // Past the full 24-hour window, key-0 is re-admitted with both suppressed attempts preserved.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsTrue(
            dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "key-0", clock.GetUtcNow())),
            "key-0 should be re-admitted once the full 24-hour cooldown expires.");
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        StringAssert.Contains(smtp.Sent[^1].Body, "Suppressed during cooldown: 2", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WorkerCycleFailure_UsesExponentialBackoffAndUnreadableStoreRetiresWithoutTight5SecondLoopsAsync()
    {
        // 1. Verify exponential backoff progression (5s -> 10s -> 20s -> 40s -> 80s -> 160s -> 300s cap).
        Assert.AreEqual(TimeSpan.FromSeconds(5), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(1));
        Assert.AreEqual(TimeSpan.FromSeconds(10), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(2));
        Assert.AreEqual(TimeSpan.FromSeconds(20), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(3));
        Assert.AreEqual(TimeSpan.FromSeconds(40), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(4));
        Assert.AreEqual(TimeSpan.FromSeconds(80), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(5));
        Assert.AreEqual(TimeSpan.FromSeconds(160), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(6));
        Assert.AreEqual(TimeSpan.FromSeconds(300), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(7));
        Assert.AreEqual(TimeSpan.FromSeconds(300), OperatorNotificationDispatcher.ComputeWorkerCycleFailureBackoff(12));

        // 2. When a config provider throws UnauthorizedAccessException repeatedly, the background worker backs off
        //    instead of logging a failure every 5 seconds.
        var clock = new ManualTimerClock(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var options = new OperatorNotificationsOptions
        {
            Enabled = true,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "camera@example.test",
                Recipients = ["ops@example.test"]
            },
            Webhook = new WebhookNotificationOptions { Enabled = false }
        };
        var failingConfig = new ThrowingEffectiveConfigProvider(options);
        var logger = new CapturingLogger<OperatorNotificationDispatcher>();
        using var telemetry = new OperatorNotificationTelemetry();
        using var dispatcher = new OperatorNotificationDispatcher(
            failingConfig, new FakeSmtpTransport(), new FakeWebhookTransport(), telemetry, clock, logger);

        Assert.IsTrue(dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "unreadable-loop", clock.GetUtcNow())));
        await dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Wait for first failure -> 5s delay scheduled.
            await WaitUntilAsync(() => failingConfig.EffectiveCalls >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => clock.PendingDelayCount >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            clock.AdvanceAndFire(TimeSpan.FromSeconds(5));

            // Second failure -> 10s backoff (advancing 5s MUST NOT trigger a 3rd call!).
            await WaitUntilAsync(() => failingConfig.EffectiveCalls >= 2, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => clock.PendingDelayCount >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            clock.AdvanceAndFire(TimeSpan.FromSeconds(5));
            await Task.Delay(50).ConfigureAwait(false);
            Assert.AreEqual(2, failingConfig.EffectiveCalls, "Second failure must back off to 10s rather than retrying every 5s.");

            // Advancing the remaining 5s (total 10s) fires attempt 3, which backs off to 20s!
            clock.AdvanceAndFire(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => failingConfig.EffectiveCalls >= 3, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await WaitUntilAsync(() => clock.PendingDelayCount >= 1, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            clock.AdvanceAndFire(TimeSpan.FromSeconds(15));
            await Task.Delay(50).ConfigureAwait(false);
            Assert.AreEqual(3, failingConfig.EffectiveCalls, "Third failure must back off to 20s rather than retrying every 5s.");
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // 3. When the store reports StoreUnreadable cleanly, the queued item retires once with store-unreadable and zero cycle errors.
        var unreadableConfig = new UnreadableEffectiveConfigProvider(options);
        var cleanLogger = new CapturingLogger<OperatorNotificationDispatcher>();
        using var cleanTelemetry = new OperatorNotificationTelemetry();
        using var cleanDispatcher = new OperatorNotificationDispatcher(
            unreadableConfig, new FakeSmtpTransport(), new FakeWebhookTransport(), cleanTelemetry, TimeProvider.System, cleanLogger);
        Assert.IsTrue(cleanDispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "store-unreadable-retire", DateTimeOffset.UtcNow)));
        await cleanDispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);

        var status = cleanDispatcher.GetStatus();
        Assert.AreEqual(0, status.QueueDepth);
        Assert.AreEqual(NotificationReasonCodes.StoreUnreadable, status.RecentDeliveries[0].ReasonCode);
        Assert.IsFalse(cleanLogger.Messages.Any(static m => m.Contains("dispatcher cycle failed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SendTestAsync_EnforcesRateLimitRejectsUndefinedTargetAndReportsPerChannelOutcomesAsync()
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

        // Undefined target throws before consuming rate-limit slot.
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await dispatcher.SendTestAsync((NotificationTestTarget)99, "owner-1", CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

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
        const string webhookNtfyTopic = "canary-ntfy-topic-6677";

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
                Format = WebhookPayloadFormat.Ntfy
            }
        };
        var secrets = new OperatorNotificationSecrets(
            smtpUser,
            smtpPass,
            $"https://webhook.example.test/{webhookPathSecret}?key=canary-query-9900",
            webhookToken,
            webhookUserKey,
            webhookNtfyTopic);

        var config = new InMemoryConfigProvider(options, secrets);
        var smtp = new FakeSmtpTransport
        {
            Handler = (_, _) => Task.FromResult(OperatorNotificationDeliveryResult.Failure(
                SmtpNotificationTransport.ChannelName, NotificationReasonCodes.AuthFailed, transient: false, TimeSpan.FromMilliseconds(8)))
        };
        var webhook = new FakeWebhookTransport();
        var logger = new CapturingLogger<OperatorNotificationDispatcher>();
        var observedTags = new ConcurrentBag<string>();

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

        await dispatcher.UpdateConfigurationAsync(new OperatorNotificationsUpdateRequest("v1", options, smtpUser, false, smtpPass, false, secrets.WebhookUrl, false, webhookToken, false, webhookUserKey, false, webhookNtfyTopic, false), "owner-1", CancellationToken.None).ConfigureAwait(false);
        dispatcher.TryPublish(MakeEvent(OperatorNotificationEventKind.DiskPressure, "disk", clock.GetUtcNow()));
        await dispatcher.ProcessPendingOnceAsync(CancellationToken.None).ConfigureAwait(false);
        await dispatcher.SendTestAsync(NotificationTestTarget.AllEnabled, "owner-1", CancellationToken.None).ConfigureAwait(false);

        var statusText = dispatcher.GetStatus().ToString();
        var allObserved = string.Join("\n", logger.Messages.Concat(observedTags.ToArray()).Append(statusText));

        foreach (var canary in new[] { smtpUser, smtpPass, recipient, webhookPathSecret, "canary-query-9900", webhookToken, webhookUserKey, webhookNtfyTopic })
        {
            Assert.DoesNotContain(canary, allObserved, StringComparison.Ordinal);
        }
    }

    private static void RecordTags(ReadOnlySpan<KeyValuePair<string, object?>> tags, ConcurrentBag<string> target)
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

        private ProtectedSecretBindings BuildBindings() => new(
            WebhookOrigin: ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(_secrets.WebhookUrl),
            SmtpEndpoint: ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(_options.Smtp),
            WebhookDestination: ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(_secrets.WebhookUrl));

        public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
                _secrets, OperatorNotificationSecrets.Empty, _options, null, BuildBindings(), _options);
            return ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(_options, presence, "v1", true, true));
        }

        public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult((_options, _secrets));

        public ValueTask<OperatorNotificationsSaveResult> SaveAsync(OperatorNotificationsUpdateRequest request, string actor, CancellationToken cancellationToken)
        {
            _options = request.Options;
            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
                _secrets, OperatorNotificationSecrets.Empty, _options, null, BuildBindings(), _options);
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

    private sealed class CallbackLogger<T>(Action onLog) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => onLog();

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly ConcurrentQueue<(LogLevel Level, EventId EventId, string Message)> _entries = new();
        public IReadOnlyList<string> Messages => [.. _messages];
        public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries => [.. _entries];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _entries.Enqueue((logLevel, eventId, message));
            _messages.Enqueue(message);
            if (state is IEnumerable<KeyValuePair<string, object?>> kvs)
            {
                foreach (var kv in kvs)
                {
                    _messages.Enqueue($"{kv.Key}={kv.Value}");
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Condition not met within {timeout.TotalSeconds:0.0}s.");
            }
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    private sealed class ThrowingEffectiveConfigProvider(OperatorNotificationsOptions options) : IOperatorNotificationConfigProvider
    {
        private int _effectiveCalls;
        public int EffectiveCalls => Volatile.Read(ref _effectiveCalls);
        public OperatorNotificationsOptions CurrentOptions { get; } = options;
        public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(CurrentOptions, OperatorNotificationSecretPresence.Empty, "v1", true, true));
        public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _effectiveCalls);
            throw new UnauthorizedAccessException("Simulated runtime 0000 store error.");
        }
        public ValueTask<OperatorNotificationsSaveResult> SaveAsync(OperatorNotificationsUpdateRequest request, string actor, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OperatorNotificationsSaveResult(OperatorNotificationsSaveOutcome.Unavailable, null));
    }

    private sealed class UnreadableEffectiveConfigProvider(OperatorNotificationsOptions options) : IOperatorNotificationConfigProvider
    {
        public OperatorNotificationsOptions CurrentOptions { get; } = options;
        public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecretPresence.Empty,
                "unreadable:test",
                true,
                true,
                StoreCorrupt: true,
                StoreStatusReasonCode: NotificationReasonCodes.StoreUnreadable));
        public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult((new OperatorNotificationsOptions { Enabled = false }, OperatorNotificationSecrets.Empty));
        public ValueTask<OperatorNotificationEffectiveConfiguration> GetEffectiveConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new OperatorNotificationEffectiveConfiguration(
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecrets.Empty,
                StoreCorrupt: true,
                ConfigurationErrorReasonCode: NotificationReasonCodes.StoreUnreadable));
        public ValueTask<OperatorNotificationsSaveResult> SaveAsync(OperatorNotificationsUpdateRequest request, string actor, CancellationToken cancellationToken)
            => ValueTask.FromResult(new OperatorNotificationsSaveResult(OperatorNotificationsSaveOutcome.Unavailable, null));
    }

    private sealed class ManualTimerClock(DateTimeOffset startUtc) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = startUtc;

        public int PendingDelayCount { get { lock (_gate) return _timers.Count(static t => t.DueUtc is not null); } }

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        public void AdvanceAndFire(TimeSpan delta)
        {
            List<(TimerCallback Callback, object? State)> due = [];
            lock (_gate)
            {
                _utcNow += delta;
                foreach (var timer in _timers)
                {
                    if (timer.DueUtc is { } dueAt && dueAt <= _utcNow)
                    {
                        timer.DueUtc = null;
                        due.Add((timer.Callback, timer.State));
                    }
                }
            }
            foreach (var (callback, state) in due)
            {
                callback(state);
            }
        }

        private sealed class ManualTimer(ManualTimerClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset? DueUtc { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    DueUtc = dueTime == Timeout.InfiniteTimeSpan ? null : owner._utcNow + dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    DueUtc = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
