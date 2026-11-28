using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>One bounded, sanitized local operational health event submitted for asynchronous operator notification.</summary>
public sealed record OperatorNotificationEvent(
    OperatorNotificationEventKind Kind,
    OperatorNotificationSeverity Severity,
    string DedupeKey,
    string Title,
    string Summary,
    DateTimeOffset OccurredUtc,
    string? CameraName = null,
    IReadOnlyDictionary<string, string>? Details = null)
{
    public const int MaximumDedupeKeyLength = 128;
    public const int MaximumTitleLength = 160;
    public const int MaximumSummaryLength = 1024;
    public const int MaximumDetailsCount = 8;

    public static string EventSlug(OperatorNotificationEventKind kind) => kind switch
    {
        OperatorNotificationEventKind.CaptureStalled => "capture_stalled",
        OperatorNotificationEventKind.CameraDisconnected => "camera_disconnected",
        OperatorNotificationEventKind.DiskPressure => "disk_pressure",
        OperatorNotificationEventKind.UpgradeOrRecoveryFailed => "upgrade_recovery_failed",
        OperatorNotificationEventKind.DailyHeartbeat => "daily_heartbeat",
        _ => "unknown"
    };

    public static string EventDisplayName(OperatorNotificationEventKind kind) => kind switch
    {
        OperatorNotificationEventKind.CaptureStalled => "Capture stalled",
        OperatorNotificationEventKind.CameraDisconnected => "Camera disconnected",
        OperatorNotificationEventKind.DiskPressure => "Disk pressure",
        OperatorNotificationEventKind.UpgradeOrRecoveryFailed => "Upgrade / recovery failed",
        OperatorNotificationEventKind.DailyHeartbeat => "Daily heartbeat",
        _ => "Operational event"
    };
}

/// <summary>
/// Redacted destination and revision provenance captured when credentials are read from
/// <see cref="IOperatorNotificationSecretStore.ReadAsync"/> so read-modify-write saves (including repeated
/// retries after a destination rotation) can never re-bind unchanged credentials from an earlier destination
/// onto a rotated relay or webhook origin.
/// </summary>
public sealed record OperatorNotificationSecretProvenance(
    string? Revision = null,
    string? BoundSmtpEndpoint = null,
    string? BoundWebhookOrigin = null,
    string? SmtpUsernameFingerprint = null,
    string? SmtpPasswordFingerprint = null,
    string? WebhookAuthTokenFingerprint = null,
    string? WebhookUserKeyFingerprint = null,
    string? WebhookNtfyTopicFingerprint = null,
    string? WebhookEndpointFingerprint = null,
    string? BoundWebhookDestinationFingerprint = null)
{
    public override string ToString() => "[REDACTED]";
}

/// <summary>
/// Server-side-only container for protected operator notification credentials. Never serialize into logs, diagnostics,
/// UI readouts, or HTTP responses; <see cref="ToString"/> always returns <c>[REDACTED]</c>.
/// </summary>
[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
    Justification = "Protected secret payload stores the raw validated operator webhook URL string.")]
[SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
    Justification = "Protected secret payload stores the raw validated operator webhook URL string.")]
