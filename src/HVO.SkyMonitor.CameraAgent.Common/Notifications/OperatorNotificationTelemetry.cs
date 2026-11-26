using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Bounded, redacted OpenTelemetry metrics and tracing for CameraAgent operator notifications.
/// Only low-cardinality event kinds, channel names, and sanitized outcome codes are emitted — never secrets, URLs,
/// usernames, passwords, tokens, or email addresses.
/// </summary>
public sealed class OperatorNotificationTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.Notifications";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.Notifications";

    private readonly Meter _meter;
    private readonly ActivitySource _activitySource;
    private readonly Counter<long> _published;
    private readonly Counter<long> _coalesced;
    private readonly Counter<long> _suppressed;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _deliveries;
    private readonly Histogram<double> _deliveryDurationMs;

    public OperatorNotificationTelemetry()
        : this(new Meter(MeterName))
    {
    }

    internal OperatorNotificationTelemetry(Meter meter)
    {
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _activitySource = new ActivitySource(ActivitySourceName);
        _published = _meter.CreateCounter<long>("camera_agent.notifications.published", "{event}");
        _coalesced = _meter.CreateCounter<long>("camera_agent.notifications.coalesced", "{event}");
        _suppressed = _meter.CreateCounter<long>("camera_agent.notifications.suppressed", "{event}");
        _dropped = _meter.CreateCounter<long>("camera_agent.notifications.dropped", "{event}");
        _deliveries = _meter.CreateCounter<long>("camera_agent.notifications.deliveries", "{delivery}");
        _deliveryDurationMs = _meter.CreateHistogram<double>("camera_agent.notifications.delivery.duration", "ms");
    }

    internal Activity? StartDelivery(string channel, string eventSlug, bool isTest)
    {
        var activity = _activitySource.StartActivity("operator-notification.deliver");
        activity?.SetTag("notification.channel", channel);
        activity?.SetTag("notification.event", eventSlug);
        activity?.SetTag("notification.test", isTest);
        return activity;
    }

    internal void RecordPublished(string eventSlug) =>
        _published.Add(1, new TagList { { "event", eventSlug } });

    internal void RecordCoalesced(string eventSlug) =>
        _coalesced.Add(1, new TagList { { "event", eventSlug } });

    internal void RecordSuppressed(string eventSlug, string reason) =>
        _suppressed.Add(1, new TagList { { "event", eventSlug }, { "reason", reason } });

    internal void RecordDropped(string eventSlug) =>
        _dropped.Add(1, new TagList { { "event", eventSlug } });

    internal void RecordDelivery(string channel, string eventSlug, bool succeeded, string reasonCode, TimeSpan duration, bool isTest)
    {
        var tags = new TagList
        {
            { "channel", channel },
            { "event", eventSlug },
            { "outcome", succeeded ? "sent" : "failed" },
            { "reason", reasonCode },
            { "test", isTest }
        };
        _deliveries.Add(1, tags);
        _deliveryDurationMs.Record(Math.Max(0, duration.TotalMilliseconds), tags);
    }

    public void Dispose()
    {
        _meter.Dispose();
        _activitySource.Dispose();
    }
}

/// <summary>
/// Strictly sanitized structured log messages for operator notifications (unique EventIds 16200-16219).
/// Never logs SMTP usernames, passwords, recipient emails, webhook URLs, auth tokens, or raw exception messages.
/// </summary>
internal static partial class OperatorNotificationLog
{
    [LoggerMessage(EventId = 16200, Level = LogLevel.Information,
        Message = "Operator notification queued for event {EventKind} (queueDepth={QueueDepth}).")]
    internal static partial void EventQueued(ILogger logger, string eventKind, int queueDepth);

    [LoggerMessage(EventId = 16201, Level = LogLevel.Debug,
        Message = "Operator notification coalesced for event {EventKind} (coalescedCount={CoalescedCount}).")]
    internal static partial void EventCoalesced(ILogger logger, string eventKind, int coalescedCount);

    [LoggerMessage(EventId = 16202, Level = LogLevel.Debug,
        Message = "Operator notification suppressed for event {EventKind} due to {Reason}.")]
    internal static partial void EventSuppressed(ILogger logger, string eventKind, string reason);

    [LoggerMessage(EventId = 16203, Level = LogLevel.Warning,
        Message = "Operator notification dropped for event {EventKind} because the bounded queue is full (capacity={QueueCapacity}).")]
    internal static partial void EventDropped(ILogger logger, string eventKind, int queueCapacity);

    [LoggerMessage(EventId = 16204, Level = LogLevel.Information,
        Message = "Operator notification delivered via {Channel} for event {EventKind} (coalesced={CoalescedCount}, durationMs={DurationMs}).")]
    internal static partial void DeliverySucceeded(ILogger logger, string channel, string eventKind, int coalescedCount, long durationMs);

    [LoggerMessage(EventId = 16205, Level = LogLevel.Warning,
        Message = "Operator notification delivery failed via {Channel} for event {EventKind} (attempt={Attempt}, reason={ReasonCode}, transient={Transient}, durationMs={DurationMs}).")]
    internal static partial void DeliveryFailed(ILogger logger, string channel, string eventKind, int attempt, string reasonCode, bool transient, long durationMs);

    [LoggerMessage(EventId = 16206, Level = LogLevel.Warning,
        Message = "Operator notification channel {Channel} entered outage backoff for {CooldownSeconds}s after {ConsecutiveFailures} consecutive failures.")]
    internal static partial void ChannelBackoffEntered(ILogger logger, string channel, int cooldownSeconds, int consecutiveFailures);

    [LoggerMessage(EventId = 16207, Level = LogLevel.Information,
        Message = "Operator notification configuration updated by {Actor} (enabled={Enabled}, smtpEnabled={SmtpEnabled}, webhookEnabled={WebhookEnabled}).")]
    internal static partial void ConfigurationUpdated(ILogger logger, string actor, bool enabled, bool smtpEnabled, bool webhookEnabled);

    [LoggerMessage(EventId = 16208, Level = LogLevel.Information,
        Message = "Operator notification test send requested by {Actor} for target {Target} (succeededChannels={SucceededChannels}, failedChannels={FailedChannels}).")]
    internal static partial void TestSendCompleted(ILogger logger, string actor, string target, int succeededChannels, int failedChannels);

    [LoggerMessage(EventId = 16209, Level = LogLevel.Warning,
        Message = "Operator notification monitor evaluation failed with {ExceptionType}; capture is unaffected.")]
    internal static partial void MonitorEvaluationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 16210, Level = LogLevel.Warning,
        Message = "Operator notification dispatcher cycle failed with {ExceptionType}; capture is unaffected.")]
    internal static partial void DispatcherCycleFailed(ILogger logger, string exceptionType);
}
