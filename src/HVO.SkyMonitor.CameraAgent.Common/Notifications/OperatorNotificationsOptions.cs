using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Bounded catalog of standalone CameraAgent local operational health events eligible for operator notification.
/// </summary>
public enum OperatorNotificationEventKind
{
    CaptureStalled = 0,
    CameraDisconnected = 1,
    DiskPressure = 2,
    UpgradeOrRecoveryFailed = 3,
    DailyHeartbeat = 4
}

/// <summary>Sanitized operational severity carried on a notification.</summary>
public enum OperatorNotificationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>Transport encryption mode for SMTP delivery.</summary>
public enum SmtpSecurityMode
{
    /// <summary>Opportunistic-free STARTTLS (RFC 3207, typically port 587). Fails closed if the server does not advertise STARTTLS.</summary>
    StartTls = 0,

    /// <summary>Implicit TLS immediately on connect (SMTPS / RFC 8314, typically port 465).</summary>
    ImplicitTls = 1,

    /// <summary>Plaintext SMTP without TLS (for explicit local LAN relays only).</summary>
    None = 2
}

/// <summary>JSON payload profile for outbound generic HTTP POST webhooks.</summary>
public enum WebhookPayloadFormat
{
    /// <summary>Structured CameraAgent JSON payload with top-level <c>title</c>, <c>text</c>, and <c>message</c> fields.</summary>
    Generic = 0,

    /// <summary>Slack incoming webhook JSON payload (<c>text</c> and <c>blocks</c>).</summary>
    Slack = 1,

    /// <summary>ntfy JSON webhook payload with optional <c>topic</c> and ntfy priority/tag headers.</summary>
    Ntfy = 2,

    /// <summary>Pushover Messages API JSON payload (<c>token</c>, <c>user</c>, <c>title</c>, <c>message</c>, <c>priority</c>).</summary>
    Pushover = 3
}

/// <summary>
/// Configuration for optional, opt-in local operator notifications via SMTP email and/or a generic HTTP POST webhook.
/// Notifications are disabled by default and never block capture, processing, retention, login, or recovery.
/// </summary>
public sealed class OperatorNotificationsOptions : IValidatableObject
{
    public const string SectionKey = "CameraAgent:Notifications";

    public const int MaximumRecipients = 8;
    public const int DefaultQueueCapacity = 32;
    public const int DefaultMaxNotificationsPerHour = 12;
    public const int DefaultRequestTimeoutSeconds = 10;
    public const int DefaultMaxRetryAttempts = 3;
    public const int DefaultRetryInitialDelaySeconds = 15;
    public const int DefaultRetryMaximumDelaySeconds = 300;
    public const int DefaultOutageCooldownSeconds = 300;
    public const int DefaultConsecutiveOutageThreshold = 3;
    public const int DefaultEvaluationIntervalSeconds = 30;
    public const int DefaultCaptureStallThresholdMinutes = 15;
    public const int DefaultCameraDisconnectedThresholdSeconds = 60;
    public const int DefaultTestSendMinimumIntervalSeconds = 10;
    public const string DefaultDailyHeartbeatTime = "08:00";

    /// <summary>Master opt-in switch. Always false by default so notifications perform no work unless explicitly enabled.</summary>
    public bool Enabled { get; init; }

    [Required]
    public SmtpNotificationOptions Smtp { get; init; } = new();

    [Required]
    public WebhookNotificationOptions Webhook { get; init; } = new();

    [Required]
    public OperatorNotificationEventsOptions Events { get; init; } = new();

    /// <summary>Maximum distinct pending notifications held in memory before excess events are dropped.</summary>
    [Range(4, 256)]
    public int QueueCapacity { get; init; } = DefaultQueueCapacity;

    /// <summary>Global sliding-window cap across all operational notification events per hour.</summary>
    [Range(1, 60)]
    public int MaxNotificationsPerHour { get; init; } = DefaultMaxNotificationsPerHour;

    /// <summary>Explicit per-attempt network timeout in seconds for SMTP and webhook delivery.</summary>
    [Range(1, 30)]
    public int RequestTimeoutSeconds { get; init; } = DefaultRequestTimeoutSeconds;

    /// <summary>Maximum bounded retries per channel for transient delivery failures.</summary>
    [Range(0, 5)]
    public int MaxRetryAttempts { get; init; } = DefaultMaxRetryAttempts;

    /// <summary>Initial exponential backoff delay in seconds after a transient channel failure.</summary>
    [Range(1, 300)]
    public int RetryInitialDelaySeconds { get; init; } = DefaultRetryInitialDelaySeconds;

    /// <summary>Maximum exponential backoff delay in seconds after repeated transient failures.</summary>
    [Range(1, 3600)]
    public int RetryMaximumDelaySeconds { get; init; } = DefaultRetryMaximumDelaySeconds;