public sealed record OperatorNotificationSecrets(
    string? SmtpUsername = null,
    string? SmtpPassword = null,
    string? WebhookUrl = null,
    string? WebhookAuthToken = null,
    string? WebhookUserKey = null,
    string? WebhookNtfyTopic = null)
{
    public static OperatorNotificationSecrets Empty { get; } = new();

    /// <summary>
    /// Optional read provenance preserved across <c>with</c> mutations so direct store saves never carry
    /// unchanged secrets across destination rotations. Excluded from value equality and JSON serialization.
    /// </summary>
    [JsonIgnore]
    public OperatorNotificationSecretProvenance? Provenance { get; init; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(SmtpUsername) &&
        string.IsNullOrEmpty(SmtpPassword) &&
        string.IsNullOrEmpty(WebhookUrl) &&
        string.IsNullOrEmpty(WebhookAuthToken) &&
        string.IsNullOrEmpty(WebhookUserKey) &&
        string.IsNullOrEmpty(WebhookNtfyTopic);

    public bool Equals(OperatorNotificationSecrets? other) =>
        other is not null &&
        string.Equals(SmtpUsername, other.SmtpUsername, StringComparison.Ordinal) &&
        string.Equals(SmtpPassword, other.SmtpPassword, StringComparison.Ordinal) &&
        string.Equals(WebhookUrl, other.WebhookUrl, StringComparison.Ordinal) &&
        string.Equals(WebhookAuthToken, other.WebhookAuthToken, StringComparison.Ordinal) &&
        string.Equals(WebhookUserKey, other.WebhookUserKey, StringComparison.Ordinal) &&
        string.Equals(WebhookNtfyTopic, other.WebhookNtfyTopic, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(
            SmtpUsername is null ? 0 : StringComparer.Ordinal.GetHashCode(SmtpUsername),
            SmtpPassword is null ? 0 : StringComparer.Ordinal.GetHashCode(SmtpPassword),
            WebhookUrl is null ? 0 : StringComparer.Ordinal.GetHashCode(WebhookUrl),
            WebhookAuthToken is null ? 0 : StringComparer.Ordinal.GetHashCode(WebhookAuthToken),
            WebhookUserKey is null ? 0 : StringComparer.Ordinal.GetHashCode(WebhookUserKey),
            WebhookNtfyTopic is null ? 0 : StringComparer.Ordinal.GetHashCode(WebhookNtfyTopic));

    public override string ToString() => "[REDACTED]";
}

/// <summary>Where a protected notification secret currently comes from, without revealing its value.</summary>
public enum NotificationSecretSource
{
    None = 0,
    ProtectedStore = 1,
    Configuration = 2
}

/// <summary>Sanitized presence metadata for stored notification secrets.</summary>
public sealed record OperatorNotificationSecretPresence(
    bool HasSmtpUsername,
    NotificationSecretSource SmtpUsernameSource,
    bool HasSmtpPassword,
    NotificationSecretSource SmtpPasswordSource,
    bool HasWebhookUrl,
    string? WebhookEndpointHost,
    NotificationSecretSource WebhookUrlSource,
    bool HasWebhookAuthToken,
    NotificationSecretSource WebhookAuthTokenSource,
    bool HasWebhookUserKey,
    NotificationSecretSource WebhookUserKeySource,
    bool HasWebhookNtfyTopic = false,
    NotificationSecretSource WebhookNtfyTopicSource = NotificationSecretSource.None)
{
    public static OperatorNotificationSecretPresence Empty { get; } = new(
        false, NotificationSecretSource.None,
        false, NotificationSecretSource.None,
        false, null, NotificationSecretSource.None,
        false, NotificationSecretSource.None,
        false, NotificationSecretSource.None,
        false, NotificationSecretSource.None);
}

/// <summary>Effective non-secret configuration snapshot plus optimistic concurrency version and sanitized secret presence.</summary>
public sealed record OperatorNotificationsConfigurationSnapshot(
    OperatorNotificationsOptions Options,
    OperatorNotificationSecretPresence Secrets,
    string Version,
    bool HasSavedOverrides,
    bool CanPersist,
    bool StoreCorrupt = false,
    string? StoreStatusReasonCode = null);

/// <summary>Write-only update request from the operator UI or API. Omitted or null secret fields retain existing values.</summary>
[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
    Justification = "API/UI update input carries raw operator-entered webhook URL string for strict validation before storage.")]
[SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
    Justification = "API/UI update input carries raw operator-entered webhook URL string for strict validation before storage.")]
public sealed record OperatorNotificationsUpdateRequest(
    string ExpectedVersion,
    OperatorNotificationsOptions Options,
    string? SmtpUsername = null,
    bool ClearSmtpUsername = false,
    string? SmtpPassword = null,
    bool ClearSmtpPassword = false,
    string? WebhookUrl = null,
    bool ClearWebhookUrl = false,
    string? WebhookAuthToken = null,
    bool ClearWebhookAuthToken = false,
    string? WebhookUserKey = null,
    bool ClearWebhookUserKey = false,
    string? WebhookNtfyTopic = null,
    bool ClearWebhookNtfyTopic = false)
{
    public override string ToString() => $"OperatorNotificationsUpdateRequest(Version={ExpectedVersion}, Enabled={Options.Enabled}, Secrets=[REDACTED])";
}

