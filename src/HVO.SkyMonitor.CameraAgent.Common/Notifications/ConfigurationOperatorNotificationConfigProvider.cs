using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Read-only <see cref="IConfiguration"/>-backed provider for operator notifications. Used as the default fallback
/// in hostless or Common-only test graphs; the CameraAgent host replaces this with the DataProtection-backed store.
/// </summary>
public sealed class ConfigurationOperatorNotificationConfigProvider(
    IConfiguration configuration,
    OperatorNotificationNetworkPolicy? networkPolicy = null) : IOperatorNotificationConfigProvider
{
    public const string SmtpUsernameConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:Username";
    public const string SmtpPasswordConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:Password";
    public const string WebhookUrlConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:Url";
    public const string WebhookAuthTokenConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:AuthToken";
    public const string WebhookUserKeyConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:UserKey";
    public const string WebhookNtfyTopicConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:NtfyTopic";

    private static readonly JsonSerializerOptions VersionJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly OperatorNotificationNetworkPolicy _networkPolicy = networkPolicy ?? new OperatorNotificationNetworkPolicy(isDevelopment: false);

    public OperatorNotificationsOptions CurrentOptions => ReadOptionsFromConfiguration(_configuration);

    public string? ConfigurationErrorReasonCode
    {
        get
        {
            var options = ReadOptionsFromConfiguration(_configuration);
            var secrets = ReadSecretsFromConfiguration(_configuration);
            return EvaluateConfigurationErrorReasonCode(options, secrets, _networkPolicy);
        }
    }

    public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = ReadOptionsFromConfiguration(_configuration);
        var configSecrets = ReadSecretsFromConfiguration(_configuration);
        var presence = BuildSecretPresence(OperatorNotificationSecrets.Empty, configSecrets);
        var version = ComputeVersion(options, presence);
        return ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(
            options,
            presence,
            version,
            HasSavedOverrides: false,
            CanPersist: false));
    }

    public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((ReadOptionsFromConfiguration(_configuration), ReadSecretsFromConfiguration(_configuration)));
    }

    public ValueTask<OperatorNotificationEffectiveConfiguration> GetEffectiveConfigurationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = ReadOptionsFromConfiguration(_configuration);
        var secrets = ReadSecretsFromConfiguration(_configuration);
        var errorCode = EvaluateConfigurationErrorReasonCode(options, secrets, _networkPolicy);
        return ValueTask.FromResult(new OperatorNotificationEffectiveConfiguration(
            options,
            secrets,
            StoreCorrupt: false,
            ConfigurationErrorReasonCode: errorCode));
    }

    public ValueTask<OperatorNotificationsSaveResult> SaveAsync(
        OperatorNotificationsUpdateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(new OperatorNotificationsSaveResult(
            OperatorNotificationsSaveOutcome.Unavailable,
            Snapshot: null,
            Message: "This host does not provide persistent operator notification storage."));
    }

    public static OperatorNotificationsOptions ReadOptionsFromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var bound = configuration.GetSection(OperatorNotificationsOptions.SectionKey).Get<OperatorNotificationsOptions>();
        return NormalizeOptions(bound ?? new OperatorNotificationsOptions());
    }

    public static OperatorNotificationSecrets ReadSecretsFromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new OperatorNotificationSecrets(
            SmtpUsername: NullIfEmpty(configuration[SmtpUsernameConfigKey]),
            SmtpPassword: NullIfEmpty(configuration[SmtpPasswordConfigKey]),
            WebhookUrl: NullIfEmpty(configuration[WebhookUrlConfigKey]?.Trim()),
            WebhookAuthToken: NullIfEmpty(configuration[WebhookAuthTokenConfigKey]),
            WebhookUserKey: NullIfEmpty(configuration[WebhookUserKeyConfigKey]),
            WebhookNtfyTopic: NullIfEmpty(configuration[WebhookNtfyTopicConfigKey]?.Trim()));
    }

    public static OperatorNotificationSecrets MergeSecrets(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        ArgumentNullException.ThrowIfNull(configurationSecrets);
        return new OperatorNotificationSecrets(
            SmtpUsername: Choose(protectedSecrets.SmtpUsername, configurationSecrets.SmtpUsername),
            SmtpPassword: Choose(protectedSecrets.SmtpPassword, configurationSecrets.SmtpPassword),
            WebhookUrl: Choose(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl),
            WebhookAuthToken: Choose(protectedSecrets.WebhookAuthToken, configurationSecrets.WebhookAuthToken),
            WebhookUserKey: Choose(protectedSecrets.WebhookUserKey, configurationSecrets.WebhookUserKey),
            WebhookNtfyTopic: Choose(protectedSecrets.WebhookNtfyTopic, configurationSecrets.WebhookNtfyTopic));
    }

    public static OperatorNotificationSecretPresence BuildSecretPresence(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        ArgumentNullException.ThrowIfNull(configurationSecrets);

        var (hasSmtpUser, smtpUserSource) = Presence(protectedSecrets.SmtpUsername, configurationSecrets.SmtpUsername);
        var (hasSmtpPass, smtpPassSource) = Presence(protectedSecrets.SmtpPassword, configurationSecrets.SmtpPassword);
        var (hasWebhookUrl, webhookUrlSource) = Presence(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl);
        var (hasWebhookToken, webhookTokenSource) = Presence(protectedSecrets.WebhookAuthToken, configurationSecrets.WebhookAuthToken);
        var (hasWebhookUserKey, webhookUserKeySource) = Presence(protectedSecrets.WebhookUserKey, configurationSecrets.WebhookUserKey);
        var (hasWebhookNtfyTopic, webhookNtfyTopicSource) = Presence(protectedSecrets.WebhookNtfyTopic, configurationSecrets.WebhookNtfyTopic);

        var effectiveUrl = Choose(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl);
        var endpointHost = OperatorNotificationRedactor.FormatEndpointHost(effectiveUrl);

        return new OperatorNotificationSecretPresence(
            hasSmtpUser,
            smtpUserSource,
            hasSmtpPass,
            smtpPassSource,
            hasWebhookUrl,
            endpointHost,
            webhookUrlSource,
            hasWebhookToken,
            webhookTokenSource,
            hasWebhookUserKey,
            webhookUserKeySource,
            hasWebhookNtfyTopic,
            webhookNtfyTopicSource);
    }

    public static string? EvaluateConfigurationErrorReasonCode(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets effectiveSecrets,
        OperatorNotificationNetworkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(effectiveSecrets);
        ArgumentNullException.ThrowIfNull(policy);

        if (!options.Enabled)
        {
            return null;
        }

        var problems = OperatorNotificationValidation.Validate(options, effectiveSecrets, policy);
        return problems.Count > 0 ? NotificationReasonCodes.InvalidConfiguration : null;
    }

    public static OperatorNotificationsOptions NormalizeOptions(OperatorNotificationsOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var smtp = source.Smtp ?? new SmtpNotificationOptions();
        var webhook = source.Webhook ?? new WebhookNotificationOptions();
        var events = source.Events ?? new OperatorNotificationEventsOptions();

        var recipients = (smtp.Recipients ?? [])
            .Select(static r => r?.Trim() ?? string.Empty)
            .Where(static r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new OperatorNotificationsOptions
        {
            Enabled = source.Enabled,
            QueueCapacity = source.QueueCapacity,
            MaxNotificationsPerHour = source.MaxNotificationsPerHour,
            RequestTimeoutSeconds = source.RequestTimeoutSeconds,
            MaxRetryAttempts = source.MaxRetryAttempts,
            RetryInitialDelaySeconds = source.RetryInitialDelaySeconds,
            RetryMaximumDelaySeconds = source.RetryMaximumDelaySeconds,
            OutageCooldownSeconds = source.OutageCooldownSeconds,
            ConsecutiveOutageThreshold = source.ConsecutiveOutageThreshold,
            EvaluationIntervalSeconds = source.EvaluationIntervalSeconds,
            CaptureStallThresholdMinutes = source.CaptureStallThresholdMinutes,
            CameraDisconnectedThresholdSeconds = source.CameraDisconnectedThresholdSeconds,
            TestSendMinimumIntervalSeconds = source.TestSendMinimumIntervalSeconds,
            Smtp = new SmtpNotificationOptions
            {
                Enabled = smtp.Enabled,
                Host = (smtp.Host ?? string.Empty).Trim(),
                Port = smtp.Port,
                SecurityMode = smtp.SecurityMode,
                FromAddress = (smtp.FromAddress ?? string.Empty).Trim(),
                FromDisplayName = string.IsNullOrWhiteSpace(smtp.FromDisplayName)
                    ? "HVO SkyMonitor CameraAgent"
                    : smtp.FromDisplayName.Trim(),
                Recipients = recipients
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = webhook.Enabled,
                Format = webhook.Format,
                AuthHeaderName = string.IsNullOrWhiteSpace(webhook.AuthHeaderName)
                    ? WebhookNotificationOptions.DefaultAuthHeaderName
                    : webhook.AuthHeaderName.Trim(),
                AllowPrivateNetwork = webhook.AllowPrivateNetwork
            },
            Events = new OperatorNotificationEventsOptions
            {
                CaptureStalledEnabled = events.CaptureStalledEnabled,
                CaptureStalledCooldownMinutes = events.CaptureStalledCooldownMinutes,
                CameraDisconnectedEnabled = events.CameraDisconnectedEnabled,
                CameraDisconnectedCooldownMinutes = events.CameraDisconnectedCooldownMinutes,
                DiskPressureEnabled = events.DiskPressureEnabled,
                DiskPressureCooldownMinutes = events.DiskPressureCooldownMinutes,
                UpgradeOrRecoveryFailedEnabled = events.UpgradeOrRecoveryFailedEnabled,
                UpgradeOrRecoveryFailedCooldownMinutes = events.UpgradeOrRecoveryFailedCooldownMinutes,
                DailyHeartbeatEnabled = events.DailyHeartbeatEnabled,
                DailyHeartbeatTime = string.IsNullOrWhiteSpace(events.DailyHeartbeatTime)
                    ? OperatorNotificationsOptions.DefaultDailyHeartbeatTime
                    : events.DailyHeartbeatTime.Trim()
            }
        };
    }

    public static string ComputeVersion(
        OperatorNotificationsOptions options,
        OperatorNotificationSecretPresence presence,
        string? persistedRevision = null)
    {
        var canonical = JsonSerializer.Serialize(new { options, presence, revision = persistedRevision ?? string.Empty }, VersionJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static string? Choose(string? protectedValue, string? configValue)
        => !string.IsNullOrEmpty(protectedValue)
            ? protectedValue
            : !string.IsNullOrEmpty(configValue) ? configValue : null;

    private static (bool HasValue, NotificationSecretSource Source) Presence(string? protectedValue, string? configValue)
    {
        if (!string.IsNullOrEmpty(protectedValue))
        {
            return (true, NotificationSecretSource.ProtectedStore);
        }
        if (!string.IsNullOrEmpty(configValue))
        {
            return (true, NotificationSecretSource.Configuration);
        }
        return (false, NotificationSecretSource.None);
    }
}
