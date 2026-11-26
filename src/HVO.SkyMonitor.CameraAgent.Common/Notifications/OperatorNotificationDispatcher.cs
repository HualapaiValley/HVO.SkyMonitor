using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Bounded, non-waiting, anti-storm operator notification dispatcher. Runs strictly off the capture, processing,
/// retention, login, and recovery critical paths. Provides non-waiting admission (<see cref="Monitor.TryEnter(object)"/>
/// with overflow accounting so lock contention never stalls publishers), O(1) in-queue coalescing and cooldown lookup
/// (bounded hourly-window trimming &lt;= 60 entries; expired cooldown cleanup runs strictly on background workers),
/// unexpired 24-hour cooldown preservation, independent concurrent SMTP and Webhook channel workers with independent
/// retry/backoff and circuit-breaker outage isolation, revoked-event re-checks, in-flight repeat preservation across
/// both success and terminal failure, and rate-limited operator test-send verification.
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
    // Sized safely above the mathematical upper bound of active/retained keys under the 24h cooldown and 60/hr cap (24*60 = 1440; 48*60 = 2880).
    private const int MaximumTrackedCooldownEntries = 4096;
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerCycleFailureInitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerCycleFailureMaximumDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumCooldownRetention = TimeSpan.FromHours(24);

    private readonly IOperatorNotificationConfigProvider _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    private readonly ISmtpNotificationTransport _smtpTransport = smtpTransport ?? throw new ArgumentNullException(nameof(smtpTransport));
    private readonly IWebhookNotificationTransport _webhookTransport = webhookTransport ?? throw new ArgumentNullException(nameof(webhookTransport));
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The DI container owns this singleton telemetry instance.")]
    private readonly OperatorNotificationTelemetry _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = logger ?? NullLogger<OperatorNotificationDispatcher>.Instance;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _smtpSignal = new(0, 1);
    private readonly SemaphoreSlim _webhookSignal = new(0, 1);
    private readonly SemaphoreSlim _testSendGate = new(1, 1);
    private readonly Dictionary<EventDedupeKey, QueuedNotification> _pendingByKey = [];
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

    /// <summary>Internal test visibility for bounded cooldown eviction verification.</summary>
    internal int TrackedCooldownCount
    {
        get
        {
            lock (_gate)
            {
                return _cooldowns.Count;
            }
        }
    }

    /// <summary>Internal test probe that attempts a non-waiting lock entry from the calling thread.</summary>
    internal bool TryProbeGateUnlockedForTesting()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return false;
        }
        Monitor.Exit(_gate);
        return true;
    }

    /// <summary>Internal test seam that holds the dispatcher lock on a dedicated background thread until disposed.</summary>
    internal IDisposable HoldGateForTesting() => new GateHoldHandle(_gate);

    /// <summary>
    /// Enqueues or coalesces an operational health event without waiting on lock contention, scanning cooldown history,
    /// performing I/O, or invoking logging/metrics callbacks inside the critical section. Returns false when notifications
    /// or the event are disabled, the configuration/store is degraded, the dispatcher lock is contended, or the event is
    /// suppressed/dropped.
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
            if (_configProvider.ConfigurationErrorReasonCode is not null)
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
            var cooldown = options.Events.GetCooldown(notification.Kind);
            var maxHourly = Math.Clamp(options.MaxNotificationsPerHour, 1, 60);
            var queueCapacity = Math.Clamp(options.QueueCapacity, 4, 256);

            // Non-waiting admission: never block capture/processing threads if the dispatcher lock is contended.
            if (!Monitor.TryEnter(_gate))
            {
                Interlocked.Increment(ref _totalDroppedQueueFull);
                _telemetry.RecordDropped(slug);
                OperatorNotificationLog.EventDropped(_logger, slug, queueCapacity);
                return false;
            }

            PublishDecision decision;
            try
            {
                // Bounded O(1) amortized trim (at most maxHourly <= 60 entries); cooldown pruning stays off TryPublish.
                PruneHourlyWindowLocked(now);

                // 1. O(1) coalesce when an identical event key is already queued or in flight.
                if (_pendingByKey.TryGetValue(key, out var existing))
                {
                    existing.Event = sanitizedEvent;
                    existing.CoalescedCount++;
                    existing.LastOccurredUtc = now;
                    _totalCoalesced++;
                    decision = new PublishDecision(PublishOutcome.Coalesced, existing.CoalescedCount, _pendingByKey.Count, false, false);
                }
                else
                {
                    // 2. O(1) per-event cooldown check (committed at admission).
                    if (_cooldowns.TryGetValue(key, out var cooldownRecord) &&
                        now < cooldownRecord.ExpiresUtc)
                    {
                        cooldownRecord.SuppressedSinceLastDispatch++;
                        _totalSuppressedCooldown++;
                        decision = new PublishDecision(PublishOutcome.SuppressedCooldown, 0, _pendingByKey.Count, false, false);
                    }
                    // 3. Enforce global rolling-hour anti-storm cap (committed at admission; failures never escape the cap).
                    else if (_hourlyDispatchTimestamps.Count >= maxHourly)
                    {
                        _totalSuppressedRateLimit++;
                        decision = new PublishDecision(PublishOutcome.SuppressedRateLimit, 0, _pendingByKey.Count, false, false);
                    }
                    // 4. Enforce bounded in-memory queue capacity without blocking callers.
                    else if (_pendingByKey.Count >= queueCapacity)
                    {
                        Interlocked.Increment(ref _totalDroppedQueueFull);
                        decision = new PublishDecision(PublishOutcome.DroppedQueueFull, 0, _pendingByKey.Count, false, false);
                    }
                    else
                    {
                        var priorSuppressed = cooldownRecord is not null && now < cooldownRecord.MaxRetentionUtc
                            ? cooldownRecord.SuppressedSinceLastDispatch
                            : 0;
                        var retention = ComputeCooldownRetention(cooldown);
                        _cooldowns[key] = new CooldownRecord(now, now + cooldown, now + retention);
                        _hourlyDispatchTimestamps.Enqueue(now);

                        var item = new QueuedNotification(
                            key,
                            sanitizedEvent,
                            now,
                            options.Smtp.Enabled,
                            options.Webhook.Enabled,
                            priorSuppressed);
                        _pendingByKey[key] = item;
                        _totalPublished++;
                        decision = new PublishDecision(
                            PublishOutcome.Admitted,
                            1,
                            _pendingByKey.Count,
                            WakeSmtp: options.Smtp.Enabled,
                            WakeWebhook: options.Webhook.Enabled);
                    }
                }
            }
            finally
            {
                Monitor.Exit(_gate);
            }

            // Emit metrics, logs, and channel wakeups strictly OUTSIDE the lock.
            switch (decision.Outcome)
            {
                case PublishOutcome.Coalesced:
                    _telemetry.RecordCoalesced(slug);
                    OperatorNotificationLog.EventCoalesced(_logger, slug, decision.Count);
                    return true;
                case PublishOutcome.SuppressedCooldown:
                    _telemetry.RecordSuppressed(slug, "cooldown");
                    OperatorNotificationLog.EventSuppressed(_logger, slug, "cooldown");
                    return false;
                case PublishOutcome.SuppressedRateLimit:
                    _telemetry.RecordSuppressed(slug, "hourly-cap");
                    OperatorNotificationLog.EventSuppressed(_logger, slug, "hourly-cap");
                    return false;
                case PublishOutcome.DroppedQueueFull:
                    _telemetry.RecordDropped(slug);
                    OperatorNotificationLog.EventDropped(_logger, slug, queueCapacity);
                    return false;
                case PublishOutcome.Admitted:
                    _telemetry.RecordPublished(slug);
                    OperatorNotificationLog.EventQueued(_logger, slug, decision.QueueDepth);
                    if (decision.WakeSmtp) TryWakeChannel(_smtpSignal);
                    if (decision.WakeWebhook) TryWakeChannel(_webhookSignal);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public OperatorNotificationDispatcherSnapshot GetStatus()
    {
        var options = _configProvider.CurrentOptions;
        var configError = _configProvider.ConfigurationErrorReasonCode;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            return new OperatorNotificationDispatcherSnapshot(
                options.Enabled,
                _pendingByKey.Count,
                Math.Clamp(options.QueueCapacity, 4, 256),
                _totalPublished,
                _totalCoalesced,
                _totalSuppressedCooldown,
                _totalSuppressedRateLimit,
                Interlocked.Read(ref _totalDroppedQueueFull),
                _smtpState.ToStatus(options.Enabled && options.Smtp.Enabled, now),
                _webhookState.ToStatus(options.Enabled && options.Webhook.Enabled, now),
                [.. _recentAudit.Reverse()],
                configError);
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
                    _pendingByKey.Clear();
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
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), "Unsupported test notification target.");
        }

        await _testSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var effective = await _configProvider.GetEffectiveConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var options = effective.Options;
            var secrets = effective.Secrets;
            var now = _timeProvider.GetUtcNow();
            var minInterval = TimeSpan.FromSeconds(Math.Clamp(options.TestSendMinimumIntervalSeconds, 0, 300));
            lock (_gate)
            {
                if (minInterval > TimeSpan.Zero && _lastTestSendUtc is { } last && now - last < minInterval)
                {
                    return new OperatorNotificationTestResult(true, last + minInterval, []);
                }
                _lastTestSendUtc = now;
            }

            if (effective.StoreCorrupt || effective.ConfigurationErrorReasonCode is not null)
            {
                var errorCode = effective.ConfigurationErrorReasonCode
                    ?? (effective.StoreCorrupt ? NotificationReasonCodes.StoreCorrupt : NotificationReasonCodes.InvalidConfiguration);
                var errorResults = BuildConfigErrorTestResults(target, errorCode, now);
                return new OperatorNotificationTestResult(false, null, errorResults);
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

    private List<NotificationChannelTestResult> BuildConfigErrorTestResults(
        NotificationTestTarget target,
        string reasonCode,
        DateTimeOffset now)
    {
        var results = new List<NotificationChannelTestResult>(2);
        if (target is NotificationTestTarget.Smtp or NotificationTestTarget.AllEnabled)
        {
            var res = OperatorNotificationDeliveryResult.Failure(SmtpNotificationTransport.ChannelName, reasonCode, transient: false, TimeSpan.Zero);
            RecordTestAudit(SmtpNotificationTransport.ChannelName, res, now);
            results.Add(ToTestChannelResult(res));
        }
        if (target is NotificationTestTarget.Webhook or NotificationTestTarget.AllEnabled)
        {
            var res = OperatorNotificationDeliveryResult.Failure(WebhookNotificationTransport.ChannelName, reasonCode, transient: false, TimeSpan.Zero);
            RecordTestAudit(WebhookNotificationTransport.ChannelName, res, now);
            results.Add(ToTestChannelResult(res));
        }
        return results;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var smtpWorker = RunChannelWorkerAsync(SmtpNotificationTransport.ChannelName, _smtpSignal, stoppingToken);
        var webhookWorker = RunChannelWorkerAsync(WebhookNotificationTransport.ChannelName, _webhookSignal, stoppingToken);
        return Task.WhenAll(smtpWorker, webhookWorker);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Independent per-channel background worker must never crash the host or interfere with capture or the sibling channel.")]
    private async Task RunChannelWorkerAsync(string channel, SemaphoreSlim signal, CancellationToken stoppingToken)
    {
        var consecutiveCycleFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextDelay = await ProcessDueChannelItemsAsync(channel, stoppingToken).ConfigureAwait(false);
                consecutiveCycleFailures = 0;
                await WaitForNextSignalAsync(signal, nextDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveCycleFailures++;
                OperatorNotificationLog.DispatcherCycleFailed(_logger, ex.GetType().Name);
                var backoffDelay = ComputeWorkerCycleFailureBackoff(consecutiveCycleFailures);
                try
                {
                    await Task.Delay(backoffDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static TimeSpan ComputeWorkerCycleFailureBackoff(int consecutiveFailures)
    {
        var shift = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var seconds = Math.Min((long)WorkerCycleFailureMaximumDelay.TotalSeconds, (long)WorkerCycleFailureInitialDelay.TotalSeconds << shift);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Processes all currently due queued deliveries concurrently across SMTP and Webhook channels. Exposed internally for deterministic testing.</summary>
    internal async Task<int> ProcessPendingOnceAsync(CancellationToken cancellationToken)
    {
        var smtpTask = ProcessChannelPendingOnceAsync(SmtpNotificationTransport.ChannelName, cancellationToken);
        var webhookTask = ProcessChannelPendingOnceAsync(WebhookNotificationTransport.ChannelName, cancellationToken);
        var counts = await Task.WhenAll(smtpTask, webhookTask).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneExpiredCooldownsLocked(now);
            EnforceCooldownCapLocked(now);
            PruneHourlyWindowLocked(now);
        }
        return counts[0] + counts[1];
    }

    internal async Task<int> ProcessChannelPendingOnceAsync(string channel, CancellationToken cancellationToken)
    {
        var processed = 0;
        while (true)
        {
            var item = DequeueNextDueForChannel(channel, _timeProvider.GetUtcNow());
            if (item is null)
            {
                break;
            }
            await DispatchChannelWorkAsync(item, channel, cancellationToken).ConfigureAwait(false);
            processed++;
        }
        return processed;
    }

    private async Task<TimeSpan> ProcessDueChannelItemsAsync(string channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var item = DequeueNextDueForChannel(channel, now);
            if (item is null)
            {
                lock (_gate)
                {
                    PruneExpiredCooldownsLocked(now);
                    EnforceCooldownCapLocked(now);
                    PruneHourlyWindowLocked(now);
                }
                return ComputeNextWaitDelayForChannel(channel, now);
            }

            await DispatchChannelWorkAsync(item, channel, cancellationToken).ConfigureAwait(false);
        }
        return IdlePollDelay;
    }

    private QueuedNotification? DequeueNextDueForChannel(string channel, DateTimeOffset now)
    {
        lock (_gate)
        {
            var isSmtp = channel == SmtpNotificationTransport.ChannelName;
            foreach (var item in _pendingByKey.Values)
            {
                if (isSmtp)
                {
                    if (item.SmtpPending && !item.SmtpInFlight && now >= item.SmtpNextAttemptUtc)
                    {
                        item.SmtpInFlight = true;
                        return item;
                    }
                }
                else
                {
                    if (item.WebhookPending && !item.WebhookInFlight && now >= item.WebhookNextAttemptUtc)
                    {
                        item.WebhookInFlight = true;
                        return item;
                    }
                }
            }
            return null;
        }
    }

    private TimeSpan ComputeNextWaitDelayForChannel(string channel, DateTimeOffset now)
    {
        lock (_gate)
        {
            var isSmtp = channel == SmtpNotificationTransport.ChannelName;
            DateTimeOffset? earliest = null;
            foreach (var item in _pendingByKey.Values)
            {
                if (isSmtp && item.SmtpPending && !item.SmtpInFlight &&
                    (earliest is null || item.SmtpNextAttemptUtc < earliest))
                {
                    earliest = item.SmtpNextAttemptUtc;
                }
                if (!isSmtp && item.WebhookPending && !item.WebhookInFlight &&
                    (earliest is null || item.WebhookNextAttemptUtc < earliest))
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
        var effective = await _configProvider.GetEffectiveConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var options = effective.Options;
        var secrets = effective.Secrets;
        var now = _timeProvider.GetUtcNow();
        var slug = OperatorNotificationEvent.EventSlug(item.Event.Kind);

        if (effective.StoreCorrupt || effective.ConfigurationErrorReasonCode is not null)
        {
            var errorCode = effective.ConfigurationErrorReasonCode
                ?? (effective.StoreCorrupt ? NotificationReasonCodes.StoreCorrupt : NotificationReasonCodes.InvalidConfiguration);
            _telemetry.RecordDelivery(channel, slug, false, errorCode, TimeSpan.Zero, isTest: false);
            OperatorNotificationLog.DeliveryFailed(_logger, channel, slug, 1, errorCode, transient: false, 0);
            lock (_gate)
            {
                RecordAuditLocked(now, slug, channel, "failed", errorCode, item.CoalescedCount, isTest: false);
            }
            CompleteChannel(item, channel, succeeded: false, deliveredCoalescedCount: 0, options, now);
            return;
        }

        // Re-check master switch, channel enablement, AND event-category enablement before every delivery/retry attempt.
        if (!options.Enabled ||
            (channel == SmtpNotificationTransport.ChannelName && !options.Smtp.Enabled) ||
            (channel == WebhookNotificationTransport.ChannelName && !options.Webhook.Enabled) ||
            !options.Events.IsEventEnabled(item.Event.Kind))
        {
            CompleteChannel(item, channel, succeeded: false, deliveredCoalescedCount: 0, options, now);
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
            if (!circuitOpen)
            {
                if (channel == SmtpNotificationTransport.ChannelName)
                {
                    item.SmtpLastAttemptedCoalescedCount = coalescedCount;
                }
                else
                {
                    item.WebhookLastAttemptedCoalescedCount = coalescedCount;
                }
            }
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
            CompleteChannel(item, channel, succeeded: false, deliveredCoalescedCount: 0, options, now);
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
            CompleteChannel(item, channel, succeeded: true, deliveredCoalescedCount: coalescedCount, options, completedUtc);
            return;
        }

        activity?.SetStatus(ActivityStatusCode.Error, result.ReasonCode);
        OperatorNotificationLog.DeliveryFailed(_logger, channel, slug, attemptNumber, result.ReasonCode, result.Transient, durationMs);

        bool enteredOutageBackoff;
        int consecutiveFailures;
        bool willRetry;
        var outageThreshold = Math.Clamp(options.ConsecutiveOutageThreshold, 1, 10);
        var outageCooldown = Math.Clamp(options.OutageCooldownSeconds, 10, 3600);
        var maxRetries = Math.Clamp(options.MaxRetryAttempts, 0, 5);

        lock (_gate)
        {
            (enteredOutageBackoff, consecutiveFailures) = circuit.RecordFailure(
                completedUtc,
                result.ReasonCode,
                outageThreshold,
                TimeSpan.FromSeconds(outageCooldown));
            RecordAuditLocked(completedUtc, slug, channel, "failed", result.ReasonCode, coalescedCount, isTest: false);

            var canRetry = result.Transient &&
                           !enteredOutageBackoff &&
                           attemptNumber <= maxRetries;
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
            OperatorNotificationLog.ChannelBackoffEntered(_logger, channel, outageCooldown, consecutiveFailures);
        }

        if (!willRetry)
        {
            CompleteChannel(item, channel, succeeded: false, deliveredCoalescedCount: 0, options, completedUtc);
        }
    }

    private void CompleteChannel(
        QueuedNotification item,
        string channel,
        bool succeeded,
        int deliveredCoalescedCount,
        OperatorNotificationsOptions options,
        DateTimeOffset now)
    {
        var wakeSmtp = false;
        var wakeWebhook = false;

        lock (_gate)
        {
            if (channel == SmtpNotificationTransport.ChannelName)
            {
                item.SmtpPending = false;
                if (succeeded)
                {
                    item.SmtpDeliveredCoalescedCount = deliveredCoalescedCount;
                }
            }
            else
            {
                item.WebhookPending = false;
                if (succeeded)
                {
                    item.WebhookDeliveredCoalescedCount = deliveredCoalescedCount;
                }
            }

            if (!item.SmtpPending && !item.WebhookPending)
            {
                if (_pendingByKey.TryGetValue(item.Key, out var current) && ReferenceEquals(current, item))
                {
                    _pendingByKey.Remove(item.Key);
                }

                // Preserve repeats coalesced while a delivery was in flight instead of silently dropping them.
                var deliveredBaseline = ComputeDeliveredBaseline(item);
                var undeliveredDelta = item.CoalescedCount - deliveredBaseline;
                if (undeliveredDelta > 0)
                {
                    var cooldown = options.Events.GetCooldown(item.Event.Kind);
                    var anyEnabledChannelAvailable =
                        (options.Smtp.Enabled && !_smtpState.IsInBackoff(now)) ||
                        (options.Webhook.Enabled && !_webhookState.IsInBackoff(now));
                    var canRequeueImmediately = options.Enabled &&
                                                anyEnabledChannelAvailable &&
                                                _configProvider.ConfigurationErrorReasonCode is null &&
                                                options.Events.IsEventEnabled(item.Event.Kind) &&
                                                (!_cooldowns.TryGetValue(item.Key, out var existingCd) || now >= existingCd.ExpiresUtc) &&
                                                _pendingByKey.Count < Math.Clamp(options.QueueCapacity, 4, 256);
                    if (canRequeueImmediately)
                    {
                        PruneHourlyWindowLocked(now);
                        if (_hourlyDispatchTimestamps.Count < Math.Clamp(options.MaxNotificationsPerHour, 1, 60))
                        {
                            var retention = ComputeCooldownRetention(cooldown);
                            _cooldowns[item.Key] = new CooldownRecord(now, now + cooldown, now + retention);
                            EnforceCooldownCapLocked(now);
                            _hourlyDispatchTimestamps.Enqueue(now);
                            var followUp = new QueuedNotification(
                                item.Key,
                                item.Event,
                                now,
                                options.Smtp.Enabled,
                                options.Webhook.Enabled,
                                suppressedDuringCooldown: 0)
                            {
                                CoalescedCount = undeliveredDelta
                            };
                            _pendingByKey[item.Key] = followUp;
                            wakeSmtp = options.Smtp.Enabled;
                            wakeWebhook = options.Webhook.Enabled;
                            undeliveredDelta = 0;
                        }
                    }

                    if (undeliveredDelta > 0)
                    {
                        if (_cooldowns.TryGetValue(item.Key, out var cd))
                        {
                            cd.SuppressedSinceLastDispatch += undeliveredDelta;
                        }
                        else
                        {
                            var retention = ComputeCooldownRetention(cooldown);
                            _cooldowns[item.Key] = new CooldownRecord(now, now + cooldown, now + retention)
                            {
                                SuppressedSinceLastDispatch = undeliveredDelta
                            };
                            EnforceCooldownCapLocked(now);
                        }
                    }
                }
            }
        }

        if (wakeSmtp) TryWakeChannel(_smtpSignal);
        if (wakeWebhook) TryWakeChannel(_webhookSignal);
    }

    private static int ComputeDeliveredBaseline(QueuedNotification item)
    {
        if (item.SmtpDeliveredCoalescedCount > 0 && item.WebhookDeliveredCoalescedCount > 0)
        {
            return Math.Min(item.SmtpDeliveredCoalescedCount, item.WebhookDeliveredCoalescedCount);
        }
        if (item.SmtpDeliveredCoalescedCount > 0)
        {
            return item.SmtpDeliveredCoalescedCount;
        }
        if (item.WebhookDeliveredCoalescedCount > 0)
        {
            return item.WebhookDeliveredCoalescedCount;
        }

        // If no channel succeeded, do not advance the baseline over repeats accepted while the
        // terminally failed delivery attempt(s) were in flight; carry those repeats forward.
        if (item.SmtpLastAttemptedCoalescedCount > 0 && item.WebhookLastAttemptedCoalescedCount > 0)
        {
            return Math.Min(item.SmtpLastAttemptedCoalescedCount, item.WebhookLastAttemptedCoalescedCount);
        }
        if (item.SmtpLastAttemptedCoalescedCount > 0)
        {
            return item.SmtpLastAttemptedCoalescedCount;
        }
        if (item.WebhookLastAttemptedCoalescedCount > 0)
        {
            return item.WebhookLastAttemptedCoalescedCount;
        }

        return item.CoalescedCount;
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
        var timeoutSeconds = Math.Clamp(options.RequestTimeoutSeconds, 1, 30);
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
            TimeSpan.FromSeconds(timeoutSeconds));

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
        var timeoutSeconds = Math.Clamp(options.RequestTimeoutSeconds, 1, 30);
        var request = new WebhookDeliveryRequest(
            secrets.WebhookUrl ?? string.Empty,
            webhook.Format,
            webhook.AuthHeaderName,
            secrets.WebhookNtfyTopic,
            webhook.AllowPrivateNetwork,
            secrets.WebhookAuthToken,
            secrets.WebhookUserKey,
            ev,
            coalescedCount,
            suppressedDuringCooldown,
            isTest,
            TimeSpan.FromSeconds(timeoutSeconds));

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
        var initial = Math.Clamp(options.RetryInitialDelaySeconds, 1, 300);
        var maximum = Math.Clamp(options.RetryMaximumDelaySeconds, initial, 3600);
        var multiplier = 1L << Math.Clamp(attemptNumber - 1, 0, 10);
        var seconds = Math.Min(maximum, (long)initial * multiplier);
        var computed = TimeSpan.FromSeconds(seconds);
        if (retryAfter is { } hinted && hinted > computed)
        {
            var cap = TimeSpan.FromSeconds(maximum);
            return hinted < cap ? hinted : cap;
        }
        return computed;
    }

    private static TimeSpan ComputeCooldownRetention(TimeSpan cooldown)
    {
        var doubled = TimeSpan.FromTicks(cooldown.Ticks * 2);
        return doubled > MinimumCooldownRetention ? doubled : MinimumCooldownRetention;
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

    private void PruneExpiredCooldownsLocked(DateTimeOffset now)
    {
        if (_cooldowns.Count == 0)
        {
            return;
        }

        List<EventDedupeKey>? expired = null;
        foreach (var pair in _cooldowns)
        {
            var rec = pair.Value;
            if ((now >= rec.ExpiresUtc && rec.SuppressedSinceLastDispatch == 0) || now >= rec.MaxRetentionUtc)
            {
                expired ??= [];
                expired.Add(pair.Key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _cooldowns.Remove(key);
            }
        }
    }

    private void EnforceCooldownCapLocked(DateTimeOffset now)
    {
        if (_cooldowns.Count <= MaximumTrackedCooldownEntries)
        {
            return;
        }

        PruneExpiredCooldownsLocked(now);
        while (_cooldowns.Count > MaximumTrackedCooldownEntries)
        {
            var foundExpired = false;
            var oldestExpiredKey = default(EventDedupeKey);
            var oldestExpiredUtc = DateTimeOffset.MaxValue;
            foreach (var pair in _cooldowns)
            {
                // Never evict an unexpired cooldown entry early; only evict expired records if the soft cap is exceeded.
                if (now >= pair.Value.ExpiresUtc && pair.Value.LastDispatchedUtc < oldestExpiredUtc)
                {
                    oldestExpiredUtc = pair.Value.LastDispatchedUtc;
                    oldestExpiredKey = pair.Key;
                    foundExpired = true;
                }
            }

            if (!foundExpired)
            {
                break;
            }
            _cooldowns.Remove(oldestExpiredKey);
        }
    }

    private static void TryWakeChannel(SemaphoreSlim signal)
    {
        if (signal.CurrentCount == 0)
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signaled.
            }
        }
    }

    private async Task WaitForNextSignalAsync(SemaphoreSlim signal, TimeSpan delay, CancellationToken cancellationToken)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = signal.WaitAsync(delayCts.Token);
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
        _smtpSignal.Dispose();
        _webhookSignal.Dispose();
        _testSendGate.Dispose();
    }

    private enum PublishOutcome
    {
        Admitted,
        Coalesced,
        SuppressedCooldown,
        SuppressedRateLimit,
        DroppedQueueFull
    }

    private readonly record struct PublishDecision(
        PublishOutcome Outcome,
        int Count,
        int QueueDepth,
        bool WakeSmtp,
        bool WakeWebhook);

    private readonly record struct EventDedupeKey(OperatorNotificationEventKind Kind, string Key);

    private sealed class CooldownRecord(DateTimeOffset lastDispatchedUtc, DateTimeOffset expiresUtc, DateTimeOffset maxRetentionUtc)
    {
        public DateTimeOffset LastDispatchedUtc { get; } = lastDispatchedUtc;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public DateTimeOffset MaxRetentionUtc { get; } = maxRetentionUtc;
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
        public int SmtpDeliveredCoalescedCount { get; set; }
        public int WebhookDeliveredCoalescedCount { get; set; }
        public int SmtpLastAttemptedCoalescedCount { get; set; }
        public int WebhookLastAttemptedCoalescedCount { get; set; }
    }

    private sealed class GateHoldHandle : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _thread;
        private int _disposed;

        public GateHoldHandle(object gate)
        {
            using var acquired = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                lock (gate)
                {
                    acquired.Set();
                    _release.Wait();
                }
            })
            {
                IsBackground = true,
                Name = "OperatorNotificationDispatcher-TestGateHold"
            };
            _thread.Start();
            acquired.Wait();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _release.Dispose();
        }
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