/// <summary>Outcome of persisting an operator notifications configuration update.</summary>
public enum OperatorNotificationsSaveOutcome
{
    Applied = 0,
    Invalid = 1,
    Conflict = 2,
    Unavailable = 3
}

public sealed record OperatorNotificationsSaveResult(
    OperatorNotificationsSaveOutcome Outcome,
    OperatorNotificationsConfigurationSnapshot? Snapshot,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Problems = null);

/// <summary>Target channel selection for an operator-initiated test notification.</summary>
public enum NotificationTestTarget
{
    AllEnabled = 0,
    Smtp = 1,
    Webhook = 2
}

/// <summary>Operational availability state of a single notification channel.</summary>
public enum NotificationChannelHealthState
{
    Disabled = 0,
    Ready = 1,
    Degraded = 2,
    OutageBackoff = 3
}

/// <summary>Sanitized runtime status for one notification channel.</summary>
public sealed record OperatorNotificationChannelStatus(
    string Channel,
    bool Enabled,
    NotificationChannelHealthState State,
    long SentCount,
    long FailedCount,
    int ConsecutiveFailures,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    string? LastFailureReasonCode,
    DateTimeOffset? BackoffUntilUtc);

/// <summary>One bounded, sanitized recent delivery outcome entry.</summary>
public sealed record OperatorNotificationAuditEntry(
    DateTimeOffset OccurredUtc,
    string EventKind,
    string Channel,
    string Outcome,
    string ReasonCode,
    int CoalescedCount,
    bool IsTest);

/// <summary>Bounded sanitized runtime telemetry and status snapshot for operator notifications.</summary>
public sealed record OperatorNotificationDispatcherSnapshot(
    bool Enabled,
    int QueueDepth,
    int QueueCapacity,
    long TotalPublished,
    long TotalCoalesced,
    long TotalSuppressedCooldown,
    long TotalSuppressedRateLimit,
    long TotalDroppedQueueFull,
    OperatorNotificationChannelStatus Smtp,
    OperatorNotificationChannelStatus Webhook,
    IReadOnlyList<OperatorNotificationAuditEntry> RecentDeliveries,
    string? ConfigurationErrorReasonCode = null,
    long TotalDroppedContended = 0);

/// <summary>Sanitized per-channel result of an operator-initiated test delivery.</summary>
public sealed record NotificationChannelTestResult(
    string Channel,
    bool Attempted,
    bool Succeeded,
    string ReasonCode,
    string Message,
    double DurationMilliseconds);

/// <summary>Overall sanitized response for an operator-initiated test delivery.</summary>
public sealed record OperatorNotificationTestResult(
    bool RateLimited,
    DateTimeOffset? RetryAfterUtc,
    IReadOnlyList<NotificationChannelTestResult> Channels);

/// <summary>Result returned by an SMTP or webhook transport attempt.</summary>
public sealed record OperatorNotificationDeliveryResult(
    string Channel,
    bool Succeeded,
    bool Transient,
    string ReasonCode,
    TimeSpan Duration,
    TimeSpan? RetryAfter = null)
{
    public static OperatorNotificationDeliveryResult Success(string channel, TimeSpan duration) =>
        new(channel, true, false, NotificationReasonCodes.Sent, duration);

    public static OperatorNotificationDeliveryResult Failure(
        string channel,
        string reasonCode,
        bool transient,
        TimeSpan duration,
        TimeSpan? retryAfter = null) =>
        new(channel, false, transient, reasonCode, duration, retryAfter);
}

