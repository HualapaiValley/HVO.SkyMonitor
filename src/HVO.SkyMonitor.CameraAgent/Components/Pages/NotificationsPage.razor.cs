using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class NotificationsPage : SiteTimeComponent, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private NotificationsPageView? _view;
    private string? _loadError;
    private NotificationBanner? _banner;
    private OperatorNotificationTestResult? _lastTestResult;
    private bool _loading = true;
    private bool _saving;
    private bool _testing;
    private bool _disposed;

    // Non-secret form state
    private bool _enabled;
    private int _maxNotificationsPerHour = OperatorNotificationsOptions.DefaultMaxNotificationsPerHour;
    private int _requestTimeoutSeconds = OperatorNotificationsOptions.DefaultRequestTimeoutSeconds;
    private int _captureStallThresholdMinutes = OperatorNotificationsOptions.DefaultCaptureStallThresholdMinutes;

    private bool _smtpEnabled;
    private string _smtpHost = string.Empty;
    private int _smtpPort = SmtpNotificationOptions.DefaultPort;
    private SmtpSecurityMode _smtpSecurityMode = SmtpSecurityMode.StartTls;
    private string _smtpFromAddress = string.Empty;
    private string _smtpFromDisplayName = "HVO SkyMonitor CameraAgent";
    private string _smtpRecipients = string.Empty;

    private bool _webhookEnabled;
    private WebhookPayloadFormat _webhookFormat = WebhookPayloadFormat.Generic;
    private string _webhookAuthHeaderName = WebhookNotificationOptions.DefaultAuthHeaderName;
    private bool _webhookAllowPrivateNetwork;

    private bool _captureStalledEnabled = true;
    private int _captureStalledCooldown = OperatorNotificationEventsOptions.DefaultFaultCooldownMinutes;
    private bool _cameraDisconnectedEnabled = true;
    private int _cameraDisconnectedCooldown = OperatorNotificationEventsOptions.DefaultFaultCooldownMinutes;
    private bool _diskPressureEnabled = true;
    private int _diskPressureCooldown = OperatorNotificationEventsOptions.DefaultDiskCooldownMinutes;
    private bool _upgradeFailedEnabled = true;
    private int _upgradeFailedCooldown = OperatorNotificationEventsOptions.DefaultUpgradeRecoveryCooldownMinutes;
    private bool _dailyHeartbeatEnabled = true;
    private string _dailyHeartbeatTime = OperatorNotificationsOptions.DefaultDailyHeartbeatTime;

    // Write-only secret inputs (always cleared after load/save; never populated from stored secrets)
    private string _smtpUsernameInput = string.Empty;
    private string _smtpPasswordInput = string.Empty;
    private bool _clearSmtpUsername;
    private bool _clearSmtpPassword;
    private string _webhookUrlInput = string.Empty;
    private string _webhookAuthTokenInput = string.Empty;
    private string _webhookUserKeyInput = string.Empty;
    private string _webhookNtfyTopicInput = string.Empty;
    private bool _clearWebhookUrl;
    private bool _clearWebhookAuthToken;
    private bool _clearWebhookUserKey;
    private bool _clearWebhookNtfyTopic;

    [Inject] internal ICameraAgentNotificationsUiService NotificationsService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    private bool Busy => _loading || _saving || _testing;
    private bool CanEdit => !Busy && _view is { CanMutate: true, Configuration.CanPersist: true };
    private bool CanTest => !Busy && _view is { CanMutate: true };

    private string FormatSiteTime(DateTimeOffset value) => SiteTime.Format(value);

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }
        _loading = true;
        _loadError = null;
        try
        {
            var result = await NotificationsService.GetAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied", replace: true);
                return;
            }
            if (result.Kind != OperatorUiResultKind.Success || result.Value is null)
            {
                _loadError = result.Message ?? "Notification settings could not be loaded.";
                return;
            }

            _view = result.Value;
            PopulateFormFromOptions(result.Value.Configuration.Options);
            ClearSecretInputs();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Component disposed.
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SaveAsync()
    {
        if (!CanEdit || _view is null)
        {
            return;
        }

        _saving = true;
        _banner = null;
        try
        {
            var current = _view.Configuration.Options;
            var recipients = _smtpRecipients
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var retryInitialDelaySeconds = NormalizeHiddenInt(
                current.RetryInitialDelaySeconds, 1, 300, OperatorNotificationsOptions.DefaultRetryInitialDelaySeconds);
            var retryMaximumDelaySeconds = current.RetryMaximumDelaySeconds is >= 1 and <= 3600 &&
                current.RetryMaximumDelaySeconds >= retryInitialDelaySeconds
                ? current.RetryMaximumDelaySeconds
                : OperatorNotificationsOptions.DefaultRetryMaximumDelaySeconds;

            var updatedOptions = new OperatorNotificationsOptions
            {
                Enabled = _enabled,
                QueueCapacity = NormalizeHiddenInt(
                    current.QueueCapacity, 4, 256, OperatorNotificationsOptions.DefaultQueueCapacity),
                MaxNotificationsPerHour = _maxNotificationsPerHour,
                RequestTimeoutSeconds = _requestTimeoutSeconds,
                MaxRetryAttempts = NormalizeHiddenInt(
                    current.MaxRetryAttempts, 0, 5, OperatorNotificationsOptions.DefaultMaxRetryAttempts),
                RetryInitialDelaySeconds = retryInitialDelaySeconds,
                RetryMaximumDelaySeconds = retryMaximumDelaySeconds,
                OutageCooldownSeconds = NormalizeHiddenInt(
                    current.OutageCooldownSeconds, 10, 3600, OperatorNotificationsOptions.DefaultOutageCooldownSeconds),
                ConsecutiveOutageThreshold = NormalizeHiddenInt(
                    current.ConsecutiveOutageThreshold, 1, 10, OperatorNotificationsOptions.DefaultConsecutiveOutageThreshold),
                EvaluationIntervalSeconds = NormalizeHiddenInt(
                    current.EvaluationIntervalSeconds, 5, 600, OperatorNotificationsOptions.DefaultEvaluationIntervalSeconds),
                CaptureStallThresholdMinutes = _captureStallThresholdMinutes,
                CameraDisconnectedThresholdSeconds = NormalizeHiddenInt(
                    current.CameraDisconnectedThresholdSeconds, 0, 3600, OperatorNotificationsOptions.DefaultCameraDisconnectedThresholdSeconds),
                TestSendMinimumIntervalSeconds = NormalizeHiddenInt(
                    current.TestSendMinimumIntervalSeconds, 0, 300, OperatorNotificationsOptions.DefaultTestSendMinimumIntervalSeconds),
                Smtp = new SmtpNotificationOptions
                {
                    Enabled = _smtpEnabled,
                    Host = _smtpHost,
                    Port = _smtpPort,
                    SecurityMode = Enum.IsDefined(_smtpSecurityMode) ? _smtpSecurityMode : SmtpSecurityMode.StartTls,
                    FromAddress = _smtpFromAddress,
                    FromDisplayName = _smtpFromDisplayName,
                    Recipients = recipients
                },
                Webhook = new WebhookNotificationOptions
                {
                    Enabled = _webhookEnabled,
                    Format = Enum.IsDefined(_webhookFormat) ? _webhookFormat : WebhookPayloadFormat.Generic,
                    AuthHeaderName = _webhookAuthHeaderName,
                    AllowPrivateNetwork = _webhookAllowPrivateNetwork
                },
                Events = new OperatorNotificationEventsOptions
                {
                    CaptureStalledEnabled = _captureStalledEnabled,
                    CaptureStalledCooldownMinutes = _captureStalledCooldown,
                    CameraDisconnectedEnabled = _cameraDisconnectedEnabled,
                    CameraDisconnectedCooldownMinutes = _cameraDisconnectedCooldown,
                    DiskPressureEnabled = _diskPressureEnabled,
                    DiskPressureCooldownMinutes = _diskPressureCooldown,
                    UpgradeOrRecoveryFailedEnabled = _upgradeFailedEnabled,
                    UpgradeOrRecoveryFailedCooldownMinutes = _upgradeFailedCooldown,
                    DailyHeartbeatEnabled = _dailyHeartbeatEnabled,
                    DailyHeartbeatTime = _dailyHeartbeatTime
                }
            };

            var request = new OperatorNotificationsUpdateRequest(
                _view.Configuration.Version,
                updatedOptions,
                SmtpUsername: string.IsNullOrEmpty(_smtpUsernameInput) ? null : _smtpUsernameInput,
                ClearSmtpUsername: _clearSmtpUsername,
                SmtpPassword: string.IsNullOrEmpty(_smtpPasswordInput) ? null : _smtpPasswordInput,
                ClearSmtpPassword: _clearSmtpPassword,
                WebhookUrl: string.IsNullOrEmpty(_webhookUrlInput) ? null : _webhookUrlInput,
                ClearWebhookUrl: _clearWebhookUrl,
                WebhookAuthToken: string.IsNullOrEmpty(_webhookAuthTokenInput) ? null : _webhookAuthTokenInput,
                ClearWebhookAuthToken: _clearWebhookAuthToken,
                WebhookUserKey: string.IsNullOrEmpty(_webhookUserKeyInput) ? null : _webhookUserKeyInput,
                ClearWebhookUserKey: _clearWebhookUserKey,
                WebhookNtfyTopic: string.IsNullOrEmpty(_webhookNtfyTopicInput) ? null : _webhookNtfyTopicInput,
                ClearWebhookNtfyTopic: _clearWebhookNtfyTopic);

            var result = await NotificationsService.SaveAsync(request, _lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied", replace: true);
                return;
            }
            if (result.Kind != OperatorUiResultKind.Success || result.Value is null)
            {
                _banner = new NotificationBanner(result.Message ?? "Notification settings could not be saved.", "failure", IsError: true);
                return;
            }

            _view = _view with
            {
                Configuration = result.Value.Configuration,
                Status = result.Value.Status
            };
            PopulateFormFromOptions(result.Value.Configuration.Options);
            ClearSecretInputs();
            _banner = new NotificationBanner(result.Value.Message, "success", IsError: false);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Component disposed.
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task SendTestAsync(NotificationTestTarget target)
    {
        if (!CanTest)
        {
            return;
        }

        _testing = true;
        _banner = null;
        try
        {
            var result = await NotificationsService.SendTestAsync(target, _lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied", replace: true);
                return;
            }
            if (result.Kind != OperatorUiResultKind.Success || result.Value is null)
            {
                _banner = new NotificationBanner(result.Message ?? "Test notification could not be sent.", "failure", IsError: true);
                return;
            }

            _lastTestResult = result.Value;
            // Refresh runtime status so recent delivery history reflects the test.
            var refreshed = await NotificationsService.GetAsync(_lifetime.Token);
            if (!_disposed && refreshed.Kind == OperatorUiResultKind.Success && refreshed.Value is not null)
            {
                _view = _view! with { Status = refreshed.Value.Status };
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Component disposed.
        }
        finally
        {
            _testing = false;
        }
    }

    private void PopulateFormFromOptions(OperatorNotificationsOptions options)
    {
        _enabled = options.Enabled;
        _maxNotificationsPerHour = options.MaxNotificationsPerHour;
        _requestTimeoutSeconds = options.RequestTimeoutSeconds;
        _captureStallThresholdMinutes = options.CaptureStallThresholdMinutes;

        _smtpEnabled = options.Smtp.Enabled;
        _smtpHost = options.Smtp.Host;
        _smtpPort = options.Smtp.Port;
        _smtpSecurityMode = Enum.IsDefined(options.Smtp.SecurityMode) ? options.Smtp.SecurityMode : SmtpSecurityMode.StartTls;
        _smtpFromAddress = options.Smtp.FromAddress;
        _smtpFromDisplayName = options.Smtp.FromDisplayName;
        _smtpRecipients = string.Join(", ", options.Smtp.Recipients);

        _webhookEnabled = options.Webhook.Enabled;
        _webhookFormat = Enum.IsDefined(options.Webhook.Format) ? options.Webhook.Format : WebhookPayloadFormat.Generic;
        _webhookAuthHeaderName = options.Webhook.AuthHeaderName;
        _webhookAllowPrivateNetwork = options.Webhook.AllowPrivateNetwork;

        _captureStalledEnabled = options.Events.CaptureStalledEnabled;
        _captureStalledCooldown = options.Events.CaptureStalledCooldownMinutes;
        _cameraDisconnectedEnabled = options.Events.CameraDisconnectedEnabled;
        _cameraDisconnectedCooldown = options.Events.CameraDisconnectedCooldownMinutes;
        _diskPressureEnabled = options.Events.DiskPressureEnabled;
        _diskPressureCooldown = options.Events.DiskPressureCooldownMinutes;
        _upgradeFailedEnabled = options.Events.UpgradeOrRecoveryFailedEnabled;
        _upgradeFailedCooldown = options.Events.UpgradeOrRecoveryFailedCooldownMinutes;
        _dailyHeartbeatEnabled = options.Events.DailyHeartbeatEnabled;
        _dailyHeartbeatTime = options.Events.DailyHeartbeatTime;
    }

    private void ClearSecretInputs()
    {
        _smtpUsernameInput = string.Empty;
        _smtpPasswordInput = string.Empty;
        _clearSmtpUsername = false;
        _clearSmtpPassword = false;
        _webhookUrlInput = string.Empty;
        _webhookAuthTokenInput = string.Empty;
        _webhookUserKeyInput = string.Empty;
        _webhookNtfyTopicInput = string.Empty;
        _clearWebhookUrl = false;
        _clearWebhookAuthToken = false;
        _clearWebhookUserKey = false;
        _clearWebhookNtfyTopic = false;
    }

    private static int NormalizeHiddenInt(int value, int minimum, int maximum, int defaultValue) =>
        value < minimum || value > maximum ? defaultValue : value;

    private static string ChannelStateLabel(OperatorNotificationChannelStatus status) => status.State switch
    {
        NotificationChannelHealthState.Disabled => "Disabled",
        NotificationChannelHealthState.Ready => "Ready",
        NotificationChannelHealthState.Degraded => "Degraded",
        NotificationChannelHealthState.OutageBackoff => "Outage backoff",
        _ => "Unknown"
    };

    private static string ChannelChip(OperatorNotificationChannelStatus status) => status.State switch
    {
        NotificationChannelHealthState.Disabled => "pending",
        NotificationChannelHealthState.Ready => "success",
        NotificationChannelHealthState.Degraded => "warning",
        NotificationChannelHealthState.OutageBackoff => "warning",
        _ => "pending"
    };

    private static string ChannelDetail(OperatorNotificationChannelStatus status) => status.State switch
    {
        NotificationChannelHealthState.Disabled => "Off",
        NotificationChannelHealthState.OutageBackoff => $"Backoff ({status.LastFailureReasonCode ?? "outage"})",
        _ when status.SentCount > 0 || status.FailedCount > 0 => $"{status.SentCount} sent · {status.FailedCount} failed",
        _ => "No deliveries yet"
    };

    private static string SecretSourceText(bool hasValue, NotificationSecretSource source)
    {
        if (!hasValue)
        {
            return "Not set";
        }
        return source == NotificationSecretSource.ProtectedStore ? "Protected store" : "Host configuration";
    }

    private static string WebhookUrlStatusText(OperatorNotificationSecretPresence secrets)
    {
        if (!secrets.HasWebhookUrl)
        {
            return "Not set";
        }
        var host = string.IsNullOrWhiteSpace(secrets.WebhookEndpointHost) ? "configured" : secrets.WebhookEndpointHost;
        var source = secrets.WebhookUrlSource == NotificationSecretSource.ProtectedStore ? "protected" : "config";
        return $"{host} ({source})";
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed record NotificationBanner(string Text, string Tone, bool IsError);
}