    /// <summary>Circuit-breaker cooldown in seconds after consecutive channel failures.</summary>
    [Range(10, 3600)]
    public int OutageCooldownSeconds { get; init; } = DefaultOutageCooldownSeconds;

    /// <summary>Consecutive failures required before a channel enters outage backoff.</summary>
    [Range(1, 10)]
    public int ConsecutiveOutageThreshold { get; init; } = DefaultConsecutiveOutageThreshold;

    /// <summary>Background observer evaluation period in seconds.</summary>
    [Range(5, 600)]
    public int EvaluationIntervalSeconds { get; init; } = DefaultEvaluationIntervalSeconds;

    /// <summary>Minutes without a successful capture while running and admitted before CaptureStalled fires.</summary>
    [Range(1, 1440)]
    public int CaptureStallThresholdMinutes { get; init; } = DefaultCaptureStallThresholdMinutes;

    /// <summary>Seconds a camera module must remain unavailable before CameraDisconnected fires.</summary>
    [Range(0, 3600)]
    public int CameraDisconnectedThresholdSeconds { get; init; } = DefaultCameraDisconnectedThresholdSeconds;

    /// <summary>Minimum spacing in seconds between operator-initiated test notifications.</summary>
    [Range(0, 300)]
    public int TestSendMinimumIntervalSeconds { get; init; } = DefaultTestSendMinimumIntervalSeconds;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RetryMaximumDelaySeconds < RetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "Notifications:RetryMaximumDelaySeconds must be greater than or equal to RetryInitialDelaySeconds.",
                [nameof(RetryMaximumDelaySeconds), nameof(RetryInitialDelaySeconds)]);
        }

        var nested = new List<ValidationResult>();
        Validator.TryValidateObject(Smtp, new ValidationContext(Smtp), nested, validateAllProperties: true);
        Validator.TryValidateObject(Webhook, new ValidationContext(Webhook), nested, validateAllProperties: true);
        Validator.TryValidateObject(Events, new ValidationContext(Events), nested, validateAllProperties: true);
        foreach (var result in nested)
        {
            yield return result;
        }
    }
}

/// <summary>
/// Non-secret SMTP channel configuration. Credentials (<c>Username</c> and <c>Password</c>) are resolved through the
/// protected secret store or protected configuration providers and are never logged or returned to the UI.
/// </summary>
public sealed class SmtpNotificationOptions : IValidatableObject
{
    public const int DefaultPort = 587;
    public const int MaximumHostLength = 253;
    public const int MaximumAddressLength = 254;
    public const int MaximumDisplayNameLength = 120;

    /// <summary>Opt-in switch for SMTP email delivery. Off by default.</summary>
    public bool Enabled { get; init; }

    [MaxLength(MaximumHostLength)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = DefaultPort;

    [EnumDataType(typeof(SmtpSecurityMode))]
    public SmtpSecurityMode SecurityMode { get; init; } = SmtpSecurityMode.StartTls;

    [MaxLength(MaximumAddressLength)]
    public string FromAddress { get; init; } = string.Empty;

    [MaxLength(MaximumDisplayNameLength)]
    public string FromDisplayName { get; init; } = "HVO SkyMonitor CameraAgent";

    /// <summary>Recipient email addresses (up to <see cref="OperatorNotificationsOptions.MaximumRecipients"/>).</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only",
        Justification = "Configuration binder populates list properties from appsettings.")]
    [SuppressMessage("Design", "CA1002:Do not expose generic lists",
        Justification = "Configuration binder requires concrete List<string> for section binding.")]
    public List<string> Recipients { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(SecurityMode))
        {
            yield return new ValidationResult(
                "Notifications:Smtp:SecurityMode is not supported.",
                [nameof(SecurityMode)]);
        }

        if (Recipients.Count > OperatorNotificationsOptions.MaximumRecipients)
        {
            yield return new ValidationResult(
                $"Notifications:Smtp:Recipients may list at most {OperatorNotificationsOptions.MaximumRecipients} recipients.",
                [nameof(Recipients)]);
        }
    }
}

/// <summary>
/// Non-secret generic HTTP POST webhook configuration. The target webhook URL, bearer/auth token, and optional
/// Pushover user key are treated as protected secrets and are never logged or returned to the UI.
/// </summary>
public sealed class WebhookNotificationOptions : IValidatableObject
{
    public const string DefaultAuthHeaderName = "Authorization";
    public const int MaximumHeaderNameLength = 64;
    public const int MaximumTopicLength = 128;

    /// <summary>Opt-in switch for generic HTTP POST webhook delivery. Off by default.</summary>
    public bool Enabled { get; init; }