/// <summary>Fixed, sanitized reason codes used across logs, API responses, and UI feedback.</summary>
public static class NotificationReasonCodes
{
    public const string Sent = "sent";
    public const string Disabled = "disabled";
    public const string InvalidConfiguration = "invalid-configuration";
    public const string MissingSecret = "missing-secret";
    public const string MailpitProhibited = "mailpit-prohibited";
    public const string SsrfBlocked = "ssrf-blocked";
    public const string ConnectFailed = "connect-failed";
    public const string TlsFailed = "tls-failed";
    public const string TlsUnsupported = "tls-unsupported";
    public const string TlsRequired = "tls-required";
    public const string StoreCorrupt = "store-corrupt";
    public const string StoreUnreadable = "store-unreadable";
    public const string AuthFailed = "auth-failed";
    public const string Timeout = "timeout";
    public const string RedirectBlocked = "redirect-blocked";
    public const string RateLimited = "rate-limited";
    public const string ServerError = "server-error";
    public const string ClientRejected = "client-rejected";
    public const string ProtocolError = "protocol-error";
    public const string OutageBackoff = "outage-backoff";
    public const string Cancelled = "cancelled";
    public const string TransportError = "transport-error";

    public static string DescribeForOperator(string reasonCode) => reasonCode switch
    {
        Sent => "Delivered successfully.",
        Disabled => "Channel is disabled.",
        InvalidConfiguration => "Channel configuration is incomplete or invalid.",
        MissingSecret => "Required credentials or webhook URL are not configured.",
        MailpitProhibited => "Mailpit is excluded from production releases; configure a production SMTP relay or mailbox.",
        SsrfBlocked => "The target address is blocked by outbound network safety policy.",
        ConnectFailed => "Could not connect to the configured endpoint.",
        TlsFailed => "TLS negotiation or certificate validation failed.",
        TlsUnsupported => "The SMTP server did not advertise STARTTLS; connection refused to prevent plaintext downgrade.",
        TlsRequired => "TLS (STARTTLS or Implicit TLS) is required for authenticated SMTP or non-local SMTP relays.",
        StoreCorrupt => "The saved operator notification store is unreadable or corrupted; delivery is suspended until replacement settings are saved.",
        StoreUnreadable => "The saved operator notification store path is unreadable or not a regular file; delivery is suspended until storage access is restored or replacement settings are saved.",
        AuthFailed => "Authentication was rejected by the remote server.",
        Timeout => "The remote endpoint did not respond before the configured timeout.",
        RedirectBlocked => "The webhook endpoint returned an HTTP redirect, which is refused for security.",
        RateLimited => "The remote provider rate-limited the request.",
        ServerError => "The remote server returned a transient server error.",
        ClientRejected => "The remote server rejected the request.",
        ProtocolError => "The remote server returned an unexpected protocol response.",
        OutageBackoff => "Channel is in bounded outage backoff after repeated failures.",
        Cancelled => "The delivery attempt was cancelled.",
        _ => "Delivery failed; check sanitized host logs for the event reason code."
    };
}

/// <summary>Server-side SMTP delivery request carrying protected credentials strictly in memory.</summary>
public sealed record SmtpDeliveryRequest(
    string Host,
    int Port,
    SmtpSecurityMode SecurityMode,
    string FromAddress,
    string FromDisplayName,
    IReadOnlyList<string> Recipients,
    string? Username,
    string? Password,
    string Subject,
    string Body,
    string EventSlug,
    TimeSpan Timeout)
{
    public override string ToString() => $"SmtpDeliveryRequest(Host={Host}, Port={Port}, Security={SecurityMode}, Recipients={Recipients.Count}, Event={EventSlug}, Credentials=[REDACTED])";
}

/// <summary>Server-side webhook delivery request carrying protected URL/tokens strictly in memory.</summary>
[SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
    Justification = "Delivery request carries raw stored URL string which is parsed and SSRF-validated inside the transport.")]
[SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
    Justification = "Delivery request carries raw stored URL string which is parsed and SSRF-validated inside the transport.")]
