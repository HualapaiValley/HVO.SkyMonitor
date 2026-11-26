using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Bounded, non-blocking, anti-storm operator notification dispatcher. Runs strictly off the capture, processing,
/// retention, login, and recovery critical paths. Provides in-queue coalescing, per-event cooldowns, global hourly
/// rate capping, independent per-channel retry/backoff and circuit-breaker outage isolation, and rate-limited
/// operator test-send verification.
/// </summary>
public sealed class OperatorNotificationDispatcher(
    IOperatorNotificationConfigProvider configProvider,
    ISmtpNotificationTransport smtpTransport,
    IWebhookNotificationTransport webhookTransport,
    OperatorNotificationTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<OperatorNotificationDispatcher>? logger = null)
    : BackgroundService, IOperatorNotificationPublisher, IOperatorNotificationService
{
    private const int MaximumAuditEntries = 20;
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(5);

    private readonly IOperatorNotificationConfigProvider _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    private readonly ISmtpNotificationTransport _smtpTransport = smtpTransport ?? throw new ArgumentNullException(nameof(smtpTransport));
    private readonly IWebhookNotificationTransport _webhookTransport = webhookTransport ?? throw new ArgumentNullException(nameof(webhookTransport));
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The DI container owns this singleton telemetry instance.")]
    private readonly OperatorNotificationTelemetry _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = logger ?? NullLogger<OperatorNotificationDispatcher>.Instance;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _testSendGate = new(1, 1);
    private readonly List<QueuedNotification> _queue = [];
    private readonly Dictionary<EventDedupeKey, CooldownRecord> _cooldowns = [];
    private readonly Queue<DateTimeOffset> _hourlyDispatchTimestamps = new();
    private readonly Queue<OperatorNotificationAuditEntry> _recentAudit = new();
    private readonly ChannelCircuitState _smtpState = new(SmtpNotificationTransport.ChannelName);
    private readonly ChannelCircuitState _webhookState = new(WebhookNotificationTransport.ChannelName);

    private long _totalPublished;
    private long _totalCoalesced;
    private long _totalSuppressedCooldown;
    private long _totalSuppressedRateLimit;
    private long _totalDroppedQueueFull;
    private DateTimeOffset? _lastTestSendUtc;

    /// <summary>
    /// Enqueues or coalesces an operational health event without blocking or performing I/O.
    /// Returns false when notifications or the event are disabled, suppressed by cooldown/rate-limit, or dropped.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "TryPublish must never throw into callers on capture, processing, retention, or recovery paths.")]
    public bool TryPublish(OperatorNotificationEvent notification)
    {
        if (notification is null)
        {
            return false;
        }

        try
        {
            var options = _configProvider.CurrentOptions;
            if (!options.Enabled || (!options.Smtp.Enabled && !options.Webhook.Enabled))
            {
                return false;
            }
            if (!options.Events.IsEventEnabled(notification.Kind))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            var slug = OperatorNotificationEvent.EventSlug(notification.Kind);
            var dedupeKeyText = OperatorNotificationRedactor.SanitizeOperationalText(
                string.IsNullOrWhiteSpace(notification.DedupeKey) ? slug : notification.DedupeKey,
                OperatorNotificationEvent.MaximumDedupeKeyLength);
            var key = new EventDedupeKey(notification.Kind, dedupeKeyText);
            var sanitizedEvent = SanitizeEvent(notification, dedupeKeyText);

            var shouldSignal = false;
            lock (_gate)
            {
                // 1. Coalesce in-place when an identical event key is already queued/retrying.
                var existing = _queue.FirstOrDefault(item => item.Key == key);
                if (existing is not null)
                {
                    existing.Event = sanitizedEvent;
                    existing.CoalescedCount++;
                    existing.LastOccurredUtc = now;
                    _totalCoalesced++;
                    _telemetry.RecordCoalesced(slug);
                    OperatorNotificationLog.EventCoalesced(_logger, slug, existing.CoalescedCount);
                    return true;
                }

                // 2. Enforce per-event cooldown window.
                var cooldown = options.Events.GetCooldown(notification.Kind);
                if (_cooldowns.TryGetValue(key, out var cooldownRecord) &&
                    now - cooldownRecord.LastDispatchedUtc < cooldown)
                {
                    cooldownRecord.SuppressedSinceLastDispatch++;
                    _totalSuppressedCooldown++;
                    _telemetry.RecordSuppressed(slug, "cooldown");
                    OperatorNotificationLog.EventSuppressed(_logger, slug, "cooldown");
                    return false;
                }

                // 3. Enforce global rolling-hour anti-storm cap.
                PruneHourlyWindowLocked(now);
                if (_hourlyDispatchTimestamps.Count + _queue.Count >= options.MaxNotificationsPerHour)
                {
                    _totalSuppressedRateLimit++;
                    _telemetry.RecordSuppressed(slug, "hourly-cap");
                    OperatorNotificationLog.EventSuppressed(_logger, slug, "hourly-cap");
                    return false;
                }

                // 4. Enforce bounded in-memory queue capacity without blocking callers.
                if (_queue.Count >= options.QueueCapacity)
                {
                    _totalDroppedQueueFull++;
                    _telemetry.RecordDropped(slug);
                    OperatorNotificationLog.EventDropped(_logger, slug, options.QueueCapacity);
                    return false;
                }

                var priorSuppressed = cooldownRecord?.SuppressedSinceLastDispatch ?? 0;
                if (cooldownRecord is not null)
                {
                    cooldownRecord.SuppressedSinceLastDispatch = 0;
                }

                _queue.Add(new QueuedNotification(
                    key,
                    sanitizedEvent,
                    now,
                    options.Smtp.Enabled,
                    options.Webhook.Enabled,
                    priorSuppressed));
                _totalPublished++;
                _telemetry.RecordPublished(slug);
                OperatorNotificationLog.EventQueued(_logger, slug, _queue.Count);
                shouldSignal = true;
            }

            if (shouldSignal)
            {
                TryWakeWorker();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public OperatorNotificationDispatcherSnapshot GetStatus()
    {
        var options = _configProvider.CurrentOptions;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            return new OperatorNotificationDispatcherSnapshot(
                options.Enabled,
                _queue.Count,
                options.QueueCapacity,
                _totalPublished,
                _totalCoalesced,
                _totalSuppressedCooldown,
                _totalSuppressedRateLimit,
                _totalDroppedQueueFull,
                _smtpState.ToStatus(options.Enabled && options.Smtp.Enabled, now),
                _webhookState.ToStatus(options.Enabled && options.Webhook.Enabled, now),
                [.. _recentAudit.Reverse()]);
        }
    }

    public ValueTask<OperatorNotificationsConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken)
        => _configProvider.GetSnapshotAsync(cancellationToken);

    public async ValueTask<OperatorNotificationsSaveResult> UpdateConfigurationAsync(
        OperatorNotificationsUpdateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var result = await _configProvider.SaveAsync(request, actor, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == OperatorNotificationsSaveOutcome.Applied && result.Snapshot is not null)
        {
            var opts = result.Snapshot.Options;
            lock (_gate)
            {
                // Reconfiguring resets outage circuits so newly corrected settings take effect immediately.
                _smtpState.ResetBackoff();
                _webhookState.ResetBackoff();
                if (!opts.Enabled)
                {
                    _queue.Clear();
                }
            }
            OperatorNotificationLog.ConfigurationUpdated(_logger, actor, opts.Enabled, opts.Smtp.Enabled, opts.Webhook.Enabled);
        }
        return result;
    }

    public async Task<OperatorNotificationTestResult> SendTestAsync(
        NotificationTestTarget target,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await _testSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (options, secrets) = await _configProvider.GetEffectiveForDeliveryAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var minInterval = TimeSpan.FromSeconds(options.TestSendMinimumIntervalSeconds);
            lock (_gate)
            {
                if (minInterval > TimeSpan.Zero && _lastTestSendUtc is { } last && now - last < minInterval)
                {
                    return new OperatorNotificationTestResult(true, last + minInterval, []);
                }
                _lastTestSendUtc = now;
            }

            var testEvent = new OperatorNotificationEvent(
                OperatorNotificationEventKind.DailyHeartbeat,
                OperatorNotificationSeverity.Info,
                "operator-test",
                "Test notification",
                "Operator-initiated verification test from standalone CameraAgent. No capture or processing state was modified.",
                now,
                "CameraAgent");

            var results = new List<NotificationChannelTestResult>(2);
            var runSmtp = target == NotificationTestTarget.Smtp ||
                          (target == NotificationTestTarget.AllEnabled && options.Smtp.Enabled);
            var runWebhook = target == NotificationTestTarget.Webhook ||
                             (target == NotificationTestTarget.AllEnabled && options.Webhook.Enabled);

            if (runSmtp)
            {
                var smtpResult = await DeliverSmtpAttemptAsync(options, secrets, testEvent, 1, 0, isTest: true, cancellationToken)
                    .ConfigureAwait(false);
                RecordTestAudit(SmtpNotificationTransport.ChannelName, smtpResult, now);
                results.Add(ToTestChannelResult(smtpResult));
            }
            else if (target == NotificationTestTarget.AllEnabled)
            {
                results.Add(new NotificationChannelTestResult(
                    SmtpNotificationTransport.ChannelName, false, false, NotificationReasonCodes.Disabled,
                    NotificationReasonCodes.DescribeForOperator(NotificationReasonCodes.Disabled), 0));
            }

            if (runWebhook)
            {
                var webhookResult = await DeliverWebhookAttemptAsync(options, secrets, testEvent, 1, 0, isTest: true, cancellationToken)
                    .ConfigureAwait(false);
                RecordTestAudit(WebhookNotificationTransport.ChannelName, webhookResult, now);
                results.Add(ToTestChannelResult(webhookResult));
            }
            else if (target == NotificationTestTarget.AllEnabled)
            {
                results.Add(new NotificationChannelTestResult(
                    WebhookNotificationTransport.ChannelName, false, false, NotificationReasonCodes.Disabled,
                    NotificationReasonCodes.DescribeForOperator(NotificationReasonCodes.Disabled), 0));
            }

            var succeeded = results.Count(static r => r.Attempted && r.Succeeded);
            var failed = results.Count(static r => r.Attempted && !r.Succeeded);
            var targetName = target switch
            {
                NotificationTestTarget.Smtp => "Smtp",
                NotificationTestTarget.Webhook => "Webhook",
                _ => "AllEnabled"
            };
            OperatorNotificationLog.TestSendCompleted(_logger, actor, targetName, succeeded, failed);
            return new OperatorNotificationTestResult(false, null, results);
        }
        finally
        {
            _testSendGate.Release();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background notification dispatch loop must never crash the host or interfere with capture.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextDelay = await ProcessDueItemsAsync(stoppingToken).ConfigureAwait(false);
                await WaitForNextSignalAsync(nextDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OperatorNotificationLog.DispatcherCycleFailed(_logger, ex.GetType().Name);
                try
                {
                    await Task.Delay(IdlePollDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Processes all currently due queued deliveries once. Exposed internally for deterministic testing.</summary>
    internal async Task<int> ProcessPendingOnceAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        while (true)
        {
            var (item, channel) = DequeueNextDueChannel(_timeProvider.GetUtcNow());
            if (item is null || channel is null)
            {
                break;
            }
            await DispatchChannelWorkAsync(item, channel, cancellationToken).ConfigureAwait(false);
            processed++;
        }
        return processed;
    }

    private async Task<TimeSpan> ProcessDueItemsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var (item, channel) = DequeueNextDueChannel(now);
            if (item is null || channel is null)
            {
                return ComputeNextWaitDelay(now);
            }

            await DispatchChannelWorkAsync(item, channel, cancellationToken).ConfigureAwait(false);
        }
        return IdlePollDelay;
    }

    private (QueuedNotification? Item, string? Channel) DequeueNextDueChannel(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var item in _queue)
            {
                if (item.SmtpPending && !item.SmtpInFlight && now >= item.SmtpNextAttemptUtc)
                {
                    item.SmtpInFlight = true;
                    return (item, SmtpNotificationTransport.ChannelName);
                }
                if (item.WebhookPending && !item.WebhookInFlight && now >= item.WebhookNextAttemptUtc)
                {
                    item.WebhookInFlight = true;
                    return (item, WebhookNotificationTransport.ChannelName);
                }
            }
            return (null, null);
        }
    }

    private TimeSpan ComputeNextWaitDelay(DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset? earliest = null;
            foreach (var item in _queue)
            {
                if (item.SmtpPending && (earliest is null || item.SmtpNextAttemptUtc < earliest))
                {
                    earliest = item.SmtpNextAttemptUtc;
                }
                if (item.WebhookPending && (earliest is null || item.WebhookNextAttemptUtc < earliest))
                {
                    earliest = item.WebhookNextAttemptUtc;
                }
            }

            if (earliest is null)
            {
                return IdlePollDelay;
            }
            var delta = earliest.Value - now;
            if (delta <= TimeSpan.Zero)
            {
                return TimeSpan.FromMilliseconds(50);
            }
            return delta < IdlePollDelay ? delta : IdlePollDelay;
        }
    }

    private async Task DispatchChannelWorkAsync(
        QueuedNotification item,
        string channel,
        CancellationToken cancellationToken)
    {
        try
        {
            await DispatchChannelWorkCoreAsync(item, channel, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (channel == SmtpNotificationTransport.ChannelName)
                {
                    item.SmtpInFlight = false;
                }
                else
                {
                    item.WebhookInFlight = false;
                }
            }
        }
    }

    private async Task DispatchChannelWorkCoreAsync(
        QueuedNotification item,
        string channel,
        CancellationToken cancellationToken)
    {
        var (options, secrets) = await _configProvider.GetEffectiveForDeliveryAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var slug = OperatorNotificationEvent.EventSlug(item.Event.Kind);

        if (!options.Enabled ||
            (channel == SmtpNotificationTransport.ChannelName && !options.Smtp.Enabled) ||
            (channel == WebhookNotificationTransport.ChannelName && !options.Webhook.Enabled))
        {
            CompleteChannel(item, channel, succeeded: false, recordCooldown: false, now);
            return;
        }

        var circuit = channel == SmtpNotificationTransport.ChannelName ? _smtpState : _webhookState;
        bool circuitOpen;
        int attemptNumber;
        OperatorNotificationEvent eventSnapshot;
        int coalescedCount;
        int suppressedCount;

        lock (_gate)
        {
            circuitOpen = circuit.IsInBackoff(now);
            attemptNumber = channel == SmtpNotificationTransport.ChannelName ? ++item.SmtpAttempts : ++item.WebhookAttempts;
            eventSnapshot = item.Event;
            coalescedCount = item.CoalescedCount;
            suppressedCount = item.SuppressedDuringCooldown;
        }

        if (circuitOpen)
        {
            var backoffResult = OperatorNotificationDeliveryResult.Failure(
                channel, NotificationReasonCodes.OutageBackoff, transient: true, TimeSpan.Zero);
            _telemetry.RecordDelivery(channel, slug, false, backoffResult.ReasonCode, TimeSpan.Zero, isTest: false);
            OperatorNotificationLog.DeliveryFailed(_logger, channel, slug, attemptNumber, backoffResult.ReasonCode, true, 0);
            lock (_gate)
            {
                RecordAuditLocked(now, slug, channel, "skipped", NotificationReasonCodes.OutageBackoff, coalescedCount, isTest: false);
            }
            CompleteChannel(item, channel, succeeded: false, recordCooldown: false, now);
            return;
        }

        using var activity = _telemetry.StartDelivery(channel, slug, isTest: false);
        var result = channel == SmtpNotificationTransport.ChannelName
            ? await DeliverSmtpAttemptAsync(options, secrets, eventSnapshot, coalescedCount, suppressedCount, isTest: false, cancellationToken).ConfigureAwait(false)
            : await DeliverWebhookAttemptAsync(options, secrets, eventSnapshot, coalescedCount, suppressedCount, isTest: false, cancellationToken).ConfigureAwait(false);

        var completedUtc = _timeProvider.GetUtcNow();
        var durationMs = (long)Math.Max(0, result.Duration.TotalMilliseconds);
        _telemetry.RecordDelivery(channel, slug, result.Succeeded, result.ReasonCode, result.Duration, isTest: false);
        activity?.SetTag("notification.outcome", result.Succeeded ? "sent" : "failed");
        activity?.SetTag("notification.reason", result.ReasonCode);

        if (result.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            OperatorNotificationLog.DeliverySucceeded(_logger, channel, slug, coalescedCount, durationMs);
            lock (_gate)
            {
                circuit.RecordSuccess(completedUtc);
                RecordAuditLocked(completedUtc, slug, channel, "sent", NotificationReasonCodes.Sent, coalescedCount, isTest: false);
            }
            CompleteChannel(item, channel, succeeded: true, recordCooldown: true, completedUtc);
            return;
        }

        activity?.SetStatus(ActivityStatusCode.Error, result.ReasonCode);
        OperatorNotificationLog.DeliveryFailed(_logger, channel, slug, attemptNumber, result.ReasonCode, result.Transient, durationMs);

        bool enteredOutageBackoff;
        int consecutiveFailures;
        bool willRetry;

        lock (_gate)
        {
            (enteredOutageBackoff, consecutiveFailures) = circuit.RecordFailure(
                completedUtc,
                result.ReasonCode,
                options.ConsecutiveOutageThreshold,
                TimeSpan.FromSeconds(options.OutageCooldownSeconds));
            RecordAuditLocked(completedUtc, slug, channel, "failed", result.ReasonCode, coalescedCount, isTest: false);

            var canRetry = result.Transient &&
                           !enteredOutageBackoff &&
                           attemptNumber <= options.MaxRetryAttempts;
            willRetry = canRetry;
            if (canRetry)
            {
                var delay = ComputeBackoffDelay(attemptNumber, options, result.RetryAfter);
                if (channel == SmtpNotificationTransport.ChannelName)
                {
                    item.SmtpNextAttemptUtc = completedUtc + delay;
                }
                else
                {
                    item.WebhookNextAttemptUtc = completedUtc + delay;
                }
            }
        }

        if (enteredOutageBackoff)
        {
            OperatorNotificationLog.ChannelBackoffEntered(_logger, channel, options.OutageCooldownSeconds, consecutiveFailures);
        }

        if (!willRetry)
        {
            CompleteChannel(item, channel, succeeded: false, recordCooldown: false, completedUtc);
        }
    }

    private void CompleteChannel(QueuedNotification item, string channel, bool succeeded, bool recordCooldown, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (channel == SmtpNotificationTransport.ChannelName)
            {
                item.SmtpPending = false;
            }
            else
            {
                item.WebhookPending = false;
            }

            if (succeeded && recordCooldown)
            {
                item.AnyChannelSucceeded = true;
            }

            if (!item.SmtpPending && !item.WebhookPending)
            {
                _queue.Remove(item);
                if (item.AnyChannelSucceeded)
                {
                    _cooldowns[item.Key] = new CooldownRecord(now);
                    PruneHourlyWindowLocked(now);
                    _hourlyDispatchTimestamps.Enqueue(now);
                }
            }
        }
    }

    private Task<OperatorNotificationDeliveryResult> DeliverSmtpAttemptAsync(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets secrets,
        OperatorNotificationEvent ev,
        int coalescedCount,
        int suppressedDuringCooldown,
        bool isTest,
        CancellationToken cancellationToken)
    {
        var smtp = options.Smtp;
        var camera = string.IsNullOrWhiteSpace(ev.CameraName) ? "CameraAgent" : ev.CameraName;
        var subject = $"[HVO CameraAgent | {camera}] {ev.Title}";
        var body = BuildPlainTextEmailBody(ev, camera, coalescedCount, suppressedDuringCooldown, isTest);
        var request = new SmtpDeliveryRequest(
            smtp.Host,
            smtp.Port,
            smtp.SecurityMode,
            smtp.FromAddress,
            smtp.FromDisplayName,
            smtp.Recipients,
            secrets.SmtpUsername,
            secrets.SmtpPassword,
            subject,
            body,
            isTest ? "test" : OperatorNotificationEvent.EventSlug(ev.Kind),
            TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        return _smtpTransport.SendAsync(request, cancellationToken);
    }

    private Task<OperatorNotificationDeliveryResult> DeliverWebhookAttemptAsync(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets secrets,
        OperatorNotificationEvent ev,
        int coalescedCount,
        int suppressedDuringCooldown,
        bool isTest,
        CancellationToken cancellationToken)
    {
        var webhook = options.Webhook;
        var request = new WebhookDeliveryRequest(
            secrets.WebhookUrl ?? string.Empty,
            webhook.Format,
            webhook.AuthHeaderName,
            webhook.NtfyTopic,
            webhook.AllowPrivateNetwork,
            secrets.WebhookAuthToken,
            secrets.WebhookUserKey,
            ev,
            coalescedCount,
            suppressedDuringCooldown,
            isTest,
            TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        return _webhookTransport.SendAsync(request, cancellationToken);
    }

    private static string BuildPlainTextEmailBody(
        OperatorNotificationEvent ev,
        string camera,
        int coalescedCount,
        int suppressedDuringCooldown,
        bool isTest)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("HVO.SkyMonitor Standalone CameraAgent Operator Notification");
        sb.AppendLine(new string('-', 57));
        sb.Append("Camera: ").AppendLine(camera);
        sb.Append("Event: ").AppendLine(isTest ? "Test notification" : OperatorNotificationEvent.EventDisplayName(ev.Kind));
        sb.Append("Severity: ").AppendLine(ev.Severity.ToString());
        sb.Append("Occurred (UTC): ").AppendLine(ev.OccurredUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        if (coalescedCount > 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Coalesced occurrences: {coalescedCount}").AppendLine();
        }
        if (suppressedDuringCooldown > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Suppressed during cooldown: {suppressedDuringCooldown}").AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine(ev.Summary);
        if (ev.Details is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Details:");
            var count = 0;
            foreach (var pair in ev.Details)
            {
                if (count++ >= OperatorNotificationEvent.MaximumDetailsCount) break;
                sb.Append("  - ").Append(pair.Key).Append(": ").AppendLine(pair.Value);
            }
        }
        return sb.ToString();
    }

    private static TimeSpan ComputeBackoffDelay(int attemptNumber, OperatorNotificationsOptions options, TimeSpan? retryAfter)
    {
        var multiplier = 1L << Math.Clamp(attemptNumber - 1, 0, 10);
        var seconds = Math.Min(
            options.RetryMaximumDelaySeconds,
            (long)options.RetryInitialDelaySeconds * multiplier);
        var computed = TimeSpan.FromSeconds(seconds);
        if (retryAfter is { } hinted && hinted > computed)
        {
            var cap = TimeSpan.FromSeconds(options.RetryMaximumDelaySeconds);
            return hinted < cap ? hinted : cap;
        }
        return computed;
    }

    private static OperatorNotificationEvent SanitizeEvent(OperatorNotificationEvent source, string dedupeKey)
    {
        Dictionary<string, string>? sanitizedDetails = null;
        if (source.Details is { Count: > 0 })
        {
            sanitizedDetails = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in source.Details.Take(OperatorNotificationEvent.MaximumDetailsCount))
            {
                var k = OperatorNotificationRedactor.SanitizeOperationalText(pair.Key, 48);
                var v = OperatorNotificationRedactor.SanitizeOperationalText(pair.Value, 160);
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                {
                    sanitizedDetails[k] = v;
                }
            }
        }

        return new OperatorNotificationEvent(
            source.Kind,
            source.Severity,
            dedupeKey,
            OperatorNotificationRedactor.SanitizeOperationalText(source.Title, OperatorNotificationEvent.MaximumTitleLength),
            OperatorNotificationRedactor.SanitizeOperationalText(source.Summary, OperatorNotificationEvent.MaximumSummaryLength),
            source.OccurredUtc == default ? DateTimeOffset.UtcNow : source.OccurredUtc.ToUniversalTime(),
            OperatorNotificationRedactor.SanitizeOperationalText(source.CameraName, 120),
            sanitizedDetails);
    }

    private void RecordTestAudit(string channel, OperatorNotificationDeliveryResult result, DateTimeOffset now)
    {
        _telemetry.RecordDelivery(channel, "test", result.Succeeded, result.ReasonCode, result.Duration, isTest: true);
        lock (_gate)
        {
            RecordAuditLocked(now, "test", channel, result.Succeeded ? "sent" : "failed", result.ReasonCode, 1, isTest: true);
        }
    }

    private static NotificationChannelTestResult ToTestChannelResult(OperatorNotificationDeliveryResult result) =>
        new(
            result.Channel,
            Attempted: true,
            Succeeded: result.Succeeded,
            ReasonCode: result.ReasonCode,
            Message: NotificationReasonCodes.DescribeForOperator(result.ReasonCode),
            DurationMilliseconds: Math.Round(Math.Max(0, result.Duration.TotalMilliseconds), 1));

    private void RecordAuditLocked(
        DateTimeOffset utc,
        string eventKind,
        string channel,
        string outcome,
        string reasonCode,
        int coalescedCount,
        bool isTest)
    {
        if (_recentAudit.Count >= MaximumAuditEntries)
        {
            _recentAudit.Dequeue();
        }
        _recentAudit.Enqueue(new OperatorNotificationAuditEntry(
            utc, eventKind, channel, outcome, reasonCode, coalescedCount, isTest));
    }

    private void PruneHourlyWindowLocked(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (_hourlyDispatchTimestamps.Count > 0 && _hourlyDispatchTimestamps.Peek() < cutoff)
        {
            _hourlyDispatchTimestamps.Dequeue();
        }
    }

    private void TryWakeWorker()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signaled.
            }
        }
    }

    private async Task WaitForNextSignalAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = _signal.WaitAsync(delayCts.Token);
        var timerTask = Task.Delay(delay, _timeProvider, delayCts.Token);
        _ = await Task.WhenAny(waitTask, timerTask).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await waitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timer woke first.
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _signal.Dispose();
        _testSendGate.Dispose();
    }

    private readonly record struct EventDedupeKey(OperatorNotificationEventKind Kind, string Key);

    private sealed class CooldownRecord(DateTimeOffset lastDispatchedUtc)
    {
        public DateTimeOffset LastDispatchedUtc { get; set; } = lastDispatchedUtc;
        public int SuppressedSinceLastDispatch { get; set; }
    }

    private sealed class QueuedNotification(
        EventDedupeKey key,
        OperatorNotificationEvent ev,
        DateTimeOffset queuedUtc,
        bool smtpPending,
        bool webhookPending,
        int suppressedDuringCooldown)
    {
        public EventDedupeKey Key { get; } = key;
        public OperatorNotificationEvent Event { get; set; } = ev;
        public DateTimeOffset LastOccurredUtc { get; set; } = queuedUtc;
        public int CoalescedCount { get; set; } = 1;
        public int SuppressedDuringCooldown { get; } = suppressedDuringCooldown;
        public bool SmtpPending { get; set; } = smtpPending;
        public bool WebhookPending { get; set; } = webhookPending;
        public bool SmtpInFlight { get; set; }
        public bool WebhookInFlight { get; set; }
        public int SmtpAttempts { get; set; }
        public int WebhookAttempts { get; set; }
        public DateTimeOffset SmtpNextAttemptUtc { get; set; } = queuedUtc;
        public DateTimeOffset WebhookNextAttemptUtc { get; set; } = queuedUtc;
        public bool AnyChannelSucceeded { get; set; }
    }

    private sealed class ChannelCircuitState(string channel)
    {
        private long _sentCount;
        private long _failedCount;
        private int _consecutiveFailures;
        private DateTimeOffset? _lastAttemptUtc;
        private DateTimeOffset? _lastSuccessUtc;
        private DateTimeOffset? _lastFailureUtc;
        private string? _lastFailureReasonCode;
        private DateTimeOffset? _backoffUntilUtc;

        public bool IsInBackoff(DateTimeOffset now) =>
            _backoffUntilUtc is { } until && now < until;

        public void RecordSuccess(DateTimeOffset now)
        {
            _sentCount++;
            _consecutiveFailures = 0;
            _lastAttemptUtc = now;
            _lastSuccessUtc = now;
            _lastFailureReasonCode = null;
            _backoffUntilUtc = null;
        }

        public (bool EnteredOutageBackoff, int ConsecutiveFailures) RecordFailure(
            DateTimeOffset now,
            string reasonCode,
            int outageThreshold,
            TimeSpan outageCooldown)
        {
            _failedCount++;
            _consecutiveFailures++;
            _lastAttemptUtc = now;
            _lastFailureUtc = now;
            _lastFailureReasonCode = reasonCode;
            if (_consecutiveFailures >= outageThreshold)
            {
                _backoffUntilUtc = now + outageCooldown;
                return (true, _consecutiveFailures);
            }
            return (false, _consecutiveFailures);
        }

        public void ResetBackoff()
        {
            _consecutiveFailures = 0;
            _backoffUntilUtc = null;
            _lastFailureReasonCode = null;
        }

        public OperatorNotificationChannelStatus ToStatus(bool enabled, DateTimeOffset now)
        {
            var state = !enabled
                ? NotificationChannelHealthState.Disabled
                : IsInBackoff(now)
                    ? NotificationChannelHealthState.OutageBackoff
                    : _consecutiveFailures > 0
                        ? NotificationChannelHealthState.Degraded
                        : NotificationChannelHealthState.Ready;
            return new OperatorNotificationChannelStatus(
                channel,
                enabled,
                state,
                _sentCount,
                _failedCount,
                _consecutiveFailures,
                _lastAttemptUtc,
                _lastSuccessUtc,
                _lastFailureUtc,
                _lastFailureReasonCode,
                IsInBackoff(now) ? _backoffUntilUtc : null);
        }
    }
}