    [EnumDataType(typeof(WebhookPayloadFormat))]
    public WebhookPayloadFormat Format { get; init; } = WebhookPayloadFormat.Generic;

    /// <summary>
    /// Optional HTTP header name used to carry the stored webhook auth token for Generic/Slack/ntfy formats.
    /// Defaults to <c>Authorization</c> (sent as <c>Bearer &lt;token&gt;</c>).
    /// </summary>
    [MaxLength(MaximumHeaderNameLength)]
    public string AuthHeaderName { get; init; } = DefaultAuthHeaderName;

    /// <summary>Optional ntfy topic name included in the JSON body when <see cref="Format"/> is <see cref="WebhookPayloadFormat.Ntfy"/>.</summary>
    [MaxLength(MaximumTopicLength)]
    public string NtfyTopic { get; init; } = string.Empty;

    /// <summary>
    /// Allows HTTPS webhooks targeting RFC1918/ULA private LAN hosts (for self-hosted relays such as LAN ntfy).
    /// Loopback, link-local, cloud metadata, unspecified, and multicast addresses remain unconditionally blocked.
    /// </summary>
    public bool AllowPrivateNetwork { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Format))
        {
            yield return new ValidationResult(
                "Notifications:Webhook:Format is not supported.",
                [nameof(Format)]);
        }
    }
}

/// <summary>Per-event enablement, cooldowns, and daily heartbeat schedule for the bounded 5-event catalog.</summary>
public sealed class OperatorNotificationEventsOptions : IValidatableObject
{
    public const int DefaultFaultCooldownMinutes = 30;
    public const int DefaultDiskCooldownMinutes = 60;
    public const int DefaultUpgradeRecoveryCooldownMinutes = 60;
    public const int DefaultHeartbeatCooldownMinutes = 1200;

    public bool CaptureStalledEnabled { get; init; } = true;

    [Range(1, 1440)]
    public int CaptureStalledCooldownMinutes { get; init; } = DefaultFaultCooldownMinutes;

    public bool CameraDisconnectedEnabled { get; init; } = true;

    [Range(1, 1440)]
    public int CameraDisconnectedCooldownMinutes { get; init; } = DefaultFaultCooldownMinutes;

    public bool DiskPressureEnabled { get; init; } = true;

    [Range(1, 1440)]
    public int DiskPressureCooldownMinutes { get; init; } = DefaultDiskCooldownMinutes;

    public bool UpgradeOrRecoveryFailedEnabled { get; init; } = true;

    [Range(1, 1440)]
    public int UpgradeOrRecoveryFailedCooldownMinutes { get; init; } = DefaultUpgradeRecoveryCooldownMinutes;

    public bool DailyHeartbeatEnabled { get; init; } = true;

    /// <summary>Site-local (or UTC) wall-clock time in <c>HH:mm</c> 24-hour format at or after which the daily heartbeat sends once per day.</summary>
    [Required]
    [MaxLength(5)]
    public string DailyHeartbeatTime { get; init; } = OperatorNotificationsOptions.DefaultDailyHeartbeatTime;

    public bool IsEventEnabled(OperatorNotificationEventKind kind) => kind switch
    {
        OperatorNotificationEventKind.CaptureStalled => CaptureStalledEnabled,
        OperatorNotificationEventKind.CameraDisconnected => CameraDisconnectedEnabled,
        OperatorNotificationEventKind.DiskPressure => DiskPressureEnabled,
        OperatorNotificationEventKind.UpgradeOrRecoveryFailed => UpgradeOrRecoveryFailedEnabled,
        OperatorNotificationEventKind.DailyHeartbeat => DailyHeartbeatEnabled,
        _ => false
    };

    public TimeSpan GetCooldown(OperatorNotificationEventKind kind) => TimeSpan.FromMinutes(kind switch
    {
        OperatorNotificationEventKind.CaptureStalled => CaptureStalledCooldownMinutes,
        OperatorNotificationEventKind.CameraDisconnected => CameraDisconnectedCooldownMinutes,
        OperatorNotificationEventKind.DiskPressure => DiskPressureCooldownMinutes,
        OperatorNotificationEventKind.UpgradeOrRecoveryFailed => UpgradeOrRecoveryFailedCooldownMinutes,
        OperatorNotificationEventKind.DailyHeartbeat => DefaultHeartbeatCooldownMinutes,
        _ => DefaultFaultCooldownMinutes
    });

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!OperatorNotificationValidation.TryParseTimeOfDay(DailyHeartbeatTime, out _))
        {
            yield return new ValidationResult(
                "Notifications:Events:DailyHeartbeatTime must use 24-hour HH:mm format (00:00 through 23:59).",
                [nameof(DailyHeartbeatTime)]);
        }
    }
}