public sealed record WebhookDeliveryRequest(
    string Url,
    WebhookPayloadFormat Format,
    string AuthHeaderName,
    string? NtfyTopic,
    bool AllowPrivateNetwork,
    string? AuthToken,
    string? UserKey,
    OperatorNotificationEvent Event,
    int CoalescedCount,
    int SuppressedDuringCooldown,
    bool IsTest,
    TimeSpan Timeout)
{
    public override string ToString() => $"WebhookDeliveryRequest(Host={OperatorNotificationRedactor.FormatEndpointHost(Url) ?? "[REDACTED]"}, Format={Format}, Event={OperatorNotificationEvent.EventSlug(Event.Kind)}, Credentials=[REDACTED])";
}

/// <summary>Non-blocking entry point for publishing local operational health events.</summary>
public interface IOperatorNotificationPublisher
{
    /// <summary>
    /// Enqueues or coalesces <paramref name="notification"/> in memory without ever performing I/O, waiting on network
    /// calls, or throwing. Returns <c>false</c> when notifications are disabled, the event is disabled, rate-limited,
    /// or dropped due to queue saturation.
    /// </summary>
    bool TryPublish(OperatorNotificationEvent notification);
}

/// <summary>Protected server-side store for notification credentials.</summary>
public interface IOperatorNotificationSecretStore
{
    ValueTask<OperatorNotificationSecrets> ReadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(OperatorNotificationSecrets secrets, CancellationToken cancellationToken);

    ValueTask ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Effective delivery configuration plus isolated store/validation fault status.</summary>
public sealed record OperatorNotificationEffectiveConfiguration(
    OperatorNotificationsOptions Options,
    OperatorNotificationSecrets Secrets,
    bool StoreCorrupt = false,
    string? ConfigurationErrorReasonCode = null);

/// <summary>Durable checkpoint store for the once-per-site-day operator heartbeat.</summary>
public interface IOperatorNotificationHeartbeatStore
{
    ValueTask<DateOnly?> ReadLastHeartbeatSiteDateAsync(CancellationToken cancellationToken);

    ValueTask WriteLastHeartbeatSiteDateAsync(DateOnly siteDate, CancellationToken cancellationToken);
}

/// <summary>Reads and persists operator notification configuration and protected secrets.</summary>
public interface IOperatorNotificationConfigProvider
{
    ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Returns the current non-secret options synchronously from memory without performing disk I/O.</summary>
    OperatorNotificationsOptions CurrentOptions { get; }

    /// <summary>Current non-null sanitized configuration or store-corruption reason code, if delivery is suspended.</summary>
    string? ConfigurationErrorReasonCode => null;

    /// <summary>Loads effective options together with server-side protected secrets for delivery or test-send.</summary>
    ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(
        CancellationToken cancellationToken);

    /// <summary>Loads effective delivery configuration including isolated store-corruption or validation-error status.</summary>
    async ValueTask<OperatorNotificationEffectiveConfiguration> GetEffectiveConfigurationAsync(CancellationToken cancellationToken)
    {
        var (options, secrets) = await GetEffectiveForDeliveryAsync(cancellationToken).ConfigureAwait(false);
        return new OperatorNotificationEffectiveConfiguration(options, secrets, false, ConfigurationErrorReasonCode);
    }

    ValueTask<OperatorNotificationsSaveResult> SaveAsync(
        OperatorNotificationsUpdateRequest request,
        string actor,
        CancellationToken cancellationToken);
}

public interface ISmtpNotificationTransport
{
    Task<OperatorNotificationDeliveryResult> SendAsync(SmtpDeliveryRequest request, CancellationToken cancellationToken);
}

public interface IWebhookNotificationTransport
{
    Task<OperatorNotificationDeliveryResult> SendAsync(WebhookDeliveryRequest request, CancellationToken cancellationToken);
}

/// <summary>Coordinator service for operator notification status, configuration updates, and safe test-send.</summary>
public interface IOperatorNotificationService
{
    OperatorNotificationDispatcherSnapshot GetStatus();

    ValueTask<OperatorNotificationsConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken);

    ValueTask<OperatorNotificationsSaveResult> UpdateConfigurationAsync(
        OperatorNotificationsUpdateRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<OperatorNotificationTestResult> SendTestAsync(
        NotificationTestTarget target,
        string actor,
        CancellationToken cancellationToken);
}
